namespace FiltrMikrofonu.Audio;

internal static class AlastorPreset
{
    // Approximate control points read from the user's Audacity screenshot.
    public static EqualizerSettings Equalizer => new(Curve:
    [
        new(20,-30), new(55,-30), new(60,-28), new(80,1.2f),
        new(1800,3.3f), new(2000,10), new(2300,2.2f),
        new(2800,20), new(3200,1.5f), new(5600,-30), new(20000,-30)
    ]);
    public static ManualFilterSettings Settings => new(EqualizerEnabled: true, Equalizer: Equalizer,
        HighPassEnabled: true, HighPassHz: 1000, HighPassSlope: 6,
        DistortionEnabled: true, DistortionMix: 100, MediumOverdrive: true,
        OverdriveAmount: 49, OverdriveOutput: 50);
}
