namespace FiltrMikrofonu.Audio;
internal sealed record ReverbSettings(
    float RoomSize = 50, float PreDelay = 22, float Reverberance = 50,
    float Damping = 99, float ToneLow = 50, float ToneHigh = 100,
    float WetGain = -1, float DryGain = -1, float StereoWidth = 70, bool WetOnly = false)
{
    public static ReverbSettings DefaultPreverb => new(68, 29, 61, 81, 50, 74, 5, 0, 50);
    public static ReverbSettings DefaultReverb => new(70, 71, 50, 47, 50, 100, -1, 0, 55);
}
