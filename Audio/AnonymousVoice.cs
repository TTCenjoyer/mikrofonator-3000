using NAudio.Dsp;

namespace FiltrMikrofonu.Audio;

internal sealed class AnonymousVoice
{
    private readonly float[] buffer;
    private readonly int window;
    private readonly BiQuadFilter lowPass, highPass;
    private int write;
    private float phase;

    public AnonymousVoice(int sampleRate)
    {
        window = sampleRate * 65 / 1000;
        buffer = new float[window + 4];
        highPass = BiQuadFilter.HighPassFilter(sampleRate, 75, .707f);
        lowPass = BiQuadFilter.LowPassFilter(sampleRate, Math.Min(3200, sampleRate * .4f), .8f);
    }

    public float Process(float sample)
    {
        buffer[write] = sample;
        float second = phase + .5f;
        if (second >= 1) second -= 1;
        float gain = .5f - .5f * MathF.Cos(MathF.Tau * phase);
        float shifted = Read(2 + phase * window) * gain + Read(2 + second * window) * (1 - gain);
        phase += .28f / window;
        if (phase >= 1) phase -= 1;
        write = (write + 1) % buffer.Length;
        return lowPass.Transform(.7f * MathF.Tanh(highPass.Transform(shifted) * 3.4f));
    }

    private float Read(float delay)
    {
        int whole = (int)delay;
        float fraction = delay - whole;
        int a = write - whole;
        if (a < 0) a += buffer.Length;
        int b = a == 0 ? buffer.Length - 1 : a - 1;
        return buffer[a] + (buffer[b] - buffer[a]) * fraction;
    }
}
