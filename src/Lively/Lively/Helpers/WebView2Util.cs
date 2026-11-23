using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Lively.Helpers
{
    /// <summary>
    /// WebView2 运行时检测与缺失提示。
    /// Core 启动时做一次轻量检查；播放器侧在真正启动网页壁纸时还会独立检测一次。
    ///
    /// 检测刻意做成多路兜底（随包固定版本 / 注册表 pv 任一成立即视为可用）：
    /// 单点探测失败（读不到某个注册表视图、目录判断出错）不应被当成"没装"，否则会出现
    /// "机器上明明能播网页壁纸、却提示缺少 WebView2" 的误报。
    /// </summary>
    public static class WebView2Util
    {
        private const string ClientKey = @"Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        private const string DownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        public static bool IsRuntimeInstalled()
        {
            return HasBundledRuntime() || !string.IsNullOrWhiteSpace(GetRegistryVersion());
        }

        /// <summary>
        /// 随包固定版本运行时（plugins\webview2\webview2-runtime）是否可用。
        /// 这种安装方式不写注册表，只能看文件。
        /// </summary>
        public static bool HasBundledRuntime()
        {
            try
            {
                var exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "plugins", "webview2", "webview2-runtime", "msedgewebview2.exe");
                return File.Exists(exe);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 注册表 pv。三个视图都要查：本机实测 WebView2 只写在 WOW6432Node 下。
        /// </summary>
        public static string GetRegistryVersion()
        {
            return GetPv(RegistryHive.CurrentUser, RegistryView.Default)
                ?? GetPv(RegistryHive.LocalMachine, RegistryView.Registry64)
                ?? GetPv(RegistryHive.LocalMachine, RegistryView.Registry32);
        }

        private static string GetPv(RegistryHive hive, RegistryView view)
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                using (var key = baseKey.OpenSubKey(ClientKey))
                {
                    var pv = key?.GetValue("pv") as string;
                    return string.IsNullOrWhiteSpace(pv) ? null : pv;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 缺失时给出中文提示，并提供打开微软官方下载页的入口。
        /// 在后台线程弹出，避免阻塞壁纸恢复。
        /// </summary>
        public static void ShowMissingPrompt()
        {
            try
            {
                var text =
                    "没有检测到 Microsoft Edge WebView2 运行时。" + Environment.NewLine + Environment.NewLine +
                    "网页壁纸、交互壁纸以及“新建壁纸”的预览需要它；普通视频、图片壁纸不受影响。" + Environment.NewLine + Environment.NewLine +
                    "点“是”打开微软官方下载页（需要联网）。安装完成后重启本程序即可。";

                var result = MessageBox.Show(text, "ContactableWallpaper",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes);

                if (result == MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo(DownloadUrl) { UseShellExecute = true });
                }
            }
            catch
            {
                // 提示失败时不影响主流程。
            }
        }
    }
}
