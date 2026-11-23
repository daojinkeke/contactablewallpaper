using Lively.Models.Interaction;
using System.Drawing;

namespace Lively.Core.Interaction
{
    /// <summary>
    /// Interaction event aggregation point, see docs/contracts/05-module-interfaces.md §4.4.
    /// </summary>
    public interface IInteractionRouter
    {
        void OnMouseMove(Point screenPoint);
        void OnMouseButton(Point screenPoint, InteractionPointerButton button, bool isDown);
        void OnMouseWheel(Point screenPoint, int delta);
        void OnKey(int virtualKey, int scanCode, bool isDown, bool isSystemKey);
        void Reset(); // 焦点丢失/会话切换时补发 Up 并清理拖拽状态
    }
}
