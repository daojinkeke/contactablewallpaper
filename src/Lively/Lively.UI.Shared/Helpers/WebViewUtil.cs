using Lively.Common;
using Lively.Common.Helpers;
using Lively.Common.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Lively.UI.Shared.Helpers
{
    public static class WebViewUtil
    {
        public static string DownloadUrl => "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        /// <summary>
        /// WebView2 是否可用。先问 SDK（覆盖固定版本运行时），
        /// 失败时再用注册表 pv 兜底——原生 WebView2 组件在个别环境下加载失败会抛异常，
        /// 不能因此就判定"没装"，否则会出现"能播壁纸却提示缺 WebView2"的误报。
        /// </summary>
        public static bool IsWebView2Available()
        {
            try
            {
                if (!string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()))
                    return true;
            }
            catch (Exception)
            {
                // 继续走注册表兜底。
            }

            return IsRuntimeInRegistry();
        }

        private static bool IsRuntimeInRegistry()
        {
            const string clientKey = @"Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
            return HasPv(Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Default, clientKey)
                || HasPv(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64, clientKey)
                || HasPv(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32, clientKey);
        }

        private static bool HasPv(Microsoft.Win32.RegistryHive hive, Microsoft.Win32.RegistryView view, string subKey)
        {
            try
            {
                using (var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view))
                using (var key = baseKey.OpenSubKey(subKey))
                {
                    return !string.IsNullOrWhiteSpace(key?.GetValue("pv") as string);
                }
            }
            catch
            {
                return false;
            }
        }

        public static async Task<bool> InstallWebView2(IDownloadService downloader)
        {
            if (PackageUtil.IsRunningAsPackaged)
                return false;

            try
            {
                var filePath = Path.Combine(Constants.CommonPaths.TempDir, "MicrosoftEdgeWebview2Setup.exe");
                await downloader.DownloadFile(new Uri(DownloadUrl), filePath);
                await Process.Start(filePath, "/silent /install").WaitForExitAsync();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
