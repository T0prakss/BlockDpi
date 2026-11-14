using System.Text;

namespace BlockDpi.Engine;

public static class Protocol
{
    public static bool IsClientHello(ReadOnlySpan<byte> p) =>
        p.Length > 5 && p[0] == 0x16 && p[1] == 0x03 && p[5] == 0x01;

    public static bool TryFindSni(ReadOnlySpan<byte> p, out int offset, out int length, out bool truncated)
    {
        offset = length = 0;
        truncated = false;
        if (!IsClientHello(p)) return false;
        int recLen = (p[3] << 8) | p[4];
        if (p.Length < 5 + recLen) truncated = true;

        try
        {
            int i = 5 + 4;
            i += 2 + 32;
            if (i >= p.Length) { truncated = true; return false; }
            i += 1 + p[i];
            if (i + 2 > p.Length) { truncated = true; return false; }
            i += 2 + ((p[i] << 8) | p[i + 1]);
            if (i >= p.Length) { truncated = true; return false; }
            i += 1 + p[i];
            if (i + 2 > p.Length) { truncated = true; return false; }
            int extEnd = i + 2 + ((p[i] << 8) | p[i + 1]);
            i += 2;
            while (i + 4 <= p.Length && i + 4 <= extEnd)
            {
                int type = (p[i] << 8) | p[i + 1];
                int len = (p[i + 2] << 8) | p[i + 3];
                int data = i + 4;
                if (type == 0)
                {
                    if (data + 5 > p.Length) { truncated = true; return false; }
                    int nameLen = (p[data + 3] << 8) | p[data + 4];
                    int nameOff = data + 5;
                    if (nameOff + nameLen > p.Length) { truncated = true; return false; }
                    offset = nameOff;
                    length = nameLen;
                    return nameLen > 0;
                }
                i = data + len;
            }
            if (i < extEnd) truncated = true;
        }
        catch (IndexOutOfRangeException) { truncated = true; }
        return false;
    }

    public static bool TryFindSniInContinuation(ReadOnlySpan<byte> p, out int offset, out int length)
    {
        offset = length = 0;
        for (int i = 0; i + 9 < p.Length; i++)
        {
            if (p[i] != 0x00 || p[i + 1] != 0x00) continue;
            int extLen = (p[i + 2] << 8) | p[i + 3];
            int listLen = (p[i + 4] << 8) | p[i + 5];
            byte nameType = p[i + 6];
            int nameLen = (p[i + 7] << 8) | p[i + 8];
            if (nameType == 0 && extLen == listLen + 2 && listLen == nameLen + 3 && nameLen >= 3 && nameLen <= 253 && i + 9 + nameLen <= p.Length)
            {
                bool valid = true;
                bool hasDot = false;
                for (int k = 0; k < nameLen; k++)
                {
                    byte c = p[i + 9 + k];
                    if (c == '.') hasDot = true;
                    else if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-'))
                    {
                        valid = false;
                        break;
                    }
                }
                if (valid && hasDot)
                {
                    offset = i + 9;
                    length = nameLen;
                    return true;
                }
            }
        }
        return false;
    }

    public static bool LooksLikeHttpRequest(ReadOnlySpan<byte> p)
    {
        ReadOnlySpan<string> methods = ["GET ", "POST ", "HEAD ", "PUT ", "DELETE ", "OPTIONS ", "PATCH ", "CONNECT "];
        foreach (var m in methods)
            if (p.Length >= m.Length && Encoding.ASCII.GetString(p[..m.Length]) == m) return true;
        return false;
    }

    public static bool TryFindHttpHost(ReadOnlySpan<byte> p, out int nameOffset, out int valueOffset, out int valueLength)
    {
        nameOffset = valueOffset = valueLength = 0;
        for (int i = 0; i + 7 < p.Length; i++)
        {
            if (p[i] != '\r' || p[i + 1] != '\n') continue;
            if ((p[i + 2] | 0x20) == 'h' && (p[i + 3] | 0x20) == 'o' && (p[i + 4] | 0x20) == 's' &&
                (p[i + 5] | 0x20) == 't' && p[i + 6] == ':')
            {
                nameOffset = i + 2;
                int v = i + 7;
                while (v < p.Length && (p[v] == ' ' || p[v] == '\t')) v++;
                int end = v;
                while (end < p.Length && p[end] != '\r' && p[end] != '\n') end++;
                valueOffset = v;
                valueLength = end - v;
                return valueLength > 0;
            }
        }
        return false;
    }

    public static string NormalizeHost(string host)
    {
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.StartsWith('['))
        {
            int e = host.IndexOf(']');
            return e > 0 ? host[1..e] : host;
        }
        int c = host.LastIndexOf(':');
        if (c > 0 && host.IndexOf(':') == c) host = host[..c];
        return host;
    }
}

public sealed class DomainList
{
    private readonly HashSet<string> _domains;

    public DomainList(IEnumerable<string> domains)
    {
        _domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in domains)
        {
            var x = d.Trim().TrimStart('*').TrimStart('.').TrimEnd('.').ToLowerInvariant();
            if (x.Length > 0 && !x.StartsWith('#')) _domains.Add(x);
        }
    }

    public bool Matches(string host)
    {
        host = Protocol.NormalizeHost(host);
        while (true)
        {
            if (_domains.Contains(host)) return true;
            int dot = host.IndexOf('.');
            if (dot < 0) return false;
            host = host[(dot + 1)..];
        }
    }
}

