using FiltrMikrofonu;
using FiltrMikrofonu.Audio;
using System.IO;
using System.Text.Json;
void Check(bool ok,string description){if(!ok)throw new Exception(description);Console.WriteLine("PASS "+description);}
string path=Path.Combine(Path.GetTempPath(),"normal-usage-qa-"+Guid.NewGuid().ToString("N")+".json");
try
{
    var store=new NormalUsagePresetStore(path);
    Check(store.Load() is null,"missing profile allows normal settings fallback");
    var current=new Preferences(InputDb:2,OutputDb:-1,RoomEnabled:true,EchoEnabled:true,
        BeforeEnabled:true,MonitorEnabled:true,DefaultsRevision:1,
        Noise:new(GateEnabled:true,GateThreshold:-40,DenoiseEnabled:true,NeuralStrength:100),
        Manual:new(NormalizerEnabled:true,NormalizeDb:-5,Equalizer:new(Curve:[new(20,-3),new(20000,2)])));
    store.Save(current);
    var restored=new NormalUsagePresetStore(path).Load()!;
    Check(restored.Filter==VoiceFilter.NormalUsage,"profile restores selected Normal usage identity");
    Check(restored.Manual!.NormalizeDb==-5 && restored.Manual.NormalizerEnabled && restored.Noise!.GateThreshold==-40,"exact normalization and gate settings survive restart");
    Check(restored.BeforeEnabled && restored.EchoEnabled && restored.RoomEnabled && restored.MonitorEnabled,"effect enable flags are preserved");
    Check(restored.Manual.Equalizer!.Points.SequenceEqual(current.Manual!.Equalizer!.Points),"custom equalizer curve survives snapshot");
    var changed=current with{InputDb=9,Manual=current.Manual with{NormalizeDb=-10}};
    Check(store.Load()!.Manual!.NormalizeDb==-5,"profile remains fixed until explicitly updated");
    store.Save(changed);Check(store.Load()!.Manual!.NormalizeDb==-10 && store.Load()!.InputDb==9,"explicit update replaces complete profile");
    using var document=JsonDocument.Parse(File.ReadAllText(path));
    Check(document.RootElement.GetProperty("Name").GetString()=="Normal usage","profile has requested display name");
    File.WriteAllText(path,"{ malformed");bool rejected=false;
    try{store.Load();}catch(JsonException){rejected=true;}
    Check(rejected,"malformed preset is reported rather than overwriting it");
}
finally{File.Delete(path);}

