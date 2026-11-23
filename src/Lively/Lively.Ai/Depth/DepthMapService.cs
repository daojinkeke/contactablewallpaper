using System;
using System.IO;
using ImageMagick;
using Lively.ML.DepthEstimate;
using Lively.ML.Helpers;

namespace Lively.Ai.Depth
{
    public sealed class DepthMapResult
    {
        public DepthMapResult(string depthImagePath, int width, int height, int modelWidth, int modelHeight)
        {
            DepthImagePath = depthImagePath;
            Width = width;
            Height = height;
            ModelWidth = modelWidth;
            ModelHeight = modelHeight;
        }

        /// <summary>归一化深度图（灰度 PNG）路径。</summary>
        public string DepthImagePath { get; }

        public int Width { get; }

        public int Height { get; }

        public int ModelWidth { get; }

        public int ModelHeight { get; }
    }

    /// <summary>
    /// 单目深度估计。直接使用基线 <see cref="MiDaS"/>（Lively.ML），不重写推理/前后处理参数。
    /// </summary>
    public interface IDepthMapService
    {
        DepthMapResult Create(string imagePath, string modelPath, string depthPngPath);
    }

    public sealed class DepthMapService : IDepthMapService
    {
        public DepthMapResult Create(string imagePath, string modelPath, string depthPngPath)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                throw new FileNotFoundException("找不到输入图片。", imagePath);
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
                throw new FileNotFoundException("找不到 MiDaS 模型文件。", modelPath);

            using var model = new MiDaS();
            model.LoadModel(modelPath);

            // MiDaS.Run 内部按模型输入尺寸等比缩放到 256x256（忽略宽高比）并做 min-max 归一化，
            // 与基线 DepthEstimateWallpaperViewModel 完全一致。
            var output = model.Run(imagePath);

            var directory = Path.GetDirectoryName(depthPngPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using var depth = ImageUtil.FloatArrayToMagickImage(output.Depth, output.Width, output.Height);
            depth.Resize(new MagickGeometry((uint)output.OriginalWidth, (uint)output.OriginalHeight)
            {
                IgnoreAspectRatio = true
            });
            depth.ColorSpace = ColorSpace.Gray;
            depth.Alpha(AlphaOption.Off);
            depth.ColorType = ColorType.Grayscale;
            depth.Write(depthPngPath, MagickFormat.Png);

            return new DepthMapResult(depthPngPath, output.OriginalWidth, output.OriginalHeight,
                output.Width, output.Height);
        }
    }
}
