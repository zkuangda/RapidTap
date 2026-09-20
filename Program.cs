using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace RapidTap
{
    internal static class Program
    {
        /// <summary>
        /// 互斥体名。用 Local\ 前缀而非 Global\：限制在当前登录会话内，
        /// 这样多用户同时登录同一台机器时互不影响，也不需要任何特殊权限。
        /// </summary>
        private const string SingleInstanceMutexName = @"Local\RapidTap.SingleInstance";

        [STAThread]
        private static void Main()
        {
            // 单实例：开两份会装上两套全局键盘钩子，按一次热键触发两路连点，
            // 速度凭空翻倍且谁都关不干净。直接挡在门外，并把已有的那个窗口唤到前台。
            using (var mutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew))
            {
                if (!createdNew)
                {
                    ActivateExistingInstance();
                    return;
                }

                // 注意：这里没有 Application.SetHighDpiMode —— 那是 .NET Core 3.0+ 才有的 API。
                // .NET Framework 4.8 的 DPI 感知只能在清单里声明，见 app.manifest 的 PerMonitorV2，
                // 它在进程启动前就生效，比任何代码调用都早，也更可靠。
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());

                GC.KeepAlive(mutex);
            }
        }

        /// <summary>
        /// 把已经在跑的那个实例的窗口唤到前台。找不到也无所谓——
        /// 用户至少知道"再点一次没反应"是因为程序已经开着了。
        /// </summary>
        private static void ActivateExistingInstance()
        {
            try
            {
                Process current = Process.GetCurrentProcess();
                foreach (Process other in Process.GetProcessesByName(current.ProcessName))
                {
                    if (other.Id == current.Id || other.MainWindowHandle == IntPtr.Zero)
                    {
                        continue;
                    }

                    // 可能被最小化了，先还原再置前
                    ShowWindow(other.MainWindowHandle, SW_RESTORE);
                    SetForegroundWindow(other.MainWindowHandle);
                    return;
                }
            }
            catch (Exception)
            {
                // 唤前台失败不影响"拒绝启动第二个实例"这个主要目的
            }
        }

        private const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
