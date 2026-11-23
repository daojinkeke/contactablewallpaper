namespace Lively.Player.WebView2.Interaction
{
    /// <summary>
    /// 交互桥协议常量，对应 docs/contracts/06-interaction-spec.md 第 6 节。
    /// 消息类型、版本号与字段名不得就地修改，只能按契约升级。
    /// </summary>
    internal static class InteractionProtocol
    {
        public const int Version = 1;

        /// <summary>宿主 → 页面：规范化输入事件。</summary>
        public const string InputMessageType = "contactable.input";

        /// <summary>页面 → 宿主：声明支持交互并完成握手。</summary>
        public const string ReadyMessageType = "contactable.ready";

        /// <summary>页面 → 宿主：声明矩形热区（CSS 像素）。</summary>
        public const string HitRegionsMessageType = "contactable.hitRegions";

        /// <summary>页面 → 宿主：请求开启/关闭交互。</summary>
        public const string SetInteractiveMessageType = "contactable.setInteractive";

        /// <summary>页面 → 宿主：浏览器原生 pointerleave 已发生（用于抑制合成兜底）。</summary>
        public const string PageLeaveMessageType = "contactable.leave";

        // 以下事件名与 06-interaction-spec.md §6.3 的 kind 一致。
        // 例外：pointerleave 为本次"默认可交互"新增（契约缺口，待 docs 补齐）。
        public const string KindPointerMove = "pointermove";
        public const string KindPointerDown = "pointerdown";
        public const string KindPointerUp = "pointerup";
        public const string KindWheel = "wheel";
        public const string KindKeyDown = "keydown";
        public const string KindKeyUp = "keyup";
        public const string KindPointerLeave = "pointerleave";

        public const string ButtonNone = "none";
        public const string ButtonLeft = "left";
        public const string ButtonMiddle = "middle";
        public const string ButtonRight = "right";

        public const int ButtonMaskLeft = 1;
        public const int ButtonMaskRight = 2;
        public const int ButtonMaskMiddle = 4;
    }
}
