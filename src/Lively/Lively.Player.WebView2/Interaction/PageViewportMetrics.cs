using Newtonsoft.Json;

namespace Lively.Player.WebView2.Interaction
{
    /// <summary>
    /// 页面实测视口指标，字段来自 window.devicePixelRatio / innerWidth / visualViewport。
    /// 用于计算 CSS 换算比例，不得用 Core 传入的缩放系数反推。
    /// </summary>
    internal sealed class PageViewportMetrics
    {
        [JsonProperty("devicePixelRatio")]
        public double DevicePixelRatio { get; set; }

        [JsonProperty("innerWidth")]
        public double InnerWidth { get; set; }

        [JsonProperty("innerHeight")]
        public double InnerHeight { get; set; }

        [JsonProperty("visualViewportWidth")]
        public double VisualViewportWidth { get; set; }

        [JsonProperty("visualViewportHeight")]
        public double VisualViewportHeight { get; set; }

        [JsonProperty("visualViewportScale")]
        public double VisualViewportScale { get; set; }

        [JsonProperty("visualViewportOffsetLeft")]
        public double VisualViewportOffsetLeft { get; set; }

        [JsonProperty("visualViewportOffsetTop")]
        public double VisualViewportOffsetTop { get; set; }

        [JsonProperty("documentElementClientWidth")]
        public double DocumentElementClientWidth { get; set; }

        [JsonProperty("documentElementClientHeight")]
        public double DocumentElementClientHeight { get; set; }
    }
}
