﻿﻿﻿﻿﻿﻿﻿using Lively.Common.Helpers;
using Lively.Models.Enums;
using System;
using System.IO;

namespace Lively.Common
{
    public static class Constants
    {
        private const string InstanceSuffixEnvironmentVariable = "LIVELY_INSTANCE_SUFFIX";
        private const int InstanceSuffixMaxLength = 16;

        /// <summary>
        /// 实例后缀，仅用于开发态并行（docs/contracts/01-architecture.md 实例标识）。
        /// 来源环境变量 <c>LIVELY_INSTANCE_SUFFIX</c>：只允许 ASCII 字母、数字与 '-'，最长 16 字符；
        /// 空值、超长或含非法字符一律视为无后缀。Constants 为静态初始化、早于命令行解析，故只读环境变量。
        /// </summary>
        public static string InstanceSuffix { get; } = NormalizeInstanceSuffix(Environment.GetEnvironmentVariable(InstanceSuffixEnvironmentVariable));

        /// <summary>
        /// 应用数据目录名（<see cref="ApplicationType.Name"/> + 实例后缀）。
        /// 后缀为空时与 <see cref="ApplicationType.Name"/> 逐字节一致。
        /// </summary>
        public static string AppDataFolderName { get; } = ApplicationType.Name + GetInstanceSuffixSeparator() + InstanceSuffix;

        private static string GetInstanceSuffixSeparator() => InstanceSuffix.Length == 0 ? string.Empty : "-";

        private static string NormalizeInstanceSuffix(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > InstanceSuffixMaxLength)
                return string.Empty;

            foreach (var c in value)
            {
                var isAsciiLetterOrDigit = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
                if (!isAsciiLetterOrDigit && c != '-')
                    return string.Empty;
            }

            return value;
        }

        public static class CommonPaths
        {
            public static string AppDataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Constants.AppDataFolderName);
            public static string LogDir { get; } = Path.Combine(AppDataDir, "logs");
            public static string LogDirUI { get; } = Path.Combine(AppDataDir, "UI"); //temp use..
            public static string TempDir { get; } = Path.Combine(AppDataDir, "temp");
            public static string TempCefDir { get; } = Path.Combine(AppDataDir, "Cef");
            public static string TempWebView2Dir { get; } = Path.Combine(AppDataDir, "WebView2");
            public static string TempVideoDir { get; } = Path.Combine(AppDataDir, "Mpv");
            public static string AppRulesPath { get; } = Path.Combine(AppDataDir, "AppRules.json");
            public static string WallpaperLayoutPath { get; } = Path.Combine(AppDataDir, "WallpaperLayout.json");
            public static string ScreenSaverLayoutPath { get; } = Path.Combine(AppDataDir, "ScreenSaverLayout.json");
            public static string UserSettingsPath { get; } = Path.Combine(AppDataDir, "Settings.json");
            public static string MusicAppExclusionRulesPath { get; } = Path.Combine(AppDataDir, "MusicAppExclusionRules.json");
            public static string WeatherSettingsPath { get; } = Path.Combine(AppDataDir, "WeatherSettings.json");
            public static string ThemeDir { get; } = Path.Combine(AppDataDir, "Themes");
            public static string ThemeCacheDir { get; } = Path.Combine(Path.GetTempPath(), Constants.AppDataFolderName, "themes");
            public static string CefRootCacheDir { get; } = Path.Combine(Path.GetTempPath(), Constants.AppDataFolderName, "CEF");
            public static string TokensPath { get; } = Path.Combine(AppDataDir, "Tokens.dat");
            public static string ScreenshotDir { get; } = Path.Combine(AppDataDir, "Screenshots");
            //AI 生成与打包（docs/contracts/07-ai-and-packaging.md §9）
            public static string AiSettingsPath { get; } = Path.Combine(AppDataDir, "AiSettings.json");
            public static string ProjectsDir { get; } = Path.Combine(AppDataDir, "Projects");
            public static string DeploymentsPath { get; } = Path.Combine(AppDataDir, "Deployments.json");
            public static string PackagingTempDir { get; } = Path.Combine(TempDir, "packaging");
            public static string AiTempDir { get; } = Path.Combine(TempDir, "ai");
        }

        /// <summary>
        /// To be combined with dynamic path Settings.WallpaperDir
        /// </summary>
        public static class CommonPartialPaths
        {
            public static string WallpaperInstallDir { get; } = "wallpapers";
            public static string WallpaperInstallTempDir { get; } = Path.Combine("SaveData", "wptmp");
            public static string WallpaperSettingsDir { get; } = Path.Combine("SaveData", "wpdata");
        }

        /// <summary>
        /// To be combined with Core base directory.
        /// </summary>
        public static class PlayerPartialPaths
        {
            public static string MpvDir { get; } = Path.Combine("plugins", "mpv");
            public static string MpvPath { get; } = Path.Combine(MpvDir, "mpv.exe");
            public static string CefSharpDir { get; } = Path.Combine("plugins", "cef");
            public static string CefSharpPath { get; } = Path.Combine(CefSharpDir, "Lively.Player.CefSharp.exe");
            public static string WebView2Dir { get; } = Path.Combine("plugins", "webview2");
            public static string WebView2Path { get; } = Path.Combine(WebView2Dir, "Lively.Player.WebView2.exe");
            public static string WmfDir { get; } = Path.Combine("plugins", "wmf");
            public static string WmfPath { get; } = Path.Combine(WmfDir, "Lively.PlayerWmf.exe");
            public static string VlcDir { get; } = Path.Combine("plugins", "vlc");
            public static string VlcPath { get; } = Path.Combine(VlcDir, "vlc.exe");
            public static string LibVlcDir { get; } = Path.Combine("plugins", "libvlc");
            public static string LibVlcPath { get; } = Path.Combine(LibVlcDir, "Lively.Player.Vlc.exe");
            //public static string LibVlcExtDir { get; } = Path.Combine("plugins", "libVLCPlayer");
            //public static string LibVlcExtPath { get; } = Path.Combine(LibVlcExtDir, "libVLCPlayer.exe");
            //public static string LibMpvExtDir { get; } = Path.Combine("plugins", "libMPVPlayer");
            //public static string LibMpvExtPath { get; } = Path.Combine(LibMpvExtDir, "libMPVPlayer.exe");
        }

        public static class MachineLearning
        {
            public static string BaseDir { get; } = Path.Combine(CommonPaths.AppDataDir, "ML");
            public static string MiDaSDir { get; } = Path.Combine(BaseDir, "Midas");
        }

        public static class SingleInstance
        {
            /// <summary>
            /// 本家族（安装版/试玩版）固定实例标识。
            /// 官方商店版 Lively 用的是不带该标识的 "LIVELY:DESKTOPWALLPAPERSYSTEM"，
            /// 两者若同名，本程序启动会命中官方实例并把参数转发过去、自己退出，
            /// 表现就是"点开我们这版却弹出官方界面"。数据目录不参与该标识，保持原路径不变。
            /// </summary>
            private const string FamilyInstanceId = "_ContactableWallpaper";

            // 实例后缀为空时：本家族固定标识；非空时：再加后缀，互斥体与 gRPC 管道一并隔离。
            public static string UniqueAppName { get; } = "LIVELY:DESKTOPWALLPAPERSYSTEM" + FamilyInstanceId + GetInstanceSuffixSeparator() + Constants.InstanceSuffix;
            public static string PipeServerName { get; } = UniqueAppName + Environment.UserName; //backward compatibility < v1.9
            public static string GrpcPipeServerName { get; } = "Grpc_" + PipeServerName;

            /// <summary>
            /// 凭据加密（DPAPI）的附加熵：必须保持历史字面量，
            /// 跟着 <see cref="UniqueAppName"/> 一起改会导致既有 Tokens.dat 无法解密。
            /// </summary>
            public static string EncryptionEntropy { get; } = "LIVELY:DESKTOPWALLPAPERSYSTEM";
        }
        
        public static class ApplicationType
        {
            /// <summary>
            /// 持久化标识：数据目录、缓存目录、互斥体与管道名的组成部分。
            /// 改名等同数据迁移，禁止用于界面展示（契约 01 §8.1）。
            /// </summary>
            public static string Name { get; } = "Lively Wallpaper";

            /// <summary>
            /// 界面显示名（托盘悬停、标题、提示等一切用户可见文案）。
            /// 不参与任何路径或实例标识拼接。
            /// </summary>
            public static string DisplayName { get; } = "Contactable Wallpaper";

            public static bool IsTestBuild { get; } = false;
        }

        public static class Weather
        {
            //todo: make compile-time flag.
            public static string OpenWeatherMapAPIKey = string.Empty;
        }

        public static class AppDefaults
        {
            public static LivelyWebBrowser WebBrowser { get; } = LivelyWebBrowser.webview2;
            public static LivelyMediaPlayer VideoPlayer { get; } = LivelyMediaPlayer.mpv;
            public static LivelyPicturePlayer PicturePlayer { get; } = LivelyPicturePlayer.mpv;
            public static LivelyGifPlayer GifPlayer { get; } = LivelyGifPlayer.mpv;
        }
    }
}
