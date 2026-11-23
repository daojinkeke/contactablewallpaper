using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lively.Common;
using Lively.Models;
using Lively.Models.Enums;
using Lively.Models.Packaging;
using Newtonsoft.Json;

namespace Lively.Packaging
{
    /// <summary>
    /// 把生成好的 HTML 落成标准 Lively 壁纸目录：LivelyInfo.json + index.html（07 契约 §7）。
    /// </summary>
    public interface IPackageBuilder
    {
        Task<PackageResult> BuildAsync(PackageRequest request, CancellationToken token);
    }

    public class WallpaperPackageBuilder : IPackageBuilder
    {
        public const string InfoFileName = "LivelyInfo.json";
        public const string EntryFileName = "index.html";
        public const string ThumbnailFileName = "thumbnail.png";
        public const string DefaultAuthor = "ContactableWallpaper";

        public Task<PackageResult> BuildAsync(PackageRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var result = new PackageResult { Success = false };

            if (request == null)
            {
                result.Issues.Add("打包请求为空。");
                return Task.FromResult(result);
            }

            if (string.IsNullOrWhiteSpace(request.HtmlPath) || !File.Exists(request.HtmlPath))
            {
                result.Issues.Add("待打包的 HTML 不存在：" + request.HtmlPath);
                return Task.FromResult(result);
            }

            var stagingRoot = string.IsNullOrWhiteSpace(request.StagingRoot)
                ? Constants.CommonPaths.PackagingTempDir
                : request.StagingRoot;
            var stagingDirectory = Path.Combine(stagingRoot, "pkg-" + Guid.NewGuid().ToString("N"));

            try
            {
                if (Directory.Exists(stagingDirectory))
                    Directory.Delete(stagingDirectory, true);
                Directory.CreateDirectory(stagingDirectory);

                var entryPath = Path.Combine(stagingDirectory, EntryFileName);
                File.Copy(request.HtmlPath, entryPath, true);
                result.Files.Add(EntryFileName);

                var thumbnailFile = WriteThumbnail(request, stagingDirectory);
                result.Files.Add(thumbnailFile);

                var title = string.IsNullOrWhiteSpace(request.Title)
                    ? MakeTitleFromPrompt(request.Prompt)
                    : request.Title.Trim();

                var info = new LivelyInfoModel
                {
                    AppVersion = GetAppVersion(),
                    Title = title,
                    Thumbnail = thumbnailFile,
                    Preview = null,
                    Desc = Truncate(request.Prompt?.Trim(), 200),
                    Author = DefaultAuthor,
                    License = "MIT",
                    Contact = null,
                    Type = WallpaperType.web,
                    FileName = EntryFileName,
                    Arguments = null,
                    IsAbsolutePath = false,
                    Id = null,
                    Tags = BuildTags(request),
                    Version = Math.Max(1, request.VersionNumber),
                    //Contactable extension metadata.
                    Prompt = string.IsNullOrWhiteSpace(request.Prompt) ? null : request.Prompt.Trim(),
                    GeneratedAt = request.GeneratedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
                };

                var infoPath = Path.Combine(stagingDirectory, InfoFileName);
                File.WriteAllText(infoPath,
                    JsonConvert.SerializeObject(info, Formatting.Indented),
                    new UTF8Encoding(false));
                result.Files.Add(InfoFileName);

                result.StagingDirectory = stagingDirectory;
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Issues.Add("打包失败：" + ex.Message);
                TryDelete(stagingDirectory);
            }

            return Task.FromResult(result);
        }

        private static string WriteThumbnail(PackageRequest request, string stagingDirectory)
        {
            if (!string.IsNullOrWhiteSpace(request.ThumbnailPath) && File.Exists(request.ThumbnailPath))
            {
                var extension = Path.GetExtension(request.ThumbnailPath);
                if (string.IsNullOrEmpty(extension))
                    extension = ".png";
                var fileName = "thumbnail" + extension.ToLowerInvariant();
                File.Copy(request.ThumbnailPath, Path.Combine(stagingDirectory, fileName), true);
                return fileName;
            }

            PlaceholderThumbnail.Write(Path.Combine(stagingDirectory, ThumbnailFileName));
            return ThumbnailFileName;
        }

        private static List<string> BuildTags(PackageRequest request)
        {
            var tags = new List<string>
            {
                "contactable",
                "style:" + request.Style.ToString().ToLowerInvariant()
            };
            if (request.Interaction != null && request.Interaction.Mode != InputForwardMode.off)
                tags.Add("interaction:" + request.Interaction.Mode.ToString().ToLowerInvariant());
            return tags;
        }

        private static string MakeTitleFromPrompt(string prompt)
        {
            var text = (prompt ?? string.Empty).Trim().Replace("\r", " ").Replace("\n", " ");
            if (text.Length == 0)
                return "AI Wallpaper";
            return text.Length <= 24 ? text : text.Substring(0, 24) + "…";
        }

        private static string GetAppVersion()
        {
            var entry = Assembly.GetEntryAssembly();
            var version = entry?.GetName().Version;
            if (version != null && version.Major > 0)
                return version.ToString();

            var core = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "Lively", StringComparison.OrdinalIgnoreCase));
            return core?.GetName().Version?.ToString() ?? "0.0.0.0";
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            return text.Length <= max ? text : text.Substring(0, max);
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
                // 清理失败不影响结果上报。
            }
        }
    }
}
