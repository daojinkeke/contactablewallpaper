using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Lively.Models;
using Lively.Models.Packaging;
using Newtonsoft.Json;

namespace Lively.Packaging
{
    /// <summary>
    /// 把暂存目录安装进实例壁纸库（07 契约 §7）。同一 ProjectId 复用同一目录。
    /// </summary>
    public interface IInstallationManager
    {
        Task<InstallResult> InstallAsync(InstallRequest request, CancellationToken token);
    }

    public class InstallationManager : IInstallationManager
    {
        private static readonly Regex NonSlugRegex = new Regex(@"[^a-z0-9]+", RegexOptions.Compiled);

        public Task<InstallResult> InstallAsync(InstallRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var result = new InstallResult { Success = false };

            if (request == null ||
                string.IsNullOrWhiteSpace(request.StagingDirectory) ||
                !Directory.Exists(request.StagingDirectory))
            {
                result.ErrorCode = "install_failed";
                result.Message = "暂存目录不存在，无法安装。";
                return Task.FromResult(result);
            }

            var stagingInfo = Path.Combine(request.StagingDirectory, WallpaperPackageBuilder.InfoFileName);
            var stagingHtml = Path.Combine(request.StagingDirectory, WallpaperPackageBuilder.EntryFileName);
            if (!File.Exists(stagingInfo) || !File.Exists(stagingHtml))
            {
                result.ErrorCode = "install_failed";
                result.Message = "暂存目录缺少 LivelyInfo.json 或 index.html。";
                return Task.FromResult(result);
            }

            if (string.IsNullOrWhiteSpace(request.TargetWallpaperDir))
            {
                result.ErrorCode = "install_failed";
                result.Message = "未指定壁纸库目录。";
                return Task.FromResult(result);
            }

            try
            {
                var wallpapersDir = Path.Combine(request.TargetWallpaperDir, "wallpapers");
                Directory.CreateDirectory(wallpapersDir);

                var shortId = ShortId(request.ProjectId, stagingInfo);
                var slug = string.IsNullOrWhiteSpace(request.DirectorySlug)
                    ? Slugify(ReadTitle(stagingInfo))
                    : Slugify(request.DirectorySlug);

                var existing = Directory.GetDirectories(wallpapersDir)
                    .FirstOrDefault(dir => Path.GetFileName(dir).EndsWith("-" + shortId, StringComparison.OrdinalIgnoreCase));
                var installDirectory = existing ?? Path.Combine(wallpapersDir, slug + "-" + shortId);

                var copyDir = Path.Combine(wallpapersDir, "." + Path.GetFileName(installDirectory) + ".new-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                CopyDirectory(request.StagingDirectory, copyDir);

                string backupDirectory = null;
                if (Directory.Exists(installDirectory))
                {
                    backupDirectory = installDirectory + ".bak-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                    Directory.Move(installDirectory, backupDirectory);
                }

                try
                {
                    Directory.Move(copyDir, installDirectory);
                }
                catch (Exception)
                {
                    // 回滚：恢复原目录，清理新副本。
                    if (backupDirectory != null && !Directory.Exists(installDirectory))
                        Directory.Move(backupDirectory, installDirectory);
                    TryDelete(copyDir);
                    throw;
                }

                if (backupDirectory != null)
                    TryDelete(backupDirectory);

                result.Success = true;
                result.InstallDirectory = installDirectory;
                result.LivelyInfoPath = Path.Combine(installDirectory, WallpaperPackageBuilder.InfoFileName);
                result.ReusedExistingDirectory = existing != null;
            }
            catch (UnauthorizedAccessException ex)
            {
                result.ErrorCode = "install_permission_denied";
                result.Message = "没有权限写入壁纸库，请检查目录权限。" + ex.Message;
            }
            catch (IOException ex)
            {
                result.ErrorCode = "install_failed";
                result.Message = "安装壁纸失败，目标目录可能正被占用：" + ex.Message;
            }
            catch (Exception ex)
            {
                result.ErrorCode = "install_failed";
                result.Message = "安装壁纸失败：" + ex.Message;
            }

            return Task.FromResult(result);
        }

        private static string ShortId(string projectId, string stagingInfoPath)
        {
            var seed = string.IsNullOrWhiteSpace(projectId) ? stagingInfoPath : projectId.Trim();
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(seed));
            var builder = new StringBuilder();
            for (var i = 0; i < 4; i++)
                builder.Append(hash[i].ToString("x2"));
            return builder.ToString();
        }

        private static string ReadTitle(string stagingInfoPath)
        {
            try
            {
                var info = JsonConvert.DeserializeObject<LivelyInfoModel>(File.ReadAllText(stagingInfoPath, Encoding.UTF8));
                return info?.Title ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        public static string Slugify(string text)
        {
            var slug = NonSlugRegex.Replace((text ?? string.Empty).ToLowerInvariant(), "-").Trim('-');
            if (slug.Length == 0)
                return "wallpaper";
            return slug.Length <= 32 ? slug : slug.Substring(0, 32).Trim('-');
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            foreach (var directory in Directory.GetDirectories(source))
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }

        private static void TryDelete(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
            catch (Exception)
            {
                // 备份清理失败不影响安装结果。
            }
        }
    }
}
