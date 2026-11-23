using Lively.Common.Helpers.Pinvoke;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using WebView = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Lively.Player.WebView2.Interaction
{
    /// <summary>
    /// 截获 Core 投递到 WebView2 输入窗口（Chrome_WidgetWin_1）的窗口消息，
    /// 经 InteractionCoordinateMapper 换算后推送交互桥合成 DOM 事件。
    /// 不依赖 contactable.ready；不额外采集全局输入（采集是 Core 的职责）。
    /// </summary>
    internal sealed class InteractionInputPump : IMessageFilter, IDisposable
    {
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_MBUTTONUP = 0x0208;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private const int VK_CONTROL = 0x11;
        private const int VK_SHIFT = 0x10;
        private const int VK_MENU = 0x12;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;

        /// <summary>pointermove 合并窗口（06-interaction-spec.md §8）。</summary>
        private const int MoveFlushIntervalMilliseconds = 16;

        /// <summary>离开轮询周期：指针离开客户区或前台不再是桌面时补发 pointerleave。</summary>
        private const int LeaveWatchIntervalMilliseconds = 200;

        /// <summary>原生 pointerleave 已上报时的抑制窗口，避免与合成兜底双发。</summary>
        private const int NativeLeaveSuppressionMilliseconds = 400;

        private const string InputWindowClass = "Chrome_WidgetWin_1";
        private const string HostWindowClass = "Chrome_WidgetWin_0";

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private readonly WebView webView;
        private readonly InteractionBridge bridge;
        private readonly Action<string> log;
        private readonly Timer moveFlushTimer;
        private readonly Timer leaveWatchTimer;
        private readonly Stopwatch clock = Stopwatch.StartNew();

        private IntPtr inputHandle;
        private bool started;
        private bool disposed;

        private InteractionInputMessage pendingMove;
        private long lastMoveTick;
        private double lastCssX;
        private double lastCssY;
        private bool pointerInside;
        private int buttonMask;
        private DateTime lastMetricsRequestUtc = DateTime.MinValue;

        public InteractionInputPump(WebView webView, InteractionBridge bridge, Action<string> log = null)
        {
            this.webView = webView ?? throw new ArgumentNullException(nameof(webView));
            this.bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            this.log = log;

            moveFlushTimer = new Timer { Interval = MoveFlushIntervalMilliseconds };
            moveFlushTimer.Tick += MoveFlushTimer_Tick;

            leaveWatchTimer = new Timer { Interval = LeaveWatchIntervalMilliseconds };
            leaveWatchTimer.Tick += LeaveWatchTimer_Tick;
        }

        /// <summary>鼠标是否在壁纸窗口内，用于补发 pointerleave。</summary>
        public bool PointerInside => pointerInside;

        /// <summary>是否成功挂上窗口消息过滤器（跨进程输入窗口时为 false）。</summary>
        public bool IsActive => started;

        public void Start()
        {
            if (started || disposed)
                return;

            inputHandle = ResolveInputHandle();
            if (inputHandle == IntPtr.Zero)
            {
                log?.Invoke("Interaction input pump: WebView2 input window not found, window message synthesis disabled");
            }
            // WebView2 的输入窗口可能属于 WebView2 运行时进程（跨进程宿主）。
            // 跨进程窗口的消息由该进程的消息循环分发，Application 消息过滤器看不到；
            // 同时 Chromium 会把这些消息原生转换成 DOM 事件，再合成就会双发。
            else if (!IsOwnedByCurrentProcess(inputHandle))
            {
                log?.Invoke("Interaction input pump: WebView2 input window is cross-process and handled natively by Chromium, window message synthesis disabled");
                inputHandle = IntPtr.Zero;
            }
            else
            {
                Application.AddMessageFilter(this);
                started = true;
                log?.Invoke("Interaction input pump started (window message synthesis)");
            }

            // 离开检测独立于消息过滤器：即使消息合成关闭，也要保证页面能收到 pointerleave。
            // 只做离开边沿判定，不采集/不转发任何 move/click/key。
            leaveWatchTimer.Start();
        }

        public void Stop()
        {
            if (started)
            {
                Application.RemoveMessageFilter(this);
                started = false;
            }

            moveFlushTimer.Stop();
            leaveWatchTimer.Stop();
            pendingMove = null;
            pointerInside = false;
        }

        public bool PreFilterMessage(ref Message m)
        {
            if (!started || disposed)
                return false;

            if (inputHandle == IntPtr.Zero || !NativeMethods.IsWindow(inputHandle))
            {
                inputHandle = ResolveInputHandle();
                if (inputHandle == IntPtr.Zero)
                    return false;
            }

            if (m.HWnd != inputHandle)
                return false;

            switch (m.Msg)
            {
                case WM_MOUSEMOVE:
                    OnMouseMove(ref m);
                    break;
                case WM_LBUTTONDOWN:
                    OnButton(ref m, InteractionProtocol.ButtonLeft, InteractionProtocol.ButtonMaskLeft, true);
                    break;
                case WM_LBUTTONUP:
                    OnButton(ref m, InteractionProtocol.ButtonLeft, InteractionProtocol.ButtonMaskLeft, false);
                    break;
                case WM_RBUTTONDOWN:
                    OnButton(ref m, InteractionProtocol.ButtonRight, InteractionProtocol.ButtonMaskRight, true);
                    break;
                case WM_RBUTTONUP:
                    OnButton(ref m, InteractionProtocol.ButtonRight, InteractionProtocol.ButtonMaskRight, false);
                    break;
                case WM_MBUTTONDOWN:
                    OnButton(ref m, InteractionProtocol.ButtonMiddle, InteractionProtocol.ButtonMaskMiddle, true);
                    break;
                case WM_MBUTTONUP:
                    OnButton(ref m, InteractionProtocol.ButtonMiddle, InteractionProtocol.ButtonMaskMiddle, false);
                    break;
                case WM_MOUSEWHEEL:
                    OnWheel(ref m);
                    break;
                case WM_KEYDOWN:
                case WM_SYSKEYDOWN:
                    OnKey(ref m, true);
                    break;
                case WM_KEYUP:
                case WM_SYSKEYUP:
                    OnKey(ref m, false);
                    break;
            }

            // 不吞消息：保留 Chromium 既有行为，避免影响焦点与输入法。
            return false;
        }

        private void OnMouseMove(ref Message m)
        {
            var point = GetLocalPoint(m.LParam);
            UpdatePointerInside(point);

            if (!TryConvert(point, out var cssX, out var cssY))
                return;

            lastCssX = cssX;
            lastCssY = cssY;
            pendingMove = CreateMessage(InteractionProtocol.KindPointerMove, cssX, cssY,
                InteractionProtocol.ButtonNone, buttonMask);
            EnsureMoveFlush();
        }

        private void OnButton(ref Message m, string button, int mask, bool isDown)
        {
            // 保证按下/抬起的时间顺序晚于已缓存的移动。
            FlushPendingMove();

            if (isDown)
                buttonMask |= mask;
            else
                buttonMask &= ~mask;

            if (!TryConvert(GetLocalPoint(m.LParam), out var cssX, out var cssY))
                return;

            lastCssX = cssX;
            lastCssY = cssY;
            var kind = isDown ? InteractionProtocol.KindPointerDown : InteractionProtocol.KindPointerUp;
            bridge.TryPublishInput(CreateMessage(kind, cssX, cssY, button, buttonMask));
        }

        private void OnWheel(ref Message m)
        {
            FlushPendingMove();

            // Core 沿用显示器局部物理像素写入 lParam；wParam 高字为滚轮增量。
            var delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
            if (!TryConvert(GetLocalPoint(m.LParam), out var cssX, out var cssY))
                return;

            lastCssX = cssX;
            lastCssY = cssY;
            var message = CreateMessage(InteractionProtocol.KindWheel, cssX, cssY,
                InteractionProtocol.ButtonNone, buttonMask);
            message.WheelDelta = delta;
            bridge.TryPublishInput(message);
        }

        private void OnKey(ref Message m, bool isDown)
        {
            FlushPendingMove();

            var virtualKey = m.WParam.ToInt32();
            var message = CreateMessage(isDown ? InteractionProtocol.KindKeyDown : InteractionProtocol.KindKeyUp,
                lastCssX, lastCssY, InteractionProtocol.ButtonNone, buttonMask);
            InteractionKeyMap.Resolve(virtualKey, out var key, out var code);
            message.Key = key;
            message.Code = code;
            message.Modifiers = GetModifiers();
            bridge.TryPublishInput(message);
        }

        private void MoveFlushTimer_Tick(object sender, EventArgs e)
        {
            FlushPendingMove();
        }

        private void EnsureMoveFlush()
        {
            var elapsed = clock.ElapsedMilliseconds - lastMoveTick;
            if (elapsed >= MoveFlushIntervalMilliseconds)
            {
                FlushPendingMove();
                return;
            }

            moveFlushTimer.Interval = (int)(MoveFlushIntervalMilliseconds - elapsed);
            moveFlushTimer.Start();
        }

        private void FlushPendingMove()
        {
            moveFlushTimer.Stop();
            var message = pendingMove;
            pendingMove = null;
            if (message == null)
                return;

            lastMoveTick = clock.ElapsedMilliseconds;
            bridge.TryPublishInput(message);
        }

        private bool TryConvert(Point localPhysical, out double cssX, out double cssY)
        {
            if (bridge.Coordinates.TryToCss(localPhysical, out cssX, out cssY))
                return true;

            // 实测比例尚未就绪：丢弃本次输入而不是猜测比例，并补一次采样。
            RequestMetricsRefresh();
            return false;
        }

        private void RequestMetricsRefresh()
        {
            var now = DateTime.UtcNow;
            if ((now - lastMetricsRequestUtc).TotalSeconds < 2)
                return;

            lastMetricsRequestUtc = now;
            _ = bridge.Coordinates.RefreshAsync();
        }

        private void UpdatePointerInside(Point localPhysical)
        {
            var client = webView.ClientSize;
            var inside = localPhysical.X >= 0 && localPhysical.Y >= 0 &&
                         localPhysical.X < client.Width && localPhysical.Y < client.Height;

            if (!inside)
                return;

            pointerInside = true;
            if (!leaveWatchTimer.Enabled)
                leaveWatchTimer.Start();
        }

        private void LeaveWatchTimer_Tick(object sender, EventArgs e)
        {
            if (disposed || !bridge.IsInitialized)
                return;

            if (!NativeMethods.GetCursorPos(out var cursor))
                return;

            var rect = webView.RectangleToScreen(webView.ClientRectangle);
            var insideClient = cursor.X >= rect.Left && cursor.X < rect.Right &&
                               cursor.Y >= rect.Top && cursor.Y < rect.Bottom;

            // 前台不再是桌面（或本播放器）时 Core 会停止转发，页面同样需要离开复位。
            var inside = insideClient && IsDesktopForeground();

            if (inside)
            {
                pointerInside = true;
                return;
            }

            if (!pointerInside)
                return;

            pointerInside = false;
            buttonMask = 0;

            // 浏览器原生 pointerleave 已上报时跳过合成，避免双发。
            if ((DateTime.UtcNow - bridge.LastNativeLeaveUtc).TotalMilliseconds < NativeLeaveSuppressionMilliseconds)
                return;

            // 粒子动画需要明确的离开信号，否则会停在最后一次指针位置。
            bridge.TryPublishInput(CreateMessage(InteractionProtocol.KindPointerLeave,
                lastCssX, lastCssY, InteractionProtocol.ButtonNone, 0));
        }

        /// <summary>
        /// 前台窗口是否是桌面（Progman/WorkerW）或本播放器自身。
        /// 与 Core 的 IsDesktop 判定一致：前台不是桌面时输入链路会停止转发。
        /// </summary>
        private bool IsDesktopForeground()
        {
            try
            {
                var foreground = NativeMethods.GetForegroundWindow();
                if (foreground == IntPtr.Zero)
                    return false;

                var owner = webView.FindForm()?.Handle ?? IntPtr.Zero;
                if (foreground == owner)
                    return true;

                var className = new StringBuilder(256);
                if (NativeMethods.GetClassName(foreground, className, className.Capacity) <= 0)
                    return false;

                var name = className.ToString();
                return string.Equals(name, "Progman", StringComparison.Ordinal) ||
                       string.Equals(name, "WorkerW", StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private IntPtr ResolveInputHandle()
        {
            try
            {
                var host = NativeMethods.FindWindowEx(webView.Handle, IntPtr.Zero, HostWindowClass, IntPtr.Zero);
                if (host != IntPtr.Zero)
                {
                    var widget = NativeMethods.FindWindowEx(host, IntPtr.Zero, InputWindowClass, IntPtr.Zero);
                    if (widget != IntPtr.Zero)
                        return widget;
                }

                // 找不到输入窗口时不做兜底：Core 同样按该类名定位投递目标，
                // 退回到控件句柄只会得到一条永远不会产生输入的消息过滤器。
                return IntPtr.Zero;
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
        }

        private static bool IsOwnedByCurrentProcess(IntPtr hwnd)
        {
            try
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out int pid);
                return (uint)pid == (uint)Process.GetCurrentProcess().Id;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Point GetLocalPoint(IntPtr lParam)
        {
            var value = lParam.ToInt64();
            var x = (short)(value & 0xFFFF);
            var y = (short)((value >> 16) & 0xFFFF);
            return new Point(x, y);
        }

        private static InteractionInputMessage CreateMessage(string kind, double x, double y, string button, int buttons)
        {
            return new InteractionInputMessage
            {
                Kind = kind,
                X = x,
                Y = y,
                Button = button,
                Buttons = buttons
            };
        }

        private static InteractionModifiers GetModifiers()
        {
            return new InteractionModifiers
            {
                Ctrl = IsKeyDown(VK_CONTROL),
                Shift = IsKeyDown(VK_SHIFT),
                Alt = IsKeyDown(VK_MENU),
                Meta = IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN)
            };
        }

        private static bool IsKeyDown(int virtualKey)
        {
            return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        }

        public void Dispose()
        {
            if (disposed)
                return;

            Stop();
            disposed = true;
            moveFlushTimer.Dispose();
            leaveWatchTimer.Dispose();
        }
    }
}
