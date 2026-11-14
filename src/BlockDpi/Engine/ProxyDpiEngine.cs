using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace BlockDpi.Engine;

public sealed class ProxyDpiEngine : IDpiEngine
{
    public const int Port = 8881;

    private static readonly (int LocalPort, int TargetPort)[] TransparentPorts =
    [
        (8882, 443),
        (8883, 80),
        (8884, 2053),
        (8885, 2083),
        (8886, 2087),
        (8887, 2096),
        (8888, 8443)
    ];

    [DllImport("libc", SetLastError = true)]
    private static extern int setsockopt(int sockfd, int level, int optname, ref int optval, uint optlen);

    private TcpListener? _listener;
    private readonly List<TcpListener> _extraListeners = [];
    private UdpClient? _dnsUdp;
    private TcpListener? _dnsTcp;
    private CancellationTokenSource? _cts;
    private AppSettings _s = new();
    private DomainList? _list;
    private DnsResolver _dns = new(null);
    private volatile bool _running;
    private readonly object _lock = new();

    public bool IsRunning => _running;

    public void Start(AppSettings settings)
    {
        lock (_lock)
        {
            if (_running) return;
            _s = settings.Clone();
            _list = _s.UseBlacklist ? new DomainList(_s.Blacklist) : null;
            IPEndPoint? dnsServer = null;
            if (_s.DnsRedirect && IPAddress.TryParse(_s.DnsV4, out var dnsIp))
                dnsServer = new IPEndPoint(dnsIp, _s.DnsV4Port);
            _dns = new DnsResolver(dnsServer, useDoh: _s.UseDoh);

            try
            {
                _listener = new TcpListener(IPAddress.Loopback, Port);
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Start(512);
            }
            catch (SocketException)
            {
                throw new EngineException($"Yerel port {Port} kullanımda. Başka bir BlockDpi açık olabilir.");
            }

            _cts = new CancellationTokenSource();
            _running = true;
            _ = AcceptLoop(_listener, _cts.Token);

            try
            {
                _dnsUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, SystemProxy.DnsPort));
                _ = DnsUdpLoopAsync(_dnsUdp, _cts.Token);

                _dnsTcp = new TcpListener(IPAddress.Loopback, SystemProxy.DnsPort);
                _dnsTcp.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _dnsTcp.Start(128);
                _ = DnsTcpLoopAsync(_dnsTcp, _cts.Token);
            }
            catch {  }

            _extraListeners.Clear();
            foreach (var (localPort, targetPort) in TransparentPorts)
            {
                try
                {
                    var tl = new TcpListener(IPAddress.Loopback, localPort);
                    tl.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    tl.Start(512);
                    _extraListeners.Add(tl);
                    _ = AcceptTransparentLoopAsync(tl, targetPort, _cts.Token);
                }
                catch { }
            }

            if (OperatingSystem.IsMacOS())
            {
                try { SystemProxy.Enable(Port); }
                catch (Exception ex)
                {
                    StopInternal();
                    throw new EngineException("Sistem proxy ve uygulama yönlendirme ayarı yapılamadı: " + ex.Message);
                }
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_running) return;
            if (OperatingSystem.IsMacOS())
            {
                try { SystemProxy.Disable(Port); } catch {  }
            }
            StopInternal();
        }
    }

    private void StopInternal()
    {
        _running = false;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        try { _dnsUdp?.Dispose(); } catch { }
        _dnsUdp = null;
        try { _dnsTcp?.Stop(); } catch { }
        _dnsTcp = null;
        foreach (var tl in _extraListeners)
        {
            try { tl.Stop(); } catch { }
        }
        _extraListeners.Clear();
    }

    public void Dispose() => Stop();

    private async Task DnsUdpLoopAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var res = await udp.ReceiveAsync(ct);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var ans = await _dns.ResolveWireAsync(res.Buffer, ct);
                        if (ans != null)
                            await udp.SendAsync(ans, res.RemoteEndPoint, ct);
                    }
                    catch { }
                }, ct);
            }
            catch { if (ct.IsCancellationRequested) break; }
        }
    }

    private async Task DnsTcpLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        var s = client.GetStream();
                        var lenBuf = new byte[2];
                        if (!await ReadExactAsync(s, lenBuf, ct)) return;
                        int qLen = BinaryPrimitives.ReadUInt16BigEndian(lenBuf);
                        var qBuf = new byte[qLen];
                        if (!await ReadExactAsync(s, qBuf, ct)) return;
                        var ans = await _dns.ResolveWireAsync(qBuf, ct);
                        if (ans != null)
                        {
                            var outBuf = new byte[2 + ans.Length];
                            BinaryPrimitives.WriteUInt16BigEndian(outBuf, (ushort)ans.Length);
                            Buffer.BlockCopy(ans, 0, outBuf, 2, ans.Length);
                            await s.WriteAsync(outBuf, ct);
                        }
                    }
                    catch { }
                }
            }, ct);
        }
    }

    private async Task AcceptTransparentLoopAsync(TcpListener listener, int targetPort, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = HandleTransparentClientAsync(client, targetPort, ct);
        }
    }

    private async Task HandleTransparentClientAsync(TcpClient client, int targetPort, CancellationToken ct)
    {
        TcpClient? remote = null;
        try
        {
            client.NoDelay = true;
            var cs = client.GetStream();

            if (targetPort == 80)
            {
                var (head, extra) = await ReadHeadAsync(cs, ct);
                if (head == null || !Protocol.TryFindHttpHost(head, out _, out int valOff, out int valLen)) return;
                string hostHeader = Encoding.ASCII.GetString(head, valOff, valLen);
                var (host, port) = SplitHostPort(hostHeader, 80);
                remote = await ConnectAsync(host, port, ct);
                if (remote == null) return;
                var rs = remote.GetStream();
                await SendHttpAsync(rs, head, host, ct);
                if (extra.Length > 0) await rs.WriteAsync(extra, ct);
                await PumpAsync(cs, rs, ct);
                return;
            }

            var first = await ReadClientHelloAsync(cs, [], ct);
            if (first.Length == 0) return;

            string? targetHost = null;
            if (Protocol.TryFindSni(first, out int sniOff, out int sniLen, out _) && sniLen > 0)
                targetHost = Encoding.ASCII.GetString(first, sniOff, sniLen);
            else if (Protocol.TryFindSniInContinuation(first, out int cOff, out int cLen) && cLen > 0)
                targetHost = Encoding.ASCII.GetString(first, cOff, cLen);

            if (string.IsNullOrEmpty(targetHost)) return;

            remote = await ConnectAsync(targetHost, targetPort, ct);
            if (remote == null) return;
            var rs2 = remote.GetStream();
            await SendTlsAsync(rs2, first, targetHost, ct);
            await PumpAsync(cs, rs2, ct);
        }
        catch { }
        finally
        {
            try { remote?.Dispose(); } catch { }
            try { client.Dispose(); } catch { }
        }
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = HandleClient(client, ct);
        }
    }

    private async Task HandleClient(TcpClient client, CancellationToken ct)
    {
        TcpClient? remote = null;
        try
        {
            client.NoDelay = true;
            var cs = client.GetStream();

            var firstByte = new byte[1];
            int r0 = await cs.ReadAsync(firstByte, ct);
            if (r0 == 0) return;

            if (firstByte[0] == 0x05)
            {
                await HandleSocks5Async(cs, ct);
                return;
            }

            var (head, extra) = await ReadHeadAsync(cs, ct, firstByte);
            if (head == null) return;
            string headText = Encoding.Latin1.GetString(head);
            int firstLineEnd = headText.IndexOf("\r\n", StringComparison.Ordinal);
            if (firstLineEnd < 0) return;
            var reqLine = headText[..firstLineEnd].Split(' ');
            if (reqLine.Length < 3) return;
            string method = reqLine[0].ToUpperInvariant();

            if (method == "CONNECT")
            {
                var (host, port) = SplitHostPort(reqLine[1], 443);
                remote = await ConnectAsync(host, port, ct);
                if (remote == null)
                {
                    await cs.WriteAsync("HTTP/1.1 502 Bad Gateway\r\n\r\n"u8.ToArray(), ct);
                    return;
                }
                var rs = remote.GetStream();
                await cs.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), ct);

                var first = await ReadClientHelloAsync(cs, extra, ct);
                if (first.Length > 0) await SendTlsAsync(rs, first, host, ct);
                await PumpAsync(cs, rs, ct);
            }
            else
            {
                if (!Uri.TryCreate(reqLine[1], UriKind.Absolute, out var uri)) return;
                string host = uri.Host;
                int port = uri.IsDefaultPort ? 80 : uri.Port;
                remote = await ConnectAsync(host, port, ct);
                if (remote == null)
                {
                    await cs.WriteAsync("HTTP/1.1 502 Bad Gateway\r\n\r\n"u8.ToArray(), ct);
                    return;
                }
                var rs = remote.GetStream();
                string rest = headText[firstLineEnd..];
                rest = rest.Replace("\r\nProxy-Connection:", "\r\nConnection:", StringComparison.OrdinalIgnoreCase);
                var newHead = Encoding.Latin1.GetBytes($"{method} {uri.PathAndQuery} {reqLine[2]}{rest}");
                await SendHttpAsync(rs, newHead, host, ct);
                if (extra.Length > 0) await rs.WriteAsync(extra, ct);
                await PumpAsync(cs, rs, ct);
            }
        }
        catch { }
        finally
        {
            try { remote?.Dispose(); } catch { }
            try { client.Dispose(); } catch { }
        }
    }

    private async Task HandleSocks5Async(NetworkStream cs, CancellationToken ct)
    {
        TcpClient? remote = null;
        try
        {
            var nmBuf = new byte[1];
            if (!await ReadExactAsync(cs, nmBuf, ct)) return;
            var methods = new byte[nmBuf[0]];
            if (!await ReadExactAsync(cs, methods, ct)) return;
            await cs.WriteAsync(new byte[] { 0x05, 0x00 }, ct);

            var hdr = new byte[4];
            if (!await ReadExactAsync(cs, hdr, ct)) return;
            if (hdr[1] != 0x01)
            {
                await cs.WriteAsync(new byte[] { 0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
                return;
            }

            string host;
            if (hdr[3] == 0x01)
            {
                var ipBytes = new byte[4];
                if (!await ReadExactAsync(cs, ipBytes, ct)) return;
                var ip = new IPAddress(ipBytes);
                host = _dns.TryGetHostByIp(ip, out var mapped) ? mapped : ip.ToString();
            }
            else if (hdr[3] == 0x03)
            {
                var lenB = new byte[1];
                if (!await ReadExactAsync(cs, lenB, ct)) return;
                var domB = new byte[lenB[0]];
                if (!await ReadExactAsync(cs, domB, ct)) return;
                host = Encoding.ASCII.GetString(domB);
            }
            else if (hdr[3] == 0x04)
            {
                var ip6 = new byte[16];
                if (!await ReadExactAsync(cs, ip6, ct)) return;
                host = new IPAddress(ip6).ToString();
            }
            else return;

            var portB = new byte[2];
            if (!await ReadExactAsync(cs, portB, ct)) return;
            int port = BinaryPrimitives.ReadUInt16BigEndian(portB);

            remote = await ConnectAsync(host, port, ct);
            if (remote == null)
            {
                await cs.WriteAsync(new byte[] { 0x05, 0x04, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
                return;
            }

            await cs.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
            var rs = remote.GetStream();

            if (port == 80)
            {
                var (head, extra) = await ReadHeadAsync(cs, ct);
                if (head != null) await SendHttpAsync(rs, head, host, ct);
                if (extra.Length > 0) await rs.WriteAsync(extra, ct);
            }
            else
            {
                var first = await ReadClientHelloAsync(cs, [], ct);
                if (first.Length > 0)
                {
                    if (Protocol.TryFindSni(first, out int sOff, out int sLen, out _) && sLen > 0)
                        host = Encoding.ASCII.GetString(first, sOff, sLen);
                    await SendTlsAsync(rs, first, host, ct);
                }
            }
            await PumpAsync(cs, rs, ct);
        }
        finally
        {
            try { remote?.Dispose(); } catch { }
        }
    }

    private static async Task<bool> ReadExactAsync(NetworkStream s, byte[] buf, CancellationToken ct)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int r = await s.ReadAsync(buf.AsMemory(off, buf.Length - off), ct);
            if (r <= 0) return false;
            off += r;
        }
        return true;
    }

    private static async Task<(byte[]? Head, byte[] Extra)> ReadHeadAsync(NetworkStream s, CancellationToken ct, byte[]? prefix = null)
    {
        var buf = new byte[65536];
        int n = 0;
        if (prefix is { Length: > 0 })
        {
            Buffer.BlockCopy(prefix, 0, buf, 0, prefix.Length);
            n = prefix.Length;
        }
        while (n < buf.Length)
        {
            int r = await s.ReadAsync(buf.AsMemory(n), ct);
            if (r == 0) return (null, []);
            n += r;
            int end = IndexOf(buf.AsSpan(0, n), "\r\n\r\n"u8);
            if (end >= 0)
            {
                int headLen = end + 4;
                return (buf[..headLen], buf[headLen..n]);
            }
        }
        return (null, []);
    }

    private static async Task<byte[]> ReadClientHelloAsync(NetworkStream s, byte[] already, CancellationToken ct)
    {
        var ms = new MemoryStream();
        ms.Write(already);
        var buf = new byte[16384];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(3000);
        try
        {
            while (true)
            {
                var data = ms.GetBuffer().AsSpan(0, (int)ms.Length);
                if (data.Length >= 5)
                {
                    if (data[0] != 0x16) break;
                    int need = 5 + ((data[3] << 8) | data[4]);
                    if (data.Length >= need) break;
                }
                int r = await s.ReadAsync(buf, timeout.Token);
                if (r == 0) break;
                ms.Write(buf, 0, r);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        return ms.ToArray();
    }

    private async Task SendTlsAsync(NetworkStream rs, byte[] data, string host, CancellationToken ct)
    {
        bool apply = _s.HttpsFragment && Protocol.IsClientHello(data) && (_list == null || _list.Matches(host));
        if (!apply) { await rs.WriteAsync(data, ct); return; }

        int recLen = (data[3] << 8) | data[4];
        if (data.Length < 5 + recLen || recLen < 8) { await WriteSplitAsync(rs, data, _s.FragmentSize, ct); return; }

        byte vMaj = data[1], vMin = data[2];
        var body = data.AsMemory(5, recLen);

        var cuts = new SortedSet<int> { Math.Clamp(_s.FragmentSize, 1, 3) };
        if (_s.SplitAtSni && Protocol.TryFindSni(data, out int sniOff, out int sniLen, out _) && sniLen > 1)
        {
            int sniStartInBody = Math.Clamp(sniOff - 5, 1, recLen - 2);
            int sniMidInBody = Math.Clamp(sniOff - 5 + sniLen / 2, sniStartInBody + 1, recLen - 1);
            cuts.Add(sniStartInBody);
            cuts.Add(sniMidInBody);
        }
        else
        {
            cuts.Add(Math.Clamp(recLen / 2, 4, recLen - 1));
        }

        int prev = 0;
        foreach (int c in cuts)
        {
            if (c <= prev || c >= recLen) continue;
            var rec = MakeTlsRecord(vMaj, vMin, body.Span.Slice(prev, c - prev));
            await rs.WriteAsync(rec, ct);
            await rs.FlushAsync(ct);
            await Task.Delay(2, ct);
            prev = c;
        }
        if (prev < recLen)
        {
            var lastRec = MakeTlsRecord(vMaj, vMin, body.Span.Slice(prev, recLen - prev));
            await rs.WriteAsync(lastRec, ct);
            await rs.FlushAsync(ct);
        }

        if (data.Length > 5 + recLen)
            await rs.WriteAsync(data.AsMemory(5 + recLen), ct);
    }

    private static byte[] MakeTlsRecord(byte vMaj, byte vMin, ReadOnlySpan<byte> body)
    {
        var rec = new byte[5 + body.Length];
        rec[0] = 0x16;
        rec[1] = vMaj;
        rec[2] = vMin;
        rec[3] = (byte)(body.Length >> 8);
        rec[4] = (byte)body.Length;
        body.CopyTo(rec.AsSpan(5));
        return rec;
    }

    private async Task SendHttpAsync(NetworkStream rs, byte[] head, string host, CancellationToken ct)
    {
        bool apply = _list == null || _list.Matches(host);
        if (apply && _s.HttpHostTrick && Protocol.TryFindHttpHost(head, out int nameOff, out _, out _))
        {
            head[nameOff] = (byte)'h'; head[nameOff + 1] = (byte)'o'; head[nameOff + 2] = (byte)'S'; head[nameOff + 3] = (byte)'t';
        }
        if (apply && _s.HttpFragment) await WriteSplitAsync(rs, head, _s.FragmentSize, ct);
        else await rs.WriteAsync(head, ct);
    }

    private static async Task WriteSplitAsync(NetworkStream s, byte[] data, int first, CancellationToken ct)
    {
        first = Math.Clamp(first, 1, Math.Max(1, data.Length - 1));
        if (data.Length <= 1) { await s.WriteAsync(data, ct); return; }
        await s.WriteAsync(data.AsMemory(0, first), ct);
        await s.FlushAsync(ct);
        await Task.Delay(2, ct);
        await s.WriteAsync(data.AsMemory(first), ct);
        await s.FlushAsync(ct);
    }

    private async Task<TcpClient?> ConnectAsync(string host, int port, CancellationToken ct)
    {
        IPAddress[] ips;
        try { ips = await _dns.ResolveAsync(host, ct); }
        catch { return null; }
        foreach (var ip in ips.Where(i => i.AddressFamily == AddressFamily.InterNetwork))
        {
            var c = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            try
            {
                if (OperatingSystem.IsMacOS())
                {
                    try { c.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, 0x20); } catch { }
                    try
                    {
                        int tos = 0x20;
                        setsockopt((int)c.Client.Handle, 0, 3, ref tos, 4);
                    }
                    catch { }
                }
                using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
                t.CancelAfter(8000);
                await c.ConnectAsync(ip, port, t.Token);
                return c;
            }
            catch { c.Dispose(); }
        }
        return null;
    }

    private static async Task PumpAsync(NetworkStream a, NetworkStream b, CancellationToken ct)
    {
        var t1 = a.CopyToAsync(b, 65536, ct);
        var t2 = b.CopyToAsync(a, 65536, ct);
        try { await Task.WhenAny(t1, t2); } catch { }
    }

    private static (string Host, int Port) SplitHostPort(string s, int def)
    {
        if (s.StartsWith('['))
        {
            int e = s.IndexOf(']');
            string h = s[1..e];
            return (h, e + 2 < s.Length && int.TryParse(s[(e + 2)..], out var p6) ? p6 : def);
        }
        int c = s.LastIndexOf(':');
        if (c > 0 && int.TryParse(s[(c + 1)..], out var p)) return (s[..c], p);
        return (s, def);
    }

    private static int IndexOf(ReadOnlySpan<byte> hay, ReadOnlySpan<byte> needle)
    {
        for (int i = 0; i <= hay.Length - needle.Length; i++)
            if (hay.Slice(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }
}
