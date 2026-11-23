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

        private readonly WebView webView;
        private readonly Action<string> log;
        private readonly object syncRoot = new object();

        private PageViewportMetrics metrics;
        private double cssPerPhysicalX = 1d;
        private double cssPerPhysicalY = 1d;
        private int clientOffsetX;
        private int clientOffsetY;
        private DateTime lastMetricsLogUtc = DateTime.MinValue;

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
        /// 重新采样页面视口与客户端物理尺寸。必须在 UI 线程调用（握手完成或视口变化时）。
        /// </summary>
        public async Task RefreshAsync()
        {
            await RefreshAsync(0);
        }

        private async Task RefreshAsync(int attempt)
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

                var client = webView.ClientSize;
                if (client.Width <= 0 || client.Height <= 0)
                    return;
                if (!IsPositive(measured.InnerWidth) || !IsPositive(measured.InnerHeight))
                    return;

                var scaleX = measured.InnerWidth / client.Width;
                var scaleY = measured.InnerHeight / client.Height;
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
                }

                LogMetrics(measured, client, scaleX, scaleY, offset);

                // Chromium 的布局视口在窗口尺寸变化后会滞后于宿主。实测视口与客户区
                // 不一致说明拿到的是旧布局，稍后重采（最多 2 次），避免比例长期偏差。
                if (attempt < 2 && !IsViewportConsistent(measured, client))
                {
                    await Task.Delay(250);
                    await RefreshAsync(attempt + 1);
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
        private static bool IsViewportConsistent(PageViewportMetrics measured, Size client)
        {
            var dpr = IsPositive(measured.DevicePixelRatio) ? measured.DevicePixelRatio : 1d;
            var tolerance = 0.02 * Math.Max(1, Math.Max(client.Width, client.Height));
            return Math.Abs(measured.InnerWidth * dpr - client.Width) <= tolerance &&
                   Math.Abs(measured.InnerHeight * dpr - client.Height) <= tolerance;
        }

        /// <summary>
        /// 显示器局部物理像素 → 页面 CSS 像素。未取得实测指标时返回 false，
        /// 调用方必须放弃 JS 推送而不是猜测比例。
        /// </summary>
        public bool TryToCss(Point localPhysical, out double cssX, out double cssY)
        {
            lock (syncRoot)
            {
                if (metrics == null)
                {
                    cssX = 0;
                    cssY = 0;
                    return false;
                }

                cssX = (localPhysical.X - clientOffsetX) * cssPerPhysicalX;
                cssY = (localPhysical.Y - clientOffsetY) * cssPerPhysicalY;
                return !double.IsNaN(cssX) && !double.IsNaN(cssY) &&
                       !double.IsInfinity(cssX) && !double.IsInfinity(cssY);
            }
        }

        /// <summary>导航切换时清空实测值，等待新文档重新采样。</summary>
        public void Reset()
        {
            lock (syncRoot)
            {
                metrics = null;
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
                if (topLevel == null || topLevel.Handle == IntPtr.Zero)
                    return Point.Empty;
                if (NativeMethods.GetWindowRect(topLevel.Handle, out NativeMethods.RECT rect) == 0)
                    return Point.Empty;

                var clientOrigin = webView.PointToScreen(Point.Empty);
                return new Point(clientOrigin.X - rect.Left, clientOrigin.Y - rect.Top);
            }
            catch (Exception)
            {
                return Point.Empty;
            }
        }

        private void LogMetrics(PageViewportMetrics measured, Size client, double scaleX, double scaleY, Point offset)
        {
            // devicePixelRatio 是页面自报的理论比例，实测比例来自 CSS 布局尺寸与物理客户区，
            // 两者偏差直接反映 WebView2 DPI 感知/RasterizationScale 的实测差异。
            var dprDerived = IsPositive(measured.DevicePixelRatio) ? 1d / measured.DevicePixelRatio : 0d;
            var deviation = dprDerived > 0 ? Math.Abs(dprDerived - scaleX) / dprDerived : 0d;

            LogThrottled(string.Format(
                "Interaction metrics: dpr={0:0.###}, inner={1:0.##}x{2:0.##}css, client={3}x{4}px, " +
                "cssPerPhysical={5:0.####}/{6:0.####}, dprDerived={7:0.####}, deviation={8:0.###%}, " +
                "visualViewport={9:0.##}x{10:0.##}@{11:0.###}, offset={12},{13}",
                measured.DevicePixelRatio, measured.InnerWidth, measured.InnerHeight, client.Width, client.Height,
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
