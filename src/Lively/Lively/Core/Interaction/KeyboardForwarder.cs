using Lively.Common.Helpers.Pinvoke;
using Lively.Common.Services;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Lively.Core.Interaction
{
    /// <summary>
    /// Keyboard forwarding rules, see docs/contracts/06-interaction-spec.md §9.
    /// The router calls <see cref="ShouldForward"/> for every raw key event and injects
    /// <see cref="GetWindowMessage"/> through the input injector when it returns true.
    /// </summary>
    public class KeyboardForwarder
    {
        private const int GCS_COMPSTR = 0x0008;

        private const int VK_BACK = 0x08;
        private const int VK_TAB = 0x09;
        private const int VK_RETURN = 0x0D;
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_SPACE = 0x20;
        private const int VK_LEFT = 0x25;
        private const int VK_UP = 0x26;
        private const int VK_RIGHT = 0x27;
        private const int VK_DOWN = 0x28;
        private const int VK_LSHIFT = 0xA0;
        private const int VK_RSHIFT = 0xA1;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private const int VK_LMENU = 0xA4;
        private const int VK_RMENU = 0xA5;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const int VK_F4 = 0x73;

        private readonly object syncRoot = new object();
        private readonly IUserSettingsService userSettings;
        // Modifier keys currently held, tracked from raw input so system combos can be detected.
        private readonly HashSet<int> heldModifiers = new HashSet<int>();

        public KeyboardForwarder(IUserSettingsService userSettings)
        {
            this.userSettings = userSettings;
        }

        /// <summary>
        /// Decides whether a raw key event is forwarded to the wallpaper.
        /// Modifier state is updated on every call, including filtered events.
        /// </summary>
        public bool ShouldForward(int virtualKey, bool isKeyDown, bool isSystemKey, bool isDesktopForeground)
        {
            lock (syncRoot)
            {
                UpdateModifierState(virtualKey, isKeyDown);

                if (virtualKey <= 0)
                    return false;

                // Only forwarded when the desktop is in the foreground and the keyboard
                // forwarding switch is on; independent of the mouse forwarding mode.
                if (!isDesktopForeground ||
                    !userSettings.Settings.InteractionForwardKeyboard)
                    return false;

                // Never forward the Windows key or any combo while it is held.
                if (IsWinKey(virtualKey) || heldModifiers.Contains(VK_LWIN) || heldModifiers.Contains(VK_RWIN))
                    return false;

                // Alt+Tab / Alt+F4 belong to the OS.
                if (isSystemKey && IsModifierHeld(VK_MENU, VK_LMENU, VK_RMENU) &&
                    (virtualKey == VK_TAB || virtualKey == VK_F4))
                    return false;

                // Default allow list (letters, digits, arrows, Space, Enter, Esc, Backspace, Tab, modifiers.)
                if (!IsAllowedKey(virtualKey))
                    return false;

                // Pause forwarding while an IME composition is in progress.
                if (IsImeComposing())
                    return false;

                return true;
            }
        }

        /// <summary>
        /// Clears tracked modifier state, call alongside <see cref="DragTracker.Reset"/> on focus loss.
        /// </summary>
        public void Reset()
        {
            lock (syncRoot)
            {
                heldModifiers.Clear();
            }
        }

        /// <summary>
        /// Window message matching the event, so the injected key carries the correct
        /// non-system/system semantics.
        /// </summary>
        public static int GetWindowMessage(bool isKeyDown, bool isSystemKey)
        {
            if (isKeyDown)
                return (int)(isSystemKey ? NativeMethods.WM.SYSKEYDOWN : NativeMethods.WM.KEYDOWN);

            return (int)(isSystemKey ? NativeMethods.WM.SYSKEYUP : NativeMethods.WM.KEYUP);
        }

        private void UpdateModifierState(int virtualKey, bool isKeyDown)
        {
            if (!IsModifierKey(virtualKey))
                return;

            if (isKeyDown)
                heldModifiers.Add(virtualKey);
            else
                heldModifiers.Remove(virtualKey);
        }

        private bool IsModifierHeld(int generic, int left, int right) =>
            heldModifiers.Contains(generic) || heldModifiers.Contains(left) || heldModifiers.Contains(right);

        private static bool IsWinKey(int virtualKey) => virtualKey == VK_LWIN || virtualKey == VK_RWIN;

        private static bool IsModifierKey(int virtualKey) =>
            virtualKey == VK_SHIFT || virtualKey == VK_CONTROL || virtualKey == VK_MENU ||
            virtualKey == VK_LSHIFT || virtualKey == VK_RSHIFT ||
            virtualKey == VK_LCONTROL || virtualKey == VK_RCONTROL ||
            virtualKey == VK_LMENU || virtualKey == VK_RMENU ||
            virtualKey == VK_LWIN || virtualKey == VK_RWIN;

        private static bool IsAllowedKey(int virtualKey)
        {
            // A-Z and 0-9
            if ((virtualKey >= 0x41 && virtualKey <= 0x5A) ||
                (virtualKey >= 0x30 && virtualKey <= 0x39))
                return true;

            switch (virtualKey)
            {
                case VK_BACK:
                case VK_TAB:
                case VK_RETURN:
                case VK_ESCAPE:
                case VK_SPACE:
                case VK_LEFT:
                case VK_UP:
                case VK_RIGHT:
                case VK_DOWN:
                case VK_SHIFT:
                case VK_LSHIFT:
                case VK_RSHIFT:
                case VK_CONTROL:
                case VK_LCONTROL:
                case VK_RCONTROL:
                case VK_MENU:
                case VK_LMENU:
                case VK_RMENU:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// True when the foreground window has a non empty IME composition string.
        /// </summary>
        private static bool IsImeComposing()
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return false;

            var himc = ImmGetContext(hwnd);
            if (himc == IntPtr.Zero)
                return false;

            try
            {
                return ImmGetCompositionString(himc, GCS_COMPSTR, IntPtr.Zero, 0) > 0;
            }
            finally
            {
                ImmReleaseContext(hwnd, himc);
            }
        }

        [DllImport("imm32.dll")]
        private static extern IntPtr ImmGetContext(IntPtr hWnd);

        [DllImport("imm32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

        [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
        private static extern int ImmGetCompositionString(IntPtr hIMC, int dwIndex, IntPtr lpBuf, int dwBufLen);
    }
}
