using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RapidTap
{
    public class MainForm : Form
    {
        // ========================= 控件字段 =========================
        private NumericUpDown numInterval = null!;
        private NumericUpDown numCps = null!;
        private ComboBox cmbButton = null!;
        private TextBox txtHotkey = null!;
        private Button btnSetHotkey = null!;
        private ComboBox cmbTrigger = null!;
        private ComboBox cmbPosition = null!;
        private Button btnPickPoint = null!;
        private Label lblFixedPoint = null!;
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

        // 热键框的基准宽度：按常见键名里最长的那个实测得出
        private int _hotkeyFieldWidth;

        // numInterval 与 numCps 互相换算时防止无限回弹
        private bool _syncingIntervalCps;

        // ========================= 状态 =========================

        private enum MouseButtonType
        {
            Left,
            Right,
            Middle
        }

        private readonly AppSettings _settings;

        private readonly ClickEngine _engine;

        /// <summary>
        /// 当前热键。做成不可变类而非结构体，是因为连点线程的兜底回调也要读它：
        /// 引用赋值是原子的，读到的要么是旧的完整绑定、要么是新的完整绑定，不会撕裂。
        /// </summary>
        private volatile HotkeyBinding _hotkey = HotkeyBinding.Default;

        // 下面几个字段连点线程要读，用 volatile 保证可见性（enum / bool 底层都是 int，可以 volatile）
        private volatile MouseButtonType _mouseButton = MouseButtonType.Left;
        private volatile bool _clickAtFixedPoint;
        private volatile int _fixedPointX;
        private volatile int _fixedPointY;
        private volatile bool _holdMode = true;

        // 录入热键中：此状态下按键不触发连点，而是被当作新热键
        private volatile bool _capturingHotkey;

        // 拾取坐标中：此状态下鼠标左键点击会被吞掉并记录为目标坐标
        private volatile bool _pickingPoint;
        private volatile bool _swallowNextMouseUp;

        // 热键是否正被物理按着，用来过滤按键自动重复（切换模式下尤其关键，
        // 否则按住不放会以重复速率疯狂开关）
        private bool _hotkeyDown;

        // timeBeginPeriod / timeEndPeriod 必须配对，用这个标志保证不会重复升降
        private bool _timerPeriodRaised;

        private long _lastClickCountForCps;

        public MainForm()
        {
            // 点击动作和"热键是否还按着"都注入给引擎，引擎自身不认识 Win32，也不认识窗体
            _engine = new ClickEngine(PerformClick, HotkeyStillPhysicallyHeld);
            _engine.StoppedBySafetyCheck += Engine_StoppedBySafetyCheck;

            _settings = AppSettings.Load();

            BuildUi();
            ApplySettingsToUi();

            // 委托必须先赋给字段持有，再传给 SetWindowsHookEx，防止委托被 GC 回收导致钩子失效
            _keyboardHookProc = KeyboardHookCallback;
            _mouseHookProc = MouseHookCallback;

            // 安装全局低级键盘钩子（构造函数运行在主线程，后续 Application.Run 的消息泵也在同一线程，满足钩子要求）
            InstallKeyboardHook();
        }

        // ========================= 界面构建（纯代码，不用设计器） =========================

        private void BuildUi()
        {
            this.SuspendLayout();

            this.Text = "RapidTap";
            this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            // 按"字体"自动缩放：设计基准是 96dpi 下的默认 UI 字体（平均字符约 7x15 像素）。
            // 进程已在 app.manifest 里声明 PerMonitorV2，所以系统字体拿到的像素尺寸本身就带着
            // 当前缩放，AutoScaleMode.Font 据此把整窗控件按比例放大，启动时的 125% / 150% / 175%
            // 缩放无需任何额外代码即可正确排版。
            this.AutoScaleMode = AutoScaleMode.Font;
            this.AutoScaleDimensions = new SizeF(7F, 15F);

            // 窗口尺寸不写死 ClientSize，而是在 OnLoad 里按根布局的实测需求尺寸赋值（见 FitToContent）
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

            // ================= 设置区：标签列 + 输入列 + 附加控件列 =================
            var grid = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 3,
                RowCount = 7,
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

            // 输入控件都 Left|Right 锚定：它们同处一个自适应列，列宽取最大值，
            // 拉伸后右边缘自然对齐，不会出现长短不一的参差。
            const AnchorStyles InputAnchor = AnchorStyles.Left | AnchorStyles.Right;
            var inputMargin = new Padding(0, 3, 0, 3);
            const int InputWidth = 96;

            // 热键框按实际字体测量最长的常见键名来定宽，而不是拍一个固定像素值：
            // 换字体、换 DPI、换键都不会把文字裁掉。
            _hotkeyFieldWidth = InputWidth;
            foreach (string sample in new[] { "Ctrl + Shift + PageDown", "PrintScreen", "Backspace" })
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
                Minimum = ClickTiming.MinIntervalMs,
                Maximum = ClickTiming.MaxIntervalMs,
                DecimalPlaces = 1,
                Increment = 0.1m,
                TextAlign = HorizontalAlignment.Center
            };
            numInterval.ValueChanged += NumInterval_ValueChanged;

            // 间隔和速度是同一件事的两种说法，双向联动：多数人心里想的是"每秒多少下"，
            // 但精细调节时按毫秒更直观，两个都给，改哪个另一个跟着走。
            numCps = new NumericUpDown
            {
                Width = InputWidth,
                Anchor = InputAnchor,
                Margin = inputMargin,
                Minimum = ClickTiming.MinCps,
                Maximum = ClickTiming.MaxCps,
                DecimalPlaces = 1,
                Increment = 1m,
                TextAlign = HorizontalAlignment.Center
            };
            numCps.ValueChanged += NumCps_ValueChanged;

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
                TextAlign = HorizontalAlignment.Center
            };

            // 按钮不写死 Size，改成按文字自适应 + 内边距：
            // 文案变化（"点击设置" / "请按键…"）在任何缩放下都不会被按钮边框裁掉。
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

            cmbTrigger = new ComboBox
            {
                Width = InputWidth,
                Anchor = InputAnchor,
                Margin = inputMargin,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbTrigger.Items.AddRange(new object[] { "按住连点", "切换开关" });
            cmbTrigger.SelectedIndexChanged += CmbTrigger_SelectedIndexChanged;

            cmbPosition = new ComboBox
            {
                Width = InputWidth,
                Anchor = InputAnchor,
                Margin = inputMargin,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbPosition.Items.AddRange(new object[] { "跟随鼠标", "固定坐标" });
            cmbPosition.SelectedIndexChanged += CmbPosition_SelectedIndexChanged;

            btnPickPoint = new Button
            {
                Text = "拾取坐标",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Anchor = AnchorStyles.Left,
                Padding = new Padding(10, 3, 10, 3),
                Margin = new Padding(0, 3, 0, 3)
            };
            btnPickPoint.Click += BtnPickPoint_Click;

            lblFixedPoint = new Label
            {
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(8, 0, 0, 0),
                ForeColor = Color.Gray
            };

            var pointPanel = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 1,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(8, 0, 0, 0)
            };
            pointPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pointPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pointPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            pointPanel.Controls.Add(btnPickPoint, 0, 0);
            pointPanel.Controls.Add(lblFixedPoint, 1, 0);

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

            grid.Controls.Add(MakeCaption("每秒点击次数"), 0, 1);
            grid.Controls.Add(numCps, 1, 1);

            grid.Controls.Add(MakeCaption("鼠标按键"), 0, 2);
            grid.Controls.Add(cmbButton, 1, 2);

            grid.Controls.Add(MakeCaption("触发热键"), 0, 3);
            grid.Controls.Add(txtHotkey, 1, 3);
            grid.Controls.Add(btnSetHotkey, 2, 3);

            grid.Controls.Add(MakeCaption("触发方式"), 0, 4);
            grid.Controls.Add(cmbTrigger, 1, 4);

            grid.Controls.Add(MakeCaption("点击位置"), 0, 5);
            grid.Controls.Add(cmbPosition, 1, 5);
            grid.Controls.Add(pointPanel, 2, 5);

            grid.Controls.Add(chkTopMost, 0, 6);
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

            // ================= 状态区：状态与速度压成一行 =================
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

            // CPS 刷新定时器：每 200ms 在 UI 线程读取一次计数并刷新界面，连点循环本身绝不碰 UI 控件
            cpsTimer = new System.Windows.Forms.Timer { Interval = 200 };
            cpsTimer.Tick += CpsTimer_Tick;
            cpsTimer.Start();

            this.ResumeLayout(true);
        }

        // ========================= 设置与界面的互相同步 =========================

        /// <summary>把读进来的设置灌进控件。期间各控件会触发 Changed 事件，正好完成到字段的同步。</summary>
        private void ApplySettingsToUi()
        {
            numInterval.Value = ClickTiming.ClampInterval(_settings.IntervalMs);
            cmbButton.SelectedIndex = _settings.MouseButtonIndex;
            cmbTrigger.SelectedIndex = _settings.Trigger == TriggerMode.Toggle ? 1 : 0;
            chkTopMost.Checked = _settings.TopMost;

            _fixedPointX = _settings.FixedPoint.X;
            _fixedPointY = _settings.FixedPoint.Y;

            // 放在坐标赋值之后：SelectedIndexChanged 里要用这两个值刷新显示
            cmbPosition.SelectedIndex = _settings.Position == ClickPosition.FixedPoint ? 1 : 0;

            SetHotkey(_settings.Hotkey);
        }

        private void CollectSettingsFromUi()
        {
            _settings.IntervalMs = numInterval.Value;
            _settings.MouseButtonIndex = cmbButton.SelectedIndex < 0 ? 0 : cmbButton.SelectedIndex;
            _settings.Hotkey = _hotkey;
            _settings.TopMost = chkTopMost.Checked;
            _settings.Trigger = cmbTrigger.SelectedIndex == 1 ? TriggerMode.Toggle : TriggerMode.Hold;
            _settings.Position = cmbPosition.SelectedIndex == 1 ? ClickPosition.FixedPoint : ClickPosition.FollowCursor;
            _settings.FixedPoint = new Point(_fixedPointX, _fixedPointY);
        }

        // ========================= 控件事件 =========================

        private void NumInterval_ValueChanged(object? sender, EventArgs e)
        {
            _engine.IntervalMs = numInterval.Value;

            if (_syncingIntervalCps)
            {
                return;
            }

            _syncingIntervalCps = true;
            try
            {
                numCps.Value = ClickTiming.IntervalToCps(numInterval.Value);
            }
            finally
            {
                _syncingIntervalCps = false;
            }
        }

        private void NumCps_ValueChanged(object? sender, EventArgs e)
        {
            if (_syncingIntervalCps)
            {
                return;
            }

            _syncingIntervalCps = true;
            try
            {
                numInterval.Value = ClickTiming.CpsToInterval(numCps.Value);
                _engine.IntervalMs = numInterval.Value;
            }
            finally
            {
                _syncingIntervalCps = false;
            }
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

        private void CmbTrigger_SelectedIndexChanged(object? sender, EventArgs e)
        {
            // 切换触发方式时先停下来，否则"按住模式下正在连点"的状态会被带进切换模式
            StopClicking();

            _holdMode = cmbTrigger.SelectedIndex != 1;
            UpdateHintText();
        }

        private void CmbPosition_SelectedIndexChanged(object? sender, EventArgs e)
        {
            bool fixedPoint = cmbPosition.SelectedIndex == 1;
            _clickAtFixedPoint = fixedPoint;

            btnPickPoint.Enabled = fixedPoint;
            UpdateFixedPointLabel();
            RefitIfReady();
        }

        private void ChkTopMost_CheckedChanged(object? sender, EventArgs e)
        {
            this.TopMost = chkTopMost.Checked;
        }

        private void BtnSetHotkey_Click(object? sender, EventArgs e)
        {
            if (_capturingHotkey || _pickingPoint)
            {
                return;
            }

            StopClicking();

            _capturingHotkey = true;
            btnSetHotkey.Text = "请按键…";
            txtHotkey.Text = "请按键…";
            SetStatus("状态：等待按键（Esc 取消）", Color.DarkOrange);
        }

        private void BtnPickPoint_Click(object? sender, EventArgs e)
        {
            if (_capturingHotkey || _pickingPoint)
            {
                return;
            }

            StopClicking();

            _pickingPoint = true;
            _swallowNextMouseUp = false;
            btnPickPoint.Text = "拾取中…";
            SetStatus("状态：点击目标位置（Esc 取消）", Color.DarkOrange);

            InstallMouseHook();
        }

        private void CpsTimer_Tick(object? sender, EventArgs e)
        {
            if (!_engine.IsRunning)
            {
                return;
            }

            long current = _engine.ClickCount;
            long delta = current - _lastClickCountForCps;
            _lastClickCountForCps = current;

            double cps = ClickTiming.ComputeCps(delta, cpsTimer.Interval / 1000.0);
            lblCps.Text = $"速度：{cps:0.#} 次/秒";
        }

        // ========================= 连点控制 =========================

        private void StartClicking()
        {
            if (_engine.IsRunning || _capturingHotkey || _pickingPoint)
            {
                return;
            }

            _lastClickCountForCps = 0;

            // 只在真正连点期间把系统时钟精度拉到 1ms。
            // 常驻开启会让整机的定时器中断一直维持在高频率，笔记本上是实打实的续航损耗。
            RaiseTimerPeriod();

            _engine.Start();

            SetStatus("状态：连点中", Color.OrangeRed);
        }

        private void StopClicking()
        {
            _engine.Stop();
            LowerTimerPeriod();

            SetStatus("状态：就绪", Color.SeaGreen);
            lblCps.Text = "速度：0 次/秒";
        }

        private void ToggleClicking()
        {
            if (_engine.IsRunning)
            {
                StopClicking();
            }
            else
            {
                StartClicking();
            }
        }

        /// <summary>
        /// 连点线程发现热键其实已经松开了。会发生在钩子收不到 KEYUP 的场合——
        /// 最典型的是按住热键期间前台切到了以管理员身份运行的窗口，
        /// 低级键盘钩子收不到高完整性级别进程的按键，那条 KEYUP 就永远不会到达。
        /// 没有这道兜底的话，程序会一直点下去，只能去任务管理器杀进程。
        /// </summary>
        private void Engine_StoppedBySafetyCheck(object? sender, EventArgs e)
        {
            if (!IsHandleCreated || IsDisposed)
            {
                return;
            }

            BeginInvoke(new Action(() =>
            {
                _hotkeyDown = false;
                StopClicking();
            }));
        }

        private bool HotkeyStillPhysicallyHeld()
        {
            // 切换模式下没有"一直按着"这回事，兜底不适用
            if (!_holdMode)
            {
                return true;
            }

            HotkeyBinding hotkey = _hotkey;
            return (GetAsyncKeyState((int)hotkey.Key) & 0x8000) != 0;
        }

        // ========================= 实际点击 =========================

        /// <summary>
        /// 注入给 ClickEngine 的点击动作，运行在连点线程上。
        /// 只读 volatile 字段、只调 SendInput，绝不碰任何 UI 控件。
        /// </summary>
        private void PerformClick()
        {
            if (_clickAtFixedPoint)
            {
                SendMouseClickAt(_mouseButton, _fixedPointX, _fixedPointY);
            }
            else
            {
                SendMouseClick(_mouseButton);
            }
        }

        private static void GetButtonFlags(MouseButtonType button, out uint downFlag, out uint upFlag)
        {
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
        }

        private static void SendMouseClick(MouseButtonType button)
        {
            GetButtonFlags(button, out uint downFlag, out uint upFlag);

            // 一次 SendInput 同时提交 down + up 两个 INPUT，减少一次系统调用；
            // 不设置 dx/dy 与 MOUSEEVENTF_MOVE，保证鼠标不移动，原地点击。
            INPUT[] inputs = new INPUT[2];
            inputs[0].type = INPUT_MOUSE;
            inputs[0].mi.dwFlags = downFlag;
            inputs[1].type = INPUT_MOUSE;
            inputs[1].mi.dwFlags = upFlag;

            SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        /// <summary>
        /// 在指定屏幕坐标点击：先绝对移动过去再按下抬起，三个 INPUT 一次提交。
        /// 每次都重新移动，是因为用户随时可能挪动鼠标，只在开始时移一次是不够的。
        /// </summary>
        private static void SendMouseClickAt(MouseButtonType button, int screenX, int screenY)
        {
            GetButtonFlags(button, out uint downFlag, out uint upFlag);

            // 绝对坐标必须归一化到 0..65535，且是相对整个虚拟桌面（副屏可能带负坐标）
            Rectangle virtualScreen = SystemInformation.VirtualScreen;
            int width = Math.Max(1, virtualScreen.Width - 1);
            int height = Math.Max(1, virtualScreen.Height - 1);
            int nx = (int)Math.Round((screenX - virtualScreen.Left) * 65535.0 / width);
            int ny = (int)Math.Round((screenY - virtualScreen.Top) * 65535.0 / height);

            INPUT[] inputs = new INPUT[3];
            inputs[0].type = INPUT_MOUSE;
            inputs[0].mi.dx = nx;
            inputs[0].mi.dy = ny;
            inputs[0].mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
            inputs[1].type = INPUT_MOUSE;
            inputs[1].mi.dwFlags = downFlag;
            inputs[2].type = INPUT_MOUSE;
            inputs[2].mi.dwFlags = upFlag;

            SendInput(3, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        // ========================= 热键录入 =========================

        private void SetHotkey(HotkeyBinding binding)
        {
            _hotkey = binding;
            SetHotkeyDisplay(binding.DisplayName);
        }

        /// <summary>
        /// 显示热键名。基准宽度已覆盖常见键名，这里再兜一层：
        /// 万一设了个更长的冷门键，就把框撑开并重新测算窗口，保证任何键名都不会被裁掉。
        /// </summary>
        private void SetHotkeyDisplay(string text)
        {
            txtHotkey.Text = text;

            int need = TextRenderer.MeasureText(text, txtHotkey.Font).Width + 16;
            int want = Math.Max(_hotkeyFieldWidth, need);
            if (txtHotkey.Width != want)
            {
                txtHotkey.Width = want;
                RefitIfReady();
            }
        }

        /// <summary>
        /// 录入结束的收尾，必须在钩子回调之外执行：
        /// 低级键盘钩子的回调有约 300ms 的超时，超时系统会静默卸掉钩子，
        /// 所以绝不能在回调里弹 MessageBox 等待用户点按钮。
        /// </summary>
        private void FinishHotkeyCapture(HotkeyBinding binding)
        {
            btnSetHotkey.Text = "点击设置";

            if (binding.IsRiskyBareKey)
            {
                string message =
                    "「" + binding.DisplayName + "」是不带修饰键的普通字符键。\r\n\r\n" +
                    "设为热键后，你在任何程序里输入这个字符都会触发连点，\r\n" +
                    "而且按键本身仍会照常输入。\r\n\r\n" +
                    "建议改用 F1~F12、PageDown 这类功能键，或加上 Ctrl / Alt / Shift。\r\n\r\n" +
                    "仍要使用吗？";

                DialogResult result = MessageBox.Show(this, message, "确认热键",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

                if (result != DialogResult.Yes)
                {
                    SetHotkeyDisplay(_hotkey.DisplayName);
                    SetStatus("状态：就绪", Color.SeaGreen);
                    return;
                }
            }

            SetHotkey(binding);
            SetStatus("状态：就绪", Color.SeaGreen);
        }

        private void CancelHotkeyCapture()
        {
            _capturingHotkey = false;
            btnSetHotkey.Text = "点击设置";
            SetHotkeyDisplay(_hotkey.DisplayName);
            SetStatus("状态：就绪", Color.SeaGreen);
        }

        // ========================= 坐标拾取 =========================

        private void FinishPointPick(int x, int y)
        {
            _pickingPoint = false;
            _swallowNextMouseUp = false;
            UninstallMouseHook();

            _fixedPointX = x;
            _fixedPointY = y;
            btnPickPoint.Text = "拾取坐标";
            UpdateFixedPointLabel();
            SetStatus("状态：就绪", Color.SeaGreen);
            RefitIfReady();
        }

        private void CancelPointPick()
        {
            _pickingPoint = false;
            _swallowNextMouseUp = false;
            UninstallMouseHook();

            btnPickPoint.Text = "拾取坐标";
            SetStatus("状态：就绪", Color.SeaGreen);
        }

        private void UpdateFixedPointLabel()
        {
            if (!_clickAtFixedPoint)
            {
                lblFixedPoint.Text = string.Empty;
                return;
            }

            lblFixedPoint.Text = _fixedPointX == 0 && _fixedPointY == 0
                ? "未设置"
                : "(" + _fixedPointX + ", " + _fixedPointY + ")";
        }

        // ========================= 界面小工具 =========================

        private void SetStatus(string text, Color color)
        {
            lblStatus.Text = text;
            lblStatus.ForeColor = color;
            RefitIfReady();
        }

        private void UpdateHintText()
        {
            lblHint.Text = _holdMode
                ? "按住热键开始连点，松开停止"
                : "按一下热键开始连点，再按一下停止";
            RefitIfReady();
        }

        /// <summary>文字变化可能改变所需宽度，窗口已经显示出来时就重新贴合一次。</summary>
        private void RefitIfReady()
        {
            if (IsHandleCreated && !IsDisposed)
            {
                FitToContent();
            }
        }

        // ========================= 全局钩子 =========================

        private const int WH_KEYBOARD_LL = 13;
        private const int WH_MOUSE_LL = 14;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;

        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;

        private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

        // 必须用字段持有委托，防止被 GC 回收导致钩子失效（回调地址失效会引发崩溃式异常）
        private readonly LowLevelProc _keyboardHookProc;
        private readonly LowLevelProc _mouseHookProc;
        private IntPtr _keyboardHookId = IntPtr.Zero;
        private IntPtr _mouseHookId = IntPtr.Zero;

        private void InstallKeyboardHook()
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule!)
            {
                _keyboardHookId = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardHookProc,
                    GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        private void UninstallKeyboardHook()
        {
            if (_keyboardHookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHookId);
                _keyboardHookId = IntPtr.Zero;
            }
        }

        /// <summary>鼠标钩子只在"拾取坐标"期间存在，平时不挂，避免无谓地在每次鼠标移动上过一道手。</summary>
        private void InstallMouseHook()
        {
            if (_mouseHookId != IntPtr.Zero)
            {
                return;
            }

            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule!)
            {
                _mouseHookId = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookProc,
                    GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        private void UninstallMouseHook()
        {
            if (_mouseHookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHookId);
                _mouseHookId = IntPtr.Zero;
            }
        }

        /// <summary>
        /// 全局键盘钩子回调。之所以用 WH_KEYBOARD_LL 而不是 RegisterHotKey，
        /// 是因为 RegisterHotKey 只能收到"按下"，收不到"松开"，无法实现"松开即停"。
        ///
        /// 回调运行在安装钩子的线程（本程序即 UI 线程）上，这是 WH_KEYBOARD_LL 的固定行为，
        /// 因此这里直接操作 UI 控件是安全的；但必须尽快返回，耗时的事情一律 BeginInvoke 出去。
        /// </summary>
        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var key = (Keys)kb.vkCode;
                int msg = wParam.ToInt32();
                bool isKeyDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isKeyUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                if (_pickingPoint)
                {
                    if (isKeyDown && key == Keys.Escape)
                    {
                        BeginInvoke(new Action(CancelPointPick));
                        return new IntPtr(1);
                    }
                }
                else if (_capturingHotkey)
                {
                    if (isKeyDown)
                    {
                        if (key == Keys.Escape)
                        {
                            _capturingHotkey = false;
                            BeginInvoke(new Action(CancelHotkeyCapture));
                            return new IntPtr(1);
                        }

                        // 修饰键本身不能当主键，按下它们时继续等待真正的按键，
                        // 这样才能录入 Ctrl+Shift+A 这类组合
                        if (!HotkeyBinding.IsModifierKey(key))
                        {
                            var binding = new HotkeyBinding(
                                key,
                                (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0,
                                (GetAsyncKeyState(VK_MENU) & 0x8000) != 0,
                                (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0);

                            _capturingHotkey = false;
                            BeginInvoke(new Action(() => FinishHotkeyCapture(binding)));

                            // 录入用的这一下不该漏给别的程序
                            return new IntPtr(1);
                        }
                    }
                }
                else
                {
                    HandleHotkeyMessage(key, isKeyDown, isKeyUp);
                }
            }

            // 正常情况下一律放行，绝不吞掉按键，避免影响用户正常输入
            return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
        }

        private void HandleHotkeyMessage(Keys key, bool isKeyDown, bool isKeyUp)
        {
            HotkeyBinding hotkey = _hotkey;

            if (key != hotkey.Key)
            {
                return;
            }

            if (isKeyDown)
            {
                // 按住不放时系统会持续重发 KEYDOWN，只认第一次按下这个跳变，
                // 否则切换模式会按重复速率疯狂开关
                if (_hotkeyDown)
                {
                    return;
                }

                // 修饰键只在按下时校验；松开时不校验，因为用户完全可能先松开 Ctrl 再松开主键
                bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                bool alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
                bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
                if (ctrl != hotkey.Ctrl || alt != hotkey.Alt || shift != hotkey.Shift)
                {
                    return;
                }

                _hotkeyDown = true;

                if (_holdMode)
                {
                    StartClicking();
                }
                else
                {
                    ToggleClicking();
                }
            }
            else if (isKeyUp)
            {
                _hotkeyDown = false;

                if (_holdMode)
                {
                    StopClicking();
                }
            }
        }

        /// <summary>
        /// 拾取坐标期间的鼠标钩子。吞掉这一次左键按下与抬起，
        /// 否则用户为了选坐标而点的这一下会真的点在目标程序上。
        /// </summary>
        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _pickingPoint)
            {
                int msg = wParam.ToInt32();

                if (msg == WM_LBUTTONDOWN)
                {
                    var ms = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    int x = ms.pt.X;
                    int y = ms.pt.Y;

                    _swallowNextMouseUp = true;
                    BeginInvoke(new Action(() => FinishPointPick(x, y)));
                    return new IntPtr(1);
                }

                if (msg == WM_LBUTTONUP && _swallowNextMouseUp)
                {
                    _swallowNextMouseUp = false;
                    return new IntPtr(1);
                }
            }

            return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
        }

        // ========================= 系统时钟精度 =========================

        private void RaiseTimerPeriod()
        {
            if (_timerPeriodRaised)
            {
                return;
            }

            // 不提高精度的话 Thread.Sleep 的粒度约为 15.6ms，2ms 以上的间隔全都会失准
            timeBeginPeriod(1);
            _timerPeriodRaised = true;
        }

        private void LowerTimerPeriod()
        {
            if (!_timerPeriodRaised)
            {
                return;
            }

            timeEndPeriod(1);
            _timerPeriodRaised = false;
        }

        // ========================= DPI / 分辨率自适应 =========================

        // 两个派生字体都相对窗体字体推导，DPI 变化后按新的窗体字体重推一遍即可保持相对字号不变
        private static Font MakeStatusFont(Font baseFont) =>
            new Font(baseFont.FontFamily, baseFont.Size + 1.5f, FontStyle.Bold);

        private static Font MakeHintFont(Font baseFont) =>
            new Font(baseFont.FontFamily, Math.Max(6f, baseFont.Size - 0.5f));

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            _currentDpi = GetDpiForWindowSafe();

            UpdateHintText();

            // 此时窗口已按内容 + 当前 DPI 完成自适应，尺寸是最终值，可以据此摆位置
            CenterOnActiveScreen();

        }

        private const int WM_DPICHANGED = 0x02E0;

        /// <summary>
        /// 系统缩放被改动、或窗口被拖到另一块不同缩放的显示器时，系统发来 WM_DPICHANGED。
        /// .NET Framework 4.8 的 WinForms 不会自动响应它（那套自动重排要靠 App.config 里的
        /// DpiAwareness 开关，而我们为了保持"单个 exe"没有带 config 文件），所以这里自己处理：
        /// 按新旧 DPI 的比例缩放整棵控件树，再重新推导派生字体并重新测算窗口尺寸。
        /// wParam 低 16 位是新 DPI。
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
            // AutoSize 的控件随后按新字体重新测量，固定宽度的输入框则靠这一步跟上缩放。
            this.Scale(new SizeF(ratio, ratio));
            _hotkeyFieldWidth = (int)Math.Round(_hotkeyFieldWidth * ratio);
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

        /// <summary>把窗口约束回所在显示器的工作区：先裁尺寸再推位置，保证整个窗口始终完整可见。</summary>
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
            // 连 Padding 都漏掉。
            _root.PerformLayout();

            Size need = _root.Size;
            if (this.ClientSize != need)
            {
                this.ClientSize = need;
            }
        }

        /// <summary>
        /// 兜底：极低分辨率叠加极高缩放时，自适应算出来的窗口可能比工作区还大，这里裁到工作区大小。
        /// </summary>
        private void ShrinkToFit(Rectangle workingArea)
        {
            int w = Math.Min(this.Width, workingArea.Width);
            int h = Math.Min(this.Height, workingArea.Height);
            if (w == this.Width && h == this.Height)
            {
                return;
            }

            this.Size = new Size(w, h);
        }

        // ========================= 窗体关闭 =========================

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _engine.StopAndJoin(200);
            LowerTimerPeriod();

            UninstallKeyboardHook();
            UninstallMouseHook();

            CollectSettingsFromUi();
            _settings.Save();

            base.OnFormClosing(e);
        }

        // ========================= P/Invoke 声明 =========================

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

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
        private static extern short GetAsyncKeyState(int vKey);

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

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private const uint INPUT_MOUSE = 0;
        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
        private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

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
        /// INPUT 是 C 里的"联合体"：type 之后是 MOUSEINPUT / KEYBDINPUT / HARDWAREINPUT 的联合。
        /// 这里只用鼠标输入，用 LayoutKind.Explicit + FieldOffset 手动指定内存布局：
        /// type 占 4 字节，但 MOUSEINPUT 含 8 字节对齐的 IntPtr（dwExtraInfo），
        /// x64 下整个联合体需要 8 字节对齐，所以 mi 必须从偏移量 8 开始（而不是 4），
        /// 否则 SendInput 会因结构体大小 / 对齐不对而调用失败或产生垃圾数据。
        /// 最终 sizeof(INPUT) 在 x64 下应为 40 字节，与操作系统定义一致。
        /// </summary>
        [StructLayout(LayoutKind.Explicit)]
        private struct INPUT
        {
            [FieldOffset(0)] public uint type;
            [FieldOffset(8)] public MOUSEINPUT mi;
        }
    }
}
