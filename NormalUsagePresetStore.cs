using System.IO;
using System.Text.Json;
using FiltrMikrofonu.Audio;

namespace FiltrMikrofonu;

internal sealed class NormalUsagePresetStore
{
    internal const string Name="Normal usage";
    private sealed record Document(string Name,Preferences Settings);
    private readonly string path;
    internal NormalUsagePresetStore(string? path=null)=>this.path=path??Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FiltrMikrofonu","normal-usage.json");
    internal Preferences? Load()
    {
        if(!File.Exists(path))return null;
        var document=JsonSerializer.Deserialize<Document>(File.ReadAllText(path));
        if(document is null || document.Name!=Name || document.Settings is null)
            throw new JsonException("Nieprawidłowy preset Normal usage.");
        return document.Settings with{Filter=VoiceFilter.NormalUsage};
    }
    internal void Save(Preferences settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            File.WriteAllText(temporary,JsonSerializer.Serialize(new Document(Name,settings with{Filter=VoiceFilter.NormalUsage})));
            File.Move(temporary,path,overwrite:true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
