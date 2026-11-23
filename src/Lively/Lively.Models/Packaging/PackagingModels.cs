using System;
using System.Collections.Generic;
using Lively.Models.Ai;
using Lively.Models.Enums;

namespace Lively.Models.Packaging
{
    /// <summary>
    /// 交互声明（07 契约 §4.3）。只描述壁纸希望怎样交互，不参与路由判定。
    /// </summary>
    public class InteractionProfile
    {
        public InputForwardMode Mode { get; set; }
        public bool ForwardWheel { get; set; }
        public bool ForwardMiddleButton { get; set; }
        public bool FullscreenInteraction { get; set; }
        public bool RequireHotzone { get; set; }

        public InteractionProfile()
        {
            Mode = InputForwardMode.off;
            RequireHotzone = true;
        }
    }

    /// <summary>
    /// 打包请求（07 契约 §7.3）。
    /// </summary>
    public class PackageRequest
    {
        public string ProjectId { get; set; }
        public string VersionId { get; set; }
        public int VersionNumber { get; set; }
        public string HtmlPath { get; set; }
        public string Title { get; set; }
        public string Prompt { get; set; }
        public StylePreset Style { get; set; }
        public InteractionProfile Interaction { get; set; }
        public string ThumbnailPath { get; set; }
        public string StagingRoot { get; set; }

        /// <summary>写入元数据的生成时间（UTC）。</summary>
        public DateTime GeneratedAtUtc { get; set; }

        public PackageRequest()
        {
            ProjectId = string.Empty;
            VersionId = string.Empty;
            VersionNumber = 1;
            Style = StylePreset.auto;
            Interaction = new InteractionProfile();
            GeneratedAtUtc = DateTime.UtcNow;
        }
    }

    public class PackageResult
    {
        public bool Success { get; set; }
        public string StagingDirectory { get; set; }
        public List<string> Files { get; set; }
        public List<string> Issues { get; set; }

        public PackageResult()
        {
            Files = new List<string>();
            Issues = new List<string>();
        }
    }

    /// <summary>
    /// 安装请求（07 契约 §7.3）。
    /// </summary>
    public class InstallRequest
    {
        /// <summary>已构建好的暂存目录。</summary>
        public string StagingDirectory { get; set; }

        /// <summary>壁纸库根目录（即 Settings.WallpaperDir）。</summary>
        public string TargetWallpaperDir { get; set; }

        public string ProjectId { get; set; }

        public string DirectorySlug { get; set; }
    }

    public class InstallResult
    {
        public bool Success { get; set; }
        public string LivelyInfoPath { get; set; }
        public string InstallDirectory { get; set; }
        public bool ReusedExistingDirectory { get; set; }
        public string ErrorCode { get; set; }
        public string Message { get; set; }
        public List<string> Issues { get; set; }

        public InstallResult()
        {
            Issues = new List<string>();
        }
    }
}
