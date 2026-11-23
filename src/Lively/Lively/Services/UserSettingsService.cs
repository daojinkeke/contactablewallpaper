using Lively.Common;
using Lively.Common.Helpers.Storage;
using Lively.Common.Services;
using Lively.Core.Display;
using Lively.Models;
using Lively.Models.Enums;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Lively.Services
{
    public class UserSettingsService : IUserSettingsService
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        private readonly IDisplayManager displayManager;
        private readonly string settingsPath = Constants.CommonPaths.UserSettingsPath;
        private readonly string appRulesPath = Constants.CommonPaths.AppRulesPath;
        private readonly string wallpaperLayoutPath = Constants.CommonPaths.WallpaperLayoutPath;

        public UserSettingsService(IDisplayManager displayManager)
        {
            this.displayManager = displayManager;

            Load<SettingsModel>();
            Load<List<ApplicationRulesModel>>();
            Load<List<WallpaperLayoutModel>>();

            // 交互默认值迁移（按版本号逐级回补，每个版本只执行一次）：
            // 1 = 总开关改为开启；2 = 滚轮转发改为开启。
            // 历史版本默认关闭且旧 UI 无法持久化，老 Settings.json 里会残留 false。
            // 只处理落后版本，用户迁移之后手动关闭的开关不会被再次覆盖。
            var interactionDefaultsMigrated = false;
            if (Settings.InteractionDefaultsVersion < 1)
            {
                Settings.InteractionEnabled = true;
                Settings.InteractionDefaultsVersion = 1;
                interactionDefaultsMigrated = true;
            }
            if (Settings.InteractionDefaultsVersion < 2)
            {
                Settings.InteractionForwardWheel = true;
                Settings.InteractionDefaultsVersion = 2;
                interactionDefaultsMigrated = true;
            }
            if (interactionDefaultsMigrated)
            {
                Save<SettingsModel>();
            }

            // Fallback incase player plugin is changed/missing.
            Settings.VideoPlayer = GetAvailableVideoPlayerOrDefault(Settings.VideoPlayer);
            Settings.GifPlayer = GetAvailableGifPlayerOrDefault(Settings.GifPlayer);
            Settings.WebBrowser = GetAvailableWebPlayerOrDefault(Settings.WebBrowser);
            Settings.PicturePlayer = GetAvailablePicturePlayerOrDefault(Settings.PicturePlayer);
            // Fallback incase display disconnected or not set.
            Settings.SelectedAudioOutputDisplay = ResolveDisplay(Settings.SelectedAudioOutputDisplay);
            Settings.SelectedDisplay = ResolveDisplay(Settings.SelectedDisplay);

            // Previous installed version is different from current instance.  
            if (!Settings.AppVersion.Equals(Assembly.GetExecutingAssembly().GetName().Version.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                Settings.AppPreviousVersion = Settings.AppVersion;
                Settings.AppVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString();
                Settings.IsUpdated = true;
                // This flag is set to false once UI program closes after notifying user.
                Settings.IsUpdatedNotify = true;
                // Save the new AppVersion.
                Save<SettingsModel>();
            }
            else if (Settings.IsUpdated)
            {
                // IsUpdated is set only once after each update.
                Settings.IsUpdated = false;
                Save<SettingsModel>();
            }

            // Reject unsupported language.
            Settings.Language = Languages.SupportedLanguages.FirstOrDefault(x => x.Code == Settings.Language)?.Code ?? string.Empty;
        }

        public SettingsModel Settings { get; private set; }
        public List<ApplicationRulesModel> AppRules { get; private set; }
        public List<WallpaperLayoutModel> WallpaperLayout { get; private set; }

        public void Save<T>()
        {
            if (typeof(T) == typeof(SettingsModel))
            {
                JsonStorage<SettingsModel>.StoreData(settingsPath, Settings);
            }
            else if (typeof(T) == typeof(List<ApplicationRulesModel>))
            {
                JsonStorage<List<ApplicationRulesModel>>.StoreData(appRulesPath, AppRules);
            }
            else if (typeof(T) == typeof(List<WallpaperLayoutModel>))
            {
                JsonStorage<List<WallpaperLayoutModel>>.StoreData(wallpaperLayoutPath, WallpaperLayout);
            }
            else
            {
                throw new InvalidCastException($"Type not found: {typeof(T)}");
            }
        }

        public void Load<T>()
        {
            if (typeof(T) == typeof(SettingsModel))
            {
                Settings = LoadOrInitialize(settingsPath, () => {
                    Settings = new SettingsModel();
                    // 实例隔离：壁纸库跟随本次实例的 AppData 目录，避免多个实例共用同一份壁纸库。
                    Settings.WallpaperDir = Path.Combine(Constants.CommonPaths.AppDataDir, "Library");
                    Save<SettingsModel>();
                    return Settings;
                });
            }
            else if (typeof(T) == typeof(List<ApplicationRulesModel>))
            {
                AppRules = LoadOrInitialize(appRulesPath, () => {
                    AppRules = [];
                    Save<List<ApplicationRulesModel>>();
                    return AppRules;
                });
            }
            else if (typeof(T) == typeof(List<WallpaperLayoutModel>))
            {
                WallpaperLayout = LoadOrInitialize(wallpaperLayoutPath, () => {
                    WallpaperLayout = [];
                    Save<List<WallpaperLayoutModel>>();
                    return WallpaperLayout;
                });
            }
            else
            {
                throw new InvalidCastException($"Type not found: {typeof(T)}");
            }
        }

        private static T LoadOrInitialize<T>(string path, Func<T> defaultAction)
        {
            if (File.Exists(path))
            {
                try
                {
                    return JsonStorage<T>.LoadData(path);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                }
            }
            else
            {
                Logger.Info($"File not found for {typeof(T).FullName}, creating default at {path}");
            }

            return defaultAction();
        }

        private static LivelyMediaPlayer GetAvailableVideoPlayerOrDefault(LivelyMediaPlayer mp)
        {
            var isAvailable  = mp switch
            {
                LivelyMediaPlayer.libvlc => false, //depreciated
                LivelyMediaPlayer.libmpv => false, //depreciated
                LivelyMediaPlayer.wmf => File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Constants.PlayerPartialPaths.WmfPath)),
                LivelyMediaPlayer.libvlcExt => File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Constants.PlayerPartialPaths.LibVlcPath)),
                LivelyMediaPlayer.libmpvExt => false, //depreciated
                LivelyMediaPlayer.mpv => File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Constants.PlayerPartialPaths.MpvPath)),
                LivelyMediaPlayer.vlc => File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Constants.PlayerPartialPaths.VlcPath)),
                _ => false,
            };
            // Assume default is always available.
            return isAvailable ? mp : Constants.AppDefaults.VideoPlayer;
        }

        private static LivelyGifPlayer GetAvailableGifPlayerOrDefault(LivelyGifPlayer gp)
        {
            var isAvailable = gp switch
            {
                LivelyGifPlayer.win10Img => false, //xaml island
                LivelyGifPlayer.libmpvExt => false, //depreciated
                LivelyGifPlayer.mpv => File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Constants.PlayerPartialPaths.MpvPath)),
                LivelyGifPlayer.libvlcExt => File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Constants.PlayerPartialPaths.LibVlcPath)),
                _ => false,
            };
            return isAvailable ? gp : Constants.AppDefaults.GifPlayer;
        }

        private static LivelyPicturePlayer GetAvailablePicturePlayerOrDefault(LivelyPicturePlayer pp)
        {
            var isAvailable = pp switch
            {
                LivelyPicturePlayer.picture => false,
                LivelyPicturePlayer.winApi => false,
                LivelyPicturePlayer.mpv => File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Constants.PlayerPartialPaths.MpvPath)),
                LivelyPicturePlayer.wmf => false,
                _ => false,
            };
            return isAvailable ? pp : Constants.AppDefaults.PicturePlayer;
        }

        private static LivelyWebBrowser GetAvailableWebPlayerOrDefault(LivelyWebBrowser wp)
        {
            var isAvailable = wp switch
            {
                LivelyWebBrowser.cef => File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Constants.PlayerPartialPaths.CefSharpPath)),
                LivelyWebBrowser.webview2 => File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Constants.PlayerPartialPaths.WebView2Path)),
                _ => false,
            };
            return isAvailable ? wp : Constants.AppDefaults.WebBrowser;
        }

        private DisplayMonitor ResolveDisplay(DisplayMonitor current)
        {
            return current != null
                ? displayManager.DisplayMonitors.FirstOrDefault(x => x.Equals(current)) ?? displayManager.PrimaryDisplayMonitor
                : displayManager.PrimaryDisplayMonitor;
        }
    }
}
