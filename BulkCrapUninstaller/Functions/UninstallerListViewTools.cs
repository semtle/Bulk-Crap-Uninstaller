﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using BrightIdeasSoftware;
using BulkCrapUninstaller.Forms;
using BulkCrapUninstaller.Properties;
using Klocman.Binding;
using Klocman.Events;
using Klocman.Extensions;
using Klocman.Forms;
using Klocman.Forms.Tools;
using Klocman.IO;
using Klocman.Localising;
using UninstallTools.Dialogs;
using UninstallTools.Lists;
using UninstallTools.Startup;
using UninstallTools.Uninstaller;

namespace BulkCrapUninstaller.Functions
{
    public static class Constants
    {
        public static Color VerifiedColor = Color.FromArgb(unchecked((int) 0xffccffcc));
        public static Color UnverifiedColor = Color.FromArgb(unchecked((int) 0xffbbddff));
        public static Color InvalidColor = Color.FromArgb(unchecked((int) 0xffE0E0E0));
        public static Color UnregisteredColor = Color.FromArgb(unchecked((int) 0xffffcccc));
        public static Color WindowsFeatureColor = Color.FromArgb(unchecked((int) 0xffddbbff));
    }

    internal class UninstallerListViewTools : IDisposable
    {
        private readonly UninstallerIconGetter _iconGetter = new UninstallerIconGetter();
        private readonly TypedObjectListView<ApplicationUninstallerEntry> _listView;
        private readonly List<object> _objectsToUpdate = new List<object>();
        private readonly MainWindow _reference;
        private readonly UninstallListItem _searchItem = new UninstallListItem();
        private readonly SettingBinder<Settings> _settings = Settings.Default.SettingBinder;
        private bool _abortPostprocessingThread;
        private Thread _finalizerThread;
        private bool _firstRefresh = true;
        private bool _listRefreshIsRunning;

        internal UninstallerListViewTools(MainWindow reference)
        {
            _reference = reference;
            _listView = new TypedObjectListView<ApplicationUninstallerEntry>(reference.uninstallerObjectListView);
            SetupListView();

            // Start the processing thread when user changes the test certificates option
            _settings.Subscribe((x, y) =>
            {
                if (_firstRefresh)
                    return;
                if (y.NewValue) StartProcessingThread(FilteredUninstallers);
                else
                {
                    StopProcessingThread(false);
                    _listView.ListView.SuspendLayout();
                    _listView.ListView.RefreshObjects(
                        AllUninstallers.Where(u => u.IsCertificateValid(true).HasValue).ToList());
                    _listView.ListView.ResumeLayout();
                }
            }, x => x.AdvancedTestCertificates, this);

            // Refresh items marked as invalid after corresponding setting change
            _settings.Subscribe((x, y) =>
            {
                if (!_firstRefresh)
                    _listView.ListView.RefreshObjects(AllUninstallers.Where(u => !u.IsValid).ToList());
            }, x => x.AdvancedTestInvalid, this);

            // Refresh items marked as orphans after corresponding setting change
            _settings.Subscribe((x, y) =>
            {
                if (!_firstRefresh)
                    _listView.ListView.UpdateColumnFiltering();
            }, x => x.AdvancedDisplayOrphans, this);

            AfterFiltering += (x, y) => StartProcessingThread(FilteredUninstallers);

            UninstallerFileLock = new object();

            // Prevent the thread from accessing disposed resources before getting aborted.
            _reference.FormClosed += (x, y) => StopProcessingThread(false);
        }

        public IEnumerable<ApplicationUninstallerEntry> AllUninstallers { get; private set; }

        public IEnumerable<ApplicationUninstallerEntry> FilteredUninstallers
            => _listView.ListView.FilteredObjects.Cast<ApplicationUninstallerEntry>();

        public bool FirstRefreshCompleted => !_firstRefresh;

        public bool ListRefreshIsRunning
        {
            get { return _listRefreshIsRunning; }
            private set
            {
                if (value != _listRefreshIsRunning)
                {
                    _listRefreshIsRunning = value;
                    ListRefreshIsRunningChanged?.Invoke(this, new ListRefreshEventArgs(value, !FirstRefreshCompleted));
                }
            }
        }

        /// <summary>
        ///     Faster than SelectedUninstallers.Count()
        /// </summary>
        public int SelectedUninstallerCount => _listView.ListView.CheckBoxes
            ? _listView.CheckedObjects.Count
            : _listView.SelectedObjects.Count;

        public IEnumerable<ApplicationUninstallerEntry> SelectedUninstallers => _listView.ListView.CheckBoxes
            ? _listView.CheckedObjects
            : _listView.SelectedObjects;

        /// <summary>
        ///     External lock to the uninstall system.
        /// </summary>
        public object UninstallerFileLock { get; set; }

        public void Dispose()
        {
            _iconGetter?.Dispose();
            StopProcessingThread(false);
        }

        public event EventHandler AfterFiltering;
        public event EventHandler<ListRefreshEventArgs> ListRefreshIsRunningChanged;
        public event EventHandler<CountingUpdateEventArgs> UninstallerPostprocessingProgressUpdate;

        public void DeselectAllItems(object sender, EventArgs e)
        {
            _listView.ListView.DeselectAll();
            _listView.ListView.Focus();
        }

        public bool DisplayWindowsFeatures()
        {
            if (ListRefreshIsRunning)
                return false;

            ListRefreshIsRunning = true;
            _reference.LockApplication(true);

            var error = LoadingDialog.ShowDialog(Localisable.LoadingDialogTitleLoadingWindowsFeatures, x =>
            {
                var items = ApplicationUninstallerManager.GetWindowsFeaturesList(y =>
                {
                    x.SetMaximum(y.TotalCount);
                    x.SetProgress(y.CurrentCount);
                });

                AllUninstallers =
                    AllUninstallers.Where(e => e.UninstallerKind != UninstallerType.Dism).Concat(items).ToList();
                _listView.ListView.SafeInvoke(() => _listView.ListView.SetObjects(AllUninstallers, false));
            });

            if (error != null)
                PremadeDialogs.GenericError(error);

            _reference.LockApplication(false);
            ListRefreshIsRunning = false;

            return error == null;
        }

        /// <summary>
        ///     Get total size of all visible uninstallers.
        /// </summary>
        public FileSize GetFilteredSize()
        {
            return FilteredUninstallers.Select(x => x.EstimatedSize).DefaultIfEmpty(FileSize.Empty)
                .Aggregate((size1, size2) => size1 + size2);
        }

        /// <summary>
        ///     Get total size of selected uninstallers
        /// </summary>
        /// <returns></returns>
        public FileSize GetSelectedSize()
        {
            return SelectedUninstallers.Select(x => x.EstimatedSize).DefaultIfEmpty(FileSize.Empty)
                .Aggregate((size1, size2) => size1 + size2);
        }

        public void InitiateListRefresh()
        {
            if (ListRefreshIsRunning || _listView.ListView.IsDisposed)
                return;

            ListRefreshIsRunning = true;
            try
            {
                _reference.LockApplication(true);
                _reference.Refresh();

                StopProcessingThread(false);

                var error = LoadingDialog.ShowDialog(Localisable.LoadingDialogTitlePopulatingList, ListRefreshThread);
                if (error != null)
                    PremadeDialogs.GenericError(error);

                _listView.ListView.SuspendLayout();
                _listView.ListView.BeginUpdate();

                var oldList = _listView.ListView.SmallImageList;
                _listView.ListView.SmallImageList = _iconGetter.IconList;
                oldList?.Dispose();
                
                _listView.ListView.SetObjects(AllUninstallers);
            }
            finally
            {
                _reference.LockApplication(false);

                // Run events
                ListRefreshIsRunning = false;

                // Don't redraw the list view before all events have ran
                _listView.ListView.EndUpdate();
                _listView.ListView.ResumeLayout();

                _listView.ListView.Focus();
            }

            // Update first list refresh AFTER setting ListRefreshIsRunning to false
            if (_firstRefresh)
            {
                _firstRefresh = false;

                Application.DoEvents();

                var args = Environment.GetCommandLineArgs();
                if (args.Length > 1)
                {
                    OpenUninstallLists(false, args.SubArray(1, args.Length - 1));
                }
            }
        }

        public void InvertSelectedItems(object sender, EventArgs e)
        {
            var selectedObjects = _listView.SelectedObjects;
            _listView.ListView.DeselectAll();
            _listView.ListView.SelectObjects(FilteredUninstallers.Where(x => !selectedObjects.Contains(x)).ToList());
            _listView.ListView.Focus();
        }

        /// <summary>
        ///     Select items from the uninstall list without asking user for any input.
        /// </summary>
        /// <param name="fileNames"></param>
        /// <param name="addSelection">Add item from the list to the current selection or discard it before adding</param>
        /// <returns></returns>
        public bool OpenUninstallLists(bool addSelection, params string[] fileNames)
        {
            try
            {
                var uninstallList = fileNames.Length > 0
                    ? UninstallList.FromFiles(fileNames)
                    : UninstallListOpenDialog.Show(FilteredUninstallers.Select(x => x.DisplayName));

                if (uninstallList == null)
                    return false;

                if (addSelection)
                {
                    uninstallList.AddItems(SelectedUninstallers.Select(x => x.DisplayName));
                }

                DeselectAllItems(null, EventArgs.Empty);

                var selectionList = FilteredUninstallers.Where(x => uninstallList.Items.Any(y =>
                {
                    try
                    {
                        return y.TestString(x.DisplayName);
                    }
                    catch
                    {
                        return false;
                    }
                })).ToList();

                _listView.ListView.SelectObjects(selectionList);
            }
            catch (Exception ex)
            {
                MessageBoxes.OpenUninstallListError(ex.Message);
                return false;
            }

            return true;
        }

        /// <summary>
        ///     Select items from the uninstall list after asking the user for his decision.
        /// </summary>
        /// <returns></returns>
        public bool OpenUninstallLists()
        {
            var addSelection = false;

            if (SelectedUninstallerCount > 0)
            {
                switch (MessageBoxes.OpenUninstallListQuestion())
                {
                    case MessageBoxes.PressedButton.Yes:
                        addSelection = true;
                        break;
                    case MessageBoxes.PressedButton.No:
                        break;
                    default:
                        return false;
                }
            }

            return OpenUninstallLists(addSelection);
        }

        public void RefreshList()
        {
            _listView.ListView.UpdateColumnFiltering();
            //_listView.ListView.BuildList(true); No need, UpdateColumnFiltering already does this
        }

        /// <summary>
        ///     Returns false if saving failed, otherwise returns true.
        /// </summary>
        /// <returns></returns>
        public bool SaveUninstallList()
        {
            try
            {
                UninstallListSaveDialog.Show(SelectedUninstallers.Select(x => x.DisplayName),
                    FilteredUninstallers.Select(x => x.DisplayName));
            }
            catch (Exception ex)
            {
                MessageBoxes.SaveUninstallListError(ex.Message);
                return false;
            }
            return true;
        }

        public void SelectAllItems(object sender, EventArgs e)
        {
            _listView.ListView.SelectAll();
            _listView.ListView.Focus();
        }

        /// <summary>
        ///     Select first item starting with the keycode.
        ///     If keycode leads to a valid selection true is returned. Otherwise, if there is nothing relevant to select false is
        ///     returned.
        /// </summary>
        public bool SelectItemFromKeystroke(Keys keyCode)
        {
            var keyName = keyCode.ToLetterOrNumberString();

            if (keyName != null)
            {
                var selectedObj = FilteredUninstallers.FirstOrDefault(x => x.DisplayName.StartsWith(keyName));

                _listView.ListView.DeselectAll();

                if (selectedObj != null)
                {
                    _listView.ListView.SelectObject(selectedObj, true);
                    _listView.ListView.EnsureModelVisible(selectedObj);

                    return true;
                }
            }
            return false;
        }

        public void StopProcessingThread(bool block)
        {
            if (_finalizerThread == null || !_finalizerThread.IsAlive) return;

            _abortPostprocessingThread = true;

            if (!block) return;

            do
            {
                Thread.Sleep(100);
                // Process events in case we are blocking ui thread and the worker thread is trying to invoke.
                // TODO Reimplement the whole thing to avoid having to do this
                Application.DoEvents();
            } while (_finalizerThread.IsAlive);
        }

        public void UpdateColumnFiltering(string searchString, FilterComparisonMethod method)
        {
            _searchItem.FilterText = searchString;
            _searchItem.ComparisonMethod = method;

            _listView.ListView.EmptyListMsg = !string.IsNullOrEmpty(searchString)
                ? Localisable.SearchNothingFoundMessage
                : null;

            _listView.ListView.UpdateColumnFiltering();
        }

        private void ListRefreshThread(LoadingDialog.LoadingDialogInterface dialogInterface)
        {
            dialogInterface.SetMaximum(1);
            dialogInterface.SetProgress(0);

            var detectedUninstallers =
                new List<ApplicationUninstallerEntry>(ApplicationUninstallerManager.GetUninstallerList(x =>
                {
                    if (x.CurrentCount == 1)
                        dialogInterface.SetMaximum(x.TotalCount*2);
                    dialogInterface.SetProgress(x.CurrentCount);
                }));

            detectedUninstallers.AddRange(ApplicationUninstallerManager.GetApplicationsFromDrive(detectedUninstallers,
                x =>
                {
                    dialogInterface.SetProgress(x.TotalCount + x.CurrentCount);
                    if (x.CurrentCount == 1)
                        dialogInterface.SetMaximum(x.TotalCount*2);
                }));

            dialogInterface.SetProgress(-1);

            AllUninstallers = detectedUninstallers;

            _iconGetter.UpdateIconList(detectedUninstallers);
            ReassignStartupEntries(false);
        }

        private bool ListViewFilter(object obj)
        {
            var entry = obj as ApplicationUninstallerEntry;

            if (entry == null || (Program.IsInstalled && entry.RegistryKeyName.IsNotEmpty()
                                  &&
                                  entry.RegistryKeyName.Equals(Program.InstalledRegistryKeyName,
                                      StringComparison.CurrentCulture)))
                return false;

            if (entry.UninstallerKind != UninstallerType.Dism
                && ((_settings.Settings.FilterHideMicrosoft && entry.Publisher.IsNotEmpty() &&
                 entry.Publisher.Contains("Microsoft"))
                || (!_settings.Settings.AdvancedDisplayOrphans && !entry.IsRegistered)
                || (!_settings.Settings.FilterShowProtected && entry.IsProtected)
                || (!_settings.Settings.FilterShowSystemComponents && entry.SystemComponent)
                || (!_settings.Settings.FilterShowUpdates && entry.IsUpdate)))
                return false;

            if (string.IsNullOrEmpty(_searchItem.FilterText)) return true;

            //, entry.InstallLocation, entry.AboutUrl, entry.InstallSource, entry.ModifyPath }
            var stringsToCompare = new[] {entry.DisplayName, entry.Publisher, entry.UninstallString, entry.Comment};
            return stringsToCompare.Any(str => str.IsNotEmpty() && _searchItem.TestString(str));
        }

        private void SetupListView()
        {
            _reference.uninstallerObjectListView.VirtualMode = false;

            _reference.olvColumnDisplayName.AspectName = ApplicationUninstallerEntry.RegistryNameDisplayName;
            _reference.olvColumnDisplayName.GroupKeyGetter = ListViewDelegates.GetFirstCharGroupKeyGetter;

            _reference.olvColumnDisplayName.ImageGetter = _iconGetter.ColumnImageGetter;

            _reference.olvColumnStartup.AspectGetter = x =>
            {
                var obj = x as ApplicationUninstallerEntry;
                return (obj?.StartupEntries != null && obj.StartupEntries.Any(e=>!e.Disabled)).ToYesNo();
            };

            _reference.olvColumnPublisher.AspectName = ApplicationUninstallerEntry.RegistryNamePublisher;
            _reference.olvColumnPublisher.GroupKeyGetter = ListViewDelegates.ColumnPublisherGroupKeyGetter;

            _reference.olvColumnDisplayVersion.AspectName = ApplicationUninstallerEntry.RegistryNameDisplayVersion;
            _reference.olvColumnDisplayVersion.GroupKeyGetter = ListViewDelegates.DisplayVersionGroupKeyGetter;

            _reference.olvColumnUninstallString.AspectName = ApplicationUninstallerEntry.RegistryNameUninstallString;
            _reference.olvColumnUninstallString.GroupKeyGetter = ListViewDelegates.ColumnUninstallStringGroupKeyGetter;

            _reference.olvColumnInstallDate.AspectGetter = x =>
            {
                var obj = x as ApplicationUninstallerEntry;
                if (obj != null)
                    return obj.InstallDate.Date;
                return DateTime.MinValue;
            };
            //_reference.olvColumnInstallDate.AspectName = ApplicationUninstallerEntry.RegistryNameInstallDate;
            _reference.olvColumnInstallDate.AspectToStringConverter = x =>
            {
                var entry = (DateTime) x;
                return entry.IsDefault() ? Localisable.Empty : entry.ToShortDateString();
            };

            _reference.olvColumnGuid.AspectGetter = ListViewDelegates.ColumnGuidAspectGetter;
            _reference.olvColumnGuid.GroupKeyGetter = ListViewDelegates.ColumnGuidGroupKeyGetter;

            _reference.olvColumnSystemComponent.AspectName = ApplicationUninstallerEntry.RegistryNameSystemComponent;
            _reference.olvColumnSystemComponent.AspectToStringConverter = ListViewDelegates.BoolToYesNoAspectConverter;

            _reference.olvColumnIs64.AspectToStringConverter = ListViewDelegates.BoolToYesNoAspectConverter;

            _reference.olvColumnProtected.AspectToStringConverter = ListViewDelegates.BoolToYesNoAspectConverter;

            _reference.olvColumnInstallLocation.AspectName = ApplicationUninstallerEntry.RegistryNameInstallLocation;
            _reference.olvColumnInstallLocation.GroupKeyGetter = ListViewDelegates.ColumnInstallLocationGroupKeyGetter;

            _reference.olvColumnInstallSource.AspectName = ApplicationUninstallerEntry.RegistryNameInstallSource;
            _reference.olvColumnInstallSource.GroupKeyGetter = ListViewDelegates.ColumnInstallSourceGroupKeyGetter;

            _reference.olvColumnRegistryKeyName.AspectName = "RegistryKeyName";

            _reference.olvColumnUninstallerKind.AspectGetter =
                y => (y as ApplicationUninstallerEntry)?.UninstallerKind.GetLocalisedName();

            _reference.olvColumnAbout.AspectName = "AboutUrl";
            _reference.olvColumnAbout.GroupKeyGetter = x =>
            {
                var entry = (x as ApplicationUninstallerEntry);
                if (string.IsNullOrEmpty(entry?.AboutUrl)) return Localisable.Empty;
                return entry.GetUri()?.Host ?? Localisable.Unknown;
            };

            _reference.olvColumnQuietUninstallString.AspectName =
                ApplicationUninstallerEntry.RegistryNameQuietUninstallString;
            _reference.olvColumnQuietUninstallString.GroupKeyGetter =
                ListViewDelegates.ColumnQuietUninstallStringGroupKeyGetter;

            _reference.olvColumnSize.TextAlign = HorizontalAlignment.Right;
            _reference.olvColumnSize.AspectGetter = ListViewDelegates.ColumnSizeAspectGetter;
            _reference.olvColumnSize.AspectToStringConverter = ListViewDelegates.AspectToStringConverter;
            _reference.olvColumnSize.GroupKeyGetter = ListViewDelegates.ColumnSizeGroupKeyGetter;
            _reference.olvColumnSize.GroupKeyToTitleConverter = x => x.ToString();

            _reference.uninstallerObjectListView.PrimarySortColumn = _reference.olvColumnDisplayName;
            _reference.uninstallerObjectListView.SecondarySortColumn = _reference.olvColumnPublisher;
            _reference.uninstallerObjectListView.Sorting = SortOrder.Ascending;

            _reference.uninstallerObjectListView.AdditionalFilter = new ModelFilter(ListViewFilter);
            _reference.uninstallerObjectListView.UseFiltering = true;

            _reference.uninstallerObjectListView.FormatRow += UninstallerObjectListView_FormatRow;

            UninstallerPostprocessingProgressUpdate += (x, y) =>
            {
                lock (_objectsToUpdate)
                {
                    _objectsToUpdate.Add(y.Tag);

                    if (y.Value == y.Maximum || (y.Value) % 25 == 0)
                    {
                        _listView.ListView.RefreshObjects(_objectsToUpdate);
                        _objectsToUpdate.Clear();
                    }
                }
            };

            _listView.ListView.AfterSorting += (x, y) => { AfterFiltering?.Invoke(x, y); };
        }

        private void StartProcessingThread(IEnumerable<ApplicationUninstallerEntry> itemsToProcess)
        {
            StopProcessingThread(true);

            _finalizerThread = new Thread(UninstallerPostprocessingThread)
            {Name = "UninstallerPostprocessingThread", IsBackground = true, Priority = ThreadPriority.Lowest};

            _abortPostprocessingThread = false;
            _finalizerThread.Start(itemsToProcess);
        }

        private void UninstallerObjectListView_FormatRow(object sender, FormatRowEventArgs e)
        {
            var entry = e.Model as ApplicationUninstallerEntry;
            if (entry == null) return;

            if (entry.UninstallerKind == UninstallerType.Dism)
            {
                e.Item.BackColor = Constants.WindowsFeatureColor;
            }
            else if (!entry.IsRegistered)
            {
                e.Item.BackColor = Constants.UnregisteredColor;
            }
            else if (!entry.IsValid && _settings.Settings.AdvancedTestInvalid)
            {
                e.Item.BackColor = Constants.InvalidColor;
            }
            else if (_settings.Settings.AdvancedTestCertificates)
            {
                var result = entry.IsCertificateValid(true);
                if (result.HasValue)
                    e.Item.BackColor = result.Value ? Constants.VerifiedColor : Constants.UnverifiedColor;
            }
        }

        private void UninstallerPostprocessingThread(object targets)
        {
            var items = targets as IEnumerable<ApplicationUninstallerEntry>;
            if (items == null)
                return;

            var targetList = items as IList<ApplicationUninstallerEntry> ?? items.ToList();
            var currentCount = 1;
            foreach (var uninstaller in targetList)
            {
                if (_abortPostprocessingThread)
                {
                    UninstallerPostprocessingProgressUpdate?.Invoke(this, new CountingUpdateEventArgs(0, 0, 0));
                    return;
                }

                if (_settings.Settings.AdvancedTestCertificates)
                {
                    lock (UninstallerFileLock)
                    {
                        uninstaller.GetCertificate();
                    }
                }

                UninstallerPostprocessingProgressUpdate?.Invoke(this,
                    new CountingUpdateEventArgs(0, targetList.Count, currentCount) { Tag = uninstaller });
                currentCount++;
            }
        }

        public sealed class ListRefreshEventArgs : EventArgs
        {
            public ListRefreshEventArgs(bool value, bool firstRefresh)
            {
                NewValue = value;
                FirstRefresh = firstRefresh;
            }

            public bool NewValue { get; private set; }
            public bool FirstRefresh { get; private set; }
        }

        internal void ReassignStartupEntries(bool refreshListView)
        {
            ReassignStartupEntries(refreshListView, StartupManager.GetAllStartupItems());
        }

        internal void ReassignStartupEntries(bool refreshListView, IEnumerable<StartupEntryBase> items)
        {
            // Using DoForEach to avoid multiple enumerations
            StartupManager.AssignStartupEntries(AllUninstallers
                .DoForEach(x => { if (x != null) x.StartupEntries = null; }), items);

            if (refreshListView)
                RefreshList();
        }
    }
}