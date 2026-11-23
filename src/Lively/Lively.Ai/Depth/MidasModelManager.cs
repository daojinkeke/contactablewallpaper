using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Lively.Common;
using Lively.Common.Helpers.Archive;

namespace Lively.Ai.Depth
{
    public enum MidasModelPhase
    {
        downloading,
        verifying,
        extracting,
        completed
    }

    public sealed class MidasModelProgress
    {
        public MidasModelProgress(MidasModelPhase phase, long downloadedBytes, long totalBytes)
        {
            Phase = phase;
            DownloadedBytes = downloadedBytes;
            TotalBytes = totalBytes;
        }

        public MidasModelPhase Phase { get; }

        public long DownloadedBytes { get; }

        public long TotalBytes { get; }

        public double Fraction => TotalBytes > 0 ? Math.Min(1d, (double)DownloadedBytes / TotalBytes) : 0d;
    }

    /// <summary>
    /// MiDaS 模型与 Three.js 模板的获取/校验/解压（首次使用时下载，之后离线可用）。
    /// 位置沿用基线：<c>%LOCALAPPDATA%\Lively Wallpaper\ML\Midas</c>（见 Constants.MachineLearning.MiDaSDir）。
    /// </summary>
    public interface IMidasModelManager
    {
        bool IsInstalled { get; }

        string ModelPath { get; }

        string TemplateDir { get; }

        Task<MidasModelInstallResult> EnsureAsync(IProgress<MidasModelProgress> progress, CancellationToken token);
    }

    public sealed class MidasModelInstallResult
    {
        public MidasModelInstallResult(string modelPath, string templateDir, string archivePath, bool downloadedThisTime)
        {
            ModelPath = modelPath;
            TemplateDir = templateDir;
            ArchivePath = archivePath;
            DownloadedThisTime = downloadedThisTime;
        }

        public string ModelPath { get; }

        public string TemplateDir { get; }

        public string ArchivePath { get; }

        public bool DownloadedThisTime { get; }
    }

    public sealed class MidasModelManager : IMidasModelManager
    {
        /// <summary>
        /// 与基线 DepthEstimateWallpaperViewModel.GetModelUrl() 相同的发布仓库与资产名。
        /// 用固定版本 URL 而非 GitHub API 最新版，避免上游发新版导致哈希不匹配。
        /// </summary>
        public const string DefaultDownloadUrl =
            "https://github.com/lively-community/lively-ml-models/releases/download/v1.0.0.0/midas_small.zip";

        /// <summary>midas_small.zip 官方大小（字节），用于下载完整性校验。</summary>
        public const long ArchiveSize = 62046014L;

        /// <summary>midas_small.zip 的 SHA-256（小写十六进制），用于防篡改/防半包。</summary>
        public const string ArchiveSha256 = "e53fb39c98ea9f9d57fb3856a25829e813d0d9d497c0d447688ec56f56bab4c9";

        public const string ModelFileName = "model.onnx";

        private static readonly string ArchiveName = "midas_small.zip";

        private readonly HttpClient http;
        private readonly string rootDir;

        public MidasModelManager(string rootDir = null, HttpClient httpClient = null)
        {
            this.rootDir = rootDir ?? Constants.MachineLearning.MiDaSDir;
            http = httpClient ?? new HttpClient();
            http.Timeout = TimeSpan.FromMinutes(30);
        }

        public string RootDir => rootDir;

        public string ModelPath => Path.Combine(rootDir, ModelFileName);

        public string TemplateDir => Path.Combine(rootDir, "Templates", "0");

        public string ArchivePath => Path.Combine(rootDir, ArchiveName);

        public bool IsInstalled => File.Exists(ModelPath) && Directory.Exists(TemplateDir);

        public async Task<MidasModelInstallResult> EnsureAsync(IProgress<MidasModelProgress> progress, CancellationToken token)
        {
            Directory.CreateDirectory(rootDir);

            var downloaded = false;
            if (!IsInstalled)
            {
                await EnsureArchiveAsync(progress, token).ConfigureAwait(false);
                downloaded = true;
                ExtractArchive(progress, token);
            }

            Report(progress, MidasModelPhase.completed, ArchiveSize, ArchiveSize);
            return new MidasModelInstallResult(ModelPath, TemplateDir, ArchivePath, downloaded);
        }

        private async Task EnsureArchiveAsync(IProgress<MidasModelProgress> progress, CancellationToken token)
        {
            if (File.Exists(ArchivePath) && IsArchiveValid(ArchivePath))
                return;

            var url = Environment.GetEnvironmentVariable("LIVELY_MIDAS_URL");
            if (string.IsNullOrWhiteSpace(url))
                url = DefaultDownloadUrl;

            var partPath = ArchivePath + ".part";
            if (File.Exists(ArchivePath) && !File.Exists(partPath))
            {
                // 旧包校验失败：保留为 .bad 便于排查，然后重新下载。
                TryMove(ArchivePath, ArchivePath + ".bad");
            }

            long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            if (existing >= ArchiveSize)
            {
                TryMove(partPath, ArchivePath);
                if (IsArchiveValid(ArchivePath))
                    return;
                TryMove(ArchivePath, ArchivePath + ".bad");
                existing = 0;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (existing > 0)
                request.Headers.Range = new RangeHeaderValue(existing, null);

            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // 服务端不认断点：从头来。
                TryDelete(partPath);
                await EnsureArchiveAsync(progress, token).ConfigureAwait(false);
                return;
            }

            response.EnsureSuccessStatusCode();

            var append = response.StatusCode == HttpStatusCode.PartialContent && existing > 0;
            if (!append)
                existing = 0;

            var total = response.Content.Headers.ContentLength.HasValue
                ? response.Content.Headers.ContentLength.Value + existing
                : ArchiveSize;

            Report(progress, MidasModelPhase.downloading, existing, total);

            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var target = new FileStream(partPath, append ? FileMode.Append : FileMode.Create,
                             FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long written = existing;
                long lastReportTicks = 0;
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    written += read;

                    // 限流上报：每 150 ms 最多一次，避免 UI/控制台被刷屏。
                    var now = Environment.TickCount64;
                    if (now - lastReportTicks >= 150)
                    {
                        lastReportTicks = now;
                        Report(progress, MidasModelPhase.downloading, written, total);
                    }
                }

                Report(progress, MidasModelPhase.downloading, written, total);
            }

            Report(progress, MidasModelPhase.verifying, ArchiveSize, ArchiveSize);
            if (!IsArchiveValid(partPath))
            {
                var badPath = ArchivePath + ".bad";
                TryMove(partPath, badPath);
                throw new InvalidDataException(
                    $"模型包校验失败（大小或 SHA-256 不匹配），已保留为 {badPath}。请重试下载。");
            }

            TryDelete(ArchivePath);
            File.Move(partPath, ArchivePath);
        }

        private void ExtractArchive(IProgress<MidasModelProgress> progress, CancellationToken token)
        {
            Report(progress, MidasModelPhase.extracting, ArchiveSize, ArchiveSize);

            var staging = Path.Combine(rootDir, ".extract-" + Guid.NewGuid().ToString("N"));
            try
            {
                ZipExtract.ZipExtractFile(ArchivePath, staging, false);

                var stagedModel = Path.Combine(staging, ModelFileName);
                var stagedTemplate = Path.Combine(staging, "Templates", "0");
                if (!File.Exists(stagedModel) || !Directory.Exists(stagedTemplate))
                    throw new InvalidDataException("模型包内容不完整：缺少 model.onnx 或 Templates/0。");

                // 已存在完整模型时不覆盖（避免把可用文件写坏）。
                if (IsInstalled)
                    return;

                token.ThrowIfCancellationRequested();

                // 逐个条目移动到根目录：只有完整解压校验通过后才写入，不会产生半写文件。
                foreach (var file in Directory.GetFiles(staging, "*", SearchOption.AllDirectories))
                {
                    token.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(staging, file);
                    var destination = Path.Combine(rootDir, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination, overwrite: true);
                }
            }
            finally
            {
                TryDeleteDirectory(staging);
            }
        }

        private static bool IsArchiveValid(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != ArchiveSize)
                    return false;

                using var stream = File.OpenRead(path);
                using var sha = SHA256.Create();
                var hash = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
                return string.Equals(hash, ArchiveSha256, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void Report(IProgress<MidasModelProgress> progress, MidasModelPhase phase, long done, long total)
        {
            progress?.Report(new MidasModelProgress(phase, done, total));
        }

        private static void TryMove(string from, string to)
        {
            try
            {
                if (File.Exists(to))
                    File.Delete(to);
                File.Move(from, to);
            }
            catch
            {
                // 清理失败不阻塞主流程。
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // ignore
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch
            {
                // ignore
            }
        }
    }
}
