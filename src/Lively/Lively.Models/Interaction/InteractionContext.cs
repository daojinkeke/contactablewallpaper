using Lively.Models.Enums;
using System.Drawing;

namespace Lively.Models.Interaction
{
    /// <summary>
    /// Input context evaluated by <c>Lively.Core.Interaction.IInteractionPolicy</c>,
    /// see docs/contracts/05-module-interfaces.md §4.1.
    /// </summary>
    /// <remarks>
    /// 契约位置：docs/contracts/04-shared-models.md §4 规定交互模型放在 Lively.Models/Interaction。
    /// Core 侧不得再定义同名类型。
    /// </remarks>
    public class InteractionContext
    {
        /// <summary>
        /// Wallpaper category of the hit instance.
        /// </summary>
        public WallpaperType Category { get; set; }

        /// <summary>
        /// Display under the cursor.
        /// </summary>
        public DisplayMonitor Display { get; set; }

        /// <summary>
        /// Desktop (WorkerW/Progman) is the foreground window.
        /// </summary>
        public bool IsDesktopForeground { get; set; }

        /// <summary>
        /// A fullscreen application is active.
        /// </summary>
        public bool IsFullscreenAppActive { get; set; }

        /// <summary>
        /// System is running on battery power.
        /// </summary>
        public bool IsOnBattery { get; set; }

        /// <summary>
        /// Absolute screen physical pixel position of the event.
        /// </summary>
        public Point ScreenPoint { get; set; }
    }
}
