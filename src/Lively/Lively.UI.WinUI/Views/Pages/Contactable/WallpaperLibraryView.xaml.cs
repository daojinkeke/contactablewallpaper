using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lively.Grpc.Client;
using Lively.Models;
using Lively.UI.Shared.Navigation;
using Lively.UI.WinUI.Services.Contactable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace Lively.UI.WinUI.Views.Pages.Contactable
{
    public sealed partial class WallpaperLibraryView : Page
    {
        private readonly ContactableWallpaperService wallpaperService = App.Services.GetRequiredService<ContactableWallpaperService>();
        private readonly IDesktopCoreClient desktopCore = App.Services.GetRequiredService<IDesktopCoreClient>();
        private readonly IDisplayManagerClient displayManager = App.Services.GetRequiredService<IDisplayManagerClient>();
        private readonly IContactableNavigator navigator = App.Services.GetRequiredService<IContactableNavigator>();

        private readonly ObservableCollection<LibraryItemVm> items = new();
        private readonly List<LibraryItemVm> allItems = new();
        private readonly ObservableCollection<MonitorOption> monitors = new();
        private readonly ObservableCollection<SourceFilterOption> sourceFilters = new();
        private DispatcherQueueTimer refreshTimer;
        private string selectedSourceKey = "all";
        private bool isUpdatingFilter;
        private bool libraryRootMissing;
        private bool isNavigated;
        private bool isRefreshing;
        private bool isBusy;

        public WallpaperLibraryView()
        {
            this.InitializeComponent();
            LibraryGrid.ItemsSource = items;
            MonitorCombo.ItemsSource = monitors;
            SourceFilterCombo.ItemsSource = sourceFilters;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            isNavigated = true;

            refreshTimer ??= DispatcherQueue.CreateTimer();
            refreshTimer.Interval = TimeSpan.FromMilliseconds(400);
            refreshTimer.IsRepeating = false;
            refreshTimer.Tick += RefreshTimer_Tick;

            desktopCore.WallpaperChanged += DesktopCore_WallpaperChanged;
            desktopCore.WallpaperError += DesktopCore_WallpaperError;
            displayManager.DisplayChanged += DisplayManager_DisplayChanged;

            await RefreshAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            isNavigated = false;
            desktopCore.WallpaperChanged -= DesktopCore_WallpaperChanged;
            desktopCore.WallpaperError -= DesktopCore_WallpaperError;
            displayManager.DisplayChanged -= DisplayManager_DisplayChanged;
            if (refreshTimer != null)
            {
                refreshTimer.Stop();
                refreshTimer.Tick -= RefreshTimer_Tick;
            }
            base.OnNavigatedFrom(e);
        }

        private void DesktopCore_WallpaperChanged(object sender, EventArgs e) => QueueRefresh();
        private void DisplayManager_DisplayChanged(object sender, EventArgs e) => QueueRefresh();

        private void DesktopCore_WallpaperError(object sender, Exception e)
        {
            DispatcherQueue.TryEnqueue(() => ShowStatus(InfoBarSeverity.Error, $"壁纸引擎错误：{e?.Message}"));
        }

        private void QueueRefresh()
        {
            DispatcherQueue.TryEnqueue(() => refreshTimer?.Start());
        }

        private async void RefreshTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            await RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            if (!isNavigated || isRefreshing || isBusy)
                return;

            isRefreshing = true;
            LoadingRing.Visibility = Visibility.Visible;
            LoadingRing.IsActive = true;
            LoadingBar.Visibility = Visibility.Visible;
            try
            {
                UpdateMonitors();

                var scanned = await Task.Run(() => wallpaperService.ScanLibrary());
                if (!isNavigated)
                    return;

                allItems.Clear();
                foreach (var item in scanned)
                {
                    item.Thumbnail = LoadBitmap(item.ThumbnailPath);
                    allItems.Add(item);
                }

                libraryRootMissing = !Directory.Exists(wallpaperService.InstallRoot)
                    && !Directory.Exists(wallpaperService.TempRoot)
                    && !Directory.Exists(wallpaperService.WallpaperRoot);

                UpdateStorageInfo();
                RebuildSourceFilters();
                ApplyFilter();
            }
            catch (Exception ex)
            {
                ShowStatus(InfoBarSeverity.Error, $"扫描壁纸库失败：{ex.Message}");
            }
            finally
            {
                isRefreshing = false;
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
                LoadingBar.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateMonitors()
        {
            var currentId = (MonitorCombo.SelectedItem as MonitorOption)?.Monitor?.DeviceId;
            var list = displayManager.DisplayMonitors?.ToList() ?? new System.Collections.Generic.List<DisplayMonitor>();
            if (list.Count == 0 && displayManager.PrimaryMonitor != null)
                list.Add(displayManager.PrimaryMonitor);

            monitors.Clear();
            foreach (var monitor in list)
                monitors.Add(new MonitorOption
                {
                    Monitor = monitor,
                    Label = $"显示器 {monitor.Index + 1} · {monitor.DisplayName}{(monitor.IsPrimary ? "（主屏）" : string.Empty)}",
                });

            var selected = monitors.FirstOrDefault(x => string.Equals(x.Monitor.DeviceId, currentId, StringComparison.OrdinalIgnoreCase))
                ?? monitors.FirstOrDefault(x => x.Monitor.IsPrimary)
                ?? monitors.FirstOrDefault();
            if (selected != null)
                MonitorCombo.SelectedItem = selected;
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshAsync();
        }

        private void CreateButton_Click(object sender, RoutedEventArgs e)
        {
            navigator.NavigateTo(ContactablePageType.createWallpaper);
        }

        private void UpdateStorageInfo()
        {
            LibraryPathText.Text = wallpaperService.WallpaperRoot;
            LibrarySourceSummary.Text =
                $"AI 生成 {CountOf(WallpaperSourceKind.Ai)} 张 · " +
                $"我导入的 {CountOf(WallpaperSourceKind.Imported)} 张 · " +
                $"内置 {CountOf(WallpaperSourceKind.Builtin)} 张";
        }

        private int CountOf(WallpaperSourceKind kind) => allItems.Count(x => x.SourceKind == kind);

        private void RebuildSourceFilters()
        {
            isUpdatingFilter = true;
            try
            {
                sourceFilters.Clear();
                sourceFilters.Add(new SourceFilterOption { Key = "all", Label = $"全部来源（{allItems.Count}）" });
                sourceFilters.Add(new SourceFilterOption { Key = "ai", Label = $"AI 生成（{CountOf(WallpaperSourceKind.Ai)}）" });
                sourceFilters.Add(new SourceFilterOption { Key = "imported", Label = $"我导入的（{CountOf(WallpaperSourceKind.Imported)}）" });
                sourceFilters.Add(new SourceFilterOption { Key = "builtin", Label = $"内置壁纸（{CountOf(WallpaperSourceKind.Builtin)}）" });
                SourceFilterCombo.SelectedItem =
                    sourceFilters.FirstOrDefault(x => x.Key == selectedSourceKey) ?? sourceFilters[0];
            }
            finally
            {
                isUpdatingFilter = false;
            }
        }

        private void ApplyFilter()
        {
            IEnumerable<LibraryItemVm> filtered = selectedSourceKey switch
            {
                "ai" => allItems.Where(x => x.SourceKind == WallpaperSourceKind.Ai),
                "imported" => allItems.Where(x => x.SourceKind == WallpaperSourceKind.Imported),
                "builtin" => allItems.Where(x => x.SourceKind == WallpaperSourceKind.Builtin),
                _ => allItems,
            };

            items.Clear();
            foreach (var item in filtered)
                items.Add(item);

            CountText.Text = items.Count == allItems.Count
                ? $"{items.Count} 张"
                : $"{items.Count} / {allItems.Count} 张";

            EmptyPanel.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LibraryGrid.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (items.Count > 0)
                return;

            if (allItems.Count == 0)
            {
                EmptyTitle.Text = libraryRootMissing ? "壁纸库目录不存在" : "壁纸库还是空的";
                EmptyDetail.Text = libraryRootMissing
                    ? $"未找到 {wallpaperService.WallpaperRoot}。请先启动 Core，或检查设置里的壁纸目录。"
                    : $"壁纸会保存在 {wallpaperService.WallpaperRoot}。去“新建壁纸”生成第一张，或点击刷新重新扫描。";
                EmptyCreateButton.Visibility = libraryRootMissing ? Visibility.Collapsed : Visibility.Visible;
            }
            else
            {
                EmptyTitle.Text = "这个来源下还没有壁纸";
                EmptyDetail.Text = "切换到“全部来源”查看整个壁纸库。";
                EmptyCreateButton.Visibility = Visibility.Collapsed;
            }
        }

        private void SourceFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isUpdatingFilter)
                return;
            if (SourceFilterCombo.SelectedItem is SourceFilterOption option)
            {
                selectedSourceKey = option.Key;
                ApplyFilter();
            }
        }

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFolder(wallpaperService.WallpaperRoot, "壁纸库");
        }

        private void OpenDraftsButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFolder(wallpaperService.ProjectsRoot, "生成草稿");
        }

        private void LocateItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not LibraryItemVm item)
                return;

            if (string.IsNullOrWhiteSpace(item.FolderPath) || !Directory.Exists(item.FolderPath))
            {
                ShowStatus(InfoBarSeverity.Warning, $"壁纸目录不存在：{item.FolderPath}");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{item.FolderPath}\"",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                ShowStatus(InfoBarSeverity.Error, $"打开目录失败：{ex.Message}");
            }
        }

        private void OpenFolder(string path, string label)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                ShowStatus(InfoBarSeverity.Warning, $"{label}目录不存在：{path}");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ShowStatus(InfoBarSeverity.Error, $"打开目录失败：{ex.Message}");
            }
        }

        private async void ApplyItem_Click(object sender, RoutedEventArgs e)
        {
            if (isBusy || sender is not Button button || button.Tag is not LibraryItemVm item)
                return;

            var monitor = (MonitorCombo.SelectedItem as MonitorOption)?.Monitor ?? displayManager.PrimaryMonitor;
            if (monitor == null)
            {
                ShowStatus(InfoBarSeverity.Error, "没有可用的显示器。");
                return;
            }

            SetBusy(true);
            try
            {
                var (ok, message) = await wallpaperService.ApplyAsync(item.FolderPath, monitor);
                ShowStatus(ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning, message);
            }
            catch (Exception ex)
            {
                ShowStatus(InfoBarSeverity.Error, $"应用壁纸失败：{ex.Message}");
            }
            finally
            {
                SetBusy(false);
                await RefreshAsync();
            }
        }

        private void PreviewItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is LibraryItemVm item)
                navigator.NavigateTo(ContactablePageType.wallpaperPreview, item.FolderPath);
        }

        private async void DeleteItem_Click(object sender, RoutedEventArgs e)
        {
            if (isBusy || sender is not Button button || button.Tag is not LibraryItemVm item)
                return;

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "删除壁纸",
                Content = $"确定删除“{item.Title}”吗？\n\n目录：{item.FolderPath}\n如果壁纸正在运行，会先停止再删除。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            SetBusy(true);
            try
            {
                var (ok, message) = await wallpaperService.DeleteLibraryAsync(item);
                ShowStatus(ok ? InfoBarSeverity.Success : InfoBarSeverity.Error, message);
            }
            catch (Exception ex)
            {
                ShowStatus(InfoBarSeverity.Error, $"删除失败：{ex.Message}");
            }
            finally
            {
                SetBusy(false);
                await RefreshAsync();
            }
        }

        private void SetBusy(bool busy)
        {
            isBusy = busy;
            LibraryGrid.IsEnabled = !busy;
            MonitorCombo.IsEnabled = !busy;
            ApplyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowStatus(InfoBarSeverity severity, string message)
        {
            StatusBar.Severity = severity;
            StatusBar.Message = message;
            StatusBar.IsOpen = true;
        }

        private static BitmapImage LoadBitmap(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;
            try
            {
                var bitmap = new BitmapImage { DecodePixelWidth = 480 };
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                return bitmap;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
