using Lively.Common.Helpers;
using Lively.Common.Helpers.Pinvoke;
using Lively.Common.Services;
using Lively.Core.Display;
using Lively.Models;
using Lively.Models.Enums;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace Lively.Core.Interaction
{
    /// <summary>
    /// Resolves the wallpaper under the cursor and converts absolute screen physical pixels
    /// to display local physical pixels (never CSS pixels, that conversion belongs to the Player.)
    /// </summary>
    public class HitTester : IHitTester
    {
        private readonly IDisplayManager displayManager;
        private readonly IUserSettingsService userSettings;
        private readonly Func<IReadOnlyCollection<IWallpaper>> wallpapersProvider;

        /// <param name="wallpapersProvider">
        /// Live wallpaper collection accessor, called per hit test so the result is never stale.
        /// </param>
        public HitTester(IDisplayManager displayManager,
            IUserSettingsService userSettings,
            Func<IReadOnlyCollection<IWallpaper>> wallpapersProvider)
        {
            this.displayManager = displayManager;
            this.userSettings = userSettings;
            this.wallpapersProvider = wallpapersProvider;
        }

        public bool TryHit(Point screenPoint, out IWallpaper wallpaper, out DisplayMonitor display, out Point localPoint)
        {
            wallpaper = null;
            display = null;
            localPoint = default;

            var monitor = displayManager.GetDisplayMonitorFromPoint(screenPoint);
            if (monitor == null)
                return false;

            var wallpapers = wallpapersProvider?.Invoke();
            if (wallpapers == null || wallpapers.Count == 0)
                return false;

            var arrangement = userSettings.Settings.WallpaperArrangement;

            var target = wallpapers.FirstOrDefault(x => monitor.Equals(x.Screen));
            if (target == null && arrangement == WallpaperArrangement.span)
            {
                // Span: a single instance covers the virtual screen, its Screen may not be the hit monitor.
                target = wallpapers.FirstOrDefault();
            }
            if (target == null)
                return false;

            // Contract 06 §5.1: per/duplicate use the hit display, span uses the virtual screen origin.
            // 以目标窗口的真实屏幕原点优先：显示器变更、跨屏重排或窗口被重新定位后，
            // 按显示器/虚拟屏 bounds 推算会与窗口实际位置不一致，指针会整体偏移。
            // 取窗口原点时，Player 侧只需再扣客户区偏移，链路两端始终对齐同一原点。
            var origin = GetLocalOrigin(target, monitor, arrangement);
            localPoint = new Point(screenPoint.X - origin.X, screenPoint.Y - origin.Y);

            wallpaper = target;
            display = monitor;
            return true;
        }

        /// <summary>
        /// 局部坐标原点：优先取壁纸窗口真实屏幕位置，取不到时退回显示器/虚拟屏原点。
        /// </summary>
        private Point GetLocalOrigin(IWallpaper target, DisplayMonitor monitor, WallpaperArrangement arrangement)
        {
            try
            {
                if (!IntPtr.Equals(target.Handle, IntPtr.Zero) &&
                    NativeMethods.GetWindowRect(target.Handle, out var rect) != 0 &&
                    rect.Right > rect.Left && rect.Bottom > rect.Top)
                {
                    return new Point(rect.Left, rect.Top);
                }
            }
            catch (Exception)
            {
                // 句柄已失效：退回按显示器推算，保持原有行为。
            }

            return arrangement == WallpaperArrangement.span
                ? displayManager.VirtualScreenBounds.Location
                : monitor.Bounds.Location;
        }
    }
}
