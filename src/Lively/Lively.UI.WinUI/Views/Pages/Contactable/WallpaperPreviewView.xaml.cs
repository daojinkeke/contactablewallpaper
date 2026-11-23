using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lively.Common;
using Lively.Common.Helpers;
using Lively.Grpc.Client;
using Lively.Models;
using Lively.Models.Enums;
using Lively.UI.Shared.Navigation;
using Lively.UI.WinUI.Services.Contactable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;

namespace Lively.UI.WinUI.Views.Pages.Contactable
{
    public sealed partial class WallpaperPreviewView : Page
    {
        private sealed class PreviewSnapshot
        {
            public List<DisplayMonitor> Monitors { get; set; } = new();
            public List<LibraryItemVm> Library { get; set; }
            public List<WallpaperData> Wallpapers { get; set; } = new();
        }

        private readonly ContactableWallpaperService wallpaperService = App.Services.GetRequiredService<ContactableWallpaperService>();
        private readonly IDesktopCoreClient desktopCore = App.Services.GetRequiredService<IDesktopCoreClient>();
        private readonly IDisplayManagerClient displayManager = App.Services.GetRequiredService<IDisplayManagerClient>();
        private readonly IContactableNavigator navigator = App.Services.GetRequiredService<IContactableNavigator>();

        private readonly ObservableCollection<MonitorOption> monitors = new();
        private readonly ObservableCollection<LibraryItemVm> libraryItems = new();
        private DispatcherQueueTimer refreshTimer;
        private string pendingFolder;
        private bool isNavigated;
        private bool isRefreshing;
        private bool isApplying;
        private bool webViewReady;

        public WallpaperPreviewView()
        {
            this.InitializeComponent();
            MonitorCombo.ItemsSource = monitors;
            WallpaperPicker.ItemsSource = libraryItems;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            isNavigated = true;
            pendingFolder = e.Parameter as string;

            try
            {
                refreshTimer ??= DispatcherQueue.CreateTimer();
                refreshTimer.Interval = TimeSpan.FromMilliseconds(300);
                refreshTimer.IsRepeating = false;
                refreshTimer.Tick += RefreshTimer_Tick;

                desktopCore.WallpaperChanged += DesktopCore_WallpaperChanged;
                desktopCore.WallpaperError += DesktopCore_WallpaperError;
                displayManager.DisplayChanged += DisplayManager_DisplayChanged;

                await LoadAsync();
            }
            catch (Exception ex)
            {
                // 页面结构必须始终可见，数据层异常只影响内容区。
                NoWallpaperPanel.Visibility = Visibility.Visible;
                ShowStatus(InfoBarSeverity.Error, $"预览页初始化失败：{ex.Message}");
            }
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

            CloseWebPreview();
            try
            {
                PreviewWebView.Close();
            }
            catch (Exception)
            {
                // WebView2 未初始化时忽略。
            }
            webViewReady = false;
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
            await RefreshAsync(loadLibrary: false);
        }

        private async Task LoadAsync()
        {
            await RefreshAsync(loadLibrary: true);
        }

        private async Task RefreshAsync(bool loadLibrary)
        {
            if (!isNavigated || isRefreshing || isApplying)
                return;

            isRefreshing = true;
            LoadingRing.Visibility = Visibility.Visible;
            LoadingRing.IsActive = true;
            try
            {
                // 显示器/壁纸/库数据的读取放到后台线程，避免 gRPC 同步访问阻塞 UI 线程导致整页空白。
                var snapshot = await Task.Run(() =>
                {
                    var monitorList = displayManager.DisplayMonitors?.ToList() ?? new List<DisplayMonitor>();
                    if (monitorList.Count == 0 && displayManager.PrimaryMonitor is DisplayMonitor primary)
                        monitorList.Add(primary);

                    List<LibraryItemVm> library = null;
                    if (loadLibrary)
                        library = wallpaperService.ScanLibrary().ToList();

                    var wallpapers = (desktopCore.Wallpapers as IEnumerable<WallpaperData>
                        ?? Enumerable.Empty<WallpaperData>()).ToList();

                    return new PreviewSnapshot
                    {
                        Monitors = monitorList,
                        Library = library,
                        Wallpapers = wallpapers,
                    };
                });

                if (!isNavigated)
                    return;

                UpdateMonitors(snapshot.Monitors);

                if (snapshot.Library != null)
                {
                    libraryItems.Clear();
                    foreach (var item in snapshot.Library)
                        libraryItems.Add(item);

                    if (!string.IsNullOrWhiteSpace(pendingFolder))
                    {
                        var selected = libraryItems.FirstOrDefault(x =>
                            string.Equals(x.FolderPath, pendingFolder, StringComparison.OrdinalIgnoreCase));
                        if (selected != null)
                            WallpaperPicker.SelectedItem = selected;
                        pendingFolder = null;
                    }
                    else if (WallpaperPicker.SelectedItem == null)
                    {
                        WallpaperPicker.SelectedIndex = libraryItems.Count > 0 ? 0 : -1;
                    }
                }

                await ApplyWallpaperStateAsync(snapshot.Wallpapers);
            }
            catch (Exception ex)
            {
                ShowStatus(InfoBarSeverity.Error, $"刷新预览失败：{ex.Message}");
            }
            finally
            {
                isRefreshing = false;
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateMonitors(List<DisplayMonitor> source)
        {
            var currentId = (MonitorCombo.SelectedItem as MonitorOption)?.Monitor?.DeviceId;

            monitors.Clear();
            foreach (var monitor in source)
            {
                monitors.Add(new MonitorOption
                {
                    Monitor = monitor,
                    Label = $"显示器 {monitor.Index + 1} · {monitor.DisplayName}{(monitor.IsPrimary ? "（主屏）" : string.Empty)}",
                });
            }

            var selected = monitors.FirstOrDefault(x => string.Equals(x.Monitor.DeviceId, currentId, StringComparison.OrdinalIgnoreCase))
                ?? monitors.FirstOrDefault(x => x.Monitor.IsPrimary)
                ?? monitors.FirstOrDefault();
            if (selected != null)
                MonitorCombo.SelectedItem = selected;
        }

        private async Task ApplyWallpaperStateAsync(List<WallpaperData> wallpapers)
        {
            var monitor = (MonitorCombo.SelectedItem as MonitorOption)?.Monitor ?? displayManager.PrimaryMonitor;
            if (monitor == null)
            {
                CurrentTitle.Text = "没有可用显示器";
                CurrentType.Text = string.Empty;
                CurrentPath.Text = string.Empty;
                CurrentImage.Source = null;
                NoWallpaperPanel.Visibility = Visibility.Visible;
                SetNoWallpaperMessage("没有检测到显示器", "请确认显示设备已连接，然后点“刷新”。");
                CloseWebPreview();
                return;
            }

            MonitorInfoText.Text = $"{monitor.DisplayName} · {monitor.Bounds.Width}×{monitor.Bounds.Height} · DeviceId {monitor.DeviceId}";

            var wallpaper = wallpapers.FirstOrDefault(x =>
                string.Equals(x.Display?.DeviceId, monitor.DeviceId, StringComparison.OrdinalIgnoreCase));

            if (wallpaper == null)
            {
                CurrentTitle.Text = "未应用壁纸";
                CurrentType.Text = string.Empty;
                CurrentPath.Text = string.Empty;
                CurrentImage.Source = null;
                NoWallpaperPanel.Visibility = Visibility.Visible;
                SetNoWallpaperMessage("这个显示器还没有壁纸", "在下方从壁纸库选一张并点击“更换”。");
                CloseWebPreview();
                return;
            }

            var libraryItem = libraryItems.FirstOrDefault(x =>
                string.Equals(x.FolderPath, wallpaper.LivelyInfoFolderPath, StringComparison.OrdinalIgnoreCase));

            CurrentTitle.Text = libraryItem?.Title ?? Path.GetFileName(wallpaper.LivelyInfoFolderPath);
            CurrentType.Text = libraryItem?.TypeText ?? ContactableWallpaperService.DescribeType(wallpaper.Category);
            CurrentPath.Text = wallpaper.LivelyInfoFolderPath;
            CurrentImage.Source = LoadBitmap(wallpaper.ThumbnailPath ?? wallpaper.PreviewPath);
            CurrentImage.Visibility = Visibility.Visible;
            NoWallpaperPanel.Visibility = Visibility.Collapsed;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;

            await TryShowWebPreviewAsync(wallpaper);
        }

        private void SetNoWallpaperMessage(string title, string description)
        {
            NoWallpaperTitle.Text = title;
            NoWallpaperDescription.Text = description;
        }

        private async Task TryShowWebPreviewAsync(WallpaperData wallpaper)
        {
            CloseWebPreview();

            if (wallpaper.Category is not (WallpaperType.web or WallpaperType.webaudio))
                return;

            var folder = wallpaper.LivelyInfoFolderPath;
            var html = string.IsNullOrWhiteSpace(folder) ? null : Path.Combine(folder, "index.html");
            if (html == null || !File.Exists(html))
                return;

            if (!await EnsureWebViewAsync())
            {
                PreviewPlaceholderTitle.Text = "网页预览不可用";
                PreviewPlaceholderText.Text = "缺少 WebView2 运行时，已显示壁纸缩略图。";
                PreviewPlaceholder.Visibility = Visibility.Visible;
                return;
            }

            if (!isNavigated)
                return;

            try
            {
                PreviewWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "contactable.preview", folder, CoreWebView2HostResourceAccessKind.Allow);
                PreviewWebView.Visibility = Visibility.Visible;
                CurrentImage.Visibility = Visibility.Collapsed;
                PreviewWebView.CoreWebView2.Navigate("https://contactable.preview/index.html");
            }
            catch (Exception ex)
            {
                PreviewPlaceholderTitle.Text = "网页预览失败";
                PreviewPlaceholderText.Text = $"{ex.Message}（已显示壁纸缩略图）";
                PreviewPlaceholder.Visibility = Visibility.Visible;
                CloseWebPreview();
            }
        }

        private void CloseWebPreview()
        {
            try
            {
                PreviewWebView.Visibility = Visibility.Collapsed;
            }
            catch (Exception)
            {
                // ignore
            }

            if (CurrentImage.Source != null)
                CurrentImage.Visibility = Visibility.Visible;
        }

        private async Task<bool> EnsureWebViewAsync()
        {
            if (webViewReady)
                return true;
            try
            {
                var userDataDir = Path.Combine(
                    PackageUtil.ValidateAndResolvePath(Constants.CommonPaths.TempWebView2Dir),
                    "ContactablePreviewPage");
                var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                    null, userDataDir, new CoreWebView2EnvironmentOptions());
                await PreviewWebView.EnsureCoreWebView2Async(environment);
                webViewReady = true;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void MonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!isNavigated || isRefreshing)
                return;

            _ = RefreshCurrentAsync();
        }

        private async Task RefreshCurrentAsync()
        {
            try
            {
                var wallpapers = await Task.Run(() =>
                    (desktopCore.Wallpapers as IEnumerable<WallpaperData> ?? Enumerable.Empty<WallpaperData>()).ToList());
                if (isNavigated && !isRefreshing)
                    await ApplyWallpaperStateAsync(wallpapers);
            }
            catch (Exception ex)
            {
                ShowStatus(InfoBarSeverity.Warning, $"读取当前壁纸失败：{ex.Message}");
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshAsync(loadLibrary: true);
        }

        private async void SwitchButton_Click(object sender, RoutedEventArgs e)
        {
            if (isApplying)
                return;

            if (WallpaperPicker.SelectedItem is not LibraryItemVm item)
            {
                ShowStatus(InfoBarSeverity.Warning, "请先从壁纸库选择一张壁纸。");
                return;
            }

            var monitor = (MonitorCombo.SelectedItem as MonitorOption)?.Monitor ?? displayManager.PrimaryMonitor;
            if (monitor == null)
            {
                ShowStatus(InfoBarSeverity.Error, "没有可用的显示器。");
                return;
            }

            isApplying = true;
            SwitchButton.IsEnabled = false;
            SwitchRing.Visibility = Visibility.Visible;
            SwitchRing.IsActive = true;
            SwitchProgress.Visibility = Visibility.Visible;
            try
            {
                var (ok, message) = await wallpaperService.ApplyAsync(item.FolderPath, monitor);
                ShowStatus(ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning, message);
                if (ok)
                    await RefreshAsync(loadLibrary: false);
            }
            catch (Exception ex)
            {
                ShowStatus(InfoBarSeverity.Error, $"更换壁纸失败：{ex.Message}");
            }
            finally
            {
                isApplying = false;
                SwitchButton.IsEnabled = true;
                SwitchRing.IsActive = false;
                SwitchRing.Visibility = Visibility.Collapsed;
                SwitchProgress.Visibility = Visibility.Collapsed;
            }
        }

        private void OpenLibraryButton_Click(object sender, RoutedEventArgs e)
        {
            navigator.NavigateTo(ContactablePageType.wallpaperLibrary);
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
                var bitmap = new BitmapImage { DecodePixelWidth = 800 };
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
