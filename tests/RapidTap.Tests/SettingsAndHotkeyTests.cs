using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using RapidTap;
using Xunit;

namespace RapidTap.Tests
{
    public class HotkeyBindingTests
    {
        [Fact]
        public void 序列化与解析可以往返()
        {
            var original = new HotkeyBinding(Keys.A, ctrl: true, alt: false, shift: true);
            HotkeyBinding parsed = HotkeyBinding.Parse(original.Serialize());

            Assert.Equal(original, parsed);
            Assert.True(parsed.Ctrl);
            Assert.False(parsed.Alt);
            Assert.True(parsed.Shift);
            Assert.Equal(Keys.A, parsed.Key);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("完全不是热键")]
        [InlineData("Ctrl+")]
        [InlineData("Ctrl+99999999")]
        public void 解析不了的内容一律回退到默认热键(string text)
        {
            Assert.Equal(HotkeyBinding.Default, HotkeyBinding.Parse(text));
        }

        [Fact]
        public void 解析出的主键若是修饰键本身也回退到默认()
        {
            // 光按 Ctrl 不构成热键，配置文件被手改成这样时不能让程序进入怪状态
            string bad = ((int)Keys.ControlKey).ToString();
            Assert.Equal(HotkeyBinding.Default, HotkeyBinding.Parse(bad));
        }

        [Fact]
        public void 显示名按_修饰键加主键_拼接()
        {
            var binding = new HotkeyBinding(Keys.F5, ctrl: true, alt: true, shift: false);
            Assert.Equal("Ctrl + Alt + F5", binding.DisplayName);
        }

        [Fact]
        public void PageDown_不会显示成别名_Next()
        {
            // Keys.PageDown 和 Keys.Next 是同一个值，默认 ToString 会给出不直观的旧名字
            Assert.Equal("PageDown", new HotkeyBinding(Keys.PageDown, false, false, false).DisplayName);
        }

        [Theory]
        [InlineData(Keys.A, true)]
        [InlineData(Keys.D5, true)]
        [InlineData(Keys.Space, true)]
        [InlineData(Keys.F1, false)]
        [InlineData(Keys.PageDown, false)]
        public void 裸字符键会被标记为有风险(Keys key, bool risky)
        {
            Assert.Equal(risky, new HotkeyBinding(key, false, false, false).IsRiskyBareKey);
        }

        [Fact]
        public void 带上修饰键之后就不再算有风险()
        {
            Assert.False(new HotkeyBinding(Keys.A, ctrl: true, alt: false, shift: false).IsRiskyBareKey);
        }

        [Theory]
        [InlineData(Keys.ControlKey)]
        [InlineData(Keys.LShiftKey)]
        [InlineData(Keys.RMenu)]
        [InlineData(Keys.LWin)]
        public void 修饰键会被识别出来(Keys key)
        {
            Assert.True(HotkeyBinding.IsModifierKey(key));
        }
    }

    public class AppSettingsTests
    {
        [Fact]
        public void 保存再读取可以完整往返()
        {
            var original = new AppSettings
            {
                IntervalMs = 7.5m,
                MouseButtonIndex = 2,
                Hotkey = new HotkeyBinding(Keys.F8, ctrl: true, alt: false, shift: false),
                TopMost = true,
                Trigger = TriggerMode.Toggle,
                Position = ClickPosition.FixedPoint,
                FixedPoint = new System.Drawing.Point(1234, -567)
            };

            AppSettings restored = AppSettings.FromLines(original.ToLines().ToList());

            Assert.Equal(original.IntervalMs, restored.IntervalMs);
            Assert.Equal(original.MouseButtonIndex, restored.MouseButtonIndex);
            Assert.Equal(original.Hotkey, restored.Hotkey);
            Assert.Equal(original.TopMost, restored.TopMost);
            Assert.Equal(original.Trigger, restored.Trigger);
            Assert.Equal(original.Position, restored.Position);
            Assert.Equal(original.FixedPoint, restored.FixedPoint);
        }

        [Fact]
        public void 空文件得到全默认值()
        {
            var settings = AppSettings.FromLines(new List<string>());

            Assert.Equal(15m, settings.IntervalMs);
            Assert.Equal(0, settings.MouseButtonIndex);
            Assert.Equal(HotkeyBinding.Default, settings.Hotkey);
            Assert.False(settings.TopMost);
            Assert.Equal(TriggerMode.Hold, settings.Trigger);
            Assert.Equal(ClickPosition.FollowCursor, settings.Position);
        }

        [Fact]
        public void 注释与空行会被跳过()
        {
            var lines = new List<string>
            {
                "# 这是注释",
                "; 这也是",
                "",
                "   ",
                "interval=25.5"
            };

            Assert.Equal(25.5m, AppSettings.FromLines(lines).IntervalMs);
        }

        [Fact]
        public void 坏值不会让整份配置崩掉()
        {
            var lines = new List<string>
            {
                "interval=不是数字",
                "button=99",
                "topmost=也不是布尔",
                "没有等号的行",
                "trigger=toggle"
            };

            AppSettings settings = AppSettings.FromLines(lines);

            // 坏的退回默认，好的照常生效
            Assert.Equal(15m, settings.IntervalMs);
            Assert.Equal(0, settings.MouseButtonIndex);
            Assert.False(settings.TopMost);
            Assert.Equal(TriggerMode.Toggle, settings.Trigger);
        }

        [Fact]
        public void 超范围的间隔会被夹回合法区间()
        {
            Assert.Equal(ClickTiming.MaxIntervalMs,
                AppSettings.FromLines(new[] { "interval=99999" }).IntervalMs);
            Assert.Equal(ClickTiming.MinIntervalMs,
                AppSettings.FromLines(new[] { "interval=0" }).IntervalMs);
        }

        [Fact]
        public void 键名大小写不敏感()
        {
            Assert.Equal(TriggerMode.Toggle,
                AppSettings.FromLines(new[] { "TRIGGER=Toggle" }).Trigger);
        }
    }
}
