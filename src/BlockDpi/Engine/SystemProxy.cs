using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace BlockDpi.Engine;

public static class SystemProxy
{
    public const int DnsPort = 5354;
    public const int TransparentHttpsPort = 8882;
    public const int TransparentHttpPort = 8883;

    private const string PfAnchorConfPath = "/tmp/blockdpi-pf.conf";
    private const string PfMainConfPath = "/tmp/blockdpi-main-pf.conf";
    private const string PfAnchor = "blockdpi";

    [DllImport("libc")]
    private static extern uint geteuid();

    public static bool IsRoot => OperatingSystem.IsMacOS() && geteuid() == 0;

    public static void Enable(int port)
    {
        var services = Services();
        var script = new StringBuilder();

        foreach (var svc in services)
        {
            string qSvc = ShellQuote(svc);
            script.AppendLine($"/usr/sbin/networksetup -setwebproxy {qSvc} 127.0.0.1 {port}");
            script.AppendLine($"/usr/sbin/networksetup -setsecurewebproxy {qSvc} 127.0.0.1 {port}");
            script.AppendLine($"/usr/sbin/networksetup -setsocksfirewallproxy {qSvc} 127.0.0.1 {port}");
            script.AppendLine($"/usr/sbin/networksetup -setwebproxystate {qSvc} on");
            script.AppendLine($"/usr/sbin/networksetup -setsecurewebproxystate {qSvc} on");
            script.AppendLine($"/usr/sbin/networksetup -setsocksfirewallproxystate {qSvc} on");
            script.AppendLine($"/usr/sbin/networksetup -setproxybypassdomains {qSvc} localhost 127.0.0.1 '*.local' 169.254/16 10.0.0.0/8 172.16.0.0/12 192.168.0.0/16");
            script.AppendLine($"/usr/sbin/networksetup -setdnsservers {qSvc} 127.0.0.1");
        }

        string pfAnchorRules = $$"""
            table <localnet> const { 127.0.0.0/8, 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16 }
            no rdr on lo0 inet proto tcp from any to any tos 0x20
            no rdr on lo0 inet proto udp from any to any tos 0x20
            rdr pass on lo0 inet proto udp from any to any port 53 -> 127.0.0.1 port {{DnsPort}}
            rdr pass on lo0 inet proto tcp from any to any port 53 -> 127.0.0.1 port {{DnsPort}}
            rdr pass on lo0 inet proto tcp from any to ! <localnet> port 443 -> 127.0.0.1 port {{TransparentHttpsPort}}
            rdr pass on lo0 inet proto tcp from any to ! <localnet> port 80 -> 127.0.0.1 port {{TransparentHttpPort}}
            rdr pass on lo0 inet proto tcp from any to ! <localnet> port 2053 -> 127.0.0.1 port 8884
            rdr pass on lo0 inet proto tcp from any to ! <localnet> port 2083 -> 127.0.0.1 port 8885
            rdr pass on lo0 inet proto tcp from any to ! <localnet> port 2087 -> 127.0.0.1 port 8886
            rdr pass on lo0 inet proto tcp from any to ! <localnet> port 2096 -> 127.0.0.1 port 8887
            rdr pass on lo0 inet proto tcp from any to ! <localnet> port 8443 -> 127.0.0.1 port 8888
            pass out quick inet proto tcp from any to any tos 0x20
            pass out quick inet proto udp from any to any tos 0x20
            block return-icmp out quick inet proto udp from any to any port { 443, 2053, 2083, 2087, 2096, 8443 }
            pass out on ! lo0 route-to (lo0 127.0.0.1) inet proto udp from any to any port 53 keep state
            pass out on ! lo0 route-to (lo0 127.0.0.1) inet proto tcp from any to any port 53 keep state
            pass out on ! lo0 route-to (lo0 127.0.0.1) inet proto tcp from any to ! <localnet> port { 80, 443, 2053, 2083, 2087, 2096, 8443 } keep state
            """;
        File.WriteAllText(PfAnchorConfPath, pfAnchorRules.Replace("\r\n", "\n") + "\n", new UTF8Encoding(false));

        string pfMainRules = $$"""
            scrub-anchor "com.apple/*"
            nat-anchor "com.apple/*"
            rdr-anchor "com.apple/*"
            rdr-anchor "{{PfAnchor}}"
            dummynet-anchor "com.apple/*"
            anchor "com.apple/*"
            anchor "{{PfAnchor}}"
            load anchor "com.apple" from "/etc/pf.anchors/com.apple"
            load anchor "{{PfAnchor}}" from "{{PfAnchorConfPath}}"
            """;
        File.WriteAllText(PfMainConfPath, pfMainRules.Replace("\r\n", "\n") + "\n", new UTF8Encoding(false));

        script.AppendLine($"/sbin/pfctl -f {PfMainConfPath} 2>/dev/null || true");
        script.AppendLine($"/sbin/pfctl -a {PfAnchor} -f {PfAnchorConfPath} 2>/dev/null || true");
        script.AppendLine("/sbin/pfctl -e 2>/dev/null || true");
        script.AppendLine("/usr/bin/dscacheutil -flushcache 2>/dev/null || true");
        script.AppendLine("/usr/bin/killall -HUP mDNSResponder 2>/dev/null || true");

        RunPrivilegedScript(script.ToString(), throwOnError: true);
    }

    public static void Disable(int port)
    {
        var services = Services();
        var script = new StringBuilder();

        script.AppendLine($"/sbin/pfctl -a {PfAnchor} -F all 2>/dev/null || true");
        script.AppendLine("/sbin/pfctl -f /etc/pf.conf 2>/dev/null || true");
        foreach (var svc in services)
        {
            string qSvc = ShellQuote(svc);
            script.AppendLine($"/usr/sbin/networksetup -setwebproxystate {qSvc} off");
            script.AppendLine($"/usr/sbin/networksetup -setsecurewebproxystate {qSvc} off");
            script.AppendLine($"/usr/sbin/networksetup -setsocksfirewallproxystate {qSvc} off");
            script.AppendLine($"/usr/sbin/networksetup -setdnsservers {qSvc} Empty");
        }
        script.AppendLine("/usr/bin/dscacheutil -flushcache 2>/dev/null || true");
        script.AppendLine("/usr/bin/killall -HUP mDNSResponder 2>/dev/null || true");

        RunPrivilegedScript(script.ToString(), throwOnError: false);
    }

    public static void CleanupLeftover(int port)
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            foreach (var svc in Services())
            {
                var (_, outp) = Run("networksetup", ["-getsecurewebproxy", svc]);
                var (_, dnsOut) = Run("networksetup", ["-getdnsservers", svc]);
                if ((outp.Contains("Enabled: Yes") && outp.Contains("127.0.0.1") && outp.Contains($"Port: {port}")) ||
                    dnsOut.Contains("127.0.0.1"))
                {
                    Disable(port);
                    return;
                }
            }
        }
        catch { }
    }

    private static List<string> Services()
    {
        var (code, outp) = Run("networksetup", ["-listallnetworkservices"]);
        if (code != 0) throw new InvalidOperationException("networksetup çalıştırılamadı.");
        return outp.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1)
            .Where(l => !l.StartsWith('*'))
            .ToList();
    }

    private static void RunPrivilegedScript(string scriptContent, bool throwOnError)
    {
        const string tmpScript = "/tmp/blockdpi-run.sh";
        File.WriteAllText(tmpScript, "#!/bin/sh\n" + scriptContent.Replace("\r\n", "\n"), new UTF8Encoding(false));
        Run("/bin/chmod", ["+x", tmpScript]);

        if (IsRoot)
        {
            var (c, o) = Run("/bin/sh", [tmpScript]);
            if (c != 0 && throwOnError)
                throw new InvalidOperationException(o.Trim());
            return;
        }

        string apple = $"do shell script \"/bin/sh {tmpScript}\" with administrator privileges";
        var (code, outp) = Run("osascript", ["-e", apple]);
        if (code != 0 && throwOnError)
            throw new InvalidOperationException(outp.Trim().Length > 0 ? outp.Trim() : "Yönetici izni verilmedi.");
    }

    private static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static (int Code, string Output) Run(string file, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return (p.ExitCode, o);
    }
}
