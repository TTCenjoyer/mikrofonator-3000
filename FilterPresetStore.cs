using System.IO;
using System.Text.Json;
using FiltrMikrofonu.Audio;

namespace FiltrMikrofonu;

internal sealed record FilterPreset(ManualFilterSettings? Manual = null, ReverbSettings? Room = null,
    NoiseSettings? Noise = null, double Time = 0, double Level = 0);

internal sealed class FilterPresetStore
{
    private readonly string path;
    public FilterPresetStore(string? path = null) => this.path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FiltrMikrofonu", "filter-presets.json");
    private Dictionary<string, Dictionary<string, FilterPreset>> Read()
    {
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, FilterPreset>>>(File.ReadAllText(path))
            ?? throw new JsonException("Nieprawidłowy plik presetów.");
    }
    public string[] Names(string filter) => Read().GetValueOrDefault(filter)?.Keys.Order(StringComparer.CurrentCultureIgnoreCase).ToArray() ?? [];
    public FilterPreset? Get(string filter, string name) => Read().GetValueOrDefault(filter)?.GetValueOrDefault(name.Trim());
    public void Save(string filter, string name, FilterPreset preset)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 80) throw new ArgumentException("Nazwa presetu musi mieć od 1 do 80 znaków.");
        var all = Read();
        if (!all.TryGetValue(filter, out var library)) all[filter] = library = new();
        library[name] = preset; Write(all);
    }
    public void Delete(string filter, string name)
    {
        var all = Read();
        if (all.TryGetValue(filter, out var library) && library.Remove(name.Trim())) Write(all);
    }
    private void Write(Dictionary<string, Dictionary<string, FilterPreset>> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(staging, JsonSerializer.Serialize(all));
            File.Move(staging, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }
}
