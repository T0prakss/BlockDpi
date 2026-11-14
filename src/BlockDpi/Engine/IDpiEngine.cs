namespace BlockDpi.Engine;

public interface IDpiEngine : IDisposable
{
    bool IsRunning { get; }
    void Start(AppSettings settings);
    void Stop();
}

public sealed class EngineException(string message, bool winDivertMissing = false) : Exception(message)
{
    public bool WinDivertMissing { get; } = winDivertMissing;
}

public static class EngineFactory
{
    public static IDpiEngine Create() =>
        OperatingSystem.IsWindows() ? new WindowsDpiEngine() : new ProxyDpiEngine();
}

