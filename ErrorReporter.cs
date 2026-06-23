using System.Threading;
using System.Windows.Forms;

namespace Trayce;

internal static class ErrorReporter
{
    private static int showing;

    public static void Install()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Show(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var exception = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown fatal error");
            Show(exception);
        };
    }

    public static void Show(Exception exception)
    {
        if (Interlocked.Exchange(ref showing, 1) == 1) return;

        try
        {
            if (IsAutomationMode())
            {
                Console.Error.WriteLine(exception);
                return;
            }

            MessageBox.Show(
                "Trayce hit an unexpected error.\n\n" +
                exception.Message +
                "\n\nIf this keeps happening, open an issue and include what you were doing when it happened.",
                "Trayce",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            Interlocked.Exchange(ref showing, 0);
        }
    }

    private static bool IsAutomationMode()
    {
        var args = Environment.GetCommandLineArgs();
        return args.Any(arg => arg.StartsWith("--render", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(arg, "--self-test", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(arg, "--shot", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(arg, "--dpi-probe", StringComparison.OrdinalIgnoreCase));
    }
}
