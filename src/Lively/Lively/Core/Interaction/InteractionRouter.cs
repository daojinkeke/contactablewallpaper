using Lively.Common.Helpers.Pinvoke;
using Lively.Common.Services;
using Lively.Models;
using Lively.Models.Enums;
using Lively.Models.Interaction;
using System;
using System.Drawing;
using System.Threading;

namespace Lively.Core.Interaction
{
    /// <summary>
    /// Aggregates raw input into policy decision, hit test and injection,
    /// see docs/contracts/06-interaction-spec.md §4.2 and §8-§10.
    /// </summary>
    /// <remarks>
    /// Called from the RawInput message thread. Every event is isolated, a single failing
    /// event must never break the input loop or other wallpaper instances.
    /// </remarks>
    public class InteractionRouter : IInteractionRouter
    {
        private const int MoveIntervalMilliseconds = 16;      // 60 Hz desktop
        private const int BatteryMoveIntervalMilliseconds = 33; // 30 Hz on battery
        private const int ErrorLogIntervalMilliseconds = 10000;

        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        private readonly IInteractionPolicy policy;
        private readonly IHitTester hitTester;
        private readonly IInputInjector injector;
        private readonly IUserSettingsService userSettings;
        private readonly DragTracker dragTracker;
        private readonly KeyboardForwarder keyboardForwarder;
        private readonly Func<bool> isDesktopForeground;
        private readonly Func<bool> isFullscreenAppActive;
        private readonly Func<bool> isOnBattery;
        // Reused to keep the hot path allocation free (policy only reads it.)
        private readonly InteractionContext context = new InteractionContext();

        private long lastMoveTick;
        private bool moveTickInitialized;
        private long errorCount;
        private long droppedCount;
        private long lastErrorLogTick;
        // 每类事件首次成功注入时记一条日志，便于真机确认链路已通，之后不再刷屏。
        private bool loggedMove;
        private bool loggedButton;
        private bool loggedWheel;
        private bool loggedKey;
        private bool loggedLeave;
        // 当前指针所在的壁纸输入窗口，用于在离开时补发 WM_MOUSELEAVE。
        private IntPtr hoveredHandle;
        private Point hoveredLocalPoint;

        public InteractionRouter(IInteractionPolicy policy,
            IHitTester hitTester,
            IInputInjector injector,
            IUserSettingsService userSettings,
            Func<bool> isDesktopForeground,
            Func<bool> isFullscreenAppActive,
            Func<bool> isOnBattery)
        {
            this.policy = policy;
            this.hitTester = hitTester;
            this.injector = injector;
            this.userSettings = userSettings;
            this.isDesktopForeground = isDesktopForeground;
            this.isFullscreenAppActive = isFullscreenAppActive;
            this.isOnBattery = isOnBattery;
            dragTracker = new DragTracker();
            keyboardForwarder = new KeyboardForwarder(userSettings);
        }

        /// <summary>
        /// Events dropped because the target input handle was not available.
        /// </summary>
        public long DroppedCount => Interlocked.Read(ref droppedCount);

        /// <summary>
        /// Events whose handling threw, the exception never leaves the router.
        /// </summary>
        public long ErrorCount => Interlocked.Read(ref errorCount);

        public void OnMouseMove(Point screenPoint)
        {
            try
            {
                var settings = userSettings.Settings;
                if (!settings.InteractionEnabled || settings.InputForward == InputForwardMode.off)
                {
                    LeaveHovered();
                    return;
                }

                if (!hitTester.TryHit(screenPoint, out var wallpaper, out var display, out var localPoint))
                {
                    LeaveHovered();
                    return;
                }

                // Track the position even when the event is filtered so Reset() replays the latest point.
                dragTracker.Move(localPoint);

                var onBattery = GetOnBattery();
                if (!policy.ShouldForward(FillContext(wallpaper, display, screenPoint, onBattery)))
                {
                    // 前台切到其它应用、全屏/电池规则阻断时，页面也要收到离开复位，
                    // 否则指针会停在最后一次注入的位置。hoveredHandle 已清空，
                    // 后续被阻断的移动不会重复补发。
                    LeaveHovered();
                    return;
                }

                // 指针换到另一张壁纸（或从无到有）时，先向原窗口补发离开事件。
                if (hoveredHandle != wallpaper.InputHandle)
                {
                    LeaveHovered();
                    hoveredHandle = wallpaper.InputHandle;
                }
                hoveredLocalPoint = localPoint;

                var handle = wallpaper.InputHandle;
                if (handle == IntPtr.Zero)
                {
                    Interlocked.Increment(ref droppedCount);
                    return;
                }

                if (ShouldThrottleMove(onBattery))
                    return;

                injector.MouseMove(handle, localPoint);
                LogFirst(ref loggedMove, "move", handle);
            }
            catch (Exception ex)
            {
                RecordError(ex);
            }
        }

        public void OnMouseButton(Point screenPoint, InteractionPointerButton button, bool isDown)
        {
            try
            {
                if (button == InteractionPointerButton.none)
                    return;

                if (!isDown)
                {
                    HandleButtonUp(screenPoint, button);
                    return;
                }

                var settings = userSettings.Settings;
                if (!settings.InteractionEnabled || !IsMouseButtonForwarded(settings, button))
                    return;

                if (!hitTester.TryHit(screenPoint, out var wallpaper, out var display, out var localPoint))
                    return;

                if (!policy.ShouldForward(FillContext(wallpaper, display, screenPoint, GetOnBattery())))
                    return;

                var handle = wallpaper.InputHandle;
                if (handle == IntPtr.Zero)
                {
                    Interlocked.Increment(ref droppedCount);
                    return;
                }

                if (!dragTracker.Press(button, localPoint, handle))
                    return;

                injector.MouseButton(handle, localPoint, button, true);
                LogFirst(ref loggedButton, $"button({button}) down", handle);
            }
            catch (Exception ex)
            {
                RecordError(ex);
            }
        }

        public void OnMouseWheel(Point screenPoint, int delta)
        {
            try
            {
                var settings = userSettings.Settings;
                if (!settings.InteractionEnabled || !settings.InteractionForwardWheel ||
                    settings.InputForward == InputForwardMode.off || delta == 0)
                    return;

                if (!hitTester.TryHit(screenPoint, out var wallpaper, out var display, out var localPoint))
                    return;

                if (!policy.ShouldForward(FillContext(wallpaper, display, screenPoint, GetOnBattery())))
                    return;

                var handle = wallpaper.InputHandle;
                if (handle == IntPtr.Zero)
                {
                    Interlocked.Increment(ref droppedCount);
                    return;
                }

                injector.MouseWheel(handle, localPoint, delta);
                LogFirst(ref loggedWheel, $"wheel({delta})", handle);
            }
            catch (Exception ex)
            {
                RecordError(ex);
            }
        }

        public void OnKey(int virtualKey, int scanCode, bool isDown, bool isSystemKey)
        {
            try
            {
                if (virtualKey <= 0)
                    return;

                // 修饰键状态必须始终更新，否则关闭再打开总开关后会残留按住状态。
                var shouldForward = keyboardForwarder.ShouldForward(virtualKey, isDown, isSystemKey, GetIsDesktopForeground());

                // I-01：总开关关闭时不注入任何键盘事件。
                if (!userSettings.Settings.InteractionEnabled)
                    return;

                if (!shouldForward)
                    return;

                if (!NativeMethods.GetCursorPos(out var screenPoint))
                {
                    Interlocked.Increment(ref droppedCount);
                    return;
                }

                if (!hitTester.TryHit(screenPoint, out var wallpaper, out var display, out _))
                    return;

                if (!policy.ShouldForward(FillContext(wallpaper, display, screenPoint, GetOnBattery())))
                    return;

                var handle = wallpaper.InputHandle;
                if (handle == IntPtr.Zero)
                {
                    Interlocked.Increment(ref droppedCount);
                    return;
                }

                injector.Key(handle, virtualKey, scanCode, isDown, isSystemKey);
                LogFirst(ref loggedKey, $"key(0x{virtualKey:X}) down={isDown}", handle);
            }
            catch (Exception ex)
            {
                RecordError(ex);
            }
        }

        public void Reset()
        {
            try
            {
                // 壁纸窗口即将销毁或焦点已丢失，先向它补发离开，避免页面指针悬停状态残留。
                LeaveHovered();

                var releases = dragTracker.Reset();
                foreach (var release in releases)
                {
                    try
                    {
                        if (release.TargetHandle == IntPtr.Zero)
                        {
                            Interlocked.Increment(ref droppedCount);
                            continue;
                        }

                        injector.MouseButton(release.TargetHandle, release.Point, release.Button, false);
                    }
                    catch (Exception ex)
                    {
                        RecordError(ex);
                    }
                }

                keyboardForwarder.Reset();
            }
            catch (Exception ex)
            {
                RecordError(ex);
            }
        }

        private void HandleButtonUp(Point screenPoint, InteractionPointerButton button)
        {
            if (!dragTracker.IsPressed(button))
                return;

            // Release goes to the wallpaper that received the down, even when the cursor
            // has left it, otherwise the page keeps a stuck button.
            var handle = dragTracker.GetPressedHandle(button);
            var localPoint = dragTracker.LastPoint;

            if (hitTester.TryHit(screenPoint, out var hitWallpaper, out _, out var hitLocalPoint) &&
                hitWallpaper != null && hitWallpaper.InputHandle == handle)
            {
                localPoint = hitLocalPoint;
            }

            if (!dragTracker.Release(button, localPoint))
                return;

            if (handle == IntPtr.Zero)
            {
                Interlocked.Increment(ref droppedCount);
                return;
            }

            injector.MouseButton(handle, localPoint, button, false);
        }

        private InteractionContext FillContext(IWallpaper wallpaper, DisplayMonitor display, Point screenPoint, bool onBattery)
        {
            context.Category = wallpaper.Category;
            context.Display = display;
            context.IsDesktopForeground = GetIsDesktopForeground();
            context.IsFullscreenAppActive = GetIsFullscreenAppActive();
            context.IsOnBattery = onBattery;
            context.ScreenPoint = screenPoint;
            return context;
        }

        private void LeaveHovered()
        {
            if (hoveredHandle == IntPtr.Zero)
                return;

            var handle = hoveredHandle;
            hoveredHandle = IntPtr.Zero;

            try
            {
                injector.MouseLeave(handle, hoveredLocalPoint);
                LogFirst(ref loggedLeave, "leave", handle);
            }
            catch (Exception ex)
            {
                RecordError(ex);
            }
        }

        private static void LogFirst(ref bool flag, string kind, IntPtr handle)
        {
            if (flag)
                return;

            flag = true;
            Logger.Info($"Interaction forwarded: {kind} -> 0x{handle.ToInt64():X}");
        }

        /// <summary>
        /// 记录失败并限频写日志：异常不向上冒泡打断输入循环，也不能静默吞掉。
        /// </summary>
        private void RecordError(Exception ex)
        {
            Interlocked.Increment(ref errorCount);

            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref lastErrorLogTick) < ErrorLogIntervalMilliseconds)
                return;

            Interlocked.Exchange(ref lastErrorLogTick, now);
            Logger.Error(ex, "Interaction routing failed");
        }

        private bool ShouldThrottleMove(bool onBattery)
        {
            var now = Environment.TickCount64;
            var interval = onBattery ? BatteryMoveIntervalMilliseconds : MoveIntervalMilliseconds;

            if (moveTickInitialized && now - lastMoveTick < interval)
                return true;

            lastMoveTick = now;
            moveTickInitialized = true;
            return false;
        }

        private static bool IsMouseButtonForwarded(SettingsModel settings, InteractionPointerButton button)
        {
            if (settings.InputForward == InputForwardMode.off)
                return false;

            switch (button)
            {
                case InteractionPointerButton.left:
                case InteractionPointerButton.right:
                    return true;
                case InteractionPointerButton.middle:
                    // 中键有独立细分开关，默认 false。
                    return settings.InteractionForwardMiddleButton;
                default:
                    return false;
            }
        }

        private bool GetIsDesktopForeground() => isDesktopForeground?.Invoke() ?? false;

        private bool GetIsFullscreenAppActive() => isFullscreenAppActive?.Invoke() ?? false;

        private bool GetOnBattery() => isOnBattery?.Invoke() ?? false;
    }
}
