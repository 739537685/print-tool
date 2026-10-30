using System.Runtime.InteropServices;

namespace PrintAssistant;

internal static class Program
{
    public const string ShowMainWindowMessageName = "PrintAssistant.ShowMainWindow";

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private static readonly IntPtr HwndBroadcast = (IntPtr)0xffff;

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\PrintAssistant.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            var msg = RegisterWindowMessage(ShowMainWindowMessageName);
            if (msg != 0)
            {
                PostMessage(HwndBroadcast, msg, IntPtr.Zero, IntPtr.Zero);
            }

            return;
        }

        ApplicationConfiguration.Initialize();
        var startHidden = args.Any(arg =>
            string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase));

        using var context = new TrayAppContext(startHidden);
        Application.Run(context);
    }
}
