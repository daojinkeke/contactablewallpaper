using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;
using Lively.Common.Helpers.Storage;
using Lively.Models;
using Lively.Models.Ai;

namespace Lively.Ai.Depth
{
    public enum DepthGenerationStage
    {
        checking_model,
        downloading_model,
        verifying_model,
        extracting_model,
        estimating_depth,
        encoding_images,
        writing_wallpaper,
        validating,
        completed
    }

    public sealed class DepthGenerationProgress
    {
        public DepthGenerationProgress(DepthGenerationStage stage, string message, double fraction = 0d)
        {
            Stage = stage;
            Message = message;
            Fraction = fraction;
        }

        public DepthGenerationStage Stage { get; }

        public string Message { get; }

        /// <summary>整体进度 0–1；下载阶段按 0–0.8 折算。</summary>
        public double Fraction { get; }
    }

    public sealed class DepthWallpaperRequest
    {
        public string ImagePath { get; set; }

        /// <summary>输出目录，生成后即为可直接挂载的壁纸目录。</summary>
        public string OutputDirectory { get; set; }

        public string Title { get; set; }

        /// <summary>模型根目录，默认 <c>Constants.MachineLearning.MiDaSDir</c>。</summary>
        public string ModelRoot { get; set; }

        /// <summary>嵌入 HTML 的原图长边上限，控制单文件体积（默认 2560）。</summary>
        public int MaxImageLongSide { get; set; } = 2560;

        /// <summary>嵌入 HTML 的深度图长边上限（默认 1024）。</summary>
        public int MaxEmbeddedDepthLongSide { get; set; } = 1024;
    }

    public sealed class DepthWallpaperResult
    {
        public string WallpaperDirectory { get; set; }

        public string HtmlPath { get; set; }

        /// <summary>单文件预览版（Three.js 与图片全内联），不用于 Lively 挂载。</summary>
        public string SingleFileHtmlPath { get; set; }

        /// <summary>完整分辨率深度图（灰度 PNG）。</summary>
        public string DepthPngPath { get; set; }

        public string SourceImagePath { get; set; }

        public string ThumbnailPath { get; set; }

        public string LivelyInfoPath { get; set; }

        public long HtmlBytes { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        public IReadOnlyList<ValidationIssue> Issues { get; set; } = Array.Empty<ValidationIssue>();
    }

    public interface IDepthWallpaperService
    {
        Task<DepthWallpaperResult> GenerateAsync(DepthWallpaperRequest request,
            IProgress<DepthGenerationProgress> progress, CancellationToken token);
    }

    /// <summary>
    /// 照片 → 深度视差壁纸。推理复用 <see cref="DepthMapService"/>（MiDaS），
    /// 产物遵循 Lively 壁纸目录规范：index.html + LivelyInfo.json + thumbnail。
    /// </summary>
    public sealed class DepthWallpaperService : IDepthWallpaperService
    {
        private readonly IMidasModelManager models;
        private readonly IDepthMapService depth;
        private readonly IDepthWallpaperHtmlBuilder htmlBuilder;

        public DepthWallpaperService(IMidasModelManager models = null, IDepthMapService depth = null,
            IDepthWallpaperHtmlBuilder htmlBuilder = null)
        {
            this.models = models ?? new MidasModelManager();
            this.depth = depth ?? new DepthMapService();
            this.htmlBuilder = htmlBuilder ?? new SingleFileDepthWallpaperHtmlBuilder();
        }

        public async Task<DepthWallpaperResult> GenerateAsync(DepthWallpaperRequest request,
            IProgress<DepthGenerationProgress> progress, CancellationToken token)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.ImagePath) || !File.Exists(request.ImagePath))
                throw new FileNotFoundException("找不到输入图片。", request.ImagePath);

            var outputDir = Path.GetFullPath(request.OutputDirectory);
            var mediaDir = Path.Combine(outputDir, "media");
            Directory.CreateDirectory(mediaDir);

            // 1. 模型：内置推理代码，首次使用下载权重（离线后不再联网）。
            var modelManager = string.IsNullOrWhiteSpace(request.ModelRoot)
                ? models
                : new MidasModelManager(request.ModelRoot);
            if (!modelManager.IsInstalled)
            {
                Report(progress, DepthGenerationStage.checking_model, "未找到 MiDaS 模型，准备下载（约 62 MB）。", 0d);
                await modelManager.EnsureAsync(new Progress<MidasModelProgress>(p =>
                {
                    var stage = p.Phase switch
                    {
                        MidasModelPhase.downloading => DepthGenerationStage.downloading_model,
                        MidasModelPhase.verifying => DepthGenerationStage.verifying_model,
                        MidasModelPhase.extracting => DepthGenerationStage.extracting_model,
                        _ => DepthGenerationStage.checking_model
                    };
                    var message = p.Phase switch
                    {
                        MidasModelPhase.downloading =>
                            $"下载模型 {p.DownloadedBytes / 1024d / 1024d:0.0}/{p.TotalBytes / 1024d / 1024d:0.0} MB（{p.Fraction:P0}）",
                        MidasModelPhase.verifying => "校验模型包大小与 SHA-256…",
                        MidasModelPhase.extracting => "解压模型与 Three.js 模板…",
                        _ => "模型就绪。"
                    };
                    Report(progress, stage, message, 0.8d * p.Fraction);
                }), token).ConfigureAwait(false);
            }
            else
            {
                Report(progress, DepthGenerationStage.checking_model, "已找到本地模型，离线推理。", 0.8d);
            }

            token.ThrowIfCancellationRequested();

            // 2. 规范化输入（与基线一致：去元数据，超 3840 先等比缩小）。
            Report(progress, DepthGenerationStage.estimating_depth, "正在估计深度…", 0.85d);
            // 与基线一致：模板固定读取 media/image.jpg 与 media/depth.jpg。
            var inputPath = Path.Combine(mediaDir, "image.jpg");
            int sourceWidth, sourceHeight;
            using (var source = new MagickImage(request.ImagePath))
            {
                source.AutoOrient();
                source.Strip();
                if (source.Width > 3840 || source.Height > 3840)
                {
                    source.Resize(new MagickGeometry(3840, 3840) { IgnoreAspectRatio = false });
                }

                sourceWidth = (int)source.Width;
                sourceHeight = (int)source.Height;
                source.Format = MagickFormat.Jpeg;
                source.Quality = 95;
                source.Write(inputPath);
            }

            token.ThrowIfCancellationRequested();

            // 3. 深度图（复用基线 MiDaS）。
            var depthPngPath = Path.Combine(mediaDir, "depth.png");
            var depthResult = depth.Create(inputPath, modelManager.ModelPath, depthPngPath);
            Report(progress, DepthGenerationStage.estimating_depth,
                $"深度估计完成（模型输入 {depthResult.ModelWidth}x{depthResult.ModelHeight}）。", 0.9d);

            token.ThrowIfCancellationRequested();

            // 4. 编码为可内联的数据（控制单文件体积）。
            Report(progress, DepthGenerationStage.encoding_images, "编码原图与深度图…", 0.92d);
            var imageBytes = EncodeImage(inputPath, request.MaxImageLongSide, 85);
            var depthBytes = EncodeImage(depthPngPath, request.MaxEmbeddedDepthLongSide, 80);

            // 5. 生成壁纸目录（模板本地资源 + 单文件预览）。
            Report(progress, DepthGenerationStage.writing_wallpaper, "生成壁纸目录…", 0.95d);
            var title = string.IsNullOrWhiteSpace(request.Title)
                ? Path.GetFileNameWithoutExtension(request.ImagePath)
                : request.Title;

            CopyTemplateScripts(modelManager.TemplateDir, outputDir);
            File.WriteAllText(Path.Combine(outputDir, "js", "script.js"),
                htmlBuilder.BuildScript(modelManager.TemplateDir), new UTF8Encoding(false));

            // 供模板着色器采样的深度图（jpg，控制体积）。
            using (var depthJpg = new MagickImage(depthPngPath))
            {
                if (depthJpg.Width > 2048 || depthJpg.Height > 2048)
                {
                    depthJpg.Resize(new MagickGeometry(2048u, 2048u) { IgnoreAspectRatio = false });
                }
                depthJpg.Format = MagickFormat.Jpeg;
                depthJpg.Quality = 85u;
                depthJpg.Write(Path.Combine(mediaDir, "depth.jpg"));
            }

            var htmlPath = Path.Combine(outputDir, "index.html");
            File.WriteAllText(htmlPath,
                htmlBuilder.BuildIndexHtml(modelManager.TemplateDir, title), new UTF8Encoding(false));

            var singleFilePath = Path.Combine(outputDir, "index.single.html");
            File.WriteAllText(singleFilePath, htmlBuilder.BuildSingleFile(new DepthWallpaperHtmlInput
            {
                ImageBytes = imageBytes,
                ImageMime = "image/jpeg",
                DepthBytes = depthBytes,
                DepthMime = "image/jpeg",
                Title = title,
                TemplateDirectory = modelManager.TemplateDir
            }), new UTF8Encoding(false));

            var thumbnailPath = Path.Combine(outputDir, "thumbnail.jpg");
            WriteThumbnail(inputPath, thumbnailPath);
            CopyTemplateExtras(modelManager.TemplateDir, outputDir);

            var livelyInfoPath = Path.Combine(outputDir, "LivelyInfo.json");
            var info = new LivelyInfoModel
            {
                AppVersion = typeof(DepthWallpaperService).Assembly.GetName().Version?.ToString(),
                Title = title,
                Desc = "照片深度视差壁纸：鼠标移动产生视差，静止时缓慢浮动。",
                Author = "contactable",
                License = "MIT",
                Type = Lively.Models.Enums.WallpaperType.web,
                FileName = "index.html",
                Thumbnail = "thumbnail.jpg",
                // 让宿主在暂停/恢复时通知页面（模板 script.js 的 livelyWallpaperPlaybackChanged）。
                Arguments = "--pause-event true",
                IsAbsolutePath = false,
                Tags = new List<string> { "depth", "parallax" },
                Version = 1,
                GeneratedAt = DateTime.UtcNow.ToString("o")
            };
            JsonStorage<LivelyInfoModel>.StoreData(livelyInfoPath, info);

            // 6. 本地校验（07 契约 V001–V008），仅警告不阻断。
            Report(progress, DepthGenerationStage.validating, "校验离线可播性…", 0.98d);
            var validation = new HtmlWallpaperValidator().Validate(htmlPath, new ValidationOptions
            {
                OfflineRequired = true,
                MaxBytes = 5 * 1024 * 1024,
                TargetWidth = sourceWidth,
                TargetHeight = sourceHeight
            });

            Report(progress, DepthGenerationStage.completed, "完成。", 1d);

            return new DepthWallpaperResult
            {
                WallpaperDirectory = outputDir,
                HtmlPath = htmlPath,
                SingleFileHtmlPath = singleFilePath,
                DepthPngPath = depthPngPath,
                SourceImagePath = inputPath,
                ThumbnailPath = thumbnailPath,
                LivelyInfoPath = livelyInfoPath,
                HtmlBytes = new FileInfo(htmlPath).Length,
                Width = sourceWidth,
                Height = sourceHeight,
                Issues = validation.Issues
            };
        }

        private static byte[] EncodeImage(string path, int maxLongSide, int quality)
        {
            using var image = new MagickImage(path);
            if (maxLongSide > 0 && (image.Width > maxLongSide || image.Height > maxLongSide))
            {
                image.Resize(new MagickGeometry((uint)maxLongSide, (uint)maxLongSide)
                {
                    IgnoreAspectRatio = false
                });
            }

            image.Format = MagickFormat.Jpeg;
            image.Quality = (uint)quality;
            return image.ToByteArray();
        }

        private static void CopyTemplateScripts(string templateDir, string outputDir)
        {
            var jsDir = Path.Combine(outputDir, "js");
            Directory.CreateDirectory(jsDir);
            foreach (var name in new[] { "three.min.js", "dat.gui.min.js" })
            {
                var source = Path.Combine(templateDir, "js", name);
                if (File.Exists(source))
                    File.Copy(source, Path.Combine(jsDir, name), overwrite: true);
            }
        }

        /// <summary>
        /// 随模板附带 MIT 许可证与参数面板定义（LivelyProperties.json 供宿主读取滑块/开关）。
        /// </summary>
        private static void CopyTemplateExtras(string templateDir, string outputDir)
        {
            if (string.IsNullOrWhiteSpace(templateDir) || !Directory.Exists(templateDir))
                return;

            foreach (var name in new[] { "License.txt", "LivelyProperties.json" })
            {
                var source = Path.Combine(templateDir, name);
                if (File.Exists(source))
                    File.Copy(source, Path.Combine(outputDir, name), overwrite: true);
            }
        }

        private static void WriteThumbnail(string inputPath, string thumbnailPath)
        {
            using var image = new MagickImage(inputPath);
            image.Thumbnail(new MagickGeometry(0, 0, 480u, 270u)
            {
                IgnoreAspectRatio = false,
                FillArea = true
            });
            image.Extent(480u, 270u, Gravity.Center);
            image.Format = MagickFormat.Jpeg;
            image.Quality = 88u;
            image.Write(thumbnailPath);
        }

        private static void Report(IProgress<DepthGenerationProgress> progress, DepthGenerationStage stage,
            string message, double fraction)
        {
            progress?.Report(new DepthGenerationProgress(stage, message, fraction));
        }
    }
}
