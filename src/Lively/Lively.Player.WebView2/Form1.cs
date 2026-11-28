﻿using CommandLine;
using Lively.Common;
using Lively.Common.Extensions;
using Lively.Common.Helpers;
using Lively.Common.Helpers.Pinvoke;
using Lively.Common.JsonConverters;
using Lively.Common.Services;
using Lively.Models.Enums;
using Lively.Models.Message;
using Lively.Player.WebView2.Extensions.WebView2;
using Lively.Player.WebView2.Host;
using Lively.Player.WebView2.Interaction;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using WebView = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Lively.Player.WebView2
{
    public partial class Form1 : Form
    {
        private WebView webView;
        private StartArgs startArgs;
        private bool isPaused = false;
        private bool isVideoStream = false;
        private int cefD3DRenderingSubProcessId;

        private bool initializedServices = false; //delay API init till loaded page
        private IAudioVisualizerService visualizerService;
        private IHardwareUsageService hardwareUsageService;
        private INowPlayingService nowPlayingService;
        private InteractionBridge interactionBridge;
        private InteractionInputPump interactionInputPump;
        private HostEnvironmentService hostEnvironmentService;
        private IHardwareUsageService hostStatsService;

        /// <summary>显示器缩放同步周期，覆盖 DPI 变化不触发尺寸变化的场景。</summary>
        private const int RasterizationScaleCheckIntervalMilliseconds = 3000;

        private CoreWebView2Controller webView2Controller;
        private Timer rasterizationScaleTimer;

        private bool IsDebugging { get; } = BuildInfoUtil.IsDebugBuild();

        public Form1()
        {
            InitializeComponent();
            if (IsDebugging)
            {
                startArgs = new StartArgs
                {
                    // .html fullpath
                    Url = "https://google.com/",
                    //online or local(file)
                    Type = WebPageType.online,
                    // LivelyProperties.json path if any
                    Properties = @"",
                    SysInfo = false,
                    NowPlaying = false,
                    AudioVisualizer = false,
                    PauseEvent = false
                };

                this.FormBorderStyle = FormBorderStyle.Sizable;
                this.WindowState = FormWindowState.Normal;
                this.StartPosition = FormStartPosition.Manual;
                this.Size = new Size(1920, 1080);
                this.ShowInTaskbar = true;
                this.MaximizeBox = true;
                this.MinimizeBox = true;
            }
            else
            {
                Parser.Default.ParseArguments<StartArgs>(Environment.GetCommandLineArgs())
                    .WithParsed((x) => startArgs = x)
                    .WithNotParsed(HandleParseError);

                this.WindowState = FormWindowState.Minimized;
                this.StartPosition = FormStartPosition.Manual;
                this.Location = new Point(-9999, 0);

                if (startArgs.Geometry != null)
                {
                    var msg = startArgs.Geometry.Split('x');
                    if (msg.Length >= 2 && int.TryParse(msg[0], out int width) && int.TryParse(msg[1], out int height))
                    {
                        this.Size = new Size(width, height);
                    }
                }

                var darkColor = Color.FromArgb(30, 30, 30);
                var lightColor = Color.FromArgb(240, 240, 240);
                this.BackColor = startArgs.Theme switch
                {
                    AppTheme.Auto => ThemeUtil.GetWindowsTheme() == AppTheme.Dark ? darkColor : lightColor,
                    AppTheme.Light => lightColor,
                    AppTheme.Dark => darkColor,
                    _ => darkColor,
                };
            }

            InitializeWebView2Async().Await(() => {
                _ = ListenToParent();
            }, 
            (err) =>
            {
                err.SendError(SendToParent, "Failed to initialize WebView2");
                // Exit or display custom error page.
                Environment.Exit(1);
            });
        }

        // Hide from taskview and taskbar.
        // ShowInTaskbar = true does not create TOOLWINDOW.
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                return cp;
            }
        }

        private void HandleParseError(IEnumerable<Error> errs)
        {
            if (errs != null)
                string.Join(Environment.NewLine, errs).SendError(SendToParent, "Error parsing launch arguments");

            // ERROR_INVALID_PARAMETER
            // Ref: <https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--0-499->
            Environment.Exit(87);
        }

        public async Task InitializeWebView2Async()
        {
            webView = new WebView() {
                DefaultBackgroundColor = Color.Transparent
            };
            webView.NavigationCompleted += WebView_NavigationCompleted;

            var webViewStartArgs =
                // Allow media autoplay even if not muted.
                "--autoplay-policy=no-user-gesture-required " +
                // Disable SMTC.
                "--disable-features=HardwareMediaKeyHandling ";
            CoreWebView2EnvironmentOptions options = new CoreWebView2EnvironmentOptions(webViewStartArgs);
            // WebView2 does not have in-memory mode, ref: https://github.com/MicrosoftEdge/WebView2Feedback/issues/3637
            // Custom user data folder, ref: https://docs.microsoft.com/en-us/microsoft-edge/webview2/concepts/user-data-folder
            $"Setting UserData path: {startArgs.UserDataPath}".SendLog(SendToParent);
            // 随包携带固定版本 WebView2 时优先使用它，否则为 null 走系统 Evergreen Runtime。
            var env = await CoreWebView2Environment.CreateAsync(Program.BundledRuntimePath, startArgs.UserDataPath, options);
            await webView.EnsureCoreWebView2Async(env);

            // Defaults
            webView.CoreWebView2.IsMuted = startArgs.Volume == 0;
            webView.CoreWebView2.Profile.PreferredColorScheme = startArgs.Theme.GetPreferredColorScheme();
            // RasterizationScale is better choice than ZoomFactor since it is not affected by system text size but not exposed in .NET, ref:
            // https://github.com/MicrosoftEdge/WebView2Feedback/issues/4775
            // https://github.com/MicrosoftEdge/WebView2Feedback/issues/3839
            // WebView2 作为 WorkerW/Progman 子窗口时不会自己拿到正确 DPI（初始值恒为 1，
            // 且桌面窗口的 DPI 变化也未必送达）：只信 Core 传入的一次性系数会让
            // 高 DPI 机器上画面与指针一起按缩放比错位。这里改为由本进程直读显示器 DPI，
            // 并周期性自愈，单屏高 DPI 同样生效。
            // 启动时优先沿用 Core 传入的目标显示器系数（保持原有行为），
            // 之后的看门狗/窗口移动再按本进程实测的显示器 DPI 自愈。
            if (startArgs.Scale != null)
                $"Requested scale: {startArgs.Scale.Value}".SendLog(SendToParent);
            SyncRasterizationScale("startup", startArgs.Scale);
            rasterizationScaleTimer = new Timer { Interval = RasterizationScaleCheckIntervalMilliseconds };
            rasterizationScaleTimer.Tick += (s, e) => SyncRasterizationScale("watchdog");
            rasterizationScaleTimer.Start();
            this.LocationChanged += (s, e) => SyncRasterizationScale("window moved");

            if (!IsDebugging)
            {
                // Don't allow contextmenu and devtools.
                webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            }

            if (!string.IsNullOrWhiteSpace(startArgs.DebugPort) && int.TryParse(startArgs.DebugPort, out int _))
            {
                // In WebView2 --remote-debugging-port=XXXX is not opening up a direct connection, we will just ignore port and open DevTools instead.
                webView.CoreWebView2.OpenDevToolsWindow();
                webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = true;
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            }

            webView.CoreWebView2.ProcessFailed += CoreWebView2_ProcessFailed;
            webView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
            webView.CoreWebView2.DownloadStarting += CoreWebView2_DownloadStarting;
            webView.CoreWebView2.DocumentTitleChanged += CoreWebView2_DocumentTitleChanged;

            // 交互桥注入必须在首次导航前完成；失败时降级为纯窗口消息链路，不影响壁纸加载。
            try
            {
                interactionBridge = new InteractionBridge(webView, message => message.SendLog(SendToParent));
                await interactionBridge.InitializeAsync();

                // 截获 Core 投递到 WebView2 输入窗口的消息并合成页面事件（默认开启，无需握手）。
                // 输入窗口若属 WebView2 运行时进程则由 Chromium 原生处理，此时不合成避免双发。
                interactionInputPump = new InteractionInputPump(webView, interactionBridge, message => message.SendLog(SendToParent));
                interactionInputPump.Start();
            }
            catch (Exception ex)
            {
                interactionInputPump?.Dispose();
                interactionInputPump = null;
                interactionBridge?.Dispose();
                interactionBridge = null;
                ex.SendError(SendToParent, "Failed to initialize interaction bridge");
            }

            // 本地 HTML 壁纸：脚本执行前注入宿主时间/天气（window.contactableEnv），
            // 并在「允许壁纸访问外部网络」关闭时拦截页面的外部请求。
            // 在线壁纸（含 YouTube/Shadertoy）不注入也不拦截，保持原有行为。
            if (startArgs.Type == WebPageType.local)
            {
                hostEnvironmentService = new HostEnvironmentService(message => message.SendLog(SendToParent));
                try
                {
                    await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                        hostEnvironmentService.BuildInitialScript(startArgs.AllowExternalNetwork));
                }
                catch (Exception ex)
                {
                    ex.SendError(SendToParent, "Failed to inject host environment");
                }

                // 本地壁纸：每秒推送系统占用（window.contactableStats），供硬件监控类壁纸绘制曲线。
                // 与 Lively 自带 SysInfo 互斥，避免重复采样性能计数器。
                if (!startArgs.SysInfo)
                {
                    try
                    {
                        hostStatsService = new HardwareUsageService();
                        hostStatsService.HWMonitor += (s, e) =>
                        {
                            try
                            {
                                if (isPaused)
                                    return;

                                var stats = new
                                {
                                    cpu = Math.Round(e.CurrentCpu, 1),
                                    gpu = Math.Round(e.CurrentGpu3D, 1),
                                    ramUsedMb = Math.Max(0, Math.Round(e.TotalRam - e.CurrentRamAvail, 0)),
                                    ramTotalMb = e.TotalRam,
                                    netDownBps = Math.Round(e.CurrentNetDown, 0),
                                    netUpBps = Math.Round(e.CurrentNetUp, 0),
                                    cpuName = e.NameCpu,
                                    gpuName = e.NameGpu,
                                    ts = DateTimeOffset.Now.ToUnixTimeMilliseconds()
                                };
                                var json = JsonConvert.SerializeObject(stats, Formatting.None);
                                this.Invoke((Action)(() =>
                                {
                                    try
                                    {
                                        _ = webView.CoreWebView2?.ExecuteScriptAsync("window.contactableStats=" + json + ";");
                                    }
                                    catch { }
                                }));
                            }
                            catch { }
                        };
                        hostStatsService.Start();
                    }
                    catch (Exception ex)
                    {
                        ex.SendError(SendToParent, "Failed to start host stats");
                    }
                }

                if (!startArgs.AllowExternalNetwork)
                    BlockExternalNetworkRequests();
            }

            switch (startArgs.Type)
            {
                case WebPageType.online:
                    {
                        var (contentType, id) = WebContentUtil.GetContentType(startArgs.Url);
                        switch (contentType)
                        {
                            case WebContentType.shadertoy:
                                {
                                    $"Opening shadertoy shader: {id}".SendLog(SendToParent);
                                    webView.CoreWebView2.Navigate($"https://www.shadertoy.com/embed/{id}?gui=false&t=10&paused=false&muted=true");
                                }
                                break;
                            case WebContentType.youtube:
                                {
                                    isVideoStream = true;
                                    $"Opening yt stream: {id}".SendLog(SendToParent);
                                    // Error 153, ref: https://github.com/rocksdanister/lively/issues/2991
                                    webView.CoreWebView2.AddWebResourceRequestedFilter("*youtube.com/*", CoreWebView2WebResourceContext.All);
                                    webView.CoreWebView2.WebResourceRequested += (s, e) =>
                                    {
                                        var headers = e.Request.Headers;
                                        if (!headers.Contains("Referer"))
                                            headers.SetHeader("Referer", "https://localhost/");
                                    };
                                    webView.CoreWebView2.Navigate($"https://www.youtube.com/embed/{id}?version=3&rel=0&autoplay=1&loop=1&controls=0&playlist={id}");
                                }
                                break;
                            case WebContentType.generic:
                                {
                                    $"Opening address: {startArgs.Url}".SendLog(SendToParent);
                                    webView.CoreWebView2.Navigate(startArgs.Url);
                                }
                                break;
                            case WebContentType.none:
                                {
                                    "Failed to parse url".SendError(SendToParent);
                                    throw new ArgumentException(startArgs.Url);
                                }
                            default:
                                throw new NotImplementedException();
                        }
                    }
                    break;
                case WebPageType.local:
                    {
                        $"Opening local project: {startArgs.Url}".SendLog(SendToParent);
                        webView.NavigateToLocalPath(startArgs.Url);
                    }
                    break;
            }
            this.Controls.Add(webView);
            webView.Dock = DockStyle.Fill;

            // Make sure to do this after WebView control is attached to form to avoid issues.
            SendToParent(new LivelyMessageHwnd() {
                Hwnd = webView.Handle.ToInt32()
            });
        }

        /// <summary>
        /// 用本进程直读的显示器 DPI 校正 WebView2 的 RasterizationScale。
        /// 窗口挂在 WorkerW 下时 WebView2 自带的 DPI 检测不可靠，显示器切换后也不会更新，
        /// 会让页面缩放与指针坐标一起偏离（表现为缩放错位）。
        /// </summary>
        private void SyncRasterizationScale(string reason, double? preferredScale = null)
        {
            try
            {
                if (!TryGetWebView2Controller(out var controller))
                    return;

                var targetScale = 0d;
                if (preferredScale.HasValue)
                {
                    targetScale = preferredScale.Value;
                }
                else if (!TryGetDisplayScale(out targetScale))
                {
                    return;
                }

                if (targetScale <= 0 || double.IsNaN(targetScale) || double.IsInfinity(targetScale))
                    return;

                var currentScale = controller.RasterizationScale;
                if (Math.Abs(currentScale - targetScale) < 0.01)
                    return;

                // 只在需要纠正时接管缩放：避免 Chromium 用错误的自检值把刚写入的值覆盖回去。
                // 值本来就正确时不碰检测开关，保持基线行为不变。
                controller.ShouldDetectMonitorScaleChanges = false;
                controller.RasterizationScale = targetScale;
                $"RasterizationScale {currentScale:0.###} -> {targetScale:0.###} ({reason})".SendLog(SendToParent);

                // 缩放变化会触发页面重新布局，旧实测比例立即作废。
                _ = interactionBridge?.Coordinates.RefreshAsync();
            }
            catch (Exception ex)
            {
                ex.SendError(SendToParent, "Failed to sync scale");
            }
        }

        private bool TryGetWebView2Controller(out CoreWebView2Controller controller)
        {
            controller = webView2Controller;
            if (controller != null)
                return true;

            try
            {
                // RasterizationScale 未在 WinForms 包装中公开，只能取内部 controller。
                var field = typeof(WebView).GetField("_coreWebView2Controller", BindingFlags.Instance | BindingFlags.NonPublic);
                controller = field?.GetValue(webView) as CoreWebView2Controller;
                webView2Controller = controller;
                return controller != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 窗口所在显示器的实际缩放。按窗口矩形取显示器，
        /// 不依赖 WebView2 子窗口继承的（可能错误的）DPI 上下文。
        /// </summary>
        private bool TryGetDisplayScale(out double scale)
        {
            scale = 1d;

            IntPtr handle;
            try
            {
                handle = webView != null && webView.IsHandleCreated ? webView.Handle : IntPtr.Zero;
            }
            catch (Exception)
            {
                return false;
            }

            if (handle == IntPtr.Zero)
                return false;

            var monitor = NativeMethods.MonitorFromWindow(handle, (uint)NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
                return false;
            if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MonitorDpiType.MDT_Effective_DPI,
                out uint dpiX, out _) != 0 || dpiX == 0)
                return false;

            scale = dpiX / 96d;
            return scale > 0 && !double.IsNaN(scale) && !double.IsInfinity(scale);
        }

        private void CoreWebView2_DocumentTitleChanged(object sender, object e)
        {
            this.Invoke((MethodInvoker)(() => this.Text = webView.CoreWebView2.DocumentTitle));
        }

        private void CoreWebView2_ProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            // Expected behavior: DebugActiveProcess(CEF_D3DRenderingSubProcess)
            // Ref: https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2processfailedkind
            if (isPaused && e.Reason == CoreWebView2ProcessFailedReason.Unresponsive)
                return;

            e.Reason.ToString().SendError(SendToParent, "CoreWebView2 process failed");
        }

        private void CoreWebView2_NewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            // Avoid popups.
            if (e.IsUserInitiated)
            {
                e.Handled = true;
                // Open new tab/window  hyperlink (target="_blank") in default browser.
                LinkUtil.OpenBrowser(e.Uri);
            }
        }

        private void CoreWebView2_DownloadStarting(object sender, CoreWebView2DownloadStartingEventArgs e)
        {
            // Cancel user requested downloads.
            e.Cancel = true;
        }

        /// <summary>
        /// 关闭「允许壁纸访问外部网络」时为本地 HTML 壁纸注册外部请求过滤器；
        /// 只过滤 http(s)，file://、data:、about: 等宿主/内联加载不受影响。
        /// </summary>
        private void BlockExternalNetworkRequests()
        {
            webView.CoreWebView2.AddWebResourceRequestedFilter("http://*", CoreWebView2WebResourceContext.All);
            webView.CoreWebView2.AddWebResourceRequestedFilter("https://*", CoreWebView2WebResourceContext.All);
            webView.CoreWebView2.WebResourceRequested += CoreWebView2_BlockExternalNetworkRequest;
        }

        private void CoreWebView2_BlockExternalNetworkRequest(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                var uri = e.Request.Uri ?? string.Empty;
                if (!uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    return;

                e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                    null, 403, "Forbidden", "Content-Type: text/plain");
                $"Blocked external request (network disabled): {uri}".SendLog(SendToParent);
            }
            catch (Exception ex)
            {
                ex.SendError(SendToParent, "Failed to block external request");
            }
        }

        /// <summary>
        /// 页面加载完成后异步刷新天气；成功后更新 window.contactableEnv.weather 并派发 contactable:env。
        /// 取数失败或缓存仍新鲜时不做任何事，页面保持已有回退结果。
        /// </summary>
        private async Task RefreshHostEnvironmentAsync()
        {
            if (hostEnvironmentService == null)
                return;

            try
            {
                var script = await hostEnvironmentService.RefreshScriptAsync();
                if (string.IsNullOrEmpty(script) || webView?.CoreWebView2 == null)
                    return;

                await webView.CoreWebView2.ExecuteScriptAsync(script);
                "Host weather refreshed".SendLog(SendToParent);
            }
            catch (Exception ex)
            {
                ex.SendError(SendToParent, "Failed to refresh host weather");
            }
        }

        private async void WebView_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            // Restore default.
            webView.DefaultBackgroundColor = Color.White;

            if (!e.IsSuccess)
            {
                e.WebErrorStatus.ToString().SendError(SendToParent, "WebView navigation failed");
                return;
            }

            if (!webView.TryGetCefD3DRenderingSubProcessId(out cefD3DRenderingSubProcessId))
                "Failed to retrieve GetCefD3DRenderingSubProcessId".SendError(SendToParent);

            switch (startArgs.Type)
            {
                case WebPageType.online:
                    switch (WebContentUtil.GetContentType(startArgs.Url).ContentType)
                    {
                        case WebContentType.shadertoy:
                            await webView.TryHideShaderToyGui();
                            break;
                    }
                    break;
                case WebPageType.local:
                    await RestoreLivelyProperties(startArgs.Properties);
                    // 天气不阻塞首帧：页面拿缓存/默认天空先渲染，取数成功后再就地刷新。
                    _ = RefreshHostEnvironmentAsync();
                    break;
            }
            SendToParent(new LivelyMessageWallpaperLoaded() { Success = e.IsSuccess });

            if (!initializedServices)
            {
                initializedServices = true;
                if (startArgs.NowPlaying)
                {
                    nowPlayingService = new NpsmNowPlayingService();
                    nowPlayingService.NowPlayingTrackChanged += (s, e) => {
                        try
                        {
                            if (isPaused)
                                return;

                            // CefSharp CanExecuteJavascriptInMainFrame equivalent in webview required?
                            this.Invoke((Action)(() =>
                            {
                                _ = webView.ExecuteScriptFunctionAsync("livelyCurrentTrack", JsonConvert.SerializeObject(e, Formatting.Indented));
                            }));

                        }
                        catch (Exception ex)
                        {
                            ex.SendError(SendToParent, "Error sending track information");

                        }
                    };
                    nowPlayingService.Start();
                }


                if (startArgs.SysInfo)
                {
                    hardwareUsageService = new HardwareUsageService();
                    hardwareUsageService.HWMonitor += (s, e) => {
                        try
                        {
                            if (isPaused)
                                return;

                            // CefSharp CanExecuteJavascriptInMainFrame equivalent in webview required?
                            this.Invoke((Action)(() =>
                            {
                                _ = webView.ExecuteScriptFunctionAsync("livelySystemInformation", JsonConvert.SerializeObject(e, Formatting.Indented));
                            }));
                        }
                        catch { }
                    };
                    hardwareUsageService.Start();
                }

                if (startArgs.AudioVisualizer)
                {
                    visualizerService = new NAudioVisualizerService();
                    visualizerService.AudioDataAvailable += (s, e) => {
                        try
                        {
                            if (isPaused)
                                return;

                            //TODO: CefSharp CanExecuteJavascriptInMainFrame equivalent in webview
                            this.Invoke((Action)(() =>
                            {
                                _ = webView.ExecuteScriptFunctionAsync("livelyAudioListener", e);
                            }));
                        }
                        catch { }
                    };
                    visualizerService.Start(startArgs.AudioVisualizerDeviceId);
                }
            }
        }

        /// <summary>
        /// Resumes the suspended CEF Direct3D rendering subprocess by detaching the debugger.
        /// Must be called on the same thread that previously attached, since debugger state is thread-specific.
        /// </summary>
        private void ResumeRenderingSubProcess()
        {
            if (cefD3DRenderingSubProcessId == 0)
                return;

            _ = NativeMethods.DebugActiveProcessStop((uint)cefD3DRenderingSubProcessId);
        }

        /// <summary>
        /// Suspends the CEF Direct3D rendering subprocess by attaching a debugger.
        /// Must be called on the same thread to ensure proper detachment later.
        /// </summary>
        private void SuspendRenderingSubProcess()
        {
            // The "System Idle Process" is given process ID 0, Kernel is 1.
            if (cefD3DRenderingSubProcessId == 0)
                return;

            // DebugSetProcessKillOnExit by default is TRUE.
            // Ref: https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-debugsetprocesskillonexit
            _ = NativeMethods.DebugActiveProcess((uint)cefD3DRenderingSubProcessId);
        }

        public async Task ListenToParent()
        {
            if (IsDebugging)
                return;

            var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);

            try
            {
                await Task.Run(async () =>
                {
                    while (true)
                    {
                        // Since UTF8 is backward compatible, will work without this reader for non unicode characters.
                        string text = await reader.ReadLineAsync();
                        if (startArgs.VerboseLog)
                            Console.WriteLine(text);

                        if (string.IsNullOrEmpty(text))
                        {
                            // When the redirected stream is closed, a null line is sent to the event handler. 
                            break;
                        }
                        else
                        {
                            try
                            {
                                var obj = JsonConvert.DeserializeObject<IpcMessage>(text, new JsonSerializerSettings() { Converters = { new IpcMessageConverter() } });
                                if (obj.Type == MessageType.cmd_close)
                                    break;

                                // Ensure all processing is on the same thread.
                                // Some WebView2 calls require UI thread.
                                this.Invoke((Action)(async () =>
                                {
                                    try
                                    {
                                        await ProcessMessage(obj);
                                    }
                                    catch (Exception ie1)
                                    {
                                        ie1.SendError(SendToParent);
                                    }
                                }));
                            }
                            catch (Exception ie2)
                            {
                                ie2.SendError(SendToParent);
                            }
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                ex.SendError(SendToParent);
            }
            finally
            {
                reader?.Dispose();
                this.Invoke((Action)Application.Exit);
            }
        }

        private async Task ProcessMessage(IpcMessage obj)
        {
            switch (obj.Type)
            {
                case MessageType.cmd_reload:
                    // ConnectionAborted issue.
                    //try
                    //{
                    //    webView?.Reload();
                    //}
                    //catch (Exception ie)
                    //{
                    //    WriteToParent(new LivelyMessageConsole()
                    //    {
                    //        Category = ConsoleMessageType.error,
                    //        Message = $"Reload failed: {ie.Message}"
                    //    });
                    //}
                    break;
                case MessageType.cmd_suspend:
                    await HandleSuspend();
                    break;
                case MessageType.cmd_resume:
                    await HandleResume();
                    break;
                case MessageType.cmd_volume:
                    var vc = (LivelyVolumeCmd)obj;
                    webView.CoreWebView2.IsMuted = vc.Volume == 0;
                    break;
                case MessageType.cmd_screenshot:
                    var success = true;
                    var scr = (LivelyScreenshotCmd)obj;
                    try
                    {
                        await webView.CaptureScreenshot(scr.Format, scr.FilePath);
                    }
                    catch (Exception ie)
                    {
                        success = false;
                        ie.SendError(SendToParent, "Failed to capture screenshot");
                    }
                    finally
                    {
                        SendToParent(new LivelyMessageScreenshot()
                        {
                            FileName = Path.GetFileName(scr.FilePath),
                            Success = success
                        });
                    }
                    break;
                case MessageType.lp_slider:
                    var sl = (LivelySlider)obj;
                    await webView.ExecuteScriptFunctionAsync("livelyPropertyListener", sl.Name, sl.Value);
                    break;
                case MessageType.lp_textbox:
                    var tb = (LivelyTextBox)obj;
                    await webView.ExecuteScriptFunctionAsync("livelyPropertyListener", tb.Name, tb.Value);
                    break;
                case MessageType.lp_dropdown:
                    var dd = (LivelyDropdown)obj;
                    await webView.ExecuteScriptFunctionAsync("livelyPropertyListener", dd.Name, dd.Value);
                    break;
                case MessageType.lp_cpicker:
                    var cp = (LivelyColorPicker)obj;
                    await webView.ExecuteScriptFunctionAsync("livelyPropertyListener", cp.Name, cp.Value);
                    break;
                case MessageType.lp_chekbox:
                    var cb = (LivelyCheckbox)obj;
                    await webView.ExecuteScriptFunctionAsync("livelyPropertyListener", cb.Name, cb.Value);
                    break;
                case MessageType.lp_fdropdown:
                    var fd = (LivelyFolderDropdown)obj;
                    var filePath = fd.Value is null ? null : Path.Combine(Path.GetDirectoryName(startArgs.Url), fd.Value);
                    await webView.ExecuteScriptFunctionAsync("livelyPropertyListener", fd.Name, File.Exists(filePath) ? fd.Value : null);
                    break;
                case MessageType.lp_button:
                    var btn = (LivelyButton)obj;
                    if (btn.IsDefault)
                        await RestoreLivelyProperties(startArgs.Properties);
                    else
                        await webView.ExecuteScriptFunctionAsync("livelyPropertyListener", btn.Name, true);
                    break;
                case MessageType.lsp_perfcntr:
                    await webView.ExecuteScriptFunctionAsync("livelySystemInformation", JsonConvert.SerializeObject(((LivelySystemInformation)obj).Info, Formatting.Indented));

                    break;
                case MessageType.lsp_nowplaying:
                    await webView.ExecuteScriptFunctionAsync("livelyCurrentTrack", JsonConvert.SerializeObject(((LivelySystemNowPlaying)obj).Info, Formatting.Indented));
                    break;
                case MessageType.msg_input_keyboard:
                    var keyInput = (LivelyKeyboardInput)obj;
                    interactionInputPump?.PublishKey(keyInput.VirtualKey, keyInput.IsDown);
                    break;
                case MessageType.cmd_close:
                    // Handle on caller
                    break;
            }
        }

        private async Task HandleSuspend()
        {
            SuspendRenderingSubProcess();
            if (!isPaused)
            {
                if (startArgs.PauseWebMedia || isVideoStream)
                    await webView.TryPauseMedia();

                if (startArgs.PauseEvent)
                    await webView.ExecuteScriptFunctionAsync("livelyWallpaperPlaybackChanged",
                        JsonConvert.SerializeObject(new WallpaperPlaybackState() { IsPaused = true }),
                        Formatting.Indented);
            }
            isPaused = true;
        }

        private async Task HandleResume()
        {
            ResumeRenderingSubProcess();
            if (isPaused)
            {
                if (startArgs.PauseWebMedia || isVideoStream)
                    await webView.TryPlayMedia();

                if (startArgs.PauseEvent)
                    await webView.ExecuteScriptFunctionAsync("livelyWallpaperPlaybackChanged",
                            JsonConvert.SerializeObject(new WallpaperPlaybackState() { IsPaused = false }),
                            Formatting.Indented);

                if (startArgs.NowPlaying)
                    await webView.ExecuteScriptFunctionAsync("livelyCurrentTrack", JsonConvert.SerializeObject(nowPlayingService?.CurrentTrack, Formatting.Indented));
            }
            isPaused = false;
        }

        private async Task RestoreLivelyProperties(string propertyPath)
        {
            try
            {
                await LivelyPropertyUtil.LoadProperty(propertyPath, Path.GetDirectoryName(startArgs.Url), async (key, value) =>
                {
                    await webView.ExecuteScriptFunctionAsync("livelyPropertyListener", key, value);
                });
            }
            catch (Exception ex)
            {
                ex.SendError(SendToParent);
            }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            visualizerService?.Dispose();
            hardwareUsageService?.Stop();
            hostStatsService?.Stop();
            nowPlayingService?.Stop();
            rasterizationScaleTimer?.Stop();
            rasterizationScaleTimer?.Dispose();
            rasterizationScaleTimer = null;
            interactionInputPump?.Dispose();
            interactionBridge?.Dispose();
            webView?.Dispose();
        }

        private void SendToParent(IpcMessage obj)
        {
            if (!IsDebugging)
                Console.WriteLine(JsonConvert.SerializeObject(obj));

            Debug.WriteLine(JsonConvert.SerializeObject(obj));
        }

        private sealed class WallpaperPlaybackState
        {
            public bool IsPaused { get; set; }
        }
    }
}
