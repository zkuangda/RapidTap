using System;
using System.Text;
using System.Windows.Forms;

namespace RapidTap
{
    /// <summary>
    /// 一个热键绑定：主键 + 可选的 Ctrl / Alt / Shift 修饰键。
    ///
    /// 刻意做成不可变的"类"而不是结构体：连点线程的兜底回调要读这个字段，
    /// 而结构体有四个成员、赋值不是原子操作，跨线程可能读到"新主键配旧修饰位"的撕裂值。
    /// 换成类之后字段只存引用，赋值天然原子，读到的一定是某个完整的绑定。
    /// </summary>
    internal sealed class HotkeyBinding : IEquatable<HotkeyBinding>
    {
        public static readonly HotkeyBinding Default = new HotkeyBinding(Keys.PageDown, false, false, false);

        public HotkeyBinding(Keys key, bool ctrl, bool alt, bool shift)
        {
            Key = key;
            Ctrl = ctrl;
            Alt = alt;
            Shift = shift;
        }

        /// <summary>主键，不含任何修饰位。</summary>
        public Keys Key { get; }

        public bool Ctrl { get; }

        public bool Alt { get; }

        public bool Shift { get; }

        public bool HasModifier => Ctrl || Alt || Shift;

        /// <summary>
        /// 裸的字母 / 数字键（不带任何修饰键）作为热键是有风险的：
        /// 钩子从不吞掉按键，所以设成 A 之后，在任何地方打字打到 a 都会触发连点。
        /// 界面据此在设置前弹确认。
        /// </summary>
        public bool IsRiskyBareKey
        {
            get
            {
                if (HasModifier)
                {
                    return false;
                }

                return (Key >= Keys.A && Key <= Keys.Z)
                    || (Key >= Keys.D0 && Key <= Keys.D9)
                    || (Key >= Keys.NumPad0 && Key <= Keys.NumPad9)
                    || Key == Keys.Space;
            }
        }

        /// <summary>
        /// 修饰键本身（Ctrl / Alt / Shift / Win）不能单独当热键，
        /// 录入时按下它们应当继续等待真正的主键。
        /// </summary>
        public static bool IsModifierKey(Keys key)
        {
            switch (key)
            {
                case Keys.ControlKey:
                case Keys.LControlKey:
                case Keys.RControlKey:
                case Keys.Menu:
                case Keys.LMenu:
                case Keys.RMenu:
                case Keys.ShiftKey:
                case Keys.LShiftKey:
                case Keys.RShiftKey:
                case Keys.LWin:
                case Keys.RWin:
                    return true;
                default:
                    return false;
            }
        }

        public string DisplayName
        {
            get
            {
                var sb = new StringBuilder();
                if (Ctrl)
                {
                    sb.Append("Ctrl + ");
                }

                if (Alt)
                {
                    sb.Append("Alt + ");
                }

                if (Shift)
                {
                    sb.Append("Shift + ");
                }

                sb.Append(GetKeyDisplayName(Key));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Keys 枚举里有些键存在新旧同值别名（例如 PageDown 和 Next 同为 0x22），
        /// 默认 ToString() 会返回不直观的旧名字，这里做一层友好名映射。
        /// </summary>
        public static string GetKeyDisplayName(Keys key)
        {
            switch (key)
            {
                case Keys.PageDown: return "PageDown";
                case Keys.PageUp: return "PageUp";
                case Keys.CapsLock: return "CapsLock";
                case Keys.Enter: return "Enter";
                case Keys.PrintScreen: return "PrintScreen";
                case Keys.Scroll: return "ScrollLock";
                case Keys.Back: return "Backspace";
                default: return key.ToString();
            }
        }

        /// <summary>存进配置文件用的紧凑写法，例如 "Ctrl+Shift+A" 或 "PageDown"。</summary>
        public string Serialize()
        {
            var sb = new StringBuilder();
            if (Ctrl)
            {
                sb.Append("Ctrl+");
            }

            if (Alt)
            {
                sb.Append("Alt+");
            }

            if (Shift)
            {
                sb.Append("Shift+");
            }

            sb.Append((int)Key);
            return sb.ToString();
        }

        /// <summary>解析 <see cref="Serialize"/> 的输出；任何无法识别的内容都回退到默认热键。</summary>
        public static HotkeyBinding Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Default;
            }

            bool ctrl = false, alt = false, shift = false;
            string[] parts = text!.Split('+');

            for (int i = 0; i < parts.Length - 1; i++)
            {
                string part = parts[i].Trim();
                if (string.Equals(part, "Ctrl", StringComparison.OrdinalIgnoreCase))
                {
                    ctrl = true;
                }
                else if (string.Equals(part, "Alt", StringComparison.OrdinalIgnoreCase))
                {
                    alt = true;
                }
                else if (string.Equals(part, "Shift", StringComparison.OrdinalIgnoreCase))
                {
                    shift = true;
                }
            }

            string last = parts[parts.Length - 1].Trim();
            if (!int.TryParse(last, out int code) || !Enum.IsDefined(typeof(Keys), code))
            {
                return Default;
            }

            var key = (Keys)code;
            if (IsModifierKey(key))
            {
                return Default;
            }

            return new HotkeyBinding(key, ctrl, alt, shift);
        }

        public bool Equals(HotkeyBinding? other)
        {
            return other != null
                && Key == other.Key && Ctrl == other.Ctrl && Alt == other.Alt && Shift == other.Shift;
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as HotkeyBinding);
        }

        public override int GetHashCode()
        {
            int hash = (int)Key;
            hash = (hash * 397) ^ (Ctrl ? 1 : 0);
            hash = (hash * 397) ^ (Alt ? 2 : 0);
            hash = (hash * 397) ^ (Shift ? 4 : 0);
            return hash;
        }

        public override string ToString() => DisplayName;
    }
}
