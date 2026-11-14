using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BlockDpi.Engine;

[SupportedOSPlatform("windows")]
public sealed class WindowsDpiEngine : IDpiEngine
{
    private IntPtr _handle = IntPtr.Zero;
    private volatile bool _running;
    private readonly List<Thread> _threads = [];
    private CancellationTokenSource? _cts;
    private AppSettings _s = new();
    private DomainList? _list;
    private DnsResolver? _dohResolver;
    private byte[]? _dnsV4, _dnsV6;
    private readonly ConcurrentDictionary<int, DnsEntry> _dnsMap = new();
    private readonly ConcurrentDictionary<int, byte> _autoTtlMap = new();
    private readonly ConcurrentDictionary<int, long> _pendingTlsCont = new();
    private readonly ConcurrentDictionary<int, byte[]> _btkRedirectMap = new();
    private static readonly byte[] BtkBlockIpV4 = [195, 175, 254, 2];
    private static readonly byte[] CloudflareFallbackIpV4 = [162, 159, 135, 232];
    private readonly object _lock = new();

    private sealed record DnsEntry(byte[] OriginalIp, long Ticks);

    private static readonly byte[] FakeHttpRequest =
        "GET / HTTP/1.1\r\nHost: www.w3.org\r\nUser-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64)\r\nAccept: */*\r\nConnection: close\r\n\r\n"u8.ToArray();

    private static readonly byte[] FakeTlsClientHello = BuildCompleteFakeClientHello();

    private static byte[] BuildCompleteFakeClientHello()
    {
        var body = new List<byte>(128)
        {
            0x01, 0x00, 0x00, 0x00,
            0x03, 0x03
        };
        for (int i = 0; i < 32; i++) body.Add((byte)(i + 1));
        body.Add(0x00);
        body.AddRange([0x00, 0x04, 0x13, 0x01, 0xC0, 0x2F]);
        body.AddRange([0x01, 0x00]);
        byte[] host = "www.w3.org"u8.ToArray();
        var exts = new List<byte>
        {
            0x00, 0x00,
            0x00, (byte)(host.Length + 5),
            0x00, (byte)(host.Length + 3),
            0x00,
            0x00, (byte)host.Length
        };
        exts.AddRange(host);
        exts.AddRange([0x00, 0x2B, 0x00, 0x05, 0x04, 0x03, 0x04, 0x03, 0x03]);

        body.Add((byte)(exts.Count >> 8));
        body.Add((byte)exts.Count);
        body.AddRange(exts);

        int hsLen = body.Count - 4;
        body[1] = (byte)(hsLen >> 16);
        body[2] = (byte)(hsLen >> 8);
        body[3] = (byte)hsLen;

        var record = new List<byte>(5 + body.Count)
        {
            0x16, 0x03, 0x01,
            (byte)(body.Count >> 8),
            (byte)body.Count
        };
        record.AddRange(body);
        return record.ToArray();
    }

    public bool IsRunning => _running;

    public void Start(AppSettings settings)
    {
        lock (_lock)
        {
            if (_running) return;
            _s = settings.Clone();
            _list = _s.UseBlacklist ? new DomainList(_s.Blacklist) : null;
            _dnsV4 = _dnsV6 = null;
            _dnsMap.Clear();
            _autoTtlMap.Clear();

            IPEndPoint? udpFallback = null;
            if (_s.DnsRedirect)
            {
                if (IPAddress.TryParse(_s.DnsV4, out var a4) && a4.AddressFamily == AddressFamily.InterNetwork)
                {
                    _dnsV4 = a4.GetAddressBytes();
                    udpFallback = new IPEndPoint(a4, _s.DnsV4Port);
                }
                if (IPAddress.TryParse(_s.DnsV6, out var a6) && a6.AddressFamily == AddressFamily.InterNetworkV6)
                    _dnsV6 = a6.GetAddressBytes();

                _dohResolver = new DnsResolver(udpFallback, useDoh: _s.UseDoh);
            }

            if (!WinDivertNative.FilesPresent())
                throw new EngineException(
                    "WinDivert dosyaları bulunamadı (WinDivert.dll ve WinDivert64.sys).\n" +
                    "Bunlar BlockDpi.exe ile aynı klasörde olmalı.", winDivertMissing: true);

            string filter = BuildFilter();
            IntPtr h;
            try
            {
                h = WinDivertNative.WinDivertOpen(filter, WinDivertNative.LayerNetwork, 0, 0);
            }
            catch (DllNotFoundException)
            {
                throw new EngineException("WinDivert.dll yüklenemedi. Dosya eksik ya da antivirüs tarafından silinmiş olabilir.", true);
            }
            catch (BadImageFormatException)
            {
                throw new EngineException("WinDivert.dll hatalı mimaride. 64-bit (x64) sürümünü kullanın.", true);
            }

            if (h == WinDivertNative.InvalidHandle || h == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                throw new EngineException(err switch
                {
                    2 => "WinDivert64.sys bulunamadı. Antivirüs dosyayı silmiş olabilir.",
                    5 => "Yönetici yetkisi gerekli. BlockDpi'ı yönetici olarak çalıştırın.",
                    87 => "WinDivert filtresi geçersiz (DNS adresini kontrol edin).",
                    577 or 1275 => "WinDivert sürücüsü Windows tarafından engellendi (sürücü imzası / güvenlik politikası).",
                    1753 => "Windows 'Temel Filtreleme Altyapısı' (BFE) servisi çalışmıyor.",
                    654 => "Başka bir WinDivert sürümü yüklü. GoodbyeDPI vb. programları kapatıp bilgisayarı yeniden başlatın.",
                    _ => $"WinDivert açılamadı (hata kodu {err})."
                }, err is 2);
            }

            _cts = new CancellationTokenSource();
            _handle = h;
            _running = true;
            _threads.Clear();
            var t = new Thread(Worker) { IsBackground = true, Name = "BlockDpi-Worker", Priority = ThreadPriority.Highest };
            _threads.Add(t);
            t.Start();

            FlushWindowsDnsCache();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_running) return;
            _running = false;
            try { _cts?.Cancel(); } catch { }
            var h = _handle;
            _handle = IntPtr.Zero;
            WinDivertNative.WinDivertShutdown(h, WinDivertNative.ShutdownBoth);
            foreach (var t in _threads) t.Join(1500);
            _threads.Clear();
            WinDivertNative.WinDivertClose(h);
            _dnsMap.Clear();
            _autoTtlMap.Clear();
        }
    }

    public void Dispose() => Stop();

    private static void FlushWindowsDnsCache()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ipconfig", "/flushdns")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
            p?.WaitForExit(2000);
        }
        catch { }
    }

    private string BuildFilter()
    {
        var parts = new List<string>();
        if (_s.HttpsFragment || _s.HttpFragment || _s.HttpHostTrick || _s.FakePacket)
            parts.Add("(outbound and tcp.PayloadLength > 0)");
        parts.Add("(outbound and ip.DstAddr == 195.175.254.2 and tcp)");
        parts.Add("(inbound and ip.SrcAddr == 162.159.135.232 and tcp)");
        if (_s.FakePacket)
            parts.Add("(inbound and tcp.Syn == 1 and tcp.Ack == 1)");
        if (_s.BlockQuic)
            parts.Add("(outbound and (udp.DstPort == 443 or udp.DstPort == 2053 or udp.DstPort == 2083 or udp.DstPort == 2087 or udp.DstPort == 2096 or udp.DstPort == 8443))");

        if (_s.DnsRedirect)
        {
            parts.Add("(outbound and udp.DstPort == 53)");
            if (!_s.UseDoh)
            {
                if (_dnsV4 != null)
                    parts.Add($"(inbound and ip.SrcAddr == {new IPAddress(_dnsV4)} and udp.SrcPort == {_s.DnsV4Port})");
                if (_dnsV6 != null)
                    parts.Add($"(inbound and ipv6.SrcAddr == {new IPAddress(_dnsV6)} and udp.SrcPort == {_s.DnsV6Port})");
            }
        }
        if (parts.Count == 0) parts.Add("false");
        return "!impostor and !loopback and (" + string.Join(" or ", parts) + ")";
    }

    private unsafe void Worker()
    {
        var buf = new byte[0xFFFF + 64];
        fixed (byte* p = buf)
        {
            while (_running)
            {
                var h = _handle;
                if (h == IntPtr.Zero) break;
                if (!WinDivertNative.WinDivertRecv(h, p, (uint)buf.Length, out uint len, out var addr))
                {
                    if (!_running) break;
                    int err = Marshal.GetLastWin32Error();
                    if (err is 232 or 6 or 995) break;
                    continue;
                }
                try
                {
                    ProcessPacket(buf, (int)len, ref addr);
                }
                catch
                {
                    Send(buf, (int)len, ref addr);
                }
            }
        }
    }

    private void ProcessPacket(byte[] pkt, int len, ref WinDivertAddress addr)
    {
        int ver = pkt[0] >> 4;
        int ipHl, proto;
        if (ver == 4) { ipHl = (pkt[0] & 0x0F) * 4; proto = pkt[9]; }
        else if (ver == 6) { ipHl = 40; proto = pkt[6]; }
        else { Send(pkt, len, ref addr); return; }

        if (proto == 17 && len >= ipHl + 8)
        {
            int srcPort = BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(ipHl));
            int dstPort = BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(ipHl + 2));
            bool outbound = addr.Outbound;

            if (outbound && _s.BlockQuic && dstPort is 443 or 2053 or 2083 or 2087 or 2096 or 8443) return;
            if (outbound && dstPort == 53 && _s.DnsRedirect)
            {
                if (_s.UseDoh && _dohResolver != null)
                {
                    var copy = pkt.AsSpan(0, len).ToArray();
                    var addrCopy = addr;
                    _ = HandleDnsWithDohAsync(copy, len, addrCopy, ver, ipHl);
                    return;
                }
                RedirectDns(pkt, len, ref addr, ver, ipHl, srcPort);
                return;
            }
            if (!outbound) { RestoreDns(pkt, len, ref addr, ver, ipHl, dstPort); return; }
            Send(pkt, len, ref addr);
            return;
        }

        if (proto == 6 && len >= ipHl + 20)
        {
            int srcPort = BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(ipHl));
            int dstPort = BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(ipHl + 2));

            if (!addr.Outbound)
            {
                if (ver == 4 && _btkRedirectMap.TryGetValue(dstPort, out var origBtkIp) &&
                    pkt.AsSpan(12, 4).SequenceEqual(CloudflareFallbackIpV4))
                {
                    origBtkIp.CopyTo(pkt.AsSpan(12, 4));
                    CalcChecksums(pkt, len, ref addr);
                }

                byte incomingTtl = ver == 4 ? pkt[8] : pkt[7];
                byte fakeTtl = ComputeAutoTtl(incomingTtl);
                if (fakeTtl > 0)
                {
                    if (_autoTtlMap.Count > 8192) _autoTtlMap.Clear();
                    _autoTtlMap[dstPort] = fakeTtl;
                }
                Send(pkt, len, ref addr);
                return;
            }

            if (ver == 4 && pkt.AsSpan(16, 4).SequenceEqual(BtkBlockIpV4))
            {
                if (_btkRedirectMap.Count > 4096) _btkRedirectMap.Clear();
                _btkRedirectMap[srcPort] = BtkBlockIpV4;
                CloudflareFallbackIpV4.CopyTo(pkt.AsSpan(16, 4));
                CalcChecksums(pkt, len, ref addr);
            }

            int tcpHl = (pkt[ipHl + 12] >> 4) * 4;
            int payOff = ipHl + tcpHl;
            int payLen = len - payOff;
            if (payLen > 0 && dstPort == 80) { HandleHttp(pkt, len, ref addr, ver, ipHl, payOff, payLen, srcPort); return; }
            if (payLen > 0) { HandleTls(pkt, len, ref addr, ver, ipHl, payOff, payLen, srcPort); return; }
        }

        Send(pkt, len, ref addr);
    }

    private byte ComputeAutoTtl(byte incomingTtl)
    {
        int orig = incomingTtl <= 64 ? 64 : incomingTtl <= 128 ? 128 : 255;
        int nhops = orig - incomingTtl;
        if (nhops <= 2 || nhops > 30) return (byte)Math.Clamp(_s.FakeTtl, 3, 12);
        return (byte)Math.Clamp(nhops - 1, 3, 15);
    }

    private byte GetFakeTtlForPort(int clientPort)
    {
        return (byte)Math.Clamp(_s.FakeTtl, 1, 64);
    }

    private void HandleTls(byte[] pkt, int len, ref WinDivertAddress addr, int ver, int ipHl, int payOff, int payLen, int srcPort)
    {
        var payload = new ReadOnlySpan<byte>(pkt, payOff, payLen);
        bool isFirstHello = Protocol.IsClientHello(payload);
        bool isSecondSegment = !isFirstHello && _pendingTlsCont.TryRemove(srcPort, out _);

        int sniOff = 0, sniLen = 0;
        bool hasSni = false, truncated = false;

        if (isFirstHello)
        {
            hasSni = Protocol.TryFindSni(payload, out sniOff, out sniLen, out truncated);
            if (!hasSni)
                hasSni = Protocol.TryFindSniInContinuation(payload, out sniOff, out sniLen);
            if (!hasSni && truncated)
            {
                if (_pendingTlsCont.Count > 4096) _pendingTlsCont.Clear();
                _pendingTlsCont[srcPort] = Environment.TickCount64;
            }
        }
        else if (isSecondSegment || (payLen < 1400 && Protocol.TryFindSniInContinuation(payload, out sniOff, out sniLen)))
        {
            if (sniLen == 0)
                hasSni = Protocol.TryFindSniInContinuation(payload, out sniOff, out sniLen);
            else
                hasSni = true;
            if (!hasSni && !isSecondSegment) { Send(pkt, len, ref addr); return; }
        }
        else
        {
            Send(pkt, len, ref addr);
            return;
        }

        if (_list != null)
        {
            if (hasSni)
            {
                string host = System.Text.Encoding.ASCII.GetString(payload.Slice(sniOff, sniLen));
                if (!_list.Matches(host)) { Send(pkt, len, ref addr); return; }
            }
            else if (!truncated && !isSecondSegment) { Send(pkt, len, ref addr); return; }
        }

        if (_s.FakePacket)
        {
            byte ttl = GetFakeTtlForPort(srcPort);
            SendFakeTlsPackets(pkt, len, ref addr, ver, ipHl, payOff, hasSni ? sniOff : 6, hasSni ? sniLen : Math.Min(32, Math.Max(1, payLen - 6)), ttl);
        }

        if (!_s.HttpsFragment) { Send(pkt, len, ref addr); return; }

        var cuts = new SortedSet<int>();
        if (_s.FragmentSize > 0 && _s.FragmentSize < payLen) cuts.Add(_s.FragmentSize);
        if (_s.SplitAtSni && hasSni && sniLen > 1) cuts.Add(sniOff + sniLen / 2);
        else if (isSecondSegment && !hasSni)
        {
            if (payLen > 16) cuts.Add(16);
            if (payLen > 48) cuts.Add(48);
        }
        SendSegments(pkt, len, ref addr, ver, ipHl, payOff, payLen, cuts);
    }

    private void HandleHttp(byte[] pkt, int len, ref WinDivertAddress addr, int ver, int ipHl, int payOff, int payLen, int srcPort)
    {
        var payload = new ReadOnlySpan<byte>(pkt, payOff, payLen);
        if (!Protocol.LooksLikeHttpRequest(payload)) { Send(pkt, len, ref addr); return; }

        bool hasHost = Protocol.TryFindHttpHost(payload, out int nameOff, out int valOff, out int valLen);
        if (_list != null)
        {
            if (!hasHost) { Send(pkt, len, ref addr); return; }
            string host = System.Text.Encoding.ASCII.GetString(payload.Slice(valOff, valLen));
            if (!_list.Matches(host)) { Send(pkt, len, ref addr); return; }
        }

        if (_s.FakePacket && hasHost)
        {
            byte ttl = GetFakeTtlForPort(srcPort);
            SendFakeHttpPacket(pkt, ver, ipHl, payOff, ref addr, ttl);
        }

        if (_s.HttpHostTrick && hasHost)
        {
            int o = payOff + nameOff;
            pkt[o] = (byte)'h'; pkt[o + 1] = (byte)'o'; pkt[o + 2] = (byte)'S'; pkt[o + 3] = (byte)'t';
        }

        var cuts = new SortedSet<int>();
        if (_s.HttpFragment)
        {
            if (_s.FragmentSize > 0 && _s.FragmentSize < payLen) cuts.Add(_s.FragmentSize);
            if (_s.SplitAtSni && hasHost && valLen > 1) cuts.Add(valOff + valLen / 2);
        }
        SendSegments(pkt, len, ref addr, ver, ipHl, payOff, payLen, cuts);
    }

    private void SendSegments(byte[] pkt, int len, ref WinDivertAddress addr, int ver, int ipHl, int payOff, int payLen, SortedSet<int> cuts)
    {
        if (cuts.Count == 0)
        {
            CalcChecksums(pkt, len, ref addr);
            Send(pkt, len, ref addr);
            return;
        }

        var segs = new List<byte[]>(cuts.Count + 1);
        int start = 0;
        int idx = 0;
        foreach (int c in cuts)
        {
            if (c <= start || c >= payLen) continue;
            segs.Add(BuildSegment(pkt, ver, ipHl, payOff, start, c - start, idx++, ref addr));
            start = c;
        }
        segs.Add(BuildSegment(pkt, ver, ipHl, payOff, start, payLen - start, idx, ref addr));

        if (_s.ReverseFragments) segs.Reverse();
        foreach (var s in segs) Send(s, s.Length, ref addr);
    }

    private static byte[] BuildSegment(byte[] pkt, int ver, int ipHl, int payOff, int start, int count, int segIdx, ref WinDivertAddress addr)
    {
        var seg = new byte[payOff + count];
        Buffer.BlockCopy(pkt, 0, seg, 0, payOff);
        Buffer.BlockCopy(pkt, payOff + start, seg, payOff, count);

        if (ver == 4)
        {
            BinaryPrimitives.WriteUInt16BigEndian(seg.AsSpan(2), (ushort)seg.Length);
            ushort ipId = BinaryPrimitives.ReadUInt16BigEndian(seg.AsSpan(4));
            BinaryPrimitives.WriteUInt16BigEndian(seg.AsSpan(4), unchecked((ushort)(ipId + segIdx + 1)));
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(seg.AsSpan(4), (ushort)(seg.Length - 40));
        }

        uint seq = BinaryPrimitives.ReadUInt32BigEndian(seg.AsSpan(ipHl + 4));
        BinaryPrimitives.WriteUInt32BigEndian(seg.AsSpan(ipHl + 4), unchecked(seq + (uint)start));

        if (start == 0)
            BinaryPrimitives.WriteUInt16BigEndian(seg.AsSpan(ipHl + 14), 2);

        CalcChecksums(seg, seg.Length, ref addr);
        return seg;
    }

    private static unsafe void CalcChecksums(byte[] pkt, int len, ref WinDivertAddress addr)
    {
        fixed (byte* p = pkt)
            WinDivertNative.WinDivertHelperCalcChecksums(p, (uint)len, IntPtr.Zero, 0);
        addr.MarkFullChecksums();
    }

    private void SendFakeTlsPackets(byte[] pkt, int len, ref WinDivertAddress addr, int ver, int ipHl, int payOff, int off, int count, byte autoTtl)
    {
        var fakeComplete = new byte[payOff + FakeTlsClientHello.Length];
        Buffer.BlockCopy(pkt, 0, fakeComplete, 0, payOff);
        Buffer.BlockCopy(FakeTlsClientHello, 0, fakeComplete, payOff, FakeTlsClientHello.Length);
        if (ver == 4)
        {
            BinaryPrimitives.WriteUInt16BigEndian(fakeComplete.AsSpan(2), (ushort)fakeComplete.Length);
            fakeComplete[8] = autoTtl;
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(fakeComplete.AsSpan(4), (ushort)(fakeComplete.Length - 40));
            fakeComplete[7] = autoTtl;
        }
        var a0 = addr;
        CalcChecksums(fakeComplete, fakeComplete.Length, ref a0);
        Send(fakeComplete, fakeComplete.Length, ref a0);

        if (count <= 0) return;

        var baseFake = new byte[len];
        Buffer.BlockCopy(pkt, 0, baseFake, 0, len);
        ReadOnlySpan<byte> safeHost = "www.wikipedia.org"u8;
        for (int i = 0; i < count && payOff + off + i < len; i++)
        {
            byte b = baseFake[payOff + off + i];
            if (b != '.') baseFake[payOff + off + i] = safeHost[i % safeHost.Length];
        }

        var fakeAutoTtl = (byte[])baseFake.Clone();
        if (ver == 4) fakeAutoTtl[8] = autoTtl; else fakeAutoTtl[7] = autoTtl;
        var a1 = addr;
        CalcChecksums(fakeAutoTtl, len, ref a1);
        Send(fakeAutoTtl, len, ref a1);
    }

    private void SendFakeHttpPacket(byte[] pkt, int ver, int ipHl, int payOff, ref WinDivertAddress addr, byte autoTtl)
    {
        var fake = new byte[payOff + FakeHttpRequest.Length];
        Buffer.BlockCopy(pkt, 0, fake, 0, payOff);
        Buffer.BlockCopy(FakeHttpRequest, 0, fake, payOff, FakeHttpRequest.Length);
        if (ver == 4)
        {
            BinaryPrimitives.WriteUInt16BigEndian(fake.AsSpan(2), (ushort)fake.Length);
            fake[8] = autoTtl;
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(fake.AsSpan(4), (ushort)(fake.Length - 40));
            fake[7] = autoTtl;
        }
        var a = addr;
        CalcChecksums(fake, fake.Length, ref a);
        Send(fake, fake.Length, ref a);
    }

    private async Task HandleDnsWithDohAsync(byte[] pkt, int len, WinDivertAddress addr, int ver, int ipHl)
    {
        try
        {
            int udpPayOff = ipHl + 8;
            int udpPayLen = len - udpPayOff;
            if (udpPayLen < 12 || _dohResolver == null)
            {
                Send(pkt, len, ref addr);
                return;
            }

            var query = pkt.AsSpan(udpPayOff, udpPayLen).ToArray();

            byte[]? dnsResp = IsIPv6Query(query)
                ? BuildEmptyNoErrorDnsResponse(query)
                : await _dohResolver.ResolveWireAsync(query, _cts?.Token ?? CancellationToken.None);

            if (dnsResp == null || dnsResp.Length < 12)
            {
                Send(pkt, len, ref addr);
                return;
            }

            int respTotalLen = ipHl + 8 + dnsResp.Length;
            var respPkt = new byte[respTotalLen];
            Buffer.BlockCopy(pkt, 0, respPkt, 0, ipHl);

            if (ver == 4)
            {
                BinaryPrimitives.WriteUInt16BigEndian(respPkt.AsSpan(2), (ushort)respTotalLen);
                respPkt[8] = 64;
                Buffer.BlockCopy(pkt, 16, respPkt, 12, 4);
                Buffer.BlockCopy(pkt, 12, respPkt, 16, 4);
            }
            else
            {
                BinaryPrimitives.WriteUInt16BigEndian(respPkt.AsSpan(4), (ushort)(8 + dnsResp.Length));
                respPkt[7] = 64;
                Buffer.BlockCopy(pkt, 24, respPkt, 8, 16);
                Buffer.BlockCopy(pkt, 8, respPkt, 24, 16);
            }

            Buffer.BlockCopy(pkt, ipHl + 2, respPkt, ipHl, 2);
            Buffer.BlockCopy(pkt, ipHl, respPkt, ipHl + 2, 2);
            BinaryPrimitives.WriteUInt16BigEndian(respPkt.AsSpan(ipHl + 4), (ushort)(8 + dnsResp.Length));
            Buffer.BlockCopy(dnsResp, 0, respPkt, ipHl + 8, dnsResp.Length);

            var inAddr = addr;
            inAddr.Outbound = false;
            CalcChecksums(respPkt, respTotalLen, ref inAddr);
            Send(respPkt, respTotalLen, ref inAddr);
        }
        catch
        {
            Send(pkt, len, ref addr);
        }
    }

    private static bool IsIPv6Query(byte[] q)
    {
        try
        {
            int i = 12;
            while (i < q.Length && q[i] != 0)
            {
                int l = q[i];
                if ((l & 0xC0) == 0xC0) { i += 2; break; }
                i += l + 1;
            }
            if (i < q.Length && q[i] == 0) i++;
            if (i + 4 <= q.Length)
            {
                ushort qtype = BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(i));
                return qtype is 28 or 65;
            }
        }
        catch { }
        return false;
    }

    private static byte[] BuildEmptyNoErrorDnsResponse(byte[] q)
    {
        var r = (byte[])q.Clone();
        r[2] = (byte)(0x80 | (q[2] & 0x01));
        r[3] = 0x80;
        r[6] = 0; r[7] = 0;
        r[8] = 0; r[9] = 0;
        r[10] = 0; r[11] = 0;
        return r;
    }

    private void RedirectDns(byte[] pkt, int len, ref WinDivertAddress addr, int ver, int ipHl, int srcPort)
    {
        byte[]? target = ver == 4 ? _dnsV4 : _dnsV6;
        if (target == null) { Send(pkt, len, ref addr); return; }

        int dstIpOff = ver == 4 ? 16 : 24;
        int ipLen = ver == 4 ? 4 : 16;
        var original = pkt.AsSpan(dstIpOff, ipLen).ToArray();

        _dnsMap[Key(ver, srcPort)] = new DnsEntry(original, Environment.TickCount64);
        if (_dnsMap.Count > 4096) CleanupDns();

        target.CopyTo(pkt, dstIpOff);
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(ipHl + 2), (ushort)(ver == 4 ? _s.DnsV4Port : _s.DnsV6Port));
        CalcChecksums(pkt, len, ref addr);
        Send(pkt, len, ref addr);
    }

    private void RestoreDns(byte[] pkt, int len, ref WinDivertAddress addr, int ver, int ipHl, int dstPort)
    {
        if (!_dnsMap.TryGetValue(Key(ver, dstPort), out var e)) { Send(pkt, len, ref addr); return; }
        int srcIpOff = ver == 4 ? 12 : 8;
        e.OriginalIp.CopyTo(pkt, srcIpOff);
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(ipHl), 53);
        CalcChecksums(pkt, len, ref addr);
        Send(pkt, len, ref addr);
    }

    private static int Key(int ver, int port) => (ver << 16) | port;

    private void CleanupDns()
    {
        long now = Environment.TickCount64;
        foreach (var kv in _dnsMap)
            if (now - kv.Value.Ticks > 30_000) _dnsMap.TryRemove(kv.Key, out _);
    }

    private unsafe void Send(byte[] pkt, int len, ref WinDivertAddress addr)
    {
        var h = _handle;
        if (h == IntPtr.Zero) return;
        fixed (byte* p = pkt) WinDivertNative.WinDivertSend(h, p, (uint)len, out _, in addr);
    }
}
