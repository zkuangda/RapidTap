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
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
