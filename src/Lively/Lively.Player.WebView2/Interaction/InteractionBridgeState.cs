namespace Lively.Player.WebView2.Interaction
{
    /// <summary>
    /// 交互桥握手状态，用于降级判定与 Core 侧观测（日志计数）。
    /// </summary>
    internal enum InteractionBridgeState
    {
        /// <summary>文档已开始加载，尚未收到 contactable.ready。</summary>
        NotReady = 0,

        /// <summary>页面已完成握手，可推送 JS 输入事件。</summary>
        Ready = 1,

        /// <summary>2 秒内未收到 ready，判定页面不参与交互，降级为纯窗口消息链路。</summary>
        TimedOut = 2
    }
}
