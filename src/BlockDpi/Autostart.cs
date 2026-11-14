using System.Diagnostics;
using System.Security;

namespace BlockDpi;

public static class Autostart
{
    private const string TaskName = "BlockDpi";
    private const string MacLabel = "com.blockdpi.app";

    private static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "BlockDpi");

    private static string MacPlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "LaunchAgents", MacLabel + ".plist");

    public static void Apply(bool enable)
    {
        if (OperatingSystem.IsWindows())
        {
            if (enable)
                Run("schtasks", ["/Create", "/TN", TaskName, "/TR", $"\"{ExePath}\" --autostart", "/SC", "ONLOGON", "/RL", "HIGHEST", "/F"]);
            else
                Run("schtasks", ["/Delete", "/TN", TaskName, "/F"]);
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (enable)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MacPlistPath)!);
                File.WriteAllText(MacPlistPath, $"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                    <plist version="1.0">
                    <dict>
                      <key>Label</key><string>{MacLabel}</string>
                      <key>ProgramArguments</key>
                      <array>
                        <string>{SecurityElement.Escape(ExePath)}</string>
                        <string>--autostart</string>
                      </array>
                      <key>RunAtLoad</key><true/>
                    </dict>
                    </plist>
                    """);
            }
            else if (File.Exists(MacPlistPath))
            {
                File.Delete(MacPlistPath);
            }
        }
    }

    private static void Run(string file, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            p?.WaitForExit(10_000);
        }
        catch { }
    }
}

