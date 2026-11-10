using System.Diagnostics;
using System.Text;
using DiskDoctor.Core;

namespace DiskDoctor;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        int st = Array.IndexOf(args, "--selftest");
        if (st >= 0) return SelfTest.Run(args.Length > st + 1 ? args[st + 1] : "selftest.txt", args.Skip(st + 2).ToArray());

        if (!Sys.IsAdmin && !args.Contains("--no-elevate"))
        {
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--elevated " + string.Join(' ', args)) { UseShellExecute = true, Verb = "runas" });
                return 0;
            }
            catch
            {
                // пользователь отказался в UAC — продолжаем с ограниченными правами
            }
        }

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => ShowError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowError(e.ExceptionObject as Exception);
        Application.Run(new MainForm(args));
        return 0;
    }

    static void ShowError(Exception ex)
    {
        Log.Write("ОШИБКА: " + ex);
        MessageBox.Show(ex?.Message ?? "Неизвестная ошибка", "DiskDoctor — ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
