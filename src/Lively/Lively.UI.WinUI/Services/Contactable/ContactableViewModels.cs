using System;
using System.Collections.Generic;
using System.IO;
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
        /// <summary>true 表示「不指定风格」，由 AI 按描述自行决定；此时 ColorHex 为空。</summary>
        public bool IsAuto { get; init; }
    }

    public static class ContactableStyles
    {
        public const string AutoKey = "auto";

        public static IReadOnlyList<ContactableStyleInfo> All { get; } = new List<ContactableStyleInfo>
        {
            new() { Key = AutoKey, Name = "不指定", Description = "不选风格与配色，交给 AI 按描述决定", ColorHex = null, GradientStartHex = "#1B2436", GradientEndHex = "#3B4A63", IsAuto = true },
            new() { Key = "minimal", Name = "极简", Description = "留白、克制线条与柔和光晕", ColorHex = "#94A3B8", GradientStartHex = "#1E293B", GradientEndHex = "#94A3B8" },
            new() { Key = "cyber", Name = "赛博", Description = "霓虹网格、冷光与高对比", ColorHex = "#22D3EE", GradientStartHex = "#0F172A", GradientEndHex = "#22D3EE" },
            new() { Key = "nature", Name = "自然", Description = "苔绿、雾气与缓慢呼吸感", ColorHex = "#34D399", GradientStartHex = "#052E2B", GradientEndHex = "#34D399" },
            new() { Key = "pixel", Name = "像素", Description = "低分辨率色块与复古颗粒", ColorHex = "#F472B6", GradientStartHex = "#1F1235", GradientEndHex = "#F472B6" },
            new() { Key = "ink", Name = "水墨", Description = "墨色扩散、留白与单色层次", ColorHex = "#64748B", GradientStartHex = "#0F172A", GradientEndHex = "#CBD5E1" },
            new() { Key = "ocean", Name = "深海", Description = "幽蓝洋流、光斑与缓慢波浪", ColorHex = "#38BDF8", GradientStartHex = "#031A2E", GradientEndHex = "#38BDF8" },
            new() { Key = "space", Name = "星空", Description = "星云、银河与缓慢漂移的星点", ColorHex = "#A78BFA", GradientStartHex = "#0B1026", GradientEndHex = "#A78BFA" },
            new() { Key = "aurora", Name = "极光", Description = "青绿光带在夜空中缓慢流动", ColorHex = "#2DD4BF", GradientStartHex = "#041B2A", GradientEndHex = "#2DD4BF" },
            new() { Key = "ember", Name = "熔岩", Description = "暗红岩纹与上升的橙色余烬", ColorHex = "#FB923C", GradientStartHex = "#2A0A05", GradientEndHex = "#FB923C" },
            new() { Key = "desert", Name = "沙丘", Description = "暖沙丘线、低日与缓慢光影", ColorHex = "#E9C46A", GradientStartHex = "#2B1B12", GradientEndHex = "#E9C46A" },
            new() { Key = "glass", Name = "玻璃", Description = "磨砂半透明层与柔和折射光", ColorHex = "#A5B4FC", GradientStartHex = "#1E1B4B", GradientEndHex = "#A5B4FC" },
            new() { Key = "retro", Name = "复古", Description = "暖褐胶片颗粒与复古印刷色", ColorHex = "#D97757", GradientStartHex = "#2B1A12", GradientEndHex = "#D97757" },
            new() { Key = "geometry", Name = "几何", Description = "大色块构图与克制的几何动效", ColorHex = "#6366F1", GradientStartHex = "#111827", GradientEndHex = "#6366F1" },
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

    /// <summary>壁纸来源分类，用于在壁纸库里一眼区分 AI 生成、自己添加和随包内置。</summary>
    public enum WallpaperSourceKind
    {
        Ai,
        Imported,
        Builtin,
    }

    public sealed class LibraryItemVm
    {
        public string Title { get; set; }
        public string TypeText { get; set; }
        public string FolderPath { get; set; }
        public string ThumbnailPath { get; set; }
        public bool IsInUse { get; set; }
        public WallpaperSourceKind SourceKind { get; set; }
        public string SourceText => SourceKind switch
        {
            WallpaperSourceKind.Ai => "AI 生成",
            WallpaperSourceKind.Builtin => "内置",
            _ => "我导入的",
        };
        public string FolderNameText => string.IsNullOrWhiteSpace(FolderPath) ? string.Empty : Path.GetFileName(FolderPath);
        public string InUseText => IsInUse ? "使用中" : string.Empty;
        public ImageSource Thumbnail { get; set; }
        public bool HasThumbnail => Thumbnail != null;
    }

    /// <summary>壁纸库的“来源”筛选下拉项。</summary>
    public sealed class SourceFilterOption
    {
        public string Key { get; init; }
        public string Label { get; init; }
        public override string ToString() => Label;
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

        /// <summary>该轮附带的参考图缩略图；为空表示没有附图。</summary>
        public ImageSource Attachment { get; set; }

        public bool HasAttachment => Attachment != null;
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
