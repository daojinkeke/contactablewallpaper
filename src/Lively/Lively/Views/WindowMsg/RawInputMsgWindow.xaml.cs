using Linearstar.Windows.RawInput;
using Lively.Common.Helpers.Pinvoke;
using Lively.Models.Enums;
using System;
using System.Windows;
using System.Windows.Interop;
using RawMouseButtonFlags = Linearstar.Windows.RawInput.Native.RawMouseButtonFlags;

namespace Lively.Views.WindowMsg
{
    /// <summary>
    /// DirectX rawinput hook.
    /// Ref: https://docs.microsoft.com/en-us/windows/win32/inputdev/raw-input
    /// </summary>
    public partial class RawInputMsgWindow : Window
    {
        public InputForwardMode InputMode { get; private set; }
        //public events
        public event EventHandler<MouseRawArgs> MouseMoveRaw;
        public event EventHandler<MouseClickRawArgs> MouseDownRaw;
        public event EventHandler<MouseClickRawArgs> MouseUpRaw;
        public event EventHandler<MouseWheelRawArgs> MouseWheelRaw;
        public event EventHandler<KeyboardClickRawArgs> KeyboardClickRaw;

        public RawInputMsgWindow()
        {
            InitializeComponent();

            this.InputMode = InputForwardMode.mousekeyboard;
        }

        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            var windowInteropHelper = new WindowInteropHelper(this);
            var hwnd = windowInteropHelper.Handle;

            switch (InputMode)
            {
                case InputForwardMode.off:
                    this.Close();
                    break;
                case InputForwardMode.mouse:
                    //ExInputSink flag makes it work even when not in foreground and async..
                    RawInputDevice.RegisterDevice(HidUsageAndPage.Mouse,
                        RawInputDeviceFlags.ExInputSink, hwnd);
                    break;
                case InputForwardMode.mousekeyboard:
                    RawInputDevice.RegisterDevice(HidUsageAndPage.Mouse,
                        RawInputDeviceFlags.ExInputSink, hwnd);
                    RawInputDevice.RegisterDevice(HidUsageAndPage.Keyboard,
                        RawInputDeviceFlags.ExInputSink, hwnd);
                    break;
            }

            HwndSource source = HwndSource.FromHwnd(hwnd);
            source.AddHook(Hook);
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            switch (InputMode)
            {
                case InputForwardMode.off:
                    break;
                case InputForwardMode.mouse:
                    RawInputDevice.UnregisterDevice(HidUsageAndPage.Mouse);
                    break;
                case InputForwardMode.mousekeyboard:
                    RawInputDevice.UnregisterDevice(HidUsageAndPage.Mouse);
                    RawInputDevice.UnregisterDevice(HidUsageAndPage.Keyboard);
                    break;
            }
        }

        protected IntPtr Hook(IntPtr hwnd, int msg, IntPtr wparam, IntPtr lparam, ref bool handled)
        {
            // You can read inputs by processing the WM_INPUT message.
            if (msg == (int)NativeMethods.WM.INPUT)
            {
                // Create an RawInputData from the handle stored in lParam.
                var data = RawInputData.FromHandle(lparam);

                //You can identify the source device using Header.DeviceHandle or just Device.
                //var sourceDeviceHandle = data.Header.DeviceHandle;
                //var sourceDevice = data.Device;

                // The data will be an instance of either RawInputMouseData, RawInputKeyboardData, or RawInputHidData.
                // They contain the raw input data in their properties.
                switch (data)
                {
                    case RawInputMouseData mouse:
                        //RawInput only gives relative mouse movement value.. 
                        if (!NativeMethods.GetCursorPos(out NativeMethods.POINT P))
                        {
                            break;
                        }

                        // A single raw packet can carry multiple button/wheel flags, so test each flag
                        // instead of switching on the exact value.
                        var buttons = mouse.Mouse.Buttons;

                        if ((buttons & RawMouseButtonFlags.LeftButtonDown) != 0)
                        {
                            MouseDownRaw?.Invoke(this, new MouseClickRawArgs(P.X, P.Y, RawInputMouseBtn.left));
                        }
                        if ((buttons & RawMouseButtonFlags.LeftButtonUp) != 0)
                        {
                            MouseUpRaw?.Invoke(this, new MouseClickRawArgs(P.X, P.Y, RawInputMouseBtn.left));
                        }
                        if ((buttons & RawMouseButtonFlags.RightButtonDown) != 0)
                        {
                            MouseDownRaw?.Invoke(this, new MouseClickRawArgs(P.X, P.Y, RawInputMouseBtn.right));
                        }
                        if ((buttons & RawMouseButtonFlags.RightButtonUp) != 0)
                        {
                            MouseUpRaw?.Invoke(this, new MouseClickRawArgs(P.X, P.Y, RawInputMouseBtn.right));
                        }
                        if ((buttons & RawMouseButtonFlags.MiddleButtonDown) != 0)
                        {
                            MouseDownRaw?.Invoke(this, new MouseClickRawArgs(P.X, P.Y, RawInputMouseBtn.middle));
                        }
                        if ((buttons & RawMouseButtonFlags.MiddleButtonUp) != 0)
                        {
                            MouseUpRaw?.Invoke(this, new MouseClickRawArgs(P.X, P.Y, RawInputMouseBtn.middle));
                        }
                        if ((buttons & RawMouseButtonFlags.MouseWheel) != 0)
                        {
                            /*
                            Mouse wheel deltas are represented as multiples of 120.
                            MSDN: The delta was set to 120 to allow Microsoft or other vendors to build
                            finer-resolution wheels (a freely-rotating wheel with no notches) to send more
                            messages per rotation, but with a smaller value in each message.
                            Ref: https://docs.microsoft.com/en-us/windows/win32/inputdev/wm-mousewheel
                            */
                            MouseWheelRaw?.Invoke(this, new MouseWheelRawArgs(P.X, P.Y, mouse.Mouse.ButtonData));
                        }
                        if (buttons == RawMouseButtonFlags.None)
                        {
                            MouseMoveRaw?.Invoke(this, new MouseRawArgs(P.X, P.Y));
                        }
                        break;
                    case RawInputKeyboardData keyboard:
                        {
                            KeyboardClickRaw?.Invoke(this,
                                new KeyboardClickRawArgs((int)keyboard.Keyboard.WindowMessage,
                                    (IntPtr)keyboard.Keyboard.VirutalKey,
                                    keyboard.Keyboard.ScanCode,
                                    (keyboard.Keyboard.Flags != Linearstar.Windows.RawInput.Native.RawKeyboardFlags.Up)));
                        }
                        break;
                }
            }
            return IntPtr.Zero;
        }
    }

    public enum RawInputMouseBtn
    {
        left,
        right,
        middle
    }

    public class MouseRawArgs : EventArgs
    {
        public int X { get; }
        public int Y { get; }
        public MouseRawArgs(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    public class MouseWheelRawArgs : MouseRawArgs
    {
        /// <summary>
        /// Wheel delta in multiples of WHEEL_DELTA (120), sign matches Windows.
        /// </summary>
        public int Delta { get; }

        public MouseWheelRawArgs(int x, int y, int delta) : base(x, y)
        {
            Delta = delta;
        }
    }

    public class MouseClickRawArgs : MouseRawArgs
    {
        public RawInputMouseBtn Button { get; }
        public MouseClickRawArgs(int x, int y, RawInputMouseBtn btn) : base(x, y)
        {
            Button = btn;
        }
    }

    public class KeyboardClickRawArgs : EventArgs
    {
        /// <summary>
        /// The Windows message (WM_KEYDOWN, WM_KEYUP, etc.)
        /// </summary>
        public int WindowMessage { get; }

        /// <summary>
        /// The virtual key code.
        /// </summary>
        public IntPtr VirtualKey { get; }

        /// <summary>
        /// The hardware scan code.
        /// </summary>
        public int ScanCode { get; }

        /// <summary>
        /// True if this is a key down, false if key up.
        /// </summary>
        public bool IsKeyDown { get; }

        public KeyboardClickRawArgs(int windowMessage, IntPtr virtualKey, int scanCode, bool isKeyDown)
        {
            WindowMessage = windowMessage;
            VirtualKey = virtualKey;
            ScanCode = scanCode;
            IsKeyDown = isKeyDown;
        }
    }
}
