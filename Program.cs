using System;
using System.Windows.Forms;

namespace RapidTap
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // 注意：这里没有 Application.SetHighDpiMode —— 那是 .NET Core 3.0+ 才有的 API。
            // .NET Framework 4.8 的 DPI 感知只能在清单里声明，见 app.manifest 的 PerMonitorV2，
            // 它在进程启动前就生效，比任何代码调用都早，也更可靠。
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
