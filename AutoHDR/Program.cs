using AutoHDR.Forms;

namespace AutoHDR;

internal static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    private static void Main()
    {
        _mutex = new Mutex(true, @"Local\AutoHDR_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "AutoHDR is already running (check the system tray).",
                "AutoHDR",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());

        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
