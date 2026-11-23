using Lively.Common.Helpers;
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
            localPoint = arrangement == WallpaperArrangement.span
                ? InputUtil.ToMouseSpanLocal(screenPoint.X, screenPoint.Y, displayManager.VirtualScreenBounds)
                : InputUtil.ToMouseDisplayLocal(screenPoint.X, screenPoint.Y, monitor.Bounds);

            wallpaper = target;
            display = monitor;
            return true;
        }
    }
}
