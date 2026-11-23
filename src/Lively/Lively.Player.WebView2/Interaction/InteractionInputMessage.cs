using Newtonsoft.Json;

namespace Lively.Player.WebView2.Interaction
{
    /// <summary>
    /// 宿主 → 页面 contactable.input 消息，字段与 06-interaction-spec.md §6.2/§6.3 完全一致。
    /// x/y 为 CSS 像素，由 Player 侧换算一次后填入，桥不重复换算。
    /// </summary>
    internal sealed class InteractionInputMessage
    {
        [JsonProperty("type", Order = -2)]
        public string Type { get; set; }

        [JsonProperty("version")]
        public int Version { get; set; }

        /// <summary>pointermove / pointerdown / pointerup / wheel / keydown / keyup</summary>
        [JsonProperty("kind")]
        public string Kind { get; set; }

        [JsonProperty("x")]
        public double X { get; set; }

        [JsonProperty("y")]
        public double Y { get; set; }

        /// <summary>none / left / middle / right</summary>
        [JsonProperty("button")]
        public string Button { get; set; }

        /// <summary>按下位掩码：1 左、2 右、4 中</summary>
        [JsonProperty("buttons")]
        public int Buttons { get; set; }

        [JsonProperty("wheelDelta")]
        public int WheelDelta { get; set; }

        [JsonProperty("key")]
        public string Key { get; set; }

        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("modifiers")]
        public InteractionModifiers Modifiers { get; set; }

        [JsonProperty("ts")]
        public long Ts { get; set; }
    }

    internal sealed class InteractionModifiers
    {
        [JsonProperty("ctrl")]
        public bool Ctrl { get; set; }

        [JsonProperty("shift")]
        public bool Shift { get; set; }

        [JsonProperty("alt")]
        public bool Alt { get; set; }

        [JsonProperty("meta")]
        public bool Meta { get; set; }
    }
}
