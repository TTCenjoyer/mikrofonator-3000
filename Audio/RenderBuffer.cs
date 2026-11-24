using NAudio.Wave;

namespace FiltrMikrofonu.Audio;

internal sealed class RenderBuffer : IWaveProvider
{
    private readonly SampleFormat format;
    private readonly StereoFrame[] samples;
    private readonly object mutex = new();
    private readonly int sampleRate;
    private readonly int targetFrames;
    private readonly int maxFrames;
    private readonly double nominalRatio;
    private int head, tail, count;
    private bool primed;
    private double fraction, clockCorrection;
    private StereoFrame lastSample;

    public RenderBuffer(int sourceRate, WaveFormat destination)
    {
        WaveFormat = destination;
        format = new SampleFormat(destination);
        sampleRate = sourceRate;
        targetFrames = sourceRate * 28 / 1000;
        maxFrames = sourceRate * 95 / 1000;
        samples = new StereoFrame[sourceRate / 5];
        nominalRatio = (double)sourceRate / destination.SampleRate;
    }

    public WaveFormat WaveFormat { get; }

    public void Write(ReadOnlySpan<float> block, ReadOnlySpan<float> right = default)
    {
        lock (mutex)
        {
            int skip = Math.Max(0, block.Length - maxFrames);
            block = block[skip..];
            if (!right.IsEmpty) right = right[skip..];
            if (count + block.Length > maxFrames)
            {
                int discard = Math.Min(count, count + block.Length - maxFrames);
                head = (head + discard) % samples.Length;
                count -= discard;
                fraction = 0;
            }

            for (int i = 0; i < block.Length; i++)
                samples[(tail + i) % samples.Length] = new StereoFrame(block[i], right.IsEmpty ? block[i] : right[i]);
            tail = (tail + block.Length) % samples.Length;
            count += block.Length;
        }
    }

    public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public int Read(Span<byte> buffer)
    {
        format.Clear(buffer);
        lock (mutex)
        {
            if (!primed && count < targetFrames)
                return buffer.Length;

            bool fadeIn = !primed;
            primed = true;
            // Capture and render devices have independent clocks. Small, smoothed
            // ratio changes hold the queue near its target during long calls.
            double desired = Math.Clamp(.45 * (count - targetFrames) / sampleRate, -.003, .003);
            clockCorrection += (desired - clockCorrection) * .025;
            double step = nominalRatio * (1 + clockCorrection);
            int frames = buffer.Length / format.FrameBytes;
            int fadeFrames = Math.Max(1, WaveFormat.SampleRate / 450);
            for (int frame = 0; frame < frames; frame++)
            {
                double position = fraction + step;
                int advance = (int)position;
                if (count <= Math.Max(1, advance))
                {
                    primed = false;
                    for (int i = 0; i < Math.Min(frames - frame, fadeFrames); i++)
                        format.WriteStereo(buffer[((frame + i) * format.FrameBytes)..], lastSample * (1 - (i + 1f) / fadeFrames));
                    lastSample = default;
                    break;
                }

                int next = head + 1;
                if (next == samples.Length)
                    next = 0;
                StereoFrame value = samples[head] + (samples[next] - samples[head]) * (float)fraction;
                if (fadeIn && frame < fadeFrames)
                    value *= (frame + 1f) / fadeFrames;
                format.WriteStereo(buffer[(frame * format.FrameBytes)..], value);
                lastSample = value;
                head += advance;
                if (head >= samples.Length)
                    head -= samples.Length;
                count -= advance;
                fraction = position - advance;
            }
        }
        return buffer.Length;
    }
}
