using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlockDpi;

public sealed class AppSettings
{
    public bool StartWithOs { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;

    public bool DnsRedirect { get; set; } = true;
    public bool UseDoh { get; set; } = true;
    public string DnsV4 { get; set; } = "77.88.8.8";
    public int DnsV4Port { get; set; } = 1253;
    public string DnsV6 { get; set; } = "2a02:6b8::feed:0ff";
    public int DnsV6Port { get; set; } = 1253;

    public bool HttpsFragment { get; set; } = true;
    public int FragmentSize { get; set; } = 2;
    public bool SplitAtSni { get; set; } = true;
    public bool ReverseFragments { get; set; } = true;
    public bool HttpFragment { get; set; } = true;
    public bool HttpHostTrick { get; set; } = true;
    public bool BlockQuic { get; set; } = true;
    public bool FakePacket { get; set; } = true;
    public int FakeTtl { get; set; } = 4;

    public bool UseBlacklist { get; set; } = false;
    public List<string> Blacklist { get; set; } =
    [
        "discord.com", "discord.gg", "discordapp.com", "discordapp.net",
        "discord.media", "discordcdn.com", "discord.dev", "discord.new",
        "discord.gift", "discordstatus.com", "dis.gd", "discord.co"
    ];

    [JsonIgnore]
    public static string Folder
    {
        get
        {
            var root = OperatingSystem.IsMacOS()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "Application Support")
                : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(root, "BlockDpi");
        }
    }

    [JsonIgnore] public static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts) ?? new AppSettings();
        }
        catch {  }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
    }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOpts), JsonOpts)!;
}
