using Lively.Common.Helpers;
using Lively.Models.Interaction;
using System;
using System.Drawing;

namespace Lively.Core.Interaction
{
    /// <summary>
    /// Forwards interaction events to the wallpaper input window through PostMessage.
    /// </summary>
    public class InputInjector : IInputInjector
    {
        public void MouseMove(IntPtr hwnd, Point localPoint)
        {
            if (hwnd == IntPtr.Zero)
                return;

            InputUtil.MouseMove(hwnd, localPoint.X, localPoint.Y);
        }

        public void MouseLeave(IntPtr hwnd, Point localPoint)
        {
            if (hwnd == IntPtr.Zero)
                return;

            InputUtil.MouseLeave(hwnd, localPoint.X, localPoint.Y);
        }

        public void MouseButton(IntPtr hwnd, Point localPoint, InteractionPointerButton button, bool isDown)
        {
            if (hwnd == IntPtr.Zero)
                return;

            switch (button)
            {
                case InteractionPointerButton.left:
                    if (isDown)
                        InputUtil.MouseLeftButtonDown(hwnd, localPoint.X, localPoint.Y);
                    else
                        InputUtil.MouseLeftButtonUp(hwnd, localPoint.X, localPoint.Y);
                    break;
                case InteractionPointerButton.right:
                    if (isDown)
                        InputUtil.MouseRightButtonDown(hwnd, localPoint.X, localPoint.Y);
                    else
                        InputUtil.MouseRightButtonUp(hwnd, localPoint.X, localPoint.Y);
                    break;
                case InteractionPointerButton.middle:
                    if (isDown)
                        InputUtil.MouseMiddleButtonDown(hwnd, localPoint.X, localPoint.Y);
                    else
                        InputUtil.MouseMiddleButtonUp(hwnd, localPoint.X, localPoint.Y);
                    break;
            }
        }

        public void MouseWheel(IntPtr hwnd, Point localPoint, int delta)
        {
            if (hwnd == IntPtr.Zero)
                return;

            InputUtil.MouseWheel(hwnd, localPoint.X, localPoint.Y, delta);
        }

        public void Key(IntPtr hwnd, int virtualKey, int scanCode, bool isDown, bool isSystemKey)
        {
            if (hwnd == IntPtr.Zero)
                return;

            var message = KeyboardForwarder.GetWindowMessage(isDown, isSystemKey);
            InputUtil.ForwardMessageKeyboard(hwnd, message, (IntPtr)virtualKey, scanCode, isDown);
        }
    }
}
