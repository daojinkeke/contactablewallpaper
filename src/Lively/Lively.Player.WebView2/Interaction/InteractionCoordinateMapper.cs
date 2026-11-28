using Lively.Common.Helpers.Pinvoke;
using Newtonsoft.Json;
using System;
using System.Drawing;
using System.Threading.Tasks;
using WebView = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Lively.Player.WebView2.Interaction
{
    /// <summary>
    /// 物理像素 → CSS 像素的唯一换算入口（06-interaction-spec.md §5）。
    /// 输入为显示器局部物理像素，输出为页面 CSS 像素；桥与 Form 均不得再做换算。
    /// 比例来自页面实测的 innerWidth/innerHeight 与 WebView2 客户端物理尺寸，
    /// 不使用 StartArgs.Scale（Core 传入的系数可能被 RasterizationScale 覆盖而后不准）。
    /// </summary>
    /// <remarks>
    /// 稳健性要点：
    /// 1. 客户端尺寸用 Win32 GetClientRect 读取（物理像素、可跨线程调用），不用 WinForms ClientSize；
    /// 2. 每次换算前校验实时尺寸，尺寸变化后旧比例立即作废，宁可丢帧也不按错比例换算；
    /// 3. 非客户区偏移用 Win32 MapWindowPoints 换算，不依赖 WinForms PointToScreen 的 DPI 上下文；
    /// 4. 比例与偏移在导航、尺寸变化、DPI 变化与看门狗周期重新采样。
    /// </remarks>
    internal sealed class InteractionCoordinateMapper
    {
        private const string MetricsScript = @"
(function () {
    var vv = window.visualViewport;
    var doc = document.documentElement;
    return {
        devicePixelRatio: window.devicePixelRatio || 1,
        innerWidth: window.innerWidth,
        innerHeight: window.innerHeight,
        visualViewportWidth: vv ? vv.width : window.innerWidth,
        visualViewportHeight: vv ? vv.height : window.innerHeight,
        visualViewportScale: vv ? vv.scale : 1,
        visualViewportOffsetLeft: vv ? vv.offsetLeft : 0,
        visualViewportOffsetTop: vv ? vv.offsetTop : 0,
        documentElementClientWidth: doc ? doc.clientWidth : window.innerWidth,
        documentElementClientHeight: doc ? doc.clientHeight : window.innerHeight
    };
})()";

        /// <summary>换算失败时的重采样限频，避免指针移动把采样刷爆。</summary>
        private const int InternalRefreshIntervalMilliseconds = 300;

        /// <summary>指标日志限频。</summary>
        private const int MetricsLogIntervalMilliseconds = 2000;

        private readonly WebView webView;
        private readonly Action<string> log;
        private readonly object syncRoot = new object();

        private PageViewportMetrics metrics;
        private double cssPerPhysicalX = 1d;
        private double cssPerPhysicalY = 1d;
        private int clientOffsetX;
        private int clientOffsetY;
        private int sampledClientWidth;
        private int sampledClientHeight;
        private DateTime lastMetricsLogUtc = DateTime.MinValue;
        private DateTime lastInternalRefreshUtc = DateTime.MinValue;
        private string lastLoggedSignature;

        public InteractionCoordinateMapper(WebView webView, Action<string> log = null)
        {
            this.webView = webView ?? throw new ArgumentNullException(nameof(webView));
            this.log = log;
        }

        /// <summary>是否已取得页面实测指标；未取得时不得换算。</summary>
        public bool HasMetrics
        {
            get { lock (syncRoot) { return metrics != null; } }
        }

        /// <summary>最近一次实测的页面视口指标。</summary>
        public PageViewportMetrics Metrics
        {
            get { lock (syncRoot) { return metrics; } }
        }

        public double CssPerPhysicalX
        {
            get { lock (syncRoot) { return cssPerPhysicalX; } }
        }

        public double CssPerPhysicalY
        {
            get { lock (syncRoot) { return cssPerPhysicalY; } }
        }

        /// <summary>
        /// 重新采样页面视口与客户端物理尺寸。可在任意线程调用，
        /// 内部会切回 UI 线程（WebView2 与句柄操作是 UI 线程亲和的）。
        /// </summary>
        public Task RefreshAsync()
        {
            var view = webView;
            if (view == null)
                return Task.CompletedTask;

            try
            {
                if (view.IsDisposed)
                    return Task.CompletedTask;
                if (view.InvokeRequired)
                {
                    view.BeginInvoke(new Action(() => { _ = RefreshAsync(); }));
                    return Task.CompletedTask;
                }
            }
            catch (Exception)
            {
                return Task.CompletedTask;
            }

            return RefreshCoreAsync(0);
        }

        /// <summary>限频请求一次重采样（换算失败或检测到尺寸变化时使用）。</summary>
        public void RequestRefresh()
        {
            var now = DateTime.UtcNow;
            if ((now - lastInternalRefreshUtc).TotalMilliseconds < InternalRefreshIntervalMilliseconds)
                return;

            lastInternalRefreshUtc = now;
            _ = RefreshAsync();
        }

        private async Task RefreshCoreAsync(int attempt)
        {
            try
            {
                var core = webView?.CoreWebView2;
                if (core == null)
                    return;

                var json = await core.ExecuteScriptAsync(MetricsScript);
                if (string.IsNullOrWhiteSpace(json) || json == "null")
                    return;

                var measured = JsonConvert.DeserializeObject<PageViewportMetrics>(json);
                if (measured == null)
                    return;

                if (!TryGetPhysicalClientSize(out var clientWidth, out var clientHeight))
                    return;
                if (!IsPositive(measured.InnerWidth) || !IsPositive(measured.InnerHeight))
                    return;

                var scaleX = measured.InnerWidth / clientWidth;
                var scaleY = measured.InnerHeight / clientHeight;
                if (!IsPositive(scaleX) || !IsPositive(scaleY))
                    return;

                var offset = GetClientOffset();

                lock (syncRoot)
                {
                    metrics = measured;
                    cssPerPhysicalX = scaleX;
                    cssPerPhysicalY = scaleY;
                    clientOffsetX = offset.X;
                    clientOffsetY = offset.Y;
                    sampledClientWidth = clientWidth;
                    sampledClientHeight = clientHeight;
                }

                LogMetrics(measured, clientWidth, clientHeight, scaleX, scaleY, offset);

                // Chromium 的布局视口在窗口尺寸变化后会滞后于宿主。实测视口与客户区
                // 不一致说明拿到的是旧布局，稍后重采（最多 2 次），避免比例长期偏差。
                if (attempt < 2 && !IsViewportConsistent(measured, clientWidth, clientHeight))
                {
                    await Task.Delay(250);
                    await RefreshCoreAsync(attempt + 1);
                }
            }
            catch (Exception ex)
            {
                LogThrottled($"Interaction coordinate metrics failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 页面视口换算成设备像素后应与客户区物理尺寸一致；不一致通常意味着布局尚未跟上窗口尺寸。
        /// </summary>
        private static bool IsViewportConsistent(PageViewportMetrics measured, int clientWidth, int clientHeight)
        {
            var dpr = IsPositive(measured.DevicePixelRatio) ? measured.DevicePixelRatio : 1d;
            var tolerance = 0.02 * Math.Max(1, Math.Max(clientWidth, clientHeight));
            return Math.Abs(measured.InnerWidth * dpr - clientWidth) <= tolerance &&
                   Math.Abs(measured.InnerHeight * dpr - clientHeight) <= tolerance;
        }

        /// <summary>
        /// 显示器局部物理像素 → 页面 CSS 像素。未取得实测指标时返回 false，
        /// 调用方必须放弃 JS 推送而不是猜测比例。
        /// </summary>
        public bool TryToCss(Point localPhysical, out double cssX, out double cssY)
        {
            cssX = 0;
            cssY = 0;

            double scaleX, scaleY;
            int offsetX, offsetY, sampledWidth, sampledHeight;
            lock (syncRoot)
            {
                if (metrics == null)
                    return false;

                scaleX = cssPerPhysicalX;
                scaleY = cssPerPhysicalY;
                offsetX = clientOffsetX;
                offsetY = clientOffsetY;
                sampledWidth = sampledClientWidth;
                sampledHeight = sampledClientHeight;
            }

            // 客户区尺寸变了（分辨率/窗口尺寸/DPI 布局变化），旧比例立即作废。
            // 按旧比例换算会直接产生随缩放比放大的错位，宁可丢帧并触发重采样。
            if (TryGetPhysicalClientSize(out var liveWidth, out var liveHeight) &&
                (liveWidth != sampledWidth || liveHeight != sampledHeight))
            {
                RequestRefresh();
                return false;
            }

            cssX = (localPhysical.X - offsetX) * scaleX;
            cssY = (localPhysical.Y - offsetY) * scaleY;
            return !double.IsNaN(cssX) && !double.IsNaN(cssY) &&
                   !double.IsInfinity(cssX) && !double.IsInfinity(cssY);
        }

        /// <summary>导航切换时清空实测值，等待新文档重新采样。</summary>
        public void Reset()
        {
            lock (syncRoot)
            {
                metrics = null;
                sampledClientWidth = 0;
                sampledClientHeight = 0;
            }
        }

        /// <summary>
        /// 读取客户端物理尺寸。优先用 Win32 GetClientRect（物理像素且不做 DPI 虚拟化），
        /// 句柄尚未创建时退回 WinForms ClientSize。
        /// </summary>
        private bool TryGetPhysicalClientSize(out int width, out int height)
        {
            width = 0;
            height = 0;
            try
            {
                var handle = webView?.Handle ?? IntPtr.Zero;
                if (handle != IntPtr.Zero && NativeMethods.GetClientRect(handle, out var rect))
                {
                    width = rect.Right - rect.Left;
                    height = rect.Bottom - rect.Top;
                    return width > 0 && height > 0;
                }

                var size = webView?.ClientSize ?? Size.Empty;
                width = size.Width;
                height = size.Height;
                return width > 0 && height > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 客户区原点相对窗口原点的物理像素偏移。壁纸窗口由 Core 定位到显示器
        /// （或 virtual screen）原点，因此窗口原点即输入坐标原点，这里只扣除
        /// 非客户区/布局造成的客户区偏移，不重复枚举显示器。
        /// </summary>
        private Point GetClientOffset()
        {
            try
            {
                var topLevel = webView.TopLevelControl;
                var handle = webView.Handle;
                if (topLevel == null || handle == IntPtr.Zero || topLevel.Handle == IntPtr.Zero)
                    return Point.Empty;
                if (NativeMethods.GetWindowRect(topLevel.Handle, out var rect) == 0)
                    return Point.Empty;

                // MapWindowPoints(hwnd, IntPtr.Zero) 即 ClientToScreen，返回物理像素；
                // 不使用 WinForms PointToScreen，避免其在部分 .NET Framework DPI 上下文下的虚拟化差异。
                var clientOrigin = Point.Empty;
                NativeMethods.MapWindowPoints(handle, IntPtr.Zero, ref clientOrigin, 1);
                return new Point(clientOrigin.X - rect.Left, clientOrigin.Y - rect.Top);
            }
            catch (Exception)
            {
                return Point.Empty;
            }
        }

        private void LogMetrics(PageViewportMetrics measured, int clientWidth, int clientHeight,
            double scaleX, double scaleY, Point offset)
        {
            // 相同指标不重复刷日志；看门狗会周期性采样，必须避免日志无限增长。
            var signature = string.Format("{0:0.###}|{1:0.#}x{2:0.#}|{3}x{4}|{5:0.####}x{6:0.####}|{7},{8}",
                measured.DevicePixelRatio, measured.InnerWidth, measured.InnerHeight,
                clientWidth, clientHeight, scaleX, scaleY, offset.X, offset.Y);
            if (string.Equals(signature, lastLoggedSignature, StringComparison.Ordinal))
                return;

            var now = DateTime.UtcNow;
            if ((now - lastMetricsLogUtc).TotalMilliseconds < MetricsLogIntervalMilliseconds)
                return;

            lastMetricsLogUtc = now;
            lastLoggedSignature = signature;

            // devicePixelRatio 是页面自报的理论比例，实测比例来自 CSS 布局尺寸与物理客户区，
            // 两者偏差直接反映 WebView2 DPI 感知/RasterizationScale 的实测差异。
            var dprDerived = IsPositive(measured.DevicePixelRatio) ? 1d / measured.DevicePixelRatio : 0d;
            var deviation = dprDerived > 0 ? Math.Abs(dprDerived - scaleX) / dprDerived : 0d;

            log?.Invoke(string.Format(
                "Interaction metrics: dpr={0:0.###}, inner={1:0.##}x{2:0.##}css, client={3}x{4}px, " +
                "cssPerPhysical={5:0.####}/{6:0.####}, dprDerived={7:0.####}, deviation={8:0.###%}, " +
                "visualViewport={9:0.##}x{10:0.##}@{11:0.###}, offset={12},{13}",
                measured.DevicePixelRatio, measured.InnerWidth, measured.InnerHeight, clientWidth, clientHeight,
                scaleX, scaleY, dprDerived, deviation,
                measured.VisualViewportWidth, measured.VisualViewportHeight, measured.VisualViewportScale,
                offset.X, offset.Y));
        }

        private void LogThrottled(string message)
        {
            var now = DateTime.UtcNow;
            if ((now - lastMetricsLogUtc).TotalSeconds < 10)
                return;

            lastMetricsLogUtc = now;
            log?.Invoke(message);
        }

        private static bool IsPositive(double value)
        {
            return value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
