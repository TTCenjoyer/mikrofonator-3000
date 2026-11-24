using NAudio.Dsp;
using NAudio.Wave;

namespace FiltrMikrofonu.Audio;

internal sealed class BeforeVoiceEcho
{
    public const int DefaultMilliseconds = 1300;
    public const int MinimumMilliseconds = 400;
    public const int MaximumMilliseconds = 2400;
    public const int StepMilliseconds = 100;

    private const int ReferenceRate = 44100;
    private const int PartitionSize = 512;
    private const int TransformSize = 2 * PartitionSize;
    private const int FftOrder = 10;
    private const float VoiceGain = .89125f;

    private sealed record Kernel(int DelayFrames, Complex[][] Spectra, Complex[][] RightSpectra);
    private sealed record Configuration(int Milliseconds, ReverbSettings? Settings);

    private sealed class RenderPath(Kernel kernel)
    {
        public Kernel Kernel { get; set; } = kernel;
        public float[] Wet { get; } = new float[PartitionSize];
        public float[] Overlap { get; } = new float[PartitionSize];
        public float[] RightWet { get; } = new float[PartitionSize];
        public float[] RightOverlap { get; } = new float[PartitionSize];
    }

    private readonly int fadeFrames;
    private readonly float[] voiceDelay;
    private readonly int sampleRate;
    private readonly object preparationLock = new();
    private Configuration configuration;
    private bool preparing;
    private float width, wetGain, dryGain, targetWidth, targetWet, targetDry;
    private readonly float smoothing;
    private readonly Complex[][] history;
    private readonly Complex[] sum = new Complex[TransformSize];
    private RenderPath current, next;
    private Kernel requested;
    private int delayPosition, blockPosition, currentPartition, validBlocks, fadeRemaining;

    public BeforeVoiceEcho(int sampleRate, int milliseconds = DefaultMilliseconds, ReverbSettings? settings = null)
    {
        this.sampleRate = sampleRate;
        fadeFrames = sampleRate * 70 / 1000;
        smoothing = 1 - MathF.Exp(-1f / (sampleRate * .02f));
        configuration = GetConfiguration(milliseconds, settings);
        int maximumFrames = sampleRate * MaximumMilliseconds / 1000;
        voiceDelay = new float[maximumFrames];
        history = new Complex[maximumFrames / PartitionSize + 2][];
        for (int i = 0; i < history.Length; i++)
            history[i] = new Complex[TransformSize];
        requested = Prepare(configuration);
        current = new RenderPath(requested);
        next = new RenderPath(requested);
        UpdateLevels(settings);
        width = targetWidth;
        wetGain = targetWet;
        dryGain = targetDry;
    }

    public void SetParameters(int milliseconds, ReverbSettings? settings)
    {
        UpdateLevels(settings);
        Configuration nextConfiguration = GetConfiguration(milliseconds, settings);
        lock (preparationLock)
        {
            if (configuration == nextConfiguration)
                return;
            configuration = nextConfiguration;
            if (preparing)
                return;
            preparing = true;
            _ = Task.Run(PreparePending);
        }
    }

    private void PreparePending()
    {
        while (true)
        {
            Configuration change;
            lock (preparationLock)
                change = configuration;
            Kernel ready = Prepare(change);
            lock (preparationLock)
            {
                if (configuration != change)
                    continue;
                Volatile.Write(ref requested, ready);
                preparing = false;
                return;
            }
        }
    }

    private static Configuration GetConfiguration(int milliseconds, ReverbSettings? settings) => new(
        Math.Clamp(milliseconds, MinimumMilliseconds, MaximumMilliseconds),
        settings is null ? null : settings with { WetGain = 0, DryGain = 0, StereoWidth = 100, WetOnly = true });

    private void UpdateLevels(ReverbSettings? settings)
    {
        targetWidth = settings is null ? 0 : Math.Clamp(settings.StereoWidth, 0, 100) / 100;
        targetWet = settings is null ? 1 : FilterSettings.DecibelsToGain(Math.Clamp(settings.WetGain, -30, 10));
        targetDry = settings is null ? 1 : settings.WetOnly ? 0 : FilterSettings.DecibelsToGain(Math.Clamp(settings.DryGain, -30, 10));
    }

    private Kernel Prepare(Configuration change)
    {
        int delay = (int)Math.Round(sampleRate * change.Milliseconds / 1000.0);
        if (change.Settings is null)
            return BuildKernel(ReadReferenceImpulse(), delay);
        // Render a finite stereo room impulse and reverse it into the look-ahead
        // window. Pre-delay becomes the gap immediately before the direct voice.
        var parameters = new FilterSettings(VoiceFilter.Clean, 1, 1, 1, RoomMix: 1,
            Reverb: change.Settings with { PreDelay = 0 });
        var room = new VoiceAmbience(sampleRate, parameters);
        int gap = (int)(Math.Clamp(change.Settings.PreDelay, 0, 200) * sampleRate / 1000);
        int responseFrames = delay - gap;
        var left = new float[delay];
        var right = new float[delay];
        for (int i = 0; i < responseFrames; i++)
        {
            StereoFrame frame = room.Process(i == 0 ? 1 : 0);
            left[responseFrames - 1 - i] = frame.Left;
            right[responseFrames - 1 - i] = frame.Right;
        }
        // Fade in the start to avoid truncation clicks at long decay settings.
        int fade = Math.Min(sampleRate / 50, responseFrames);
        for (int i = 0; i < fade; i++)
        {
            float gain = i / (float)fade;
            left[i] *= gain;
            right[i] *= gain;
        }
        return new Kernel(delay, TransformImpulse(left), TransformImpulse(right));
    }

    public (float Voice, StereoFrame Reverb) Process(float sample)
    {
        width += (targetWidth - width) * smoothing;
        wetGain += (targetWet - wetGain) * smoothing;
        dryGain += (targetDry - dryGain) * smoothing;
        float voice = ReadVoice(current.Kernel.DelayFrames);
        var wet = new StereoFrame(current.Wet[blockPosition], current.RightWet[blockPosition]);
        if (fadeRemaining > 0)
        {
            float blend = 1 - fadeRemaining / (float)fadeFrames;
            voice += (ReadVoice(next.Kernel.DelayFrames) - voice) * blend;
            wet += (new StereoFrame(next.Wet[blockPosition], next.RightWet[blockPosition]) - wet) * blend;
            if (--fadeRemaining == 0)
                (current, next) = (next, current);
        }

        voiceDelay[delayPosition++] = sample;
        if (delayPosition == voiceDelay.Length)
            delayPosition = 0;

        history[currentPartition][blockPosition++] = new Complex { X = sample };
        if (blockPosition == PartitionSize)
        {
            RenderBlock();
            blockPosition = 0;
        }
        float mid = (wet.Left + wet.Right) * .5f;
        float side = (wet.Left - wet.Right) * .5f * width;
        return (voice * VoiceGain * dryGain, new StereoFrame(mid + side, mid - side) * wetGain);
    }

    public void Reset()
    {
        current.Kernel = Volatile.Read(ref requested);
        Array.Clear(voiceDelay);
        Array.Clear(current.Wet);
        Array.Clear(current.Overlap);
        Array.Clear(current.RightWet);
        Array.Clear(current.RightOverlap);
        delayPosition = blockPosition = currentPartition = validBlocks = fadeRemaining = 0;
    }

    private float ReadVoice(int frames)
    {
        int index = delayPosition - frames;
        return voiceDelay[index >= 0 ? index : index + voiceDelay.Length];
    }

    private void RenderBlock()
    {
        var newest = history[currentPartition];
        newest.AsSpan(PartitionSize).Clear();
        FastFourierTransform.FFT(true, FftOrder, newest);
        validBlocks = Math.Min(validBlocks + 1, history.Length);

        Kernel ready = Volatile.Read(ref requested);
        if (fadeRemaining == 0 && current.Kernel != ready)
        {
            next.Kernel = ready;
            int previous = (currentPartition + history.Length - 1) % history.Length;
            Convolve(next.Kernel.Spectra, previous, validBlocks - 1);
            for (int i = 0; i < PartitionSize; i++)
                next.Overlap[i] = sum[PartitionSize + i].X;
            Convolve(next.Kernel.RightSpectra, previous, validBlocks - 1);
            for (int i = 0; i < PartitionSize; i++)
                next.RightOverlap[i] = sum[PartitionSize + i].X;
            fadeRemaining = fadeFrames;
        }

        RenderPathBlock(current);
        if (fadeRemaining > 0)
            RenderPathBlock(next);
        currentPartition = (currentPartition + 1) % history.Length;
    }

    private void RenderPathBlock(RenderPath path)
    {
        Convolve(path.Kernel.Spectra, currentPartition, validBlocks);
        for (int i = 0; i < PartitionSize; i++)
        {
            path.Wet[i] = sum[i].X + path.Overlap[i];
            path.Overlap[i] = sum[PartitionSize + i].X;
        }
        Convolve(path.Kernel.RightSpectra, currentPartition, validBlocks);
        for (int i = 0; i < PartitionSize; i++)
        {
            path.RightWet[i] = sum[i].X + path.RightOverlap[i];
            path.RightOverlap[i] = sum[PartitionSize + i].X;
        }
    }

    private void Convolve(Complex[][] spectra, int slot, int blocks)
    {
        sum.AsSpan(0, PartitionSize + 1).Clear();
        int count = Math.Min(blocks, spectra.Length);
        for (int partition = 0; partition < count; partition++)
        {
            var input = history[slot];
            var filter = spectra[partition];
            for (int bin = 0; bin <= PartitionSize; bin++)
            {
                float a = input[bin].X;
                float b = input[bin].Y;
                float c = filter[bin].X;
                float d = filter[bin].Y;
                sum[bin].X += a * c - b * d;
                sum[bin].Y += a * d + b * c;
            }
            if (--slot < 0)
                slot = history.Length - 1;
        }

        for (int bin = 1; bin < PartitionSize; bin++)
            sum[TransformSize - bin] = new Complex { X = sum[bin].X, Y = -sum[bin].Y };
        FastFourierTransform.FFT(false, FftOrder, sum);
    }

    private static Kernel BuildKernel(float[] reference, int delayFrames)
    {
        var converter = new WdlResampler();
        converter.SetMode(false, 0, true);
        converter.SetRates(ReferenceRate, delayFrames);
        var impulse = new float[delayFrames];
        int needed = converter.ResamplePrepare(impulse.Length, 1, out Span<float> input);
        int available = Math.Min(needed, reference.Length);
        reference.AsSpan(0, available).CopyTo(input);
        if (converter.ResampleOut(impulse, available, impulse.Length, 1) != impulse.Length)
            throw new InvalidOperationException("Profil pogĹ‚osu Przed gĹ‚os jest niekompletny.");

        float scale = (float)ReferenceRate / delayFrames;
        for (int i = 0; i < impulse.Length; i++)
            impulse[i] *= scale;
        Complex[][] spectra = TransformImpulse(impulse);
        return new Kernel(delayFrames, spectra, spectra);
    }

    private static Complex[][] TransformImpulse(float[] impulse)
    {
        var spectra = new Complex[(impulse.Length - 1) / PartitionSize][];
        var coefficients = new Complex[TransformSize];
        for (int partition = 0; partition < spectra.Length; partition++)
        {
            coefficients.AsSpan().Clear();
            int start = (partition + 1) * PartitionSize;
            int length = Math.Min(PartitionSize, impulse.Length - start);
            for (int i = 0; i < length; i++)
                coefficients[i].X = impulse[start + i] * TransformSize;
            FastFourierTransform.FFT(true, FftOrder, coefficients);
            spectra[partition] = coefficients[..(PartitionSize + 1)];
        }
        return spectra;
    }

    private static float[] ReadReferenceImpulse()
    {
        // This response was measured from the two halves of 12.wav.
        using var data = typeof(BeforeVoiceEcho).Assembly.GetManifestResourceStream("FiltrMikrofonu.BeforeVoiceImpulse")
            ?? throw new InvalidOperationException("Brak profilu pogĹ‚osu Przed gĹ‚os.");
        using var wave = new RawSourceWaveStream(data, WaveFormat.CreateIeeeFloatWaveFormat(ReferenceRate, 1));
        ISampleProvider samples = wave.ToSampleProvider();
        var impulse = new float[(int)(wave.Length / sizeof(float))];
        if (samples.Read(impulse) != impulse.Length)
            throw new InvalidOperationException("Nie udaĹ‚o siÄ™ odczytaÄ‡ profilu pogĹ‚osu.");
        return impulse;
    }
}
