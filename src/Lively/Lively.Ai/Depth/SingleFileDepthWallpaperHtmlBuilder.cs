using System;
using System.IO;
using System.Text;

namespace Lively.Ai.Depth
{
    public sealed class DepthWallpaperHtmlInput
    {
        public byte[] ImageBytes { get; set; }

        public string ImageMime { get; set; } = "image/jpeg";

        public byte[] DepthBytes { get; set; }

        public string DepthMime { get; set; } = "image/jpeg";

        public string Title { get; set; }

        /// <summary>模型包内的 Three.js 模板目录（Templates/0）。</summary>
        public string TemplateDirectory { get; set; }
    }

    public interface IDepthWallpaperHtmlBuilder
    {
        /// <summary>模板入口页（本地引用 js/ 与 media/），可直接作为 LivelyInfo.FileName 挂载。</summary>
        string BuildIndexHtml(string templateDirectory, string title);

        /// <summary>模板脚本的补丁版：增加 pointermove 兼容与静止漂浮，其余逻辑原样保留。</summary>
        string BuildScript(string templateDirectory);

        /// <summary>可选的单文件版本：Three.js 与图片全部内联，适合直接双击预览。</summary>
        string BuildSingleFile(DepthWallpaperHtmlInput input);
    }

    /// <summary>
    /// 复用 MiDaS 模型包自带的 depthmap-wallpaper 模板（MIT，rocksdanister/depthmap-wallpaper）：
    /// 渲染、视差、自适应逻辑一律沿用模板，只追加“桥推 pointermove + 静止漂浮”两段兼容代码。
    /// </summary>
    public sealed class SingleFileDepthWallpaperHtmlBuilder : IDepthWallpaperHtmlBuilder
    {
        /// <summary>鼠标停止移动多久后进入缓慢漂浮（毫秒）。</summary>
        private const string IdleDelayMs = "4000";

        public string BuildIndexHtml(string templateDirectory, string title)
        {
            var html = ReadRequired(templateDirectory, "index.html");
            if (html.IndexOf("<meta charset", StringComparison.OrdinalIgnoreCase) < 0)
            {
                html = html.Replace("<head>", "<head>\n    <meta charset=\"UTF-8\" />",
                    StringComparison.OrdinalIgnoreCase);
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                html = System.Text.RegularExpressions.Regex.Replace(html, "<title>.*?</title>",
                    "<title>" + EscapeHtml(title) + "</title>",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            return html;
        }

        public string BuildScript(string templateDirectory)
        {
            var script = ReadRequired(templateDirectory, "js", "script.js");
            return script + "\n" + PointerAndIdlePatch;
        }

        public string BuildSingleFile(DepthWallpaperHtmlInput input)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));
            if (input.ImageBytes == null || input.ImageBytes.Length == 0)
                throw new ArgumentException("缺少原图数据。", nameof(input));
            if (input.DepthBytes == null || input.DepthBytes.Length == 0)
                throw new ArgumentException("缺少深度图数据。", nameof(input));

            var templateDir = input.TemplateDirectory;
            var three = ReadRequired(templateDir, "js", "three.min.js");
            var gui = ReadRequired(templateDir, "js", "dat.gui.min.js");
            var script = BuildScript(templateDir);

            var imageDataUri = "data:" + input.ImageMime + ";base64," + Convert.ToBase64String(input.ImageBytes);
            var depthDataUri = "data:" + input.DepthMime + ";base64," + Convert.ToBase64String(input.DepthBytes);

            script = script
                .Replace("\"media/image.jpg\"", "\"" + imageDataUri + "\"")
                .Replace("'media/image.jpg'", "'" + imageDataUri + "'")
                .Replace("\"media/depth.jpg\"", "\"" + depthDataUri + "\"")
                .Replace("'media/depth.jpg'", "'" + depthDataUri + "'");

            var templateHtml = ReadRequired(templateDir, "index.html");
            var bodyStart = templateHtml.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
            var head = bodyStart > 0 ? templateHtml[..bodyStart] : DefaultHead;
            if (!string.IsNullOrWhiteSpace(input.Title) && bodyStart > 0)
            {
                head = System.Text.RegularExpressions.Regex.Replace(head, "<title>.*?</title>",
                    "<title>" + EscapeHtml(input.Title) + "</title>",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            var html = new StringBuilder(script.Length + three.Length + gui.Length +
                                         imageDataUri.Length + depthDataUri.Length + 4096);
            html.Append(head);
            html.Append("  <body>\n");
            html.Append("    <div id=\"container\"></div>\n");
            html.Append("    <script>").Append(EscapeInlineScript(three)).Append("</script>\n");
            html.Append("    <script>").Append(EscapeInlineScript(gui)).Append("</script>\n");
            html.Append("    <script>").Append(EscapeInlineScript(script)).Append("</script>\n");
            html.Append("  </body>\n</html>\n");
            return html.ToString();
        }

        /// <summary>
        /// 追加在模板脚本之后的兼容层（同一 &lt;script&gt; 作用域，可直接访问模板变量）：
        /// 1) 桥推送的 pointermove 复用模板的 mousemove 视差逻辑（页面无需握手）；
        /// 2) 鼠标静止 4 秒后缓慢漂浮，重新移动时重新锚定，避免跳变。
        /// </summary>
        private const string PointerAndIdlePatch = @"
/* ---- Contactable: pointer bridge + idle drift (template logic untouched) ---- */
document.addEventListener('pointermove', function (e) {
  document.dispatchEvent(new MouseEvent('mousemove', {
    pageX: e.pageX, pageY: e.pageY, clientX: e.clientX, clientY: e.clientY, bubbles: true
  }));
}, { passive: true });

(function () {
  var idleDelay = " + IdleDelayMs + @";
  var lastMove = Date.now();
  var anchor = Date.now();
  var drifting = false;

  document.addEventListener('mousemove', function () {
    lastMove = Date.now();
    if (drifting) { drifting = false; wasPaused = true; }
  }, { passive: true });

  (function tick() {
    requestAnimationFrame(tick);
    if (isPaused) return;
    if (Date.now() - lastMove < idleDelay) return;

    drifting = true;
    var t = (Date.now() - anchor) / 1000;
    cursor.x = Math.sin(t * 0.22) * 0.10;
    cursor.y = Math.cos(t * 0.17) * 0.06;
  })();
})();
";

        private static string ReadRequired(string root, params string[] parts)
        {
            var path = Path.Combine(root ?? string.Empty, Path.Combine(parts));
            if (!File.Exists(path))
                throw new FileNotFoundException("MiDaS 模型包缺少模板文件：" + path, path);
            return File.ReadAllText(path, Encoding.UTF8);
        }

        private static string EscapeInlineScript(string js) =>
            js.Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);

        private static string EscapeHtml(string text) =>
            text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        private const string DefaultHead = @"<!DOCTYPE html>
<html lang=""zh-CN"">
  <head>
    <meta charset=""UTF-8"" />
    <title>Depth Parallax Wallpaper</title>
  </head>
  <style>
    html, body { margin: 0; padding: 0; width: 100%; height: 100%; overflow: hidden; background: #000; }
    #container { width: 100vw; height: 100vh; }
    canvas { display: block; }
  </style>
";
    }
}
