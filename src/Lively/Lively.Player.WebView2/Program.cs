using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace Lively.Player.WebView2
{
    internal static class Program
    {
        private const string WebView2DownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        /// <summary>
        /// 实际使用的固定版本 WebView2 运行时目录：只有存在且能真正取到版本号时才返回路径，
        /// 否则为 null 表示走系统 Evergreen Runtime。
        /// 启动时校验一次，避免"随包目录存在但内容不全"被误判成"没有 WebView2"。
        /// </summary>
        public static string BundledRuntimePath { get; private set; }

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            var bundledPath = ResolveBundledRuntimePath();
            var version = GetAvailableVersion(bundledPath);

            // 随包运行时不可用时回退系统 Evergreen：机器上装了运行时就不该因为随包副本有问题而失败。
            if (version == null && bundledPath != null)
            {
                bundledPath = null;
                version = GetAvailableVersion(null);
            }

            if (version == null)
            {
                ShowWebView2MissingMessage();
                // ERROR_FILE_NOT_FOUND
                // Ref: <https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--0-499->
                Environment.Exit(2);
            }

            BundledRuntimePath = bundledPath;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Form1());
        }

        private static string ResolveBundledRuntimePath()
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "webview2-runtime");
            return Directory.Exists(path) ? path : null;
        }

        private static string GetAvailableVersion(string browserExecutableFolder)
        {
            try
            {
                return CoreWebView2Environment.GetAvailableBrowserVersionString(browserExecutableFolder);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void ShowWebView2MissingMessage()
        {
            var text = "此壁纸需要 Microsoft Edge WebView2 运行时才能播放，但系统没有检测到它。" + Environment.NewLine + Environment.NewLine +
                       "点“是”打开官方下载页，安装完成后重新应用这张壁纸即可；已安装但仍看到此提示时，请重启应用再试一次。";

            var result = MessageBox.Show(text, "Lively",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);

            if (result == DialogResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(WebView2DownloadUrl) { UseShellExecute = true });
                }
                catch (Exception)
                {
                    // 打不开浏览器时不再抛出，避免掩盖真正的缺失提示。
                }
            }
        }
    }
}
