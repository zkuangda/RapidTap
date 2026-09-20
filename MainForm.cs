using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace RapidTap
{
    public class MainForm : Form
    {
        // ========================= 控件字段 =========================
        private NumericUpDown numInterval = null!;
        private ComboBox cmbButton = null!;
        private TextBox txtHotkey = null!;
        private Button btnSetHotkey = null!;
        private CheckBox chkTopMost = null!;
        private Label lblStatus = null!;
        private Label lblCps = null!;
        private Label lblHint = null!;
        private System.Windows.Forms.Timer cpsTimer = null!;

        // ========================= 连点相关状态 =========================

        // 鼠标按键类型
        private enum MouseButtonType
        {
            Left,
            Right,
            Middle
        }

        private const decimal DefaultIntervalMs = 15m;
        private const Keys DefaultHotkey = Keys.PageDown;

        // 当前热键（默认 PageDown）
        private volatile Keys _hotkey = DefaultHotkey;

        // 是否处于"录入热键"状态：此状态下按键不触发连点，而是被当作新热键写入
        private volatile bool _capturingHotkey;

        // 当前选中的鼠标按键，volatile 保证跨线程可见性（enum 底层为 int，可用 volatile）
        private volatile MouseButtonType _mouseButton = MouseButtonType.Left;

        // 点击间隔，以 0.1ms 为单位存成整数（避免用 volatile double，double 不支持 volatile）
        // 例如 10.0ms 存成 100，读取时再 /10.0 还原
        private volatile int _intervalTenthsMs = (int)(DefaultIntervalMs * 10);

        // 连点开关标志，连点线程和 UI 线程都会读取，必须 volatile
        private volatile bool _isClicking;

        // 连点线程
        private Thread? _clickThread;

        // 点击计数器，连点线程里用 Interlocked.Increment 累加，UI 定时器用 Interlocked.Read 读取
        private long _clickCount;
        private long _lastClickCountForCps;

        public MainForm()
        {
            BuildUi();

            // 提升系统时钟精度到 1ms，否则 Thread.Sleep 的粒度约为 15.6ms
            timeBeginPeriod(1);

            // 委托必须先赋给字段持有，再传给 SetWindowsHookEx，防止委托被 GC 回收导致钩子失效
            _hookProc = HookCallback;

            // 安装全局低级键盘钩子（构造函数运行在主线程，后续 Application.Run 的消息泵也在同一线程，满足钩子要求）
            InstallKeyboardHook();
        }

        // ========================= 界面构建（纯代码，不用设计器） =========================

        private void BuildUi()
        {
            this.SuspendLayout();

            this.Text = "RapidTap";
            this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            // 按 96dpi 设计布局，交给 WinForms 按系统实际 DPI 等比缩放控件和字体，
            // 避免在高 DPI 缩放（125%/150%等）下出现文字被固定像素大小的控件裁切的问题。
            this.AutoScaleMode = AutoScaleMode.Dpi;
            this.AutoScaleDimensions = new SizeF(96F, 96F);

            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = true;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.ClientSize = new Size(420, 320);

            // 外层分两行：上面内容区占满剩余空间，下面固定高度放提示条
            var outer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2
            };
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // 字段区用 3 行 x 3 列的表格布局，标签列/控件列都设为 AutoSize，
            // 这样标签宽度会跟着实际文字和字体大小走，不会再出现固定像素宽度裁字的问题。
            var fieldsTable = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 3,
                RowCount = 3,
                Margin = new Padding(0)
            };
            fieldsTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            fieldsTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            fieldsTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            static Label MakeCaption(string text) => new Label
            {
                Text = text,
                AutoSize = true,
                Margin = new Padding(0, 13, 16, 0)
            };

            numInterval = new NumericUpDown
            {
                Width = 100,
                Margin = new Padding(0, 8, 0, 8),
                Minimum = 0.1m,
                Maximum = 1000m,
                DecimalPlaces = 1,
                Increment = 0.1m,
                TextAlign = HorizontalAlignment.Center
            };
            numInterval.ValueChanged += NumInterval_ValueChanged;

            cmbButton = new ComboBox
            {
                Width = 100,
                Margin = new Padding(0, 8, 0, 8),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbButton.Items.AddRange(new object[] { "左键", "右键", "中键" });
            cmbButton.SelectedIndexChanged += CmbButton_SelectedIndexChanged;

            txtHotkey = new TextBox
            {
                Width = 100,
                Margin = new Padding(0, 8, 12, 8),
                ReadOnly = true,
                TextAlign = HorizontalAlignment.Center,
                Text = GetKeyDisplayName(DefaultHotkey)
            };

            btnSetHotkey = new Button
            {
                Text = "点击设置",
                Size = new Size(104, 27),
                Margin = new Padding(0, 6, 0, 6)
            };
            btnSetHotkey.Click += BtnSetHotkey_Click;

            fieldsTable.Controls.Add(MakeCaption("点击间隔（毫秒）："), 0, 0);
            fieldsTable.Controls.Add(numInterval, 1, 0);

            fieldsTable.Controls.Add(MakeCaption("鼠标按键："), 0, 1);
            fieldsTable.Controls.Add(cmbButton, 1, 1);

            fieldsTable.Controls.Add(MakeCaption("触发热键："), 0, 2);
            fieldsTable.Controls.Add(txtHotkey, 1, 2);
            fieldsTable.Controls.Add(btnSetHotkey, 2, 2);

            chkTopMost = new CheckBox
            {
                Text = "窗口置顶",
                AutoSize = true,
                Margin = new Padding(0, 20, 0, 4)
            };
            chkTopMost.CheckedChanged += ChkTopMost_CheckedChanged;

            lblStatus = new Label
            {
                Text = "状态：就绪",
                AutoSize = true,
                Margin = new Padding(0, 16, 0, 4),
                Font = new Font(this.Font.FontFamily, 10.5f, FontStyle.Bold),
                ForeColor = Color.SeaGreen
            };

            lblCps = new Label
            {
                Text = "速度：0 次/秒",
                AutoSize = true,
                Margin = new Padding(0, 2, 0, 0)
            };

            var content = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(22, 20, 20, 10)
            };
            content.Controls.Add(fieldsTable);
            content.Controls.Add(chkTopMost);
            content.Controls.Add(lblStatus);
            content.Controls.Add(lblCps);

            lblHint = new Label
            {
                Text = "按住热键开始连点，松开停止",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.Gray,
                Font = new Font(this.Font.FontFamily, 8.5f),
                Padding = new Padding(0, 0, 0, 12)
            };

            outer.Controls.Add(content, 0, 0);
            outer.Controls.Add(lblHint, 0, 1);

            this.Controls.Add(outer);

            // 控件建好、事件绑定完成后再赋初值，确保 ValueChanged/SelectedIndexChanged 会正常触发一次，
            // 把默认值同步进 _intervalTenthsMs / _mouseButton
            numInterval.Value = DefaultIntervalMs;
            cmbButton.SelectedIndex = 0;

            // CPS 刷新定时器：每 200ms 在 UI 线程读取一次计数并刷新界面，连点循环本身绝不碰 UI 控件
            cpsTimer = new System.Windows.Forms.Timer { Interval = 200 };
            cpsTimer.Tick += CpsTimer_Tick;
            cpsTimer.Start();

            this.ResumeLayout(true);
        }

        /// <summary>
        /// Keys 枚举里有些键存在新旧同值别名（例如 PageDown 和 Next 是同一个值 0x22），
        /// 默认 ToString() 可能返回不直观的旧名字（比如 PageDown 显示成 "Next"），这里做一层友好名映射。
        /// </summary>
        private static string GetKeyDisplayName(Keys key)
        {
            return key switch
            {
                Keys.PageDown => "PageDown",
                Keys.PageUp => "PageUp",
                Keys.CapsLock => "CapsLock",
                Keys.Enter => "Enter",
                Keys.PrintScreen => "PrintScreen",
                Keys.Scroll => "ScrollLock",
                _ => key.ToString()
            };
        }

        // ========================= 控件事件 =========================

        private void NumInterval_ValueChanged(object? sender, EventArgs e)
        {
            decimal val = numInterval.Value;
            if (val <= 0)
            {
                // 非法值回退到默认间隔
                val = DefaultIntervalMs;
                numInterval.Value = val; // 会再次触发本事件完成同步，属正常回退流程
                return;
            }
            _intervalTenthsMs = (int)Math.Round(val * 10m);
        }

        private void CmbButton_SelectedIndexChanged(object? sender, EventArgs e)
        {
            _mouseButton = cmbButton.SelectedIndex switch
            {
                1 => MouseButtonType.Right,
                2 => MouseButtonType.Middle,
                _ => MouseButtonType.Left
            };
        }

        private void ChkTopMost_CheckedChanged(object? sender, EventArgs e)
        {
            this.TopMost = chkTopMost.Checked;
        }

        private void BtnSetHotkey_Click(object? sender, EventArgs e)
        {
            if (_capturingHotkey)
            {
                return;
            }

            // 录入热键期间不应该还在连点
            if (_isClicking)
            {
                StopClicking();
            }

            _capturingHotkey = true;
            btnSetHotkey.Text = "请按键…";
            txtHotkey.Text = "请按键…";
        }

        private void CpsTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isClicking)
            {
                return;
            }

            long current = Interlocked.Read(ref _clickCount);
            long delta = current - _lastClickCountForCps;
            _lastClickCountForCps = current;

            double seconds = cpsTimer.Interval / 1000.0;
            double cps = delta / seconds;
            lblCps.Text = $"速度：{cps:0.#} 次/秒";
        }

        // ========================= 连点控制 =========================

        private void StartClicking()
        {
            if (_isClicking || _capturingHotkey)
            {
                return;
            }

            _isClicking = true;
            _clickCount = 0;
            _lastClickCountForCps = 0;

            // 连点循环放独立后台线程，避免用 Windows.Forms.Timer（精度只有约 15.6ms）
            _clickThread = new Thread(ClickLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _clickThread.Start();

            lblStatus.Text = "状态：连点中";
            lblStatus.ForeColor = Color.OrangeRed;
        }

        private void StopClicking()
        {
            if (!_isClicking)
            {
                return;
            }

            _isClicking = false;

            lblStatus.Text = "状态：就绪";
            lblStatus.ForeColor = Color.SeaGreen;
            lblCps.Text = "速度：0 次/秒";
        }

        /// <summary>
        /// 连点主循环，运行在独立后台线程上。
        /// 只允许：发送鼠标点击、Interlocked 计数、按精确延时休眠；绝不直接访问任何 UI 控件。
        /// </summary>
        private void ClickLoop()
        {
            while (_isClicking)
            {
                SendMouseClick(_mouseButton);
                Interlocked.Increment(ref _clickCount);

                double intervalMs = _intervalTenthsMs / 10.0;
                PreciseDelay(intervalMs);
            }
        }

        /// <summary>
        /// 精确延时：
        /// - 间隔 >= 2ms 时用 Thread.Sleep，让出 CPU，避免空转浪费资源；
        /// - 间隔 < 2ms 时 Thread.Sleep 的操作系统调度粒度不够（哪怕开了 timeBeginPeriod(1) 也只能到 ~1ms），
        ///   所以改用 Stopwatch + SpinWait 忙等，靠高精度计时器自旋到时间点，牺牲少量 CPU 换取精度。
        /// </summary>
        private static void PreciseDelay(double milliseconds)
        {
            if (milliseconds <= 0)
            {
                return;
            }

            if (milliseconds >= 2.0)
            {
                Thread.Sleep((int)Math.Round(milliseconds));
            }
            else
            {
                var sw = Stopwatch.StartNew();
                double targetTicks = milliseconds / 1000.0 * Stopwatch.Frequency;
                while (sw.ElapsedTicks < targetTicks)
                {
                    Thread.SpinWait(20);
                }
            }
        }

        // ========================= SendInput 鼠标点击 =========================

        private static void SendMouseClick(MouseButtonType button)
        {
            uint downFlag, upFlag;
            switch (button)
            {
                case MouseButtonType.Right:
                    downFlag = MOUSEEVENTF_RIGHTDOWN;
                    upFlag = MOUSEEVENTF_RIGHTUP;
                    break;
                case MouseButtonType.Middle:
                    downFlag = MOUSEEVENTF_MIDDLEDOWN;
                    upFlag = MOUSEEVENTF_MIDDLEUP;
                    break;
                default:
                    downFlag = MOUSEEVENTF_LEFTDOWN;
                    upFlag = MOUSEEVENTF_LEFTUP;
                    break;
            }

            // 一次 SendInput 调用同时提交 down + up 两个 INPUT，减少一次系统调用，
            // 且不设置 dx/dy 与 MOUSEEVENTF_MOVE，保证鼠标不移动，原地点击。
            INPUT[] inputs = new INPUT[2];
            inputs[0].type = INPUT_MOUSE;
            inputs[0].mi.dwFlags = downFlag;
            inputs[1].type = INPUT_MOUSE;
            inputs[1].mi.dwFlags = upFlag;

            SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        // ========================= 全局低级键盘钩子 =========================

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        // 必须用字段持有委托，防止被 GC 回收导致钩子失效（回调地址失效会引发系统崩溃式异常）
        private readonly LowLevelKeyboardProc _hookProc;
        private IntPtr _hookId = IntPtr.Zero;

        private void InstallKeyboardHook()
        {
            using Process curProcess = Process.GetCurrentProcess();
            using ProcessModule curModule = curProcess.MainModule!;
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(curModule.ModuleName), 0);
        }

        private void UninstallKeyboardHook()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }

        /// <summary>
        /// 全局键盘钩子回调。之所以用 WH_KEYBOARD_LL 而不是 RegisterHotKey，
        /// 是因为 RegisterHotKey 只能收到"按下"消息，收不到"松开"消息，无法实现"松开即停"。
        /// 这里同时判断 KEYDOWN/SYSKEYDOWN（按下）和 KEYUP/SYSKEYUP（松开），
        /// 处理完毕无论如何都调用 CallNextHookEx 放行，绝不吞掉按键，避免影响用户正常输入。
        /// </summary>
        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                Keys key = (Keys)kb.vkCode;
                int msg = wParam.ToInt32();
                bool isKeyDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isKeyUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                if (_capturingHotkey)
                {
                    if (isKeyDown)
                    {
                        ApplyCapturedHotkey(key);
                    }
                }
                else if (key == _hotkey)
                {
                    if (isKeyDown)
                    {
                        StartClicking();
                    }
                    else if (isKeyUp)
                    {
                        StopClicking();
                    }
                }
            }

            // 钩子回调运行在安装钩子的线程（本程序即 UI 线程）上，属于 WH_KEYBOARD_LL 的固定行为，
            // 因此上面直接操作了 UI 控件也是安全的；但这里仍显式放行，绝不拦截按键。
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private void ApplyCapturedHotkey(Keys key)
        {
            _hotkey = key;
            _capturingHotkey = false;
            btnSetHotkey.Text = "点击设置";
            txtHotkey.Text = GetKeyDisplayName(key);
        }

        // ========================= 窗体关闭：完全退出 =========================

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _isClicking = false;
            _clickThread?.Join(200);

            UninstallKeyboardHook();
            timeEndPeriod(1);

            base.OnFormClosing(e);
        }

        // ========================= P/Invoke 声明 =========================

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("winmm.dll", SetLastError = true)]
        private static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll", SetLastError = true)]
        private static extern uint timeEndPeriod(uint uPeriod);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private const uint INPUT_MOUSE = 0;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        /// <summary>
        /// INPUT 是一个 C 语言里的"联合体"结构：type 字段之后是 MOUSEINPUT / KEYBDINPUT / HARDWAREINPUT 的联合。
        /// 这里只用到鼠标输入，用 LayoutKind.Explicit + FieldOffset 手动指定内存布局：
        /// type 占 4 字节，但因为 MOUSEINPUT 里含有 8 字节对齐的 IntPtr（dwExtraInfo），
        /// 在 x64 下整个联合体需要 8 字节对齐，所以 mi 必须从偏移量 8 开始（而不是 4），
        /// 否则在 x64 进程里 SendInput 会因为结构体大小/对齐不对而调用失败或产生垃圾数据。
        /// 最终 sizeof(INPUT) 在 x64 下应为 40 字节，与操作系统的定义一致。
        /// </summary>
        [StructLayout(LayoutKind.Explicit)]
        private struct INPUT
        {
            [FieldOffset(0)] public uint type;
            [FieldOffset(8)] public MOUSEINPUT mi;
        }
    }
}
