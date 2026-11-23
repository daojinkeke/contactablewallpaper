using System;
using System.Collections.Generic;

namespace Lively.Models.Ai
{
    /// <summary>
    /// 可续接的壁纸会话：保存在项目侧磁盘，CLI/UI 进程重启后仍可继续修改。
    /// </summary>
    public sealed class WallpaperSession
    {
        public string SessionId { get; set; }
        public string Prompt { get; set; }
        /// <summary>会话是否处于节能模式；revise 未显式传参时沿用。</summary>
        public bool Eco { get; set; }
        public string CreatedAt { get; set; }
        public string UpdatedAt { get; set; }
        public List<WallpaperSessionVersion> Versions { get; set; } = new List<WallpaperSessionVersion>();
    }

    /// <summary>
    /// 单个版本：HTML 单独落 vN.html，这里只存元数据与人话摘要。
    /// </summary>
    public sealed class WallpaperSessionVersion
    {
        public int Version { get; set; }
        public string FileName { get; set; }
        /// <summary>触发本版的用户诉求；初始版本为空。</summary>
        public string Instruction { get; set; }
        /// <summary>这版相对上一版改了什么（人话摘要）。</summary>
        public string Summary { get; set; }
        /// <summary>ReAct 计划（模型给出的步骤）。</summary>
        public string Plan { get; set; }
        public double? Score { get; set; }
        public long ElapsedMs { get; set; }
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens { get; set; }
        public string CreatedAt { get; set; }
    }
}
