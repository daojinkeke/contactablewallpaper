using System.Collections.Generic;

namespace Lively.Models.Ai
{
    public enum ValidationSeverity
    {
        warning = 0,
        fatal = 1
    }

    /// <summary>
    /// 校验输出约束（07 契约 §6.3）。
    /// </summary>
    public class ValidationOptions
    {
        public bool OfflineRequired { get; set; }
        public long MaxBytes { get; set; }
        public PerformanceMode PerformanceMode { get; set; }
        public int TargetWidth { get; set; }
        public int TargetHeight { get; set; }

        /// <summary>允许外部联网：开启时 V004 不再判定外链资源与网络请求为致命问题。</summary>
        public bool AllowExternalNetwork { get; set; }

        public ValidationOptions()
        {
            OfflineRequired = true;
            AllowExternalNetwork = false;
            MaxBytes = 5 * 1024 * 1024;
            PerformanceMode = PerformanceMode.balanced;
            TargetWidth = 1920;
            TargetHeight = 1080;
        }
    }

    /// <summary>
    /// 单条校验问题，Code 对应 07 契约 §6.1 的 V001–V008。
    /// </summary>
    public class ValidationIssue
    {
        public string Code { get; set; }
        public ValidationSeverity Severity { get; set; }
        public string Message { get; set; }

        public ValidationIssue() { }

        public ValidationIssue(string code, ValidationSeverity severity, string message)
        {
            Code = code;
            Severity = severity;
            Message = message;
        }
    }

    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public List<ValidationIssue> Issues { get; set; }
        public long SizeBytes { get; set; }

        public ValidationResult()
        {
            IsValid = true;
            Issues = new List<ValidationIssue>();
        }

        public bool HasFatal
        {
            get
            {
                foreach (var issue in Issues)
                {
                    if (issue.Severity == ValidationSeverity.fatal)
                        return true;
                }
                return false;
            }
        }
    }
}
