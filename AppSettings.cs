using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;

namespace RapidTap
{
    internal enum TriggerMode
    {
        /// <summary>按住热键连点，松开停止。</summary>
        Hold,

        /// <summary>按一下开始，再按一下停止。</summary>
        Toggle
    }

    internal enum ClickPosition
    {
        /// <summary>点鼠标当前所在位置。</summary>
        FollowCursor,

        /// <summary>点一个事先拾取好的固定屏幕坐标。</summary>
        FixedPoint
    }

    /// <summary>
    /// 程序设置。存成 %AppData%\RapidTap\settings.ini 的 key=value 文本。
    ///
    /// 刻意不用 JSON：net48 里 System.Text.Json 要额外引 NuGet 包，
    /// 而内置的 DataContractJsonSerializer 又会把一个 55KB 的小工具拖进一堆序列化机制。
    /// 这里的配置项就十来个，手写解析反而更小、更好读，也不会因为格式升级而炸掉。
    ///
    /// 解析和格式化拆成了纯函数（FromLines / ToLines），便于单元测试。
    /// </summary>
    internal sealed class AppSettings
    {
        public decimal IntervalMs { get; set; } = 15m;

        /// <summary>0 = 左键，1 = 右键，2 = 中键，与界面下拉框顺序一致。</summary>
        public int MouseButtonIndex { get; set; }

        public HotkeyBinding Hotkey { get; set; } = HotkeyBinding.Default;

        public bool TopMost { get; set; }

        public TriggerMode Trigger { get; set; } = TriggerMode.Hold;

        public ClickPosition Position { get; set; } = ClickPosition.FollowCursor;

        public Point FixedPoint { get; set; } = Point.Empty;

        public static string DirectoryPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RapidTap");

        public static string FilePath => Path.Combine(DirectoryPath, "settings.ini");

        /// <summary>
        /// 读取配置。任何异常（文件损坏、没有权限、磁盘只读…）都退回默认值：
        /// 一个连点器不该因为配置读不出来就起不来。
        /// </summary>
        public static AppSettings Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return new AppSettings();
                }

                // 明确指定 UTF-8：文件里有中文注释，交给编码探测不如直接写死
                return FromLines(File.ReadAllLines(FilePath, Encoding.UTF8));
            }
            catch (Exception)
            {
                return new AppSettings();
            }
        }

        /// <summary>保存配置。同样吞掉异常——存不上顶多下次回到默认值，不值得打断用户。</summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);

                // 带 BOM 写出：这个文件是给人看、也允许手改的，
                // 不带 BOM 的话 Windows 记事本和旧版 PowerShell 会按本地代码页去读，中文注释变乱码
                File.WriteAllLines(FilePath, ToLines(), new UTF8Encoding(true));
            }
            catch (Exception)
            {
                // 忽略：写不进去不影响本次使用
            }
        }

        public IEnumerable<string> ToLines()
        {
            yield return "# RapidTap 设置文件，删除本文件即可恢复默认值";
            yield return "interval=" + IntervalMs.ToString(CultureInfo.InvariantCulture);
            yield return "button=" + MouseButtonIndex.ToString(CultureInfo.InvariantCulture);
            yield return "hotkey=" + Hotkey.Serialize();
            yield return "topmost=" + (TopMost ? "1" : "0");
            yield return "trigger=" + (Trigger == TriggerMode.Toggle ? "toggle" : "hold");
            yield return "position=" + (Position == ClickPosition.FixedPoint ? "fixed" : "cursor");
            yield return "x=" + FixedPoint.X.ToString(CultureInfo.InvariantCulture);
            yield return "y=" + FixedPoint.Y.ToString(CultureInfo.InvariantCulture);
        }

        public static AppSettings FromLines(IEnumerable<string> lines)
        {
            var settings = new AppSettings();
            if (lines == null)
            {
                return settings;
            }

            int x = 0, y = 0;

            foreach (string raw in lines)
            {
                if (raw == null)
                {
                    continue;
                }

                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                {
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string value = line.Substring(eq + 1).Trim();

                switch (key)
                {
                    case "interval":
                        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal interval))
                        {
                            settings.IntervalMs = ClickTiming.ClampInterval(interval);
                        }

                        break;

                    case "button":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int button)
                            && button >= 0 && button <= 2)
                        {
                            settings.MouseButtonIndex = button;
                        }

                        break;

                    case "hotkey":
                        settings.Hotkey = HotkeyBinding.Parse(value);
                        break;

                    case "topmost":
                        settings.TopMost = value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                        break;

                    case "trigger":
                        settings.Trigger = string.Equals(value, "toggle", StringComparison.OrdinalIgnoreCase)
                            ? TriggerMode.Toggle
                            : TriggerMode.Hold;
                        break;

                    case "position":
                        settings.Position = string.Equals(value, "fixed", StringComparison.OrdinalIgnoreCase)
                            ? ClickPosition.FixedPoint
                            : ClickPosition.FollowCursor;
                        break;

                    case "x":
                        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out x);
                        break;

                    case "y":
                        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out y);
                        break;
                }
            }

            settings.FixedPoint = new Point(x, y);
            return settings;
        }
    }
}
