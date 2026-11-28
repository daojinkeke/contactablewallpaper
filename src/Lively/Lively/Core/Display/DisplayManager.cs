// Copyright (c) 2020 Shankar
// The Shankar licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
// Source: https://github.com/ModernFlyouts-Community/ModernFlyouts

using CommunityToolkit.Mvvm.ComponentModel;
using Lively.Common.Helpers.Pinvoke;
using Lively.Models;
using Lively.Views.WindowMsg;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;

// Ref:
// https://github.com/micdenny/WpfScreenHelper
// http://referencesource.microsoft.com/#System.Windows.Forms/ndp/fx/src/winforms/Managed/System/WinForms/Screen.cs
// http://msdn.microsoft.com/en-us/library/windows/desktop/dd145072.aspx
// http://msdn.microsoft.com/en-us/library/windows/desktop/dd183314.aspx
namespace Lively.Core.Display
{
    public class DisplayManager : ObservableObject, IDisplayManager //,IWndProcHookHandler
    {
        public event EventHandler DisplayUpdated;

        public ObservableCollection<DisplayMonitor> DisplayMonitors { get; } = new ObservableCollection<DisplayMonitor>();

        // This identifier is just for us, so that we don't try to call the multimon
        // functions if we just need the primary monitor... this is safer for
        // non-multimon OSes.
        private const int PRIMARY_MONITOR = unchecked((int)0xBAADF00D);
        private const int MONITORINFOF_PRIMARY = 0x00000001;
        private const int MONITOR_DEFAULTTONEAREST = 0x00000002;
        private const string defaultDisplayDeviceName = "DISPLAY";

        // Indicates if we have more than one monitor.
        private bool multiMonitorSupport;

        private Rectangle virtualScreenBounds = Rectangle.Empty;

        public Rectangle VirtualScreenBounds
        {
            get => virtualScreenBounds;
            private set => SetProperty(ref virtualScreenBounds, value);
        }

        public DisplayMonitor PrimaryDisplayMonitor => DisplayMonitors
            .FirstOrDefault(x => x.IsPrimary);

        private readonly WndProcMsgWindow wndProc;

        public DisplayManager(WndProcMsgWindow wndProc)
        {
            this.wndProc = wndProc;
            this.wndProc.WindowMessageReceived += WndProc_WindowMessageReceived;

            RefreshDisplayMonitorList();
        }

        private void WndProc_WindowMessageReceived(object sender, WindowMessageEventArgs args)
        {
            if (args.Message == (uint)NativeMethods.WM.DISPLAYCHANGE)
            //|| (msg == (uint)NativeMethods.WM.SETTINGCHANGE && wParam == ((IntPtr)NativeMethods.SPI.SPI_SETWORKAREA)))
            {
                RefreshDisplayMonitorList();
            }
        }

        public uint OnHwndCreated(IntPtr hWnd, out bool register)
        {
            register = false;
            return 0;
        }

        public DisplayMonitor GetDisplayMonitorFromHWnd(IntPtr hWnd)
        {
            IntPtr hMonitor = multiMonitorSupport
                ? NativeMethods.MonitorFromWindow(new HandleRef(null, hWnd), MONITOR_DEFAULTTONEAREST)
                : (IntPtr)PRIMARY_MONITOR;

            return GetDisplayMonitorFromHMonitor(hMonitor);
        }

        public DisplayMonitor GetDisplayMonitorFromPoint(Point point)
        {
            IntPtr hMonitor;
            if (multiMonitorSupport)
            {
                var pt = new NativeMethods.POINT(  //POINTSTRUCT
                    (int)Math.Round((double)point.X),
                    (int)Math.Round((double)point.Y));
                hMonitor = NativeMethods.MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            }
            else
                hMonitor = (IntPtr)PRIMARY_MONITOR;

            return GetDisplayMonitorFromHMonitor(hMonitor);
        }

        private void RefreshDisplayMonitorList()
        {
            multiMonitorSupport = NativeMethods.GetSystemMetrics((int)NativeMethods.SystemMetric.SM_CMONITORS) != 0;

            var hMonitors = GetHMonitors();

            foreach (var displayMonitor in DisplayMonitors)
            {
                displayMonitor.isStale = true;
            }

            for (int i = 0; i < hMonitors.Count; i++)
            {
                var displayMonitor = GetDisplayMonitorFromHMonitor(hMonitors[i]);
                displayMonitor.Index = i + 1;
            }

            var staleDisplayMonitors = DisplayMonitors
                .Where(x => x.isStale).ToList();
            foreach (var displayMonitor in staleDisplayMonitors)
            {
                DisplayMonitors.Remove(displayMonitor);
            }

            staleDisplayMonitors.Clear();
            staleDisplayMonitors = null;

            VirtualScreenBounds = GetVirtualScreenBounds();

            DisplayUpdated?.Invoke(this, EventArgs.Empty);
        }

        private DisplayMonitor GetDisplayMonitorFromHMonitor(IntPtr hMonitor)
        {
            DisplayMonitor displayMonitor = null;

            if (!multiMonitorSupport || hMonitor == (IntPtr)PRIMARY_MONITOR)
            {
                displayMonitor = GetDisplayMonitorByDeviceName(defaultDisplayDeviceName);

                if (displayMonitor == null)
                {
                    displayMonitor = new DisplayMonitor(defaultDisplayDeviceName);
                    DisplayMonitors.Add(displayMonitor);
                }

                displayMonitor.Bounds = GetVirtualScreenBounds();
                displayMonitor.DeviceId = string.Empty;
                displayMonitor.DisplayName = "Display";
                displayMonitor.HMonitor = hMonitor;
                displayMonitor.IsPrimary = true;
                displayMonitor.WorkingArea = GetWorkingArea();

                displayMonitor.isStale = false;
            }
            else
            {
                var info = new NativeMethods.MONITORINFOEX();
                NativeMethods.GetMonitorInfo(new HandleRef(null, hMonitor), info);

                string deviceName = new string(info.szDevice).TrimEnd((char)0);

                displayMonitor = GetDisplayMonitorByDeviceName(deviceName);

                displayMonitor ??= CreateDisplayMonitorFromMonitorInfo(deviceName);

                displayMonitor.HMonitor = hMonitor;

                UpdateDisplayMonitor(displayMonitor, info);
            }

            if (string.IsNullOrWhiteSpace(displayMonitor.DeviceId))
                displayMonitor.DeviceId = GetDefaultDisplayDeviceId(displayMonitor.Bounds);

            // 身份唯一性兜底：部分显卡/镜像拓扑下 EnumDisplayDevices 会给多台显示器返回同一个
            // 接口名，边界哈希在相同边界下也可能撞值。一旦重复，所有按 DeviceId 精确匹配的
            // 逻辑（选屏应用、关闭壁纸、窗口重排）都只会命中第一台显示器。
            EnsureUniqueDeviceId(displayMonitor);

            return displayMonitor;
        }

        /// <summary>
        /// 保证当前会话内每台显示器拥有唯一 DeviceId。
        /// </summary>
        private void EnsureUniqueDeviceId(DisplayMonitor displayMonitor)
        {
            if (string.IsNullOrWhiteSpace(displayMonitor.DeviceId))
                return;

            var conflict = DisplayMonitors.FirstOrDefault(x => !ReferenceEquals(x, displayMonitor)
                && string.Equals(x.DeviceId, displayMonitor.DeviceId, StringComparison.OrdinalIgnoreCase));
            if (conflict == null)
                return;

            var suffix = NormalizeDeviceName(displayMonitor.DeviceName);
            if (string.IsNullOrEmpty(suffix))
                suffix = $"{displayMonitor.Bounds.X}-{displayMonitor.Bounds.Y}-{displayMonitor.Bounds.Width}-{displayMonitor.Bounds.Height}";

            displayMonitor.DeviceId = $"{displayMonitor.DeviceId}#{suffix}";
        }

        private static string NormalizeDeviceName(string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName))
                return string.Empty;

            return deviceName
                .Replace("\\\\?\\", string.Empty)
                .Replace("\\\\.\\", string.Empty)
                .Replace('\\', '_')
                .TrimEnd('_');
        }

        /// <summary>
        /// 按外部传入的显示器标识解析当前会话中的显示器。
        /// 兼容旧版本/旧配置写入的 DeviceName（如 "\\.\DISPLAY2"），
        /// 解析不到时返回 null 由调用方决定兜底策略。
        /// </summary>
        public DisplayMonitor GetDisplayMonitor(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return null;

            return DisplayMonitors.FirstOrDefault(x => string.Equals(x.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                ?? DisplayMonitors.FirstOrDefault(x => string.Equals(x.DeviceName, deviceId, StringComparison.OrdinalIgnoreCase));
        }

        private DisplayMonitor GetDisplayMonitorByDeviceName(string deviceName)
        {
            return DisplayMonitors.FirstOrDefault(x => x.DeviceName == deviceName);
        }

        private DisplayMonitor CreateDisplayMonitorFromMonitorInfo(string deviceName)
        {
            var displayMonitor = new DisplayMonitor(deviceName);

            var displayDevice = GetDisplayDevice(deviceName);
            displayMonitor.DeviceId = displayDevice.DeviceID;
            displayMonitor.DisplayName = displayDevice.DeviceString;

            DisplayMonitors.Add(displayMonitor);

            return displayMonitor;
        }

        private void UpdateDisplayMonitor(DisplayMonitor displayMonitor, NativeMethods.MONITORINFOEX info)
        {
            displayMonitor.Bounds = new Rectangle(
                info.rcMonitor.Left, info.rcMonitor.Top,
                info.rcMonitor.Right - info.rcMonitor.Left,
                info.rcMonitor.Bottom - info.rcMonitor.Top);

            displayMonitor.IsPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;

            displayMonitor.WorkingArea = new Rectangle(
                info.rcWork.Left, info.rcWork.Top,
                info.rcWork.Right - info.rcWork.Left,
                info.rcWork.Bottom - info.rcWork.Top);

            displayMonitor.isStale = false;
        }

        private IList<IntPtr> GetHMonitors()
        {
            if (multiMonitorSupport)
            {
                var hMonitors = new List<IntPtr>();

                bool callback(IntPtr monitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr lParam)
                {
                    hMonitors.Add(monitor);
                    return true;
                }

                NativeMethods.EnumDisplayMonitors(new HandleRef(null, IntPtr.Zero), null, callback, IntPtr.Zero);

                return hMonitors;
            }

            return new[] { (IntPtr)PRIMARY_MONITOR };
        }

        private static NativeMethods.DISPLAY_DEVICE GetDisplayDevice(string deviceName)
        {
            var result = new NativeMethods.DISPLAY_DEVICE();

            var displayDevice = new NativeMethods.DISPLAY_DEVICE();
            displayDevice.cb = Marshal.SizeOf(displayDevice);
            try
            {
                for (uint id = 0; NativeMethods.EnumDisplayDevices(deviceName, id, ref displayDevice, NativeMethods.EDD_GET_DEVICE_INTERFACE_NAME); id++)
                {
                    if (displayDevice.StateFlags.HasFlag(NativeMethods.DisplayDeviceStateFlags.AttachedToDesktop)
                        && !displayDevice.StateFlags.HasFlag(NativeMethods.DisplayDeviceStateFlags.MirroringDriver))
                    {
                        result = displayDevice;
                        break;
                    }

                    displayDevice.cb = Marshal.SizeOf(displayDevice);
                }
            }
            catch { }

            return result;
        }

        private static string GetDefaultDisplayDeviceId(Rectangle displayBounds)
        {
            var isRemote = NativeMethods.GetSystemMetrics((int)NativeMethods.SystemMetric.SM_REMOTESESSION) != 0;
            var boundsString = $"{displayBounds.X}-{displayBounds.Y}-{displayBounds.Width}-{displayBounds.Height}";

            // Calculate SHA-256 hash
            var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(boundsString));
            var hashString = BitConverter.ToString(hashBytes).Replace("-", string.Empty).ToLowerInvariant();
            var prefix = isRemote ? "\\\\?\\DISPLAY#REMOTEDISPLAY#" : "\\\\?\\DISPLAY#LOCALDISPLAY#";

            return $"{prefix}{hashString}";
        }

        private static Rectangle GetVirtualScreenBounds()
        {
            var location = new Point(NativeMethods.GetSystemMetrics(
                (int)NativeMethods.SystemMetric.SM_XVIRTUALSCREEN), NativeMethods.GetSystemMetrics((int)NativeMethods.SystemMetric.SM_YVIRTUALSCREEN));
            var size = new Size(NativeMethods.GetSystemMetrics(
                (int)NativeMethods.SystemMetric.SM_CXVIRTUALSCREEN), NativeMethods.GetSystemMetrics((int)NativeMethods.SystemMetric.SM_CYVIRTUALSCREEN));
            return new Rectangle(location, size);
        }

        private static Rectangle GetWorkingArea()
        {
            var rc = new NativeMethods.RECT();
            NativeMethods.SystemParametersInfo((int)NativeMethods.SPI.SPI_GETWORKAREA, 0, ref rc, 0);
            return new Rectangle(rc.Left, rc.Top,
                rc.Right - rc.Left, rc.Bottom - rc.Top);
        }

        public bool ScreenExists(DisplayMonitor display)
        {
            return DisplayMonitors.Any(x => display.Equals(x));
        }

        public bool IsMultiScreen()
        {
            return DisplayMonitors.Count > 1;
        }
    }
}
