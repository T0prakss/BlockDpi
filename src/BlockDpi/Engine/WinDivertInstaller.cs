using System.IO.Compression;

namespace BlockDpi.Engine;

public static class WinDivertInstaller
{
    private const string Url = "https://github.com/basil00/WinDivert/releases/download/v2.2.2/WinDivert-2.2.2-A.zip";

    public static async Task InstallAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BlockDpi/1.0");
        var bytes = await http.GetByteArrayAsync(Url);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        string dir = AppContext.BaseDirectory;
        foreach (var name in new[] { "WinDivert.dll", "WinDivert64.sys" })
        {
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("/x64/" + name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException(name + " arşivde bulunamadı.");
            try
            {
                entry.ExtractToFile(Path.Combine(dir, name), overwrite: true);
            }
            catch (IOException)
            {
                throw new InvalidOperationException(
                    "Windows Defender dosyayı engelledi. BlockDpi klasörünü Defender'da 'Dışlamalar'a ekleyip tekrar deneyin.");
            }
        }

        await Task.Delay(1500);
        if (!WinDivertNative.FilesPresent())
            throw new InvalidOperationException(
                "Windows Defender WinDivert dosyalarını sildi (yanlış alarm). BlockDpi klasörünü Defender'da " +
                "'Dışlamalar'a ekleyip tekrar deneyin.");
    }
}

