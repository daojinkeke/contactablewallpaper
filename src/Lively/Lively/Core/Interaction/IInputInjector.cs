using Lively.Models.Interaction;
using System;
using System.Drawing;

namespace Lively.Core.Interaction
{
    /// <summary>
    /// Window message injection, see docs/contracts/05-module-interfaces.md §4.3.
    /// No policy logic belongs here.
    /// </summary>
    public interface IInputInjector
    {
        void MouseMove(IntPtr hwnd, Point localPoint);
        /// <summary>
        /// 指针离开壁纸区域，向原窗口补发 WM_MOUSELEAVE。
        /// </summary>
        void MouseLeave(IntPtr hwnd, Point localPoint);
        void MouseButton(IntPtr hwnd, Point localPoint, InteractionPointerButton button, bool isDown);
        void MouseWheel(IntPtr hwnd, Point localPoint, int delta);
        void Key(IntPtr hwnd, int virtualKey, int scanCode, bool isDown, bool isSystemKey);
    }
}
