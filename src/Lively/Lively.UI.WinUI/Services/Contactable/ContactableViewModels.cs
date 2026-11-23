using System;
using System.Collections.Generic;
using System.Linq;
using Lively.Models;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Lively.UI.WinUI.Services.Contactable
{
    /// <summary>
    /// 风格预设的静态定义（不持有 WinUI 对象，可安全在后台线程读取）。
    /// </summary>
    public sealed class ContactableStyleInfo
    {
        public string Key { get; init; }
        public string Name { get; init; }
        public string Description { get; init; }
        public string ColorHex { get; init; }
        public string GradientStartHex { get; init; }
        public string GradientEndHex { get; init; }
    }

    public static class ContactableStyles
    {
        public static IReadOnlyList<ContactableStyleInfo> All { get; } = new List<ContactableStyleInfo>
        {
            new() { Key = "minimal", Name = "极简", Description = "留白、克制线条与柔和光晕", ColorHex = "#94A3B8", GradientStartHex = "#1E293B", GradientEndHex = "#94A3B8" },
            new() { Key = "cyber", Name = "赛博", Description = "霓虹网格、冷光与高对比", ColorHex = "#22D3EE", GradientStartHex = "#0F172A", GradientEndHex = "#22D3EE" },
            new() { Key = "nature", Name = "自然", Description = "苔绿、雾气与缓慢呼吸感", ColorHex = "#34D399", GradientStartHex = "#052E2B", GradientEndHex = "#34D399" },
            new() { Key = "pixel", Name = "像素", Description = "低分辨率色块与复古颗粒", ColorHex = "#F472B6", GradientStartHex = "#1F1235", GradientEndHex = "#F472B6" },
            new() { Key = "ink", Name = "水墨", Description = "墨色扩散、留白与单色层次", ColorHex = "#64748B", GradientStartHex = "#0F172A", GradientEndHex = "#CBD5E1" },
        };

        public static ContactableStyleInfo Find(string key) =>
            All.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)) ?? All[0];

        public static Color ParseColor(string hex, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(hex))
                return fallback;
            try
            {
                var value = hex.Trim().TrimStart('#');
                if (value.Length == 6)
                    return Color.FromArgb(255,
                        Convert.ToByte(value.Substring(0, 2), 16),
                        Convert.ToByte(value.Substring(2, 2), 16),
                        Convert.ToByte(value.Substring(4, 2), 16));
                if (value.Length == 8)
                    return Color.FromArgb(
                        Convert.ToByte(value.Substring(0, 2), 16),
                        Convert.ToByte(value.Substring(2, 2), 16),
                        Convert.ToByte(value.Substring(4, 2), 16),
                        Convert.ToByte(value.Substring(6, 2), 16));
            }
            catch (Exception)
            {
                // fall through
            }
            return fallback;
        }
    }

    /// <summary>
    /// 风格卡片的数据项（PreviewBrush 在 UI 线程创建）。
    /// </summary>
    public sealed class StyleOption
    {
        public string Key { get; init; }
        public string Name { get; init; }
        public string Description { get; init; }
        public string ColorHex { get; init; }
        public Brush PreviewBrush { get; init; }
    }

    public sealed class LibraryItemVm
    {
        public string Title { get; set; }
        public string TypeText { get; set; }
        public string FolderPath { get; set; }
        public string ThumbnailPath { get; set; }
        public bool IsInUse { get; set; }
        public string InUseText => IsInUse ? "使用中" : string.Empty;
        public ImageSource Thumbnail { get; set; }
        public bool HasThumbnail => Thumbnail != null;
    }

    public sealed class RecentItemVm
    {
        public GeneratedWallpaper Source { get; set; }
        public string Title { get; set; }
        public string Prompt { get; set; }
        public string StyleName { get; set; }
        public string CreatedText { get; set; }
        public string LibraryStateText { get; set; }
        public ImageSource Thumbnail { get; set; }
        public bool HasThumbnail => Thumbnail != null;
    }

    /// <summary>对话续改面板里的一条记录。</summary>
    public sealed class ConversationTurnVm
    {
        public string Caption { get; set; }
        public string Body { get; set; }
    }

    public sealed class MonitorOption
    {
        public string Label { get; set; }
        public DisplayMonitor Monitor { get; set; }
        public override string ToString() => Label;
    }

    public sealed class GeneratedWallpaper
    {
        public string ProjectFolder { get; set; }
        public string HtmlPath { get; set; }
        public string ThumbnailPath { get; set; }
        public string Title { get; set; }
        public string Prompt { get; set; }
        public string Style { get; set; }
        public string Color { get; set; }
        public DateTime CreatedAt { get; set; }
        public string LibraryFolderPath { get; set; }

        /// <summary>true 表示目录本身已是完整壁纸包（如深度视差 CLI 产物），入库时整目录复制。</summary>
        public bool IsPackaged { get; set; }

        /// <summary>AI 对话续改会话标识（wp-…）；为空表示该结果没有可续接的会话。</summary>
        public string SessionId { get; set; }
    }
}
