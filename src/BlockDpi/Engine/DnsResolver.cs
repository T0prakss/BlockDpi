using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace BlockDpi.Engine;

public sealed class DnsResolver
{
    private readonly IPEndPoint? _udpServer;
    private readonly bool _useDoh;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, (IPAddress[] Ips, DateTime Expires)> _ipCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (byte[] Wire, DateTime Expires)> _wireCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<IPAddress, string> _ipToHost = new();

    private static readonly IPAddress BtkIpV4 = IPAddress.Parse("195.175.254.2");

    private static readonly string[] DohUrls =
    [
        "https://1.1.1.1/dns-query",
        "https://1.0.0.1/dns-query",
        "https://8.8.8.8/dns-query"
    ];

    public DnsResolver(IPEndPoint? udpServer, bool useDoh = true)
    {
        _udpServer = udpServer;
        _useDoh = useDoh;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectCallback = async (ctx, ct) =>
            {
                var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    if (OperatingSystem.IsMacOS())
                    {
                        try { sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, 0x20); } catch { }
                    }
                    await sock.ConnectAsync(ctx.DnsEndPoint, ct);
                    return new NetworkStream(sock, ownsSocket: true);
                }
                catch
                {
                    sock.Dispose();
                    throw;
                }
            }
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
    }

    public bool TryGetHostByIp(IPAddress ip, out string host) => _ipToHost.TryGetValue(ip, out host!);

    public async Task<byte[]?> ResolveWireAsync(byte[] query, CancellationToken ct)
    {
        if (query.Length < 12) return null;
        ushort txId = BinaryPrimitives.ReadUInt16BigEndian(query);
        var (hostName, qtype) = ParseQuestionInfo(query);

        if (qtype is 65 or 28)
            return BuildEmptyNoErrorResponse(query);

        string cacheKey = hostName.Length > 0 ? $"{hostName}:{qtype}" : "";

        if (cacheKey.Length > 0 && _wireCache.TryGetValue(cacheKey, out var cached) && cached.Expires > DateTime.UtcNow)
        {
            var copy = (byte[])cached.Wire.Clone();
            BinaryPrimitives.WriteUInt16BigEndian(copy, txId);
            return copy;
        }

        byte[]? resp = null;

        if (_useDoh)
        {
            foreach (var url in DohUrls)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new ByteArrayContent(query)
                    };
                    req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));

                    using var res = await _http.SendAsync(req, ct);
                    if (res.IsSuccessStatusCode)
                    {
                        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
                        if (bytes.Length >= 12)
                        {
                            resp = bytes;
                            break;
                        }
                    }
                }
                catch {  }
            }
        }

        if (resp == null && _udpServer != null)
        {
            try
            {
                using var udp = new UdpClient(_udpServer.AddressFamily);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(2000);
                await udp.SendAsync(query, _udpServer, cts.Token);
                var res = await udp.ReceiveAsync(cts.Token);
                if (res.Buffer.Length >= 12) resp = res.Buffer;
            }
            catch { }
        }

        if (resp != null && resp.Length >= 12)
        {
            BinaryPrimitives.WriteUInt16BigEndian(resp, txId);
            if (hostName.Length > 0 && qtype == 1)
            {
                var (ips, _) = Parse(resp);
                foreach (var ip in ips)
                {
                    if (!ip.Equals(BtkIpV4))
                    {
                        if (_ipToHost.Count > 8192) _ipToHost.Clear();
                        _ipToHost[ip] = hostName;
                    }
                }
            }
            if (cacheKey.Length > 0)
                _wireCache[cacheKey] = ((byte[])resp.Clone(), DateTime.UtcNow.AddMinutes(3));
        }
        return resp;
    }

    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.Equals(BtkIpV4)) return [IPAddress.Parse("162.159.135.232")];
            return [ip];
        }
        if (_ipCache.TryGetValue(host, out var c) && c.Expires > DateTime.UtcNow) return c.Ips;

        var q = BuildQueryPacket(host);
        var wire = await ResolveWireAsync(q, ct);
        if (wire != null)
        {
            var (ips, ttl) = Parse(wire);
            ips = ips.Where(i => !i.Equals(BtkIpV4)).ToArray();
            if (ips.Length > 0)
            {
                foreach (var resolvedIp in ips) _ipToHost[resolvedIp] = host;
                _ipCache[host] = (ips, DateTime.UtcNow.AddSeconds(Math.Clamp(ttl, 60, 600)));
                return ips;
            }
        }

        var sys = (await Dns.GetHostAddressesAsync(host, ct)).Where(i => !i.Equals(BtkIpV4)).ToArray();
        if (sys.Length > 0) _ipCache[host] = (sys, DateTime.UtcNow.AddMinutes(2));
        return sys;
    }

    private static byte[] BuildQueryPacket(string host)
    {
        ushort id = (ushort)Random.Shared.Next(1, 65535);
        var q = new List<byte>(64);
        q.AddRange([(byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0]);
        foreach (var label in host.TrimEnd('.').Split('.'))
        {
            var b = Encoding.ASCII.GetBytes(label);
            q.Add((byte)b.Length);
            q.AddRange(b);
        }
        q.AddRange([0, 0, 1, 0, 1]);
        return q.ToArray();
    }

    private static byte[] BuildEmptyNoErrorResponse(byte[] q)
    {
        int i = 12;
        while (i < q.Length && q[i] != 0)
        {
            int l = q[i];
            if ((l & 0xC0) == 0xC0) { i += 2; goto doneName; }
            i += l + 1;
        }
        if (i < q.Length && q[i] == 0) i++;
    doneName:
        int qEnd = Math.Min(q.Length, i + 4);
        var resp = new byte[qEnd];
        Buffer.BlockCopy(q, 0, resp, 0, qEnd);
        resp[2] = 0x81;
        resp[3] = 0x80;
        resp[4] = 0x00; resp[5] = 0x01;
        resp[6] = 0x00; resp[7] = 0x00;
        resp[8] = 0x00; resp[9] = 0x00;
        resp[10] = 0x00; resp[11] = 0x00;
        return resp;
    }

    private static (string Host, ushort QType) ParseQuestionInfo(byte[] q)
    {
        try
        {
            int i = 12;
            var sb = new StringBuilder();
            while (i < q.Length && q[i] != 0)
            {
                int len = q[i++];
                if (len <= 0 || i + len > q.Length) return ("", 0);
                if (sb.Length > 0) sb.Append('.');
                sb.Append(Encoding.ASCII.GetString(q, i, len));
                i += len;
            }
            if (i + 5 > q.Length) return ("", 0);
            ushort qtype = BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(i + 1));
            return (sb.ToString(), qtype);
        }
        catch { return ("", 0); }
    }

    private static (IPAddress[], int) Parse(byte[] r)
    {
        int qd = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(4));
        int an = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(6));
        int i = 12;
        for (int k = 0; k < qd; k++) { i = SkipName(r, i); i += 4; }
        var list = new List<IPAddress>();
        int ttl = 300;
        for (int k = 0; k < an && i + 10 <= r.Length; k++)
        {
            i = SkipName(r, i);
            if (i + 10 > r.Length) break;
            int type = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(i));
            int t = (int)BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(i + 4));
            int rdLen = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(i + 8));
            i += 10;
            if (type == 1 && rdLen == 4 && i + 4 <= r.Length)
            {
                list.Add(new IPAddress(r.AsSpan(i, 4)));
                ttl = Math.Min(ttl, t);
            }
            i += rdLen;
        }
        return (list.ToArray(), ttl);
    }

    private static int SkipName(byte[] r, int i)
    {
        while (i < r.Length)
        {
            int l = r[i];
            if (l == 0) return i + 1;
            if ((l & 0xC0) == 0xC0) return i + 2;
            i += l + 1;
        }
        return i;
    }
}
