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

        // 相对窗体字体推导出来的两个派生字体，DPI 变化时会被重建（见 ApplyDerivedFonts）
        private Font _statusFont = null!;
        private Font _hintFont = null!;

        // 根布局的引用，DPI 变化后要拿它重新测算窗口该多大（见 FitToContent）
        private TableLayoutPanel _root = null!;

        // 当前窗口所在显示器的 DPI，用来算 WM_DPICHANGED 前后的缩放比例
        private int _currentDpi = 96;

        // 热键框的基准宽度：按常见键名里最长的那个实测得出，避免"PageDown"被裁成"PageDowr"
        private int _hotkeyFieldWidth;

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

            // 按"字体"自动缩放：设计基准是 96dpi 下的默认 UI 字体（Segoe UI 9pt，平均字符约 7x15 像素）。
            // 进程已在 app.manifest 里声明 PerMonitorV2，所以系统字体（SystemFonts.MessageBoxFont）
            // 拿到的像素尺寸本身就带着当前缩放，AutoScaleMode.Font 据此把整窗控件按比例放大，
            // 启动时的 125% / 150% / 175% 缩放无需任何额外代码即可正确排版。
            this.AutoScaleMode = AutoScaleMode.Font;
            this.AutoScaleDimensions = new SizeF(7F, 15F);

            // 窗口尺寸不写死 ClientSize，而是在 OnLoad 里按根布局的实测需求尺寸赋值（见 FitToContent）。
            // 这里不用 Form.AutoSize：它对"Dock=Fill 的子布局"测不准，会把底部几行漏算掉导致裁切。
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = true;
            // 起始位置在 OnLoad 里按"鼠标所在显示器的工作区"算，比 CenterScreen 更适合多屏 + 任务栏占位
            this.StartPosition = FormStartPosition.Manual;

            // .NET Framework 在中文系统上的默认控件字体是"宋体"（.NET Core 用的是 Segoe UI），
            // 点阵渲染、观感陈旧。这里显式改用系统 UI 字体（Win10/11 中文环境为 Microsoft YaHei UI）。
            this.Font = SystemFonts.MessageBoxFont;

            _statusFont = MakeStatusFont(this.Font);
            _hintFont = MakeHintFont(this.Font);

            // ================= 根布局：单列纵向堆叠，每行高度按内容自适应 =================
            _root = new TableLayoutPanel
            {
                // 不用 Dock=Fill：一旦 Dock，尺寸就由窗体反过来决定，测不出内容到底要多大。
                // 让它自己 AutoSize，算完之后它的 Size 就是内容的真实尺寸，窗体照抄即可。
                Location = new Point(0, 0),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(16, 14, 16, 10)
            };
            _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            for (int i = 0; i < 4; i++)
            {
                _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }

            // ================= 设置区：标签列 + 输入列 + 附加按钮列 =================
            var grid = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 3,
                RowCount = 4,
                Margin = new Padding(0)
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            // 标签只锚左边：TableLayoutPanel 会让它在单元格里自动垂直居中，
            // 不需要手工写上边距去跟右侧输入控件对齐（手工边距一换字体 / DPI 就会错位）。
            static Label MakeCaption(string text) => new Label
            {
                Text = text,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 0, 10, 0)
            };

            // 三个输入控件都 Left|Right 锚定：它们同处一个自适应列，列宽取三者最大值，
            // 拉伸后右边缘自然对齐，不会出现长短不一的参差。
            const AnchorStyles InputAnchor = AnchorStyles.Left | AnchorStyles.Right;
            var inputMargin = new Padding(0, 3, 0, 3);
            const int InputWidth = 96;

            // 热键框按实际字体测量最长的常见键名来定宽，而不是拍一个固定像素值：
            // 换字体、换 DPI、换键都不会把文字裁掉。
            _hotkeyFieldWidth = InputWidth;
            foreach (string sample in new[] { "PageDown", "PrintScreen", "ScrollLock", "Backspace" })
            {
                int w = TextRenderer.MeasureText(sample, this.Font).Width + 16;
                if (w > _hotkeyFieldWidth)
                {
                    _hotkeyFieldWidth = w;
                }
            }

            numInterval = new NumericUpDown
            {
                Width = InputWidth,
                Anchor = InputAnchor,
                Margin = inputMargin,
                Minimum = 0.1m,
                Maximum = 1000m,
                DecimalPlaces = 1,
                Increment = 0.1m,
                TextAlign = HorizontalAlignment.Center
            };
            numInterval.ValueChanged += NumInterval_ValueChanged;

            cmbButton = new ComboBox
            {
                Width = InputWidth,
                Anchor = InputAnchor,
                Margin = inputMargin,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbButton.Items.AddRange(new object[] { "左键", "右键", "中键" });
            cmbButton.SelectedIndexChanged += CmbButton_SelectedIndexChanged;

            txtHotkey = new TextBox
            {
                Width = _hotkeyFieldWidth,
                Anchor = InputAnchor,
                Margin = inputMargin,
                ReadOnly = true,
                TextAlign = HorizontalAlignment.Center,
                Text = GetKeyDisplayName(DefaultHotkey)
            };

            // 按钮不写死 Size，改成按文字自适应 + 内边距：
            // 这样"点击设置" / "请按键…"两种文案在任何缩放下都不会被按钮边框裁掉。
            btnSetHotkey = new Button
            {
                Text = "点击设置",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Anchor = AnchorStyles.Left,
                Padding = new Padding(10, 3, 10, 3),
                Margin = new Padding(8, 3, 0, 3)
            };
            btnSetHotkey.Click += BtnSetHotkey_Click;

            chkTopMost = new CheckBox
            {
                Text = "窗口置顶",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 8, 0, 0)
            };
            chkTopMost.CheckedChanged += ChkTopMost_CheckedChanged;

            grid.Controls.Add(MakeCaption("点击间隔（毫秒）"), 0, 0);
            grid.Controls.Add(numInterval, 1, 0);

            grid.Controls.Add(MakeCaption("鼠标按键"), 0, 1);
            grid.Controls.Add(cmbButton, 1, 1);

            grid.Controls.Add(MakeCaption("触发热键"), 0, 2);
            grid.Controls.Add(txtHotkey, 1, 2);
            grid.Controls.Add(btnSetHotkey, 2, 2);

            grid.Controls.Add(chkTopMost, 0, 3);
            grid.SetColumnSpan(chkTopMost, 3);

            // ================= 分隔线 =================
            // 宽度给 1 是为了不让它撑大所在列；Anchor 左右会把它自动拉满整行宽度。
            var divider = new Panel
            {
                Size = new Size(1, 1),
                BackColor = SystemColors.ControlLight,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = new Padding(0, 12, 0, 10)
            };

            // ================= 状态区：状态靠左、速度靠右，压成一行 =================
            lblStatus = new Label
            {
                Text = "状态：就绪",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0),
                Font = _statusFont,
                ForeColor = Color.SeaGreen
            };

            lblCps = new Label
            {
                Text = "速度：0 次/秒",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(16, 0, 0, 0)
            };

            // 用 FlowLayoutPanel 横着排：它的需求尺寸能被外层 TableLayoutPanel 正确测出来，
            // 而 Dock=Fill 的嵌套 TableLayoutPanel 在自适应行里会被算成 0 高，导致状态区被裁掉。
            var statusRow = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0)
            };
            statusRow.Controls.Add(lblStatus);
            statusRow.Controls.Add(lblCps);

            // ================= 底部提示 =================
            // Anchor=None 让它在整行里自动水平居中，AutoSize 保证整句文字宽度被完整计入窗口宽度。
            lblHint = new Label
            {
                Text = "按住热键开始连点，松开停止",
                AutoSize = true,
                Anchor = AnchorStyles.None,
                ForeColor = Color.Gray,
                Font = _hintFont,
                Margin = new Padding(0, 12, 0, 0)
            };

            _root.Controls.Add(grid, 0, 0);
            _root.Controls.Add(divider, 0, 1);
            _root.Controls.Add(statusRow, 0, 2);
            _root.Controls.Add(lblHint, 0, 3);

            this.Controls.Add(_root);

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

        // ========================= DPI / 分辨率自适应 =========================

        // 两个派生字体都相对窗体字体推导，DPI 变化后按新的窗体字体重推一遍即可保持相对字号不变
        // （Scale 之后 this.Font 已经是新 DPI 下的字号，所以这里直接拿它当基准）
        private static Font MakeStatusFont(Font baseFont) =>
            new Font(baseFont.FontFamily, baseFont.Size + 1.5f, FontStyle.Bold);

        private static Font MakeHintFont(Font baseFont) =>
            new Font(baseFont.FontFamily, Math.Max(6f, baseFont.Size - 0.5f));

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            _currentDpi = GetDpiForWindowSafe();

            // 此时窗口已按内容 + 当前 DPI 完成自适应，尺寸是最终值，可以据此摆位置
            CenterOnActiveScreen();
        }

        private const int WM_DPICHANGED = 0x02E0;

        /// <summary>
        /// 系统缩放被改动、或窗口被拖到另一块不同缩放的显示器时，系统发来 WM_DPICHANGED。
        /// .NET Framework 4.8 的 WinForms 不会自动响应它（那套自动重排要靠 app.config 里的
        /// DpiAwareness 开关，而我们为了保持"单个 exe"没有带 config 文件），所以这里自己处理：
        /// 按新旧 DPI 的比例缩放整棵控件树，再重新推导派生字体并重新测算窗口尺寸。
        /// wParam 低 16 位是新 DPI，lParam 指向系统建议的新窗口矩形。
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED)
            {
                int newDpi = m.WParam.ToInt32() & 0xFFFF;
                HandleDpiChanged(newDpi);
            }

            base.WndProc(ref m);
        }

        private void HandleDpiChanged(int newDpi)
        {
            if (newDpi <= 0 || newDpi == _currentDpi)
            {
                return;
            }

            float ratio = newDpi / (float)_currentDpi;
            _currentDpi = newDpi;

            this.SuspendLayout();

            // Scale 会把整棵控件树的尺寸、位置、内外边距按比例缩放；
            // AutoSize 的控件随后会按新字体重新测量，固定宽度的输入框则靠这一步跟上缩放。
            this.Scale(new SizeF(ratio, ratio));
            ApplyDerivedFonts();

            this.ResumeLayout(true);

            ClampIntoWorkingArea();
        }

        private void ApplyDerivedFonts()
        {
            Font oldStatus = _statusFont;
            Font oldHint = _hintFont;

            _statusFont = MakeStatusFont(this.Font);
            _hintFont = MakeHintFont(this.Font);

            lblStatus.Font = _statusFont;
            lblHint.Font = _hintFont;

            oldStatus.Dispose();
            oldHint.Dispose();
        }

        /// <summary>
        /// 取当前窗口所在显示器的 DPI。GetDpiForWindow 是 Win10 1607+ 才有的 API，
        /// 更早的系统上回退到桌面 DC 的全局 DPI（那些系统本来也没有按显示器分别缩放的能力）。
        /// </summary>
        private int GetDpiForWindowSafe()
        {
            try
            {
                int dpi = GetDpiForWindow(this.Handle);
                if (dpi > 0)
                {
                    return dpi;
                }
            }
            catch (EntryPointNotFoundException)
            {
                // 旧系统没有这个导出，走下面的回退分支
            }
            catch (DllNotFoundException)
            {
            }

            using (Graphics g = this.CreateGraphics())
            {
                return (int)Math.Round(g.DpiX);
            }
        }

        /// <summary>
        /// 分辨率自适应：把窗口摆到"鼠标所在显示器"的工作区正中。
        /// 用工作区而不是整块屏幕，窗口就不会被任务栏压住；
        /// 用鼠标所在屏幕而不是主屏，多显示器下才会开在用户正看着的那块屏上。
        /// </summary>
        private void CenterOnActiveScreen()
        {
            FitToContent();

            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            ShrinkToFit(wa);

            this.Location = new Point(
                wa.X + Math.Max(0, (wa.Width - this.Width) / 2),
                wa.Y + Math.Max(0, (wa.Height - this.Height) / 2));
        }

        /// <summary>
        /// 把窗口约束回所在显示器的工作区：先裁尺寸再推位置，保证整个窗口始终完整可见。
        /// </summary>
        private void ClampIntoWorkingArea()
        {
            FitToContent();

            Rectangle wa = Screen.FromControl(this).WorkingArea;
            ShrinkToFit(wa);

            int x = Math.Min(Math.Max(this.Left, wa.Left), wa.Right - this.Width);
            int y = Math.Min(Math.Max(this.Top, wa.Top), wa.Bottom - this.Height);
            if (x != this.Left || y != this.Top)
            {
                this.Location = new Point(x, y);
            }
        }

        /// <summary>
        /// 按当前 DPI / 字体下根布局的实测需求尺寸来定窗口大小。
        /// 所有控件都是 AutoSize + 自适应行列，所以这个需求尺寸天然跟着缩放和字体走，
        /// 既不会把文字裁掉、也不会让控件互相遮挡，同时不留多余空白。
        /// </summary>
        private void FitToContent()
        {
            // 先让根表格按当前字体 / DPI 把自己撑到该有的大小，再把窗体客户区对齐过去。
            // 不用 GetPreferredSize：它在这种"表格套表格"的结构上只会算出第一个子表格的尺寸，
            // 连 Padding 都漏掉，结果就是按钮和下半部分被裁掉。
            _root.PerformLayout();

            Size need = _root.Size;
            if (this.ClientSize != need)
            {
                this.ClientSize = need;
            }
        }

        /// <summary>
        /// 兜底：极低分辨率叠加极高缩放时，自适应算出来的窗口可能比工作区还大，这里裁到工作区大小。
        /// 裁之前必须先关掉 AutoSize，否则刚写进去的 Size 会被自适应逻辑立刻改回去。
        /// </summary>
        private void ShrinkToFit(Rectangle workingArea)
        {
            int w = Math.Min(this.Width, workingArea.Width);
            int h = Math.Min(this.Height, workingArea.Height);
            if (w == this.Width && h == this.Height)
            {
                return;
            }

            this.AutoSize = false;
            this.Size = new Size(w, h);
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
            SetHotkeyDisplay(GetKeyDisplayName(key));
        }

        /// <summary>
        /// 显示热键名。基准宽度已经覆盖了常见键名，这里再兜一层：
        /// 万一用户设了个更长的冷门键（比如 BrowserFavorites），就把框撑开并重新测算窗口，
        /// 保证任何键名都不会被裁掉。
        /// </summary>
        private void SetHotkeyDisplay(string text)
        {
            txtHotkey.Text = text;

            int need = TextRenderer.MeasureText(text, txtHotkey.Font).Width + 16;
            int want = Math.Max(_hotkeyFieldWidth, need);
            if (txtHotkey.Width != want)
            {
                txtHotkey.Width = want;
                if (this.IsHandleCreated)
                {
                    FitToContent();
                }
            }
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

        [DllImport("user32.dll")]
        private static extern int GetDpiForWindow(IntPtr hwnd);

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
