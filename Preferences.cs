using System.IO;
using System.Text.Json;
using FiltrMikrofonu.Audio;

namespace FiltrMikrofonu;

internal sealed record Preferences(string? Microphone = null, string? Output = null, string? Monitor = null,
    VoiceFilter Filter = VoiceFilter.Clean, double Mix = 100, double InputDb = 0, double OutputDb = 0,
    double? BeforeMix = null, int BeforeMilliseconds = BeforeVoiceEcho.DefaultMilliseconds,
    bool RoomEnabled = false, double RoomMix = 35, int RoomMilliseconds = VoiceAmbience.DefaultRoomMilliseconds,
    bool EchoEnabled = false, double EchoMix = 30, int EchoMilliseconds = VoiceAmbience.DefaultEchoMilliseconds, ReverbSettings? Reverb = null, ReverbSettings? Preverb = null, int DefaultsRevision = 0, NoiseSettings? Noise = null)
{
    private static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FiltrMikrofonu");
    private static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public static Preferences Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath)) ?? new Preferences();
            if (settings.DefaultsRevision < 1)
                settings = settings with { Preverb = ReverbSettings.DefaultPreverb, Reverb = ReverbSettings.DefaultReverb,
                    BeforeMilliseconds = 1100, RoomEnabled = true, DefaultsRevision = 1 };
            return Enum.IsDefined(settings.Filter) ? settings : settings with { Filter = VoiceFilter.Clean };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Preferences(Preverb: ReverbSettings.DefaultPreverb, Reverb: ReverbSettings.DefaultReverb, BeforeMilliseconds: 1100, RoomEnabled: true, DefaultsRevision: 1);
        }
    }

    public static void Save(Preferences preferences)
    {
        string staging = Path.Combine(DirectoryPath, $"settings-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(staging, JsonSerializer.Serialize(preferences));
            File.Move(staging, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(staging); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }
}
