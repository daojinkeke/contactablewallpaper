using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Threading;
using System.Threading.Tasks;
using WebView = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Lively.Player.WebView2.Interaction
{
    /// <summary>
    /// 页面交互桥（L3）。负责注入 contactable 脚本、维护握手状态并推送输入事件。
    /// 契约见 docs/contracts/06-interaction-spec.md 第 6 节：
    /// 坐标由 Player 侧换算为 CSS 像素后传入，桥内不再做任何 DPI 换算。
    /// </summary>
    internal sealed class InteractionBridge : IDisposable
    {
        /// <summary>契约 §6.1：页面加载完成后 2 秒内未 ready 即视为不支持。</summary>
        private const int ReadyTimeoutMilliseconds = 2000;

        private readonly WebView webView;
        private readonly Action<string> log;
        private readonly object syncRoot = new object();
        private Timer readyTimeoutTimer;
        private bool disposed;
        private DateTime lastErrorLogUtc = DateTime.MinValue;

        public InteractionBridge(WebView webView, Action<string> log = null)
        {
            this.webView = webView ?? throw new ArgumentNullException(nameof(webView));
            this.log = log;
            Coordinates = new InteractionCoordinateMapper(webView, log);
        }

        /// <summary>物理像素 → CSS 像素的唯一换算入口，其他位置不得重复换算。</summary>
        public InteractionCoordinateMapper Coordinates { get; }

        /// <summary>桥脚本是否已注入成功。</summary>
        public bool IsInitialized { get; private set; }

        /// <summary>当前文档是否已通过 contactable.ready 完成握手。</summary>
        public bool IsPageReady { get; private set; }

        /// <summary>页面声明的协议版本，未握手时为 0。</summary>
        public int PageProtocolVersion { get; private set; }

        /// <summary>推送失败次数，用于降级判断，不做重试。</summary>
        public int PublishFailureCount { get; private set; }

        /// <summary>ready 超时次数（每次导航最多 1 次），随 msg_console 日志上报。</summary>
        public int ReadyTimeoutCount { get; private set; }

        /// <summary>当前握手状态，TimedOut 表示页面未声明 ready（已降级为可选增强，不再阻断合成）。</summary>
        public InteractionBridgeState PageReadyState { get; private set; } = InteractionBridgeState.NotReady;

        /// <summary>
        /// 页面是否允许宿主合成输入。默认 true：未握手页面同样可交互；
        /// 页面调用 contactable.setInteractive(false) 后置 false。
        /// </summary>
        public bool IsPageInteractive { get; private set; } = true;

        /// <summary>最近一次收到浏览器原生 pointerleave 的时间（UTC）。</summary>
        public DateTime LastNativeLeaveUtc { get; private set; } = DateTime.MinValue;

        /// <summary>页面首次完成握手时触发；重复 ready 不再触发。</summary>
        public event EventHandler PageReady;

        /// <summary>
        /// 注入桥脚本并挂接 WebMessage 通道。必须在首次导航前调用，
        /// 脚本对之后所有文档生效，导航后无需重复注入。
        /// </summary>
        public async Task InitializeAsync()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(InteractionBridge));
            if (IsInitialized)
                return;

            var core = webView.CoreWebView2;
            if (core == null)
                throw new InvalidOperationException("WebView2 未初始化，无法注入交互桥。");

            core.WebMessageReceived += CoreWebView2_WebMessageReceived;
            core.NavigationStarting += CoreWebView2_NavigationStarting;
            core.NavigationCompleted += CoreWebView2_NavigationCompleted;
            webView.SizeChanged += WebView_SizeChanged;
            try
            {
                await core.AddScriptToExecuteOnDocumentCreatedAsync(InteractionBridgeScript.Script);
            }
            catch
            {
                core.WebMessageReceived -= CoreWebView2_WebMessageReceived;
                core.NavigationStarting -= CoreWebView2_NavigationStarting;
                core.NavigationCompleted -= CoreWebView2_NavigationCompleted;
                webView.SizeChanged -= WebView_SizeChanged;
                throw;
            }

            IsInitialized = true;
            log?.Invoke("Interaction bridge injected");
        }

        /// <summary>
        /// 推送一条规范化输入事件。默认不依赖握手；页面显式关闭后停止合成。
        /// 失败只计数，不重试。非 UI 线程调用时自动切回 UI 线程。
        /// </summary>
        public bool TryPublishInput(InteractionInputMessage message)
        {
            if (message == null || disposed || !IsInitialized || !IsPageInteractive)
                return false;

            message.Type = InteractionProtocol.InputMessageType;
            message.Version = InteractionProtocol.Version;
            if (message.Ts <= 0)
                message.Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var json = JsonConvert.SerializeObject(message);
            try
            {
                if (webView.InvokeRequired)
                {
                    webView.BeginInvoke(new Action(() => PublishCore(json)));
                    return true;
                }
                return PublishCore(json);
            }
            catch (Exception ex)
            {
                PublishFailureCount++;
                ReportError(ex, "publish input message");
                return false;
            }
        }

        private bool PublishCore(string json)
        {
            try
            {
                var core = webView?.CoreWebView2;
                if (core == null)
                    return false;

                core.PostWebMessageAsJson(json);
                return true;
            }
            catch (Exception ex)
            {
                PublishFailureCount++;
                ReportError(ex, "post web message");
                return false;
            }
        }

        private void CoreWebView2_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            // 新文档必须重新握手，旧文档的 ready 状态与实测视口立即失效。
            lock (syncRoot)
            {
                IsPageReady = false;
                PageProtocolVersion = 0;
                PageReadyState = InteractionBridgeState.NotReady;
                IsPageInteractive = true;
                CancelReadyTimeoutLocked();
            }

            Coordinates.Reset();
        }

        private void CoreWebView2_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            // 契约 §6.1：从页面加载完成起算 2 秒，超时仅记录降级状态，不再阻断合成。
            if (e.IsSuccess)
            {
                ArmReadyTimeout();
                // 未握手页面同样需要实测视口比例才能换算坐标。
                _ = Coordinates.RefreshAsync();
            }
        }

        private void WebView_SizeChanged(object sender, EventArgs e)
        {
            // 视口尺寸或 DPI 变化后重新采样实测比例。
            // 不依赖 ready：未握手页面同样需要比例才能合成输入。
            if (!IsInitialized || disposed)
                return;

            _ = Coordinates.RefreshAsync();
        }

        private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (disposed)
                return;

            JObject payload;
            try
            {
                var json = e.WebMessageAsJson;
                if (string.IsNullOrWhiteSpace(json))
                    return;

                payload = JObject.Parse(json);
            }
            catch (Exception ex)
            {
                ReportError(ex, "parse web message");
                return;
            }

            var type = payload.Value<string>("type");
            if (string.IsNullOrEmpty(type))
                return;

            switch (type)
            {
                case InteractionProtocol.ReadyMessageType:
                    HandleReady(payload);
                    break;
                case InteractionProtocol.SetInteractiveMessageType:
                    HandleSetInteractive(payload);
                    break;
                case InteractionProtocol.PageLeaveMessageType:
                    LastNativeLeaveUtc = DateTime.UtcNow;
                    break;
                // 热区由后续任务处理；未知类型按契约忽略。
                default:
                    break;
            }
        }

        private void HandleSetInteractive(JObject payload)
        {
            var version = payload.Value<int?>("version") ?? 0;
            if (version != InteractionProtocol.Version)
                return;

            var enabled = payload.Value<bool?>("enabled") ?? true;
            if (IsPageInteractive == enabled)
                return;

            IsPageInteractive = enabled;
            log?.Invoke($"Interaction bridge page set interactive: {enabled}");
        }

        private void HandleReady(JObject payload)
        {
            var version = payload.Value<int?>("version") ?? 0;
            if (version != InteractionProtocol.Version)
            {
                // 版本不兼容：不启用 JS 推送，降级为纯窗口消息链路。
                LogThrottled($"Interaction bridge unsupported protocol version: {version}");
                return;
            }

            bool firstReady;
            lock (syncRoot)
            {
                if (PageReadyState == InteractionBridgeState.TimedOut)
                {
                    // 已按契约判定为不支持；迟到的 ready 不再启用 JS 推送，避免与窗口消息双发。
                    LogThrottled("Interaction bridge ignored late ready after timeout");
                    return;
                }

                // 握手幂等：重复 ready 只更新版本，不重复触发。
                firstReady = PageReadyState != InteractionBridgeState.Ready;
                PageReadyState = InteractionBridgeState.Ready;
                IsPageReady = true;
                PageProtocolVersion = version;
                CancelReadyTimeoutLocked();
            }

            if (!firstReady)
                return;

            PageReady?.Invoke(this, EventArgs.Empty);
            _ = Coordinates.RefreshAsync();
        }

        private void ArmReadyTimeout()
        {
            lock (syncRoot)
            {
                if (disposed || PageReadyState == InteractionBridgeState.Ready)
                    return;

                CancelReadyTimeoutLocked();
                readyTimeoutTimer = new Timer(OnReadyTimeout, null, ReadyTimeoutMilliseconds, Timeout.Infinite);
            }
        }

        private void CancelReadyTimeoutLocked()
        {
            readyTimeoutTimer?.Dispose();
            readyTimeoutTimer = null;
        }

        private void OnReadyTimeout(object state)
        {
            lock (syncRoot)
            {
                if (disposed || PageReadyState == InteractionBridgeState.Ready)
                    return;
                if (PageReadyState == InteractionBridgeState.TimedOut)
                    return;

                PageReadyState = InteractionBridgeState.TimedOut;
                ReadyTimeoutCount++;
                CancelReadyTimeoutLocked();
            }

            // 通过 msg_console 暴露状态与计数，供 Core 观测；不新增 L2 消息编号。
            // 握手只是可选增强：超时后仍按默认策略合成输入。
            LogThrottled("Interaction bridge ready timeout (2000 ms): page did not handshake, default interactive synthesis remains enabled");
        }

        private void ReportError(Exception ex, string action)
        {
            LogThrottled($"Interaction bridge error ({action}): {ex.Message}");
        }

        private void LogThrottled(string message)
        {
            var now = DateTime.UtcNow;
            if ((now - lastErrorLogUtc).TotalSeconds < 10)
                return;

            lastErrorLogUtc = now;
            log?.Invoke(message);
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            lock (syncRoot)
            {
                CancelReadyTimeoutLocked();
            }

            try
            {
                var core = webView?.CoreWebView2;
                if (core != null)
                {
                    core.WebMessageReceived -= CoreWebView2_WebMessageReceived;
                    core.NavigationStarting -= CoreWebView2_NavigationStarting;
                    core.NavigationCompleted -= CoreWebView2_NavigationCompleted;
                }
                webView.SizeChanged -= WebView_SizeChanged;
            }
            catch (Exception)
            {
                // 窗体关闭阶段 WebView2 可能已释放，忽略。
            }
        }
    }
}
