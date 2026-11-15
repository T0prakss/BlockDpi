using System.Diagnostics;
using Avalonia;
using BlockDpi.Engine;

namespace BlockDpi;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        KillExistingInstances();

        using var mutex = new Mutex(true, "Global\\BlockDpi_SingleInstance", out _);

        SystemProxy.CleanupLeftover(ProxyDpiEngine.Port);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void KillExistingInstances()
    {
        int myPid = Environment.ProcessId;
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                if (p.Id == myPid) continue;
                try
                {
                    if (p.ProcessName.StartsWith("BlockDpi", StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill();
                        p.WaitForExit(2000);
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
            .With(new SkiaOptions { MaxGpuResourceSizeBytes = 1024 * 1024 });
}
