using Lively.Models;
using System.Drawing;

namespace Lively.Core.Interaction
{
    /// <summary>
    /// Hit test and display local coordinate conversion,
    /// see docs/contracts/05-module-interfaces.md §4.2 and docs/contracts/06-interaction-spec.md §5.
    /// </summary>
    public interface IHitTester
    {
        bool TryHit(Point screenPoint, out IWallpaper wallpaper, out DisplayMonitor display, out Point localPoint);
    }
}
