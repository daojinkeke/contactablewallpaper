using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Lively.Common;
using Lively.Common.Factories;
using Lively.Common.Helpers.Storage;
using Lively.Grpc.Client;
using Lively.Models;
using Lively.Models.Enums;

namespace Lively.UI.WinUI.Services.Contactable
{
    public sealed class RecentProjectMeta
    {
        public string Title { get; set; }
        public string Prompt { get; set; }
        public string Style { get; set; }
        public string Color { get; set; }
        public string HtmlPath { get; set; }
        public string ThumbnailPath { get; set; }
        public DateTime CreatedAt { get; set; }
        public string LibraryFolderPath { get; set; }
        public bool IsPackaged { get; set; }
        public string SessionId { get; set; }
    }

    /// <summary>
    /// 壁纸库扫描/应用/删除、生成结果入库与最近生成记录。
    /// 只走现有 gRPC 与 Common 打包工具，不改 Core 契约。
    /// </summary>
    public sealed class ContactableWallpaperService
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        private readonly IUserSettingsClient userSettings;
        private readonly IWallpaperLibraryFactory libraryFactory;
        private readonly IDesktopCoreClient desktopCore;

        public ContactableWallpaperService(IUserSettingsClient userSettings, IWallpaperLibraryFactory libraryFactory, IDesktopCoreClient desktopCore)
        {
            this.userSettings = userSettings;
            this.libraryFactory = libraryFactory;
            this.desktopCore = desktopCore;
        }

        public string WallpaperRoot
        {
            get
            {
                var configured = userSettings.Settings?.WallpaperDir;
                if (!string.IsNullOrWhiteSpace(configured))
                    return configured;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lively Wallpaper", "Library");
            }
        }

        public string InstallRoot => Path.Combine(WallpaperRoot, Constants.CommonPartialPaths.WallpaperInstallDir);
        public string TempRoot => Path.Combine(WallpaperRoot, Constants.CommonPartialPaths.WallpaperInstallTempDir);
        public string ProjectsRoot => Constants.CommonPaths.ProjectsDir;

        /// <summary>随包默认壁纸的目录名，用于把播种进来的内置壁纸和用户自己的壁纸区分开。</summary>
        private static readonly string[] BuiltinFolderNames = LoadBuiltinFolderNames();

        private static string[] LoadBuiltinFolderNames()
        {
            try
            {
                // UI 位于 <安装目录>\plugins\UI，Core 侧的 Assets 在 <安装目录>\Assets，
                // 开发态也可能直接把 Assets 放在输出目录旁，这里逐级探测。
                var baseDir = AppContext.BaseDirectory;
                var candidates = new[]
                {
                    Path.Combine(baseDir, "Assets", "DefaultWallpapers"),
                    Path.Combine(baseDir, "..", "Assets", "DefaultWallpapers"),
                    Path.Combine(baseDir, "..", "..", "Assets", "DefaultWallpapers"),
                };

                foreach (var dir in candidates)
                {
                    if (!Directory.Exists(dir))
                        continue;

                    var names = Directory.GetDirectories(dir)
                        .Select(Path.GetFileName)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToArray();
                    if (names.Length > 0)
                        return names;
                }
            }
            catch (Exception)
            {
                // 读不到时退回按元数据判定内置壁纸。
            }

            return Array.Empty<string>();
        }

        /// <summary>
        /// 判定壁纸来源：内置默认壁纸、AI 生成（网页/视差）、其余视为用户自己添加。
        /// 旧版本入库的目录以 contactable- 开头，同样归为 AI 生成。
        /// </summary>
        public static WallpaperSourceKind ResolveSource(string folderPath, LivelyInfoModel info)
        {
            var folderName = Path.GetFileName(folderPath) ?? string.Empty;
            if (folderName.Length == 0)
                return WallpaperSourceKind.Imported;

            if (BuiltinFolderNames.Any(x => string.Equals(x, folderName, StringComparison.OrdinalIgnoreCase)))
                return WallpaperSourceKind.Builtin;

            if (folderName.StartsWith("ai-", StringComparison.OrdinalIgnoreCase) ||
                folderName.StartsWith("contactable-", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(info?.Prompt) ||
                string.Equals(info?.Author, "contactable", StringComparison.OrdinalIgnoreCase) ||
                (info?.Tags?.Any(x => string.Equals(x, "depth", StringComparison.OrdinalIgnoreCase)) ?? false))
                return WallpaperSourceKind.Ai;

            if (string.Equals(info?.Author, Constants.ApplicationType.DisplayName, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(info.Prompt) &&
                string.IsNullOrWhiteSpace(info.GeneratedAt))
                return WallpaperSourceKind.Builtin;

            return WallpaperSourceKind.Imported;
        }

        public IReadOnlyList<LibraryItemVm> ScanLibrary()
        {
            var items = new List<LibraryItemVm>();
            var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var wallpaper in GetRunningWallpapers())
                {
                    if (!string.IsNullOrWhiteSpace(wallpaper?.LivelyInfoFolderPath))
                        running.Add(wallpaper.LivelyInfoFolderPath);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "读取正在运行的壁纸失败");
            }

            foreach (var root in new[] { InstallRoot, TempRoot })
            {
                if (!Directory.Exists(root))
                    continue;

                IEnumerable<string> folders;
                try
                {
                    folders = Directory.EnumerateDirectories(root);
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, $"无法读取壁纸目录 {root}");
                    continue;
                }

                foreach (var folder in folders)
                {
                    try
                    {
                        if (!File.Exists(Path.Combine(folder, "LivelyInfo.json")))
                            continue;

                        var model = libraryFactory.CreateFromDirectory(folder);
                        var type = model.LivelyInfo?.Type ?? WallpaperType.web;
                        items.Add(new LibraryItemVm
                        {
                            Title = string.IsNullOrWhiteSpace(model.Title) || model.Title == "---" ? Path.GetFileName(folder) : model.Title,
                            TypeText = DescribeType(type),
                            FolderPath = folder,
                            ThumbnailPath = model.ImagePath,
                            IsInUse = running.Contains(folder),
                            SourceKind = ResolveSource(folder, model.LivelyInfo)
                        });
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, $"跳过损坏的壁纸目录 {folder}");
                    }
                }
            }

            return items
                .OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public async Task<(bool Ok, string Message)> ApplyAsync(string folderPath, DisplayMonitor monitor, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || monitor == null)
                return (false, "没有选择壁纸或显示器。");

            try
            {
                await desktopCore.SetWallpaper(folderPath, monitor.DeviceId);
            }
            catch (Exception ex)
            {
                return (false, $"应用壁纸失败：{ex.Message}");
            }

            for (var i = 0; i < 20; i++)
            {
                try
                {
                    await Task.Delay(400, cancellationToken);
                    if (GetRunningWallpapers().Any(x =>
                            string.Equals(x.LivelyInfoFolderPath, folderPath, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(x.Display?.DeviceId, monitor.DeviceId, StringComparison.OrdinalIgnoreCase)))
                    {
                        return (true, "已应用到显示器。");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "确认壁纸应用状态失败");
                }
            }

            return (false, "已发送应用请求，但 8 秒内未确认生效。可稍后在预览页刷新查看。");
        }

        public async Task<(bool Ok, string Message)> DeleteLibraryAsync(LibraryItemVm item)
        {
            if (item == null || !Directory.Exists(item.FolderPath))
                return (false, "壁纸目录不存在。");

            if (!IsUnderRoot(item.FolderPath, WallpaperRoot))
                return (false, "拒绝删除壁纸目录之外的路径。");

            try
            {
                var running = GetRunningWallpapers()
                    .Where(x => string.Equals(x.LivelyInfoFolderPath, item.FolderPath, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var wallpaper in running)
                {
                    try
                    {
                        await desktopCore.CloseWallpaper(wallpaper.Display);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, $"关闭正在运行的壁纸失败 {item.FolderPath}");
                    }
                }

                if (running.Count > 0)
                    await Task.Delay(500);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "查询正在运行的壁纸失败");
            }

            for (var i = 0; i < 3; i++)
            {
                try
                {
                    Directory.Delete(item.FolderPath, true);
                    return (true, "已删除壁纸。");
                }
                catch (Exception ex)
                {
                    if (i == 2)
                        return (false, $"删除失败：{ex.Message}");
                    await Task.Delay(400);
                }
            }

            return (false, "删除失败。");
        }

        public string CreateProjectFolder()
        {
            var folder = Path.Combine(ProjectsRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}-{RandomHex(6)}");
            Directory.CreateDirectory(Path.Combine(folder, "stage"));
            return folder;
        }

        public string CreateThumbnail(string projectFolder, string title, string styleKey, string colorHex)
        {
            var stage = Path.Combine(projectFolder, "stage");
            Directory.CreateDirectory(stage);
            var path = Path.Combine(stage, "thumb.png");
            try
            {
                var style = ContactableStyles.Find(styleKey);
                var accent = ParseSystemColor(colorHex, style.ColorHex);
                using var bitmap = new Bitmap(640, 360);
                using var graphics = Graphics.FromImage(bitmap);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;

                using (var background = new LinearGradientBrush(
                    new Rectangle(0, 0, 640, 360),
                    Blend(accent, Color.FromArgb(255, 6, 12, 28), 0.78),
                    Color.FromArgb(255, 6, 12, 28),
                    35f))
                {
                    graphics.FillRectangle(background, 0, 0, 640, 360);
                }

                using (var glow = new SolidBrush(Color.FromArgb(55, accent)))
                    graphics.FillEllipse(glow, 380, 20, 320, 320);

                using (var ring = new Pen(Color.FromArgb(225, accent), 7))
                    graphics.DrawEllipse(ring, 238, 66, 214, 214);

                using var titleFont = new Font("Segoe UI", 26, FontStyle.Bold, GraphicsUnit.Pixel);
                using var smallFont = new Font("Segoe UI", 16, FontStyle.Regular, GraphicsUnit.Pixel);
                using var white = new SolidBrush(Color.FromArgb(238, 255, 255, 255));
                using var dim = new SolidBrush(Color.FromArgb(190, 214, 228, 255));
                graphics.DrawString(Shorten(title, 26), titleFont, white, 24, 268);
                graphics.DrawString(style.IsAuto ? "Contactable Wallpaper" : $"Contactable · {style.Name}", smallFont, dim, 26, 306);

                bitmap.Save(path, ImageFormat.Png);
                return path;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "生成缩略图失败");
                return null;
            }
        }

        public void WriteMeta(GeneratedWallpaper item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.ProjectFolder))
                return;
            try
            {
                Directory.CreateDirectory(item.ProjectFolder);
                JsonStorage<RecentProjectMeta>.StoreData(Path.Combine(item.ProjectFolder, "meta.json"), ToMeta(item));
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "写入最近生成记录失败");
            }
        }

        public IReadOnlyList<GeneratedWallpaper> ScanRecent(int max = 20)
        {
            var items = new List<GeneratedWallpaper>();
            if (!Directory.Exists(ProjectsRoot))
                return items;

            IEnumerable<string> folders;
            try
            {
                folders = Directory.EnumerateDirectories(ProjectsRoot);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "读取最近生成目录失败");
                return items;
            }

            foreach (var folder in folders)
            {
                var metaPath = Path.Combine(folder, "meta.json");
                if (!File.Exists(metaPath))
                    continue;
                try
                {
                    var meta = JsonStorage<RecentProjectMeta>.LoadData(metaPath);
                    var item = FromMeta(folder, meta);
                    if (item == null || !File.Exists(item.HtmlPath))
                        continue;
                    items.Add(item);
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, $"跳过损坏的最近生成记录 {folder}");
                }
            }

            return items
                .OrderByDescending(x => x.CreatedAt)
                .Take(max)
                .ToList();
        }

        public void DeleteRecent(GeneratedWallpaper item)
        {
            if (item == null || !Directory.Exists(item.ProjectFolder))
                return;
            if (!IsUnderRoot(item.ProjectFolder, ProjectsRoot))
                return;

            try
            {
                Directory.Delete(item.ProjectFolder, true);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"删除最近生成记录失败 {item.ProjectFolder}");
            }
        }

        public async Task<string> SaveToLibraryAsync(GeneratedWallpaper item)
        {
            if (item == null || !File.Exists(item.HtmlPath))
                throw new InvalidOperationException("生成结果文件不存在。");

            // AI 生成的网页壁纸统一用 ai- 前缀，方便在资源管理器里一眼认出来源。
            var dest = Path.Combine(InstallRoot, $"ai-{DateTime.Now:yyyyMMdd-HHmmss}-{RandomHex(4)}");
            Directory.CreateDirectory(dest);

            var metadata = libraryFactory.CreateWallpaperPackage(item.HtmlPath, dest, WallpaperType.web);
            metadata.Title = string.IsNullOrWhiteSpace(item.Title) ? "Contactable Wallpaper" : item.Title;
            metadata.Desc = item.Prompt ?? string.Empty;
            metadata.Author = "Contactable Wallpaper";
            metadata.Prompt = item.Prompt;
            metadata.GeneratedAt = item.CreatedAt.ToUniversalTime().ToString("o");
            metadata.Thumbnail = File.Exists(item.ThumbnailPath) ? item.ThumbnailPath : string.Empty;

            try
            {
                await libraryFactory.ConvertAbsoluteToRelativePathAsync(metadata, dest);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "标准入库失败，使用手工打包兜底");
                var htmlName = "index.html";
                File.Copy(item.HtmlPath, Path.Combine(dest, htmlName), true);
                var thumbName = string.Empty;
                if (File.Exists(item.ThumbnailPath))
                {
                    thumbName = "thumb.png";
                    File.Copy(item.ThumbnailPath, Path.Combine(dest, thumbName), true);
                }

                metadata.FileName = htmlName;
                metadata.IsAbsolutePath = false;
                metadata.Thumbnail = thumbName;
                metadata.Preview = null;
                JsonStorage<LivelyInfoModel>.StoreData(Path.Combine(dest, "LivelyInfo.json"), metadata);
            }

            item.LibraryFolderPath = dest;
            WriteMeta(item);
            await Task.CompletedTask;
            return dest;
        }

        /// <summary>
        /// 直接导入一个已符合 Lively 壁纸目录规范的目录（如深度视差 CLI 产物）。
        /// </summary>
        public Task<string> ImportWallpaperDirectoryAsync(string sourceDirectory, string title)
        {
            return Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
                    throw new InvalidOperationException("壁纸目录不存在。");
                if (!File.Exists(Path.Combine(sourceDirectory, "LivelyInfo.json")))
                    throw new InvalidOperationException("该目录不是完整的壁纸包（缺少 LivelyInfo.json）。");

                // 整目录导入（如视差壁纸）用 ai-depth- 前缀，和网页壁纸、用户自带素材区分。
                var dest = Path.Combine(InstallRoot, $"ai-depth-{DateTime.Now:yyyyMMdd-HHmmss}-{RandomHex(4)}");
                CopyDirectory(sourceDirectory, dest);
                return dest;
            });
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(source))
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            }

            foreach (var directory in Directory.EnumerateDirectories(source))
            {
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
            }
        }

        public static string DescribeType(WallpaperType type)
        {
            return type switch
            {
                WallpaperType.web => "网页",
                WallpaperType.webaudio => "网页音频",
                WallpaperType.url => "链接",
                WallpaperType.video => "视频",
                WallpaperType.gif => "GIF",
                WallpaperType.picture => "图片",
                WallpaperType.app => "应用",
                WallpaperType.unity => "Unity",
                WallpaperType.unityaudio => "Unity 音频",
                WallpaperType.godot => "Godot",
                WallpaperType.bizhawk => "BizHawk",
                WallpaperType.videostream => "视频流",
                _ => type.ToString(),
            };
        }

        private IEnumerable<WallpaperData> GetRunningWallpapers()
        {
            try
            {
                return desktopCore.Wallpapers ?? Enumerable.Empty<WallpaperData>();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "读取正在运行的壁纸失败");
                return Enumerable.Empty<WallpaperData>();
            }
        }

        private static RecentProjectMeta ToMeta(GeneratedWallpaper item) => new()
        {
            Title = item.Title,
            Prompt = item.Prompt,
            Style = item.Style,
            Color = item.Color,
            HtmlPath = item.HtmlPath,
            ThumbnailPath = item.ThumbnailPath,
            CreatedAt = item.CreatedAt,
            LibraryFolderPath = item.LibraryFolderPath,
            IsPackaged = item.IsPackaged,
            SessionId = item.SessionId,
        };

        private static GeneratedWallpaper FromMeta(string folder, RecentProjectMeta meta)
        {
            if (meta == null)
                return null;

            var html = Resolve(folder, meta.HtmlPath) ?? Path.Combine(folder, "stage", "index.html");
            var thumb = Resolve(folder, meta.ThumbnailPath);
            return new GeneratedWallpaper
            {
                ProjectFolder = folder,
                HtmlPath = html,
                ThumbnailPath = File.Exists(thumb) ? thumb : null,
                Title = meta.Title,
                Prompt = meta.Prompt,
                Style = string.IsNullOrWhiteSpace(meta.Style) ? "auto" : meta.Style,
                Color = meta.Color,
                CreatedAt = meta.CreatedAt == default ? Directory.GetCreationTime(folder) : meta.CreatedAt,
                LibraryFolderPath = meta.LibraryFolderPath,
                IsPackaged = meta.IsPackaged,
                SessionId = meta.SessionId,
            };
        }

        private static string Resolve(string baseFolder, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            try
            {
                return Path.IsPathRooted(path) ? path : Path.Combine(baseFolder, path);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsUnderRoot(string path, string root)
        {
            try
            {
                var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var baseFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return full.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string RandomHex(int length)
        {
            var bytes = RandomNumberGenerator.GetBytes(Math.Max(1, length / 2));
            return Convert.ToHexString(bytes).ToLowerInvariant().Substring(0, Math.Min(length, bytes.Length * 2));
        }

        private static Color ParseSystemColor(string hex, string fallback)
        {
            try
            {
                return ColorTranslator.FromHtml(string.IsNullOrWhiteSpace(hex) ? fallback : hex);
            }
            catch (Exception)
            {
                return Color.FromArgb(255, 110, 168, 254);
            }
        }

        private static Color Blend(Color foreground, Color background, double foregroundWeight)
        {
            var weight = Math.Clamp(foregroundWeight, 0, 1);
            return Color.FromArgb(
                (int)(foreground.A * weight + background.A * (1 - weight)),
                (int)(foreground.R * weight + background.R * (1 - weight)),
                (int)(foreground.G * weight + background.G * (1 - weight)),
                (int)(foreground.B * weight + background.B * (1 - weight)));
        }

        private static string Shorten(string text, int max)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "Contactable Wallpaper";
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }
    }
}
