using NAudio.Dsp;

namespace FiltrMikrofonu.Audio;

internal sealed record EqualizerPoint(double Hertz, float Gain);

internal sealed record EqualizerSettings(float Band63 = 0, float Band125 = 0, float Band250 = 0,
    float Band500 = 0, float Band1000 = 0, float Band2000 = 0, float Band4000 = 0, float Band16000 = 0, EqualizerPoint[]? Curve = null)
{
    public EqualizerPoint[] Points => Curve is { Length: >= 2 } ? Curve : Frequencies.Select((hz, i) => new EqualizerPoint(hz, Gains[i])).ToArray();
    public float[] Gains => [Band63, Band125, Band250, Band500, Band1000, Band2000, Band4000, Band16000];
    public static readonly double[] Frequencies = [63, 125, 250, 500, 1000, 2000, 4000, 16000];
}

internal sealed record ManualFilterSettings(bool EqualizerEnabled = false, EqualizerSettings? Equalizer = null,
    bool HighPassEnabled = false, float HighPassHz = 80, int HighPassSlope = 12,
    bool DistortionEnabled = false, bool HardClipping = false, float DriveDb = 12, float DistortionMix = 50,
    float DistortionOutputDb = -6, bool NormalizerEnabled = false, float NormalizeDb = -3, float MaxGainDb = 12,
    bool CompressionEnabled = false, float CompressionMix = 100,
    bool MediumOverdrive = false, float OverdriveAmount = 49, float OverdriveOutput = 50,
    bool LowPassEnabled = false, float LowPassHz = 3500, int LowPassSlope = 12,
    bool PitchEnabled = false, float PitchSemitones = -5, float PitchMix = 100,
    float CompressorThreshold = -18, float CompressorRatio = 3, float CompressorAttack = 10,
    float CompressorRelease = 120, float CompressorMakeup = 3);

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
    private float firstOrderCoefficient, previousHpInput, previousHpOutput, firstOrderBlend;
    private float mediumBlend, mediumAmount, mediumLevel;
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
        firstOrderBlend = settings.HighPassSlope == 6 ? 1 : 0;
        mediumBlend = settings.MediumOverdrive ? 1 : 0;
        mediumAmount = Math.Max(.001f, settings.OverdriveAmount * MathF.Log(10) / 20);
        mediumLevel = Math.Clamp(settings.OverdriveOutput / 100, 0, 1);
    }

    public void Update(ManualFilterSettings? next)
    {
        settings = next ?? new ManualFilterSettings();
        float frequency = Math.Clamp(settings.HighPassHz, 20, Math.Min(3000, sampleRate * .4f));
        if (cutoff != frequency)
        {
            cutoff = frequency;
            firstOrderCoefficient = 1 / (1 + MathF.Tan(MathF.PI * frequency / sampleRate));
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
        float firstOrder = firstOrderCoefficient * (input - previousHpInput) + (2 * firstOrderCoefficient - 1) * previousHpOutput;
        previousHpInput = input;
        previousHpOutput = firstOrder;
        firstOrderBlend += ((settings.HighPassSlope == 6 ? 1 : 0) - firstOrderBlend) * smoothing;
        float hp = input;
        float hpOutput = input;
        slope += (Math.Clamp(settings.HighPassSlope / 12, 1, 4) - slope) * smoothing;
        for (int i = 0; i < highPass.Length; i++)
        {
            hp = highPass[i].Transform(hp);
            float amount = Math.Clamp(slope - i, 0, 1);
            hpOutput += (hp - hpOutput) * amount;
        }
        hpOutput += (firstOrder - hpOutput) * firstOrderBlend;
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
        mediumBlend += ((settings.MediumOverdrive ? 1 : 0) - mediumBlend) * smoothing;
        mediumAmount += (Math.Max(.001f, Math.Clamp(settings.OverdriveAmount, 0, 100) * MathF.Log(10) / 20) - mediumAmount) * smoothing;
        mediumLevel += (Math.Clamp(settings.OverdriveOutput / 100, 0, 1) - mediumLevel) * smoothing;
        float standard = clipped * distortionLevel;
        float medium = MediumCurve(signal, mediumAmount) * mediumLevel;
        return signal + (standard + (medium - standard) * mediumBlend - signal) * distortionMix;
    }

    // Normalized symmetric exponential waveshaper; amount uses Audacity's dB parameter scale.
    private static float MediumCurve(float signal, float amount)
    {
        float absolute = Math.Min(MathF.Abs(signal), 1);
        return MathF.CopySign((1 - MathF.Exp(-amount * absolute)) / (1 - MathF.Exp(-amount)), signal);
    }

    public float Normalize(float signal)
    {
        peak = Math.Max(MathF.Abs(signal), peak * peakRelease);
        // A quiet syllable must retain its boost: no level gate or reset to unity.
        // The maximum boost bounds amplification even through silence.
        float desired = Math.Min(FilterSettings.DecibelsToGain(Math.Clamp(settings.MaxGainDb, 0, 18)),
            FilterSettings.DecibelsToGain(Math.Clamp(settings.NormalizeDb, -24, -1)) / Math.Max(peak,1e-9f));
        normalizeGain += (desired - normalizeGain) * (desired < normalizeGain ? normalizeAttack : normalizeRelease);
        normalizeMix += ((settings.NormalizerEnabled ? 1 : 0) - normalizeMix) * smoothing;
        return signal * (1 + (normalizeGain - 1) * normalizeMix);
    }

    private float[] BuildEqualizer(EqualizerSettings equalizer)
    {
        var bins = new NAudio.Dsp.Complex[TransformSize];
        var points = equalizer.Points;
        for (int bin = 0; bin <= TransformSize / 2; bin++)
        {
            double hz = Math.Max(1, (double)bin * sampleRate / TransformSize);
            int band = 0;
            while (band < points.Length - 2 && hz > points[band + 1].Hertz)
                band++;
            double amount = Math.Clamp(Math.Log(hz / points[band].Hertz) /
                Math.Log(points[band + 1].Hertz / points[band].Hertz), 0, 1);
            float db = (float)(points[band].Gain + (points[band + 1].Gain - points[band].Gain) * amount);
            bins[bin].X = FilterSettings.DecibelsToGain(Math.Clamp(db, -30, 30)) / TransformSize;
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
