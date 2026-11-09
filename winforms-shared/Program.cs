using System;
using System.Windows.Forms;

namespace Winzilla
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
#if NET
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
#endif
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
