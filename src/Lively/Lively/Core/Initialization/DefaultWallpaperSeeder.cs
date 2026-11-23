using Lively.Common;
using System;
using System.IO;

namespace Lively.Core.Initialization
{
    /// <summary>
    /// 首次运行时向空的壁纸库播种随包默认壁纸。
    /// 触发条件：壁纸库为空且本机从未播种过（标记文件不存在）。
    /// 已有条目绝不覆盖，单个壁纸失败只跳过，不阻断启动。
    /// </summary>
    public static class DefaultWallpaperSeeder
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        // 随包资源目录，构建输出为 <应用目录>\Assets\DefaultWallpapers。
        private const string SourceFolderName = "DefaultWallpapers";
        private const string MetadataFileName = "LivelyInfo.json";
        private const string MarkerFileName = "DefaultWallpapers.seeded";

        /// <summary>
        /// 播种默认壁纸，返回成功导入的数量；任何异常都会被吞掉并记日志。
        /// </summary>
        public static int Seed(string wallpaperDir)
        {
            try
            {
                var sourceDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", SourceFolderName);
                if (!Directory.Exists(sourceDir))
                {
                    Logger.Info($"Default wallpaper seed skipped: source directory missing ({sourceDir}).");
                    return 0;
                }

                var markerPath = Path.Combine(Constants.CommonPaths.AppDataDir, MarkerFileName);
                if (File.Exists(markerPath))
                    return 0;

                var installDir = Path.Combine(wallpaperDir, Constants.CommonPartialPaths.WallpaperInstallDir);
                if (!IsLibraryEmpty(installDir))
                {
                    Logger.Info("Default wallpaper seed skipped: wallpaper library is not empty.");
                    return 0;
                }

                var imported = 0;
                foreach (var source in Directory.GetDirectories(sourceDir))
                {
                    var name = Path.GetFileName(source);
                    var destination = Path.Combine(installDir, name);

                    if (!File.Exists(Path.Combine(source, MetadataFileName)))
                    {
                        Logger.Warn($"Default wallpaper seed skipped: incomplete asset '{name}'.");
                        continue;
                    }

                    if (Directory.Exists(destination))
                    {
                        // 用户已有同名目录，绝不覆盖。
                        Logger.Info($"Default wallpaper seed skipped: '{name}' already exists.");
                        continue;
                    }

                    try
                    {
                        CopyDirectory(source, destination);
                        imported++;
                        Logger.Info($"Default wallpaper seeded: {name}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, $"Default wallpaper seed failed, skipping '{name}'.");
                        TryDelete(destination);
                    }
                }

                if (imported > 0)
                {
                    File.WriteAllText(markerPath, DateTime.UtcNow.ToString("O"));
                    Logger.Info($"Default wallpaper seed finished: {imported} wallpaper(s) imported.");
                }

                return imported;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Default wallpaper seed failed, continuing startup.");
                return 0;
            }
        }

        /// <summary>
        /// 库为空指安装目录下没有任何带 LivelyInfo.json 的壁纸条目。
        /// 读取失败时按“非空”处理，避免误写用户库。
        /// </summary>
        private static bool IsLibraryEmpty(string installDir)
        {
            try
            {
                if (!Directory.Exists(installDir))
                    return true;

                foreach (var dir in Directory.GetDirectories(installDir))
                {
                    if (File.Exists(Path.Combine(dir, MetadataFileName)))
                        return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Default wallpaper seed skipped: library could not be read.");
                return false;
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), false);

            foreach (var dir in Directory.GetDirectories(source))
                CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch { /* 清理失败不影响启动 */ }
        }
    }
}
