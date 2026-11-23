using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace Lively.Ai
{
    /// <summary>
    /// 模板壁纸来源。未配置 AI 或离线时只能走模板模式（07 契约 §5.4 / PRD AC-17）。
    /// </summary>
    public interface ITemplateProvider
    {
        string GetTemplateHtml();
    }

    public class EmbeddedTemplateProvider : ITemplateProvider
    {
        private const string ResourceName = "Lively.Ai.Templates.spacewallpaper.html";
        private string cached;

        public string GetTemplateHtml()
        {
            if (cached != null)
                return cached;

            var assembly = typeof(EmbeddedTemplateProvider).GetTypeInfo().Assembly;
            using var stream = assembly.GetManifestResourceStream(ResourceName);
            if (stream == null)
                throw new InvalidOperationException("内置模板资源缺失：" + ResourceName);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            cached = reader.ReadToEnd();
            return cached;
        }
    }
}
