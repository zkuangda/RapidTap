using System;
using System.Windows.Forms;

namespace RapidTap
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // 手写启动逻辑，不依赖 SDK 自动生成的 ApplicationConfiguration
            // PerMonitorV2：窗口跨到不同缩放的显示器、或用户中途改系统缩放时，
            // 由 WinForms 按新 DPI 真正重算控件尺寸和字体，而不是像 SystemAware 那样让系统拉伸位图（会糊）。
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
