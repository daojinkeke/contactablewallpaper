using Lively.Common.Helpers;
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace Lively.Helpers
{
    public static class WindowsStartup
    {
        public async static Task<StartupTaskState> TrySetStartup(bool isStartWithWindow)
        {
            var result = StartupTaskState.Disabled;
            try
            {
                // 带实例后缀的是试玩/调试实例：自启项按程序集名全局唯一，写入会覆盖或删除正式安装版的那条，
                // 因此隔离实例一律不碰开机自启。
                if (!string.IsNullOrEmpty(global::Lively.Common.Constants.InstanceSuffix))
                    return result;

                if (PackageUtil.IsRunningAsPackaged)
                    result = await SetStartupTask(isStartWithWindow);
                else
                    SetStartupRegistry(isStartWithWindow);
            }
            catch
            {
                return result;
            }
            return result;
        }

        /// <summary>
        /// Adds startup entry in registry under application name "livelywpf", current user ONLY. (Does not require admin rights).
        /// </summary>
        /// <param name="isStartWithWindows">Add or delete entry.</param>
        private static void SetStartupRegistry(bool isStartWithWindows = false)
        {
            Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
            Assembly curAssembly = Assembly.GetExecutingAssembly();
            try
            {
                if (isStartWithWindows)
                {
                    // 单文件发布时 Assembly.Location 为空，改用 ProcessPath 取当前 exe 全路径
                    var exePath = Environment.ProcessPath;
                    if (string.IsNullOrEmpty(exePath))
                        exePath = Path.ChangeExtension(curAssembly.Location, ".exe");
                    key.SetValue(curAssembly.GetName().Name, "\"" + exePath + "\"");
                }
                else
                {
                    key.DeleteValue(curAssembly.GetName().Name, false);
                }
            }
            finally
            {
                key.Close();
            }
        }

        //ref: https://docs.microsoft.com/en-us/uwp/api/windows.applicationmodel.startuptask?view=winrt-19041
        private async static Task<StartupTaskState> SetStartupTask(bool isStartWithWindows = false)
        {
            // Pass the task ID you specified in the appxmanifest file
            StartupTask startupTask = await StartupTask.GetAsync("AppStartup");
            var startupState = startupTask.State;
            switch (startupState)
            {
                case StartupTaskState.Disabled:
                    {
                        // Task is disabled but can be enabled.
                        // ensure that you are on a UI thread when you call RequestEnableAsync()
                        if (isStartWithWindows)
                            startupState = await startupTask.RequestEnableAsync();
                    }
                    break;
                case StartupTaskState.DisabledByUser:
                    {
                        // Task is disabled and user must enable it manually.
                    }
                    break;
                case StartupTaskState.DisabledByPolicy:
                    {
                        // Startup disabled by group policy, or not supported on this device.
                    }
                    break;
                case StartupTaskState.Enabled:
                    {
                        if (!isStartWithWindows)
                            startupTask.Disable();
                    }
                    break;
                    default:
                    {
                        if (isStartWithWindows)
                            startupState = await startupTask.RequestEnableAsync();
                    }
                    break;
            }
            return startupState;
        }
    }
}
