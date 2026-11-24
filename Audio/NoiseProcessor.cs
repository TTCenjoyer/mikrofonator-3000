using NAudio.Dsp;

namespace FiltrMikrofonu.Audio;

internal sealed record NoiseSettings(bool GateEnabled = false, float GateThreshold = -42,
    int GateHold = 120, int GateRelease = 150, bool DenoiseEnabled = false,
    float NoiseFloor = -48, float Reduction = 65);

internal sealed class NoiseProcessor
{
    private const int Size = 512, Hop = Size / 2;
    private readonly float[] input = new float[Size], output = new float[Hop], tail = new float[Hop];
    private readonly float[] window = new float[Size], gains = new float[Hop + 1];
    private readonly Complex[] spectrum = new Complex[Size];
    private readonly int sampleRate;
    private readonly float detectorAttack, detectorRelease, gateAttack, smoothing;
    private NoiseSettings settings;
    private int position, hold;
    private float envelope, gateGain = 1, mix;
    private float threshold, noisePower, minimumGain, release;
    private bool open;

    public NoiseProcessor(int sampleRate, NoiseSettings? initial)
    {
        this.sampleRate = sampleRate;
        detectorAttack = 1 - MathF.Exp(-1f / (.002f * sampleRate));
        detectorRelease = 1 - MathF.Exp(-1f / (.025f * sampleRate));
        gateAttack = 1 - MathF.Exp(-1f / (.003f * sampleRate));
        smoothing = 1 - MathF.Exp(-1f / (.015f * sampleRate));
        settings = initial ?? new NoiseSettings();
        mix = settings.DenoiseEnabled ? 1 : 0;
        gateGain = settings.GateEnabled ? 0 : 1;
        Array.Fill(gains, 1f);
        for (int i = 0; i < Size; i++)
            window[i] = MathF.Sin(MathF.PI * i / Size);
        Update(initial);
    }

    public void Update(NoiseSettings? next)
    {
        settings = next ?? new NoiseSettings();
        threshold = FilterSettings.DecibelsToGain(Math.Clamp(settings.GateThreshold, -75, -10));
        float floor = FilterSettings.DecibelsToGain(Math.Clamp(settings.NoiseFloor, -75, -20));
        // Expected FFT bin power for white noise through the analysis window.
        noisePower = floor * floor / (2 * Size);
        minimumGain = FilterSettings.DecibelsToGain(-Math.Clamp(settings.Reduction, 0, 100) * .3f);
        release = 1 - MathF.Exp(-1f / (sampleRate * Math.Clamp(settings.GateRelease, 30, 1000) / 1000f));
    }

    public float Process(float sample)
    {
        float delayedDry = input[position];
        float wet = output[position];
        input[position] = input[position + Hop];
        input[position + Hop] = sample;
        mix += ((settings.DenoiseEnabled ? 1 : 0) - mix) * smoothing;
        float cleaned = delayedDry + (wet - delayedDry) * mix;

        float level = MathF.Abs(cleaned);
        envelope += (level - envelope) * (level > envelope ? detectorAttack : detectorRelease);
        float close = threshold * .63f;
        if (envelope >= threshold)
        {
            open = true;
            hold = sampleRate * Math.Clamp(settings.GateHold, 0, 500) / 1000;
        }
        else if (open && envelope < close && hold-- <= 0)
            open = false;

        float goal = settings.GateEnabled && !open ? 0 : 1;
        gateGain += (goal - gateGain) * (goal > gateGain ? gateAttack : release);

        if (++position == Hop)
        {
            ProcessFrame();
            position = 0;
        }
        return cleaned * gateGain;
    }

    private void ProcessFrame()
    {
        for (int i = 0; i < Size; i++)
            spectrum[i] = new Complex { X = input[i] * window[i] };
        FastFourierTransform.FFT(true, 9, spectrum);
        for (int i = 0; i <= Hop; i++)
        {
            float power = spectrum[i].X * spectrum[i].X + spectrum[i].Y * spectrum[i].Y;
            float desired = Math.Max(minimumGain, 1 - noisePower * 1.7f / Math.Max(power, 1e-20f));
            gains[i] += (desired - gains[i]) * (desired > gains[i] ? .7f : .18f);
            spectrum[i].X *= gains[i];
            spectrum[i].Y *= gains[i];
            if (i > 0 && i < Hop)
                spectrum[Size - i] = new Complex { X = spectrum[i].X, Y = -spectrum[i].Y };
        }
        FastFourierTransform.FFT(false, 9, spectrum);
        for (int i = 0; i < Hop; i++)
        {
            output[i] = spectrum[i].X * window[i] + tail[i];
            tail[i] = spectrum[i + Hop].X * window[i + Hop];
        }
    }
}
