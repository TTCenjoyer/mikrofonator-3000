using NAudio.Dsp;

namespace FiltrMikrofonu.Audio;

internal sealed record EqualizerSettings(float Band63 = 0, float Band125 = 0, float Band250 = 0,
    float Band500 = 0, float Band1000 = 0, float Band2000 = 0, float Band4000 = 0, float Band16000 = 0)
{
    public float[] Gains => [Band63, Band125, Band250, Band500, Band1000, Band2000, Band4000, Band16000];
    public static readonly double[] Frequencies = [63, 125, 250, 500, 1000, 2000, 4000, 16000];
}

internal sealed record ManualFilterSettings(bool EqualizerEnabled = false, EqualizerSettings? Equalizer = null,
    bool HighPassEnabled = false, float HighPassHz = 80, int HighPassSlope = 12,
    bool DistortionEnabled = false, bool HardClipping = false, float DriveDb = 12, float DistortionMix = 50,
    float DistortionOutputDb = -6, bool NormalizerEnabled = false, float NormalizeDb = -3, float MaxGainDb = 12,
    bool RadioColorEnabled = false, float RadioColorMix = 100,
    bool CompressionEnabled = false, float CompressionMix = 100,
    bool VoiceShiftEnabled = false, float VoiceShiftMix = 100);

internal sealed class ManualFilters
{
    private const int TapCount = 1025, Delay = TapCount / 2, TransformSize = 2048;
    private readonly int sampleRate;
    private readonly float[] buffer = new float[TapCount * 2];
    private readonly BiQuadFilter[] highPass = new BiQuadFilter[4];
    private readonly float smoothing, peakRelease, normalizeAttack, normalizeRelease;
    private EqualizerSettings? previousEq;
    private float[] coefficients = new float[TapCount], target = new float[TapCount];
    private int position;
    private float cutoff = -1, slope = 1, hpMix, eqMix, distortionMix, drive, distortionLevel, normalizeMix;
    private float peak, normalizeGain = 1;
    private ManualFilterSettings settings = new();

    public ManualFilters(int sampleRate, ManualFilterSettings? initial)
    {
        this.sampleRate = sampleRate;
        smoothing = 1 - MathF.Exp(-1f / (.02f * sampleRate));
        peakRelease = MathF.Exp(-1f / (.5f * sampleRate));
        normalizeAttack = 1 - MathF.Exp(-1f / (.005f * sampleRate));
        normalizeRelease = 1 - MathF.Exp(-1f / (.8f * sampleRate));
        for (int i = 0; i < highPass.Length; i++)
            highPass[i] = BiQuadFilter.HighPassFilter(sampleRate, 80, .707f);
        Update(initial);
        target.CopyTo(coefficients, 0);
        hpMix = settings.HighPassEnabled ? 1 : 0;
        eqMix = settings.EqualizerEnabled ? 1 : 0;
        distortionMix = settings.DistortionEnabled ? settings.DistortionMix / 100 : 0;
        drive = FilterSettings.DecibelsToGain(settings.DriveDb);
        distortionLevel = FilterSettings.DecibelsToGain(settings.DistortionOutputDb);
        normalizeMix = settings.NormalizerEnabled ? 1 : 0;
        slope = Math.Clamp(settings.HighPassSlope / 12, 1, 4);
    }

    public void Update(ManualFilterSettings? next)
    {
        settings = next ?? new ManualFilterSettings();
        float frequency = Math.Clamp(settings.HighPassHz, 20, Math.Min(3000, sampleRate * .4f));
        if (cutoff != frequency)
        {
            cutoff = frequency;
            foreach (var section in highPass)
                section.SetHighPassFilter(sampleRate, frequency, .707f);
        }
        var equalizer = settings.Equalizer ?? new EqualizerSettings();
        if (previousEq != equalizer)
        {
            target = BuildEqualizer(equalizer);
            previousEq = equalizer;
        }
    }

    public float Process(float input)
    {
        hpMix += ((settings.HighPassEnabled ? 1 : 0) - hpMix) * smoothing;
        eqMix += ((settings.EqualizerEnabled ? 1 : 0) - eqMix) * smoothing;
        float hp = input;
        float hpOutput = input;
        slope += (Math.Clamp(settings.HighPassSlope / 12, 1, 4) - slope) * smoothing;
        for (int i = 0; i < highPass.Length; i++)
        {
            hp = highPass[i].Transform(hp);
            float amount = Math.Clamp(slope - i, 0, 1);
            hpOutput += (hp - hpOutput) * amount;
        }
        input += (hpOutput - input) * hpMix;
        buffer[position] = buffer[position + TapCount] = input;
        position = (position + 1) % TapCount;
        float dry = buffer[position + Delay];
        float equalized = 0;
        for (int i = 0; i < TapCount; i++)
        {
            coefficients[i] += (target[i] - coefficients[i]) * smoothing;
            equalized += coefficients[i] * buffer[position + i];
        }
        float signal = dry + (equalized - dry) * eqMix;
        distortionMix += ((settings.DistortionEnabled ? Math.Clamp(settings.DistortionMix, 0, 100) / 100 : 0) - distortionMix) * smoothing;
        drive += (FilterSettings.DecibelsToGain(Math.Clamp(settings.DriveDb, 0, 30)) - drive) * smoothing;
        distortionLevel += (FilterSettings.DecibelsToGain(Math.Clamp(settings.DistortionOutputDb, -24, 0)) - distortionLevel) * smoothing;
        float driven = signal * drive;
        float clipped = settings.HardClipping ? Math.Clamp(driven, -1, 1) : MathF.Tanh(driven);
        return signal + (clipped * distortionLevel - signal) * distortionMix;
    }

    public float Normalize(float signal)
    {
        peak = Math.Max(MathF.Abs(signal), peak * peakRelease);
        float desired = peak > .008f
            ? Math.Min(FilterSettings.DecibelsToGain(Math.Clamp(settings.MaxGainDb, 0, 18)),
                FilterSettings.DecibelsToGain(Math.Clamp(settings.NormalizeDb, -24, -1)) / peak)
            : 1;
        normalizeGain += (desired - normalizeGain) * (desired < normalizeGain ? normalizeAttack : normalizeRelease);
        normalizeMix += ((settings.NormalizerEnabled ? 1 : 0) - normalizeMix) * smoothing;
        return signal * (1 + (normalizeGain - 1) * normalizeMix);
    }

    private float[] BuildEqualizer(EqualizerSettings equalizer)
    {
        var bins = new NAudio.Dsp.Complex[TransformSize];
        var gains = equalizer.Gains;
        for (int bin = 0; bin <= TransformSize / 2; bin++)
        {
            double hz = Math.Max(1, (double)bin * sampleRate / TransformSize);
            int band = 0;
            while (band < gains.Length - 2 && hz > EqualizerSettings.Frequencies[band + 1])
                band++;
            double amount = Math.Clamp(Math.Log(hz / EqualizerSettings.Frequencies[band]) /
                Math.Log(EqualizerSettings.Frequencies[band + 1] / EqualizerSettings.Frequencies[band]), 0, 1);
            float db = (float)(gains[band] + (gains[band + 1] - gains[band]) * amount);
            bins[bin].X = FilterSettings.DecibelsToGain(Math.Clamp(db, -18, 18)) / TransformSize;
            if (bin > 0 && bin < TransformSize / 2)
                bins[TransformSize - bin].X = bins[bin].X;
        }
        FastFourierTransform.FFT(false, 11, bins);
        var taps = new float[TapCount];
        for (int i = 0; i < TapCount; i++)
        {
            int offset = i - Delay;
            double window = .42 + .5 * Math.Cos(Math.PI * offset / Delay) + .08 * Math.Cos(2 * Math.PI * offset / Delay);
            taps[i] = bins[(offset + TransformSize) % TransformSize].X * (float)window;
        }
        return taps;
    }
}
