namespace FiltrMikrofonu.Audio;

internal static class VoicePresets
{
    public static readonly (VoiceFilter Id, string Name)[] Names =
    [(VoiceFilter.Reference,"Alastor"),(VoiceFilter.Clean,"Czysty"),(VoiceFilter.Studio,"Studio"),
     (VoiceFilter.Radio,"Telefon"),(VoiceFilter.Megaphone,"Megafon"),(VoiceFilter.Anonymous,"Anonim")];
    public static ManualFilterSettings Settings(VoiceFilter preset)
    {
        if(preset==VoiceFilter.Reference)return AlastorPreset.Settings;
        if(preset is VoiceFilter.Clean or VoiceFilter.BeforeVoice)return new();
        return new(EqualizerEnabled:true,Equalizer:preset switch
        {
            VoiceFilter.Studio=>new(0,0,-3,-1,0,1,3,0),
            VoiceFilter.Radio=>new(-18,-12,-3,0,3,4,-10,-18),
            VoiceFilter.Megaphone=>new(-18,-18,-12,-4,1,9,-5,-18),
            _=>new(-4,2,3,0,-2,-4,-10,-18)
        },HighPassEnabled:true,HighPassHz:preset switch{VoiceFilter.Radio=>340,VoiceFilter.Megaphone=>640,_=>80},
        HighPassSlope:preset==VoiceFilter.Megaphone?24:12,
        DistortionEnabled:preset!=VoiceFilter.Studio,DriveDb:preset==VoiceFilter.Megaphone?16:7,
        DistortionMix:preset==VoiceFilter.Anonymous?25:65,DistortionOutputDb:-6,
        NormalizerEnabled:preset==VoiceFilter.Studio,NormalizeDb:preset==VoiceFilter.Studio?-10:-3,CompressionEnabled:preset==VoiceFilter.Studio,
        LowPassEnabled:preset!=VoiceFilter.Studio,LowPassHz:preset switch{VoiceFilter.Radio=>3150,VoiceFilter.Megaphone=>4050,_=>3200},
        PitchEnabled:preset==VoiceFilter.Anonymous,PitchSemitones:-5,
        CompressorThreshold:-18,CompressorRatio:3,CompressorAttack:10,CompressorRelease:120,CompressorMakeup:3);
    }
    public static bool Uses(string key,ManualFilterSettings m)=>key switch
    {
        "Eq"=>m.EqualizerEnabled,"HighPass"=>m.HighPassEnabled,"LowPass"=>m.LowPassEnabled,
        "Distortion"=>m.DistortionEnabled,"Normalizer"=>m.NormalizerEnabled,
        "Compression"=>m.CompressionEnabled,"Pitch"=>m.PitchEnabled,_=>false
    };
}
