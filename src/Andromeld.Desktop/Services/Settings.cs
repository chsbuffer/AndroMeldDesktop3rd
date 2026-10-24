using System.Text.Json;
using System.Text.Json.Serialization;

using Avalonia.Input;

namespace Andromeld.Desktop.Services;

public sealed class Settings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Andromeld", "settings.json");

    public string ClientId { get; set; } = Guid.NewGuid().ToString();

    public string Address { get; set; } = "";

    public bool AudioForwardingEnabled { get; set; } = true;

    public bool ClipboardSyncEnabled { get; set; } = true;

    public bool NotificationSyncEnabled { get; set; } = true;

    public bool MirrorRightClickBackEnabled { get; set; }

    public bool DevelopmentNoticeAcknowledged { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<KeyModifiers>))]
    public KeyModifiers MirrorModifiers { get; set; } = KeyModifiers.Control;

    public Dictionary<string, string> Tokens { get; init; } = [];

    public Dictionary<string, AppUsageSettings> AppUsage { get; init; } = [];

    public Dictionary<string, MirrorDisplaySettings> MirrorDisplays { get; init; } = [];

    [JsonIgnore]
    public string StoragePath { get; init; } = FilePath;

    public static Settings Load()
    {
        try
        {
            Settings? settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
            if (settings is not null && settings.ClientId.Length is > 0 and <= 64 && settings.Tokens is not null &&
                settings.AppUsage is not null &&
                settings.AppUsage.Values.All(static value => value is not null && value.Pinned is not null && value.LastOpened is not null) &&
                settings.MirrorDisplays is not null && settings.MirrorDisplays.Values.All(static value =>
                    value is not null && value.ZoomPercent is not null && value.ImmersiveTitleBars is not null &&
                    value.ScreenCameras is not null && value.Windows is not null &&
                    value.CompatiblePackages is not null))
            {
                return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NullReferenceException)
        {
            // A missing or damaged settings file starts a new pairing identity.
        }

        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
        string temporary = $"{StoragePath}.tmp";

        // Create private before writing any token; replace atomically on the same filesystem.
        FileStreamOptions options = new() { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (FileStream stream = new(temporary, options))
        {
            JsonSerializer.Serialize(stream, this, JsonOptions);
        }

        File.Move(temporary, StoragePath, true);
    }
}
