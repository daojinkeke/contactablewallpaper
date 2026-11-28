using System;

namespace Lively.Models.Message
{
    /// <summary>
    /// Core → Player：转发一次键盘事件，由 Player 侧桥接脚本合成页面 DOM 键盘事件。
    /// Chromium(WebView2 运行时) 不把投递到其输入窗口的键盘消息转成 DOM 事件，
    /// 因此键盘不再走窗口消息注入。
    /// </summary>
    [Serializable]
    public class LivelyKeyboardInput : IpcMessage
    {
        public int VirtualKey { get; set; }
        public bool IsDown { get; set; }
        public LivelyKeyboardInput() : base(MessageType.msg_input_keyboard)
        {
        }
    }
}
