namespace Lively.Player.WebView2.Interaction
{
    /// <summary>
    /// Windows 虚拟键 → DOM KeyboardEvent 的 key/code 映射。
    /// 未覆盖的键保留空字符串，页面仍可用事件顺序与修饰键。
    /// </summary>
    internal static class InteractionKeyMap
    {
        public static void Resolve(int virtualKey, out string key, out string code)
        {
            key = string.Empty;
            code = string.Empty;

            if (virtualKey >= 0x41 && virtualKey <= 0x5A)
            {
                var letter = (char)virtualKey;
                key = letter.ToString().ToLowerInvariant();
                code = "Key" + letter;
                return;
            }

            if (virtualKey >= 0x30 && virtualKey <= 0x39)
            {
                var digit = (char)virtualKey;
                key = digit.ToString();
                code = "Digit" + digit;
                return;
            }

            switch (virtualKey)
            {
                case 0x08: key = "Backspace"; code = "Backspace"; break;
                case 0x09: key = "Tab"; code = "Tab"; break;
                case 0x0D: key = "Enter"; code = "Enter"; break;
                case 0x10: key = "Shift"; code = "ShiftLeft"; break;
                case 0x11: key = "Control"; code = "ControlLeft"; break;
                case 0x12: key = "Alt"; code = "AltLeft"; break;
                case 0x14: key = "CapsLock"; code = "CapsLock"; break;
                case 0x1B: key = "Escape"; code = "Escape"; break;
                case 0x20: key = " "; code = "Space"; break;
                case 0x21: key = "PageUp"; code = "PageUp"; break;
                case 0x22: key = "PageDown"; code = "PageDown"; break;
                case 0x23: key = "End"; code = "End"; break;
                case 0x24: key = "Home"; code = "Home"; break;
                case 0x25: key = "ArrowLeft"; code = "ArrowLeft"; break;
                case 0x26: key = "ArrowUp"; code = "ArrowUp"; break;
                case 0x27: key = "ArrowRight"; code = "ArrowRight"; break;
                case 0x28: key = "ArrowDown"; code = "ArrowDown"; break;
                case 0x2E: key = "Delete"; code = "Delete"; break;
                case 0xBA: key = ";"; code = "Semicolon"; break;
                case 0xBB: key = "="; code = "Equal"; break;
                case 0xBC: key = ","; code = "Comma"; break;
                case 0xBD: key = "-"; code = "Minus"; break;
                case 0xBE: key = "."; code = "Period"; break;
                case 0xBF: key = "/"; code = "Slash"; break;
                case 0xC0: key = "`"; code = "Backquote"; break;
                case 0xDB: key = "["; code = "BracketLeft"; break;
                case 0xDC: key = "\\"; code = "Backslash"; break;
                case 0xDD: key = "]"; code = "BracketRight"; break;
                case 0xDE: key = "'"; code = "Quote"; break;
            }
        }
    }
}
