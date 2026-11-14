using System.Runtime.InteropServices;

namespace BlockDpi.Engine;

[StructLayout(LayoutKind.Sequential, Size = 80)]
internal unsafe struct WinDivertAddress
{
    public long Timestamp;
    public uint Bits;
    public uint Reserved2;
    public fixed byte Data[64];

    public bool Outbound
    {
        readonly get => (Bits & (1u << 17)) != 0;
        set
        {
            if (value) Bits |= (1u << 17);
            else Bits &= ~(1u << 17);
        }
    }

    public void MarkFullChecksums() => Bits |= (1u << 21) | (1u << 22) | (1u << 23);
}

internal static unsafe class WinDivertNative
{
    private const string Dll = "WinDivert.dll";
    public const int LayerNetwork = 0;
    public const int ShutdownBoth = 3;
    public static readonly IntPtr InvalidHandle = new(-1);

    [DllImport(Dll, SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
    public static extern IntPtr WinDivertOpen(string filter, int layer, short priority, ulong flags);

    [DllImport(Dll, SetLastError = true)]
    public static extern bool WinDivertRecv(IntPtr handle, byte* packet, uint packetLen, out uint recvLen, out WinDivertAddress addr);

    [DllImport(Dll, SetLastError = true)]
    public static extern bool WinDivertSend(IntPtr handle, byte* packet, uint packetLen, out uint sendLen, in WinDivertAddress addr);

    [DllImport(Dll, SetLastError = true)]
    public static extern bool WinDivertShutdown(IntPtr handle, int how);

    [DllImport(Dll, SetLastError = true)]
    public static extern bool WinDivertClose(IntPtr handle);

    [DllImport(Dll)]
    public static extern bool WinDivertHelperCalcChecksums(byte* packet, uint packetLen, IntPtr addr, ulong flags);

    public static bool FilesPresent() =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "WinDivert.dll")) &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, "WinDivert64.sys"));
}
