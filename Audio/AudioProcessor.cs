using NAudio.Dsp;

namespace FiltrMikrofonu.Audio;

internal enum VoiceFilter
{
    Reference, Clean, Studio, Radio, Megaphone, BeforeVoice, Anonymous
}

internal sealed record FilterSettings(VoiceFilter Filter, float Mix, float InputGain, float OutputGain,
    int BeforeMilliseconds = BeforeVoiceEcho.DefaultMilliseconds,
    float RoomMix = 0, int RoomMilliseconds = VoiceAmbience.DefaultRoomMilliseconds,
    float EchoMix = 0, int EchoMilliseconds = VoiceAmbience.DefaultEchoMilliseconds, ReverbSettings? Reverb = null, ReverbSettings? Preverb = null, bool BeforeEnabled = false, float BeforeMix = 1, NoiseSettings? Noise = null, ManualFilterSettings? Manual = null)
{
    public bool UsesPreverb => BeforeEnabled || Filter == VoiceFilter.BeforeVoice;
    public VoiceFilter Tone => Filter == VoiceFilter.BeforeVoice ? VoiceFilter.Clean : Filter;
    public float ToneMix => Filter == VoiceFilter.BeforeVoice ? 1 : Mix;
    public float PreverbMix => Filter == VoiceFilter.BeforeVoice ? Mix : BeforeMix;
    public static float DecibelsToGain(double decibels) => MathF.Pow(10, (float)(decibels / 20));
}

internal sealed class AudioProcessor
{
    private readonly ReferenceFilter reference;
    private readonly BeforeVoiceEcho beforeVoice;
    private readonly VoiceAmbience ambience;
    private readonly AnonymousVoice anonymous;
    private readonly NoiseProcessor noise;
    private readonly ManualFilters manual;
    private readonly BiQuadFilter studioHighPass, studioLowMid, studioPresence;
    private readonly BiQuadFilter radioHighPass, radioLowPass, radioPresence;
    private readonly BiQuadFilter megaphoneHighPass, megaphoneResonance, megaphoneLowPass;
    private readonly float parameterSmoothing, attack, release;
    private readonly int fadeLength;
    private VoiceFilter currentFilter, nextFilter;
    private int fadeRemaining;
    private float inputGain, outputGain, effectMix;
    private float studioEnvelope, beforeBlend, beforeMix;
    private bool beforeEnabled;
    private float radioColorBlend, compressionBlend, voiceShiftBlend;

    public AudioProcessor(int sampleRate, FilterSettings initial)
    {
        reference = new ReferenceFilter(sampleRate);
        beforeVoice = new BeforeVoiceEcho(sampleRate, initial.BeforeMilliseconds, initial.Preverb);
        ambience = new VoiceAmbience(sampleRate, initial);
        anonymous = new AnonymousVoice(sampleRate);
        noise = new NoiseProcessor(sampleRate, initial.Noise);
        manual = new ManualFilters(sampleRate, initial.Manual);
        studioHighPass = BiQuadFilter.HighPassFilter(sampleRate, 82, .707f);
        studioLowMid = BiQuadFilter.PeakingEQ(sampleRate, 260, .9f, -3);
        studioPresence = BiQuadFilter.PeakingEQ(sampleRate, Math.Min(3150, sampleRate * .4f), .8f, 3.5f);
        radioHighPass = BiQuadFilter.HighPassFilter(sampleRate, 340, .707f);
        radioLowPass = BiQuadFilter.LowPassFilter(sampleRate, Math.Min(3150, sampleRate * .4f), .8f);
        radioPresence = BiQuadFilter.PeakingEQ(sampleRate, 1480, 1, 4);
        megaphoneHighPass = BiQuadFilter.HighPassFilter(sampleRate, 640, .9f);
        megaphoneResonance = BiQuadFilter.PeakingEQ(sampleRate, 1850, 1.5f, 9);
        megaphoneLowPass = BiQuadFilter.LowPassFilter(sampleRate, Math.Min(4050, sampleRate * .42f), .75f);
        inputGain = initial.InputGain;
        outputGain = initial.OutputGain;
        effectMix = initial.ToneMix;
        currentFilter = nextFilter = initial.Tone;
        beforeEnabled = initial.UsesPreverb;
        beforeBlend = beforeEnabled ? 1 : 0;
        beforeMix = initial.PreverbMix;
        fadeLength = Math.Max(1, sampleRate * 22 / 1000);
        parameterSmoothing = 1 - MathF.Exp(-1f / (sampleRate * .012f));
        attack = 1 - MathF.Exp(-1f / (sampleRate * .006f));
        release = 1 - MathF.Exp(-1f / (sampleRate * .120f));
    }

    public void Process(Span<float> samples, FilterSettings settings, out float inputPeak, out float outputPeak)
        => ProcessStereo(samples, Span<float>.Empty, settings, out inputPeak, out outputPeak);

    public void ProcessStereo(Span<float> samples, Span<float> right, FilterSettings settings, out float inputPeak, out float outputPeak)
    {
        inputPeak = 0;
        outputPeak = 0;
        beforeVoice.SetParameters(settings.BeforeMilliseconds, settings.Preverb);
        ambience.Update(settings);
        noise.Update(settings.Noise);
        manual.Update(settings.Manual);
        if (settings.UsesPreverb && !beforeEnabled)
            beforeVoice.Reset();
        beforeEnabled = settings.UsesPreverb;
        for (int i = 0; i < samples.Length; i++)
        {
            inputGain += (settings.InputGain - inputGain) * parameterSmoothing;
            outputGain += (settings.OutputGain - outputGain) * parameterSmoothing;
            effectMix += (settings.ToneMix - effectMix) * parameterSmoothing;
            beforeMix += (settings.PreverbMix - beforeMix) * parameterSmoothing;
            beforeBlend = Math.Clamp(beforeBlend + (beforeEnabled ? 1f : -1f) / fadeLength, 0, 1);

            float input = samples[i] * inputGain;
            inputPeak = Math.Max(inputPeak, Math.Abs(input));
            float dry = reference.Push(manual.Process(noise.Process(input)));
            if (fadeRemaining == 0 && currentFilter != settings.Tone)
            {
                nextFilter = settings.Tone;
                fadeRemaining = fadeLength;
            }

            float filtered = settings.Manual is { } shared ? ApplyShared(shared, dry) : Apply(currentFilter, dry);
            if (settings.Manual is null && fadeRemaining > 0)
            {
                float blend = 1 - fadeRemaining / (float)fadeLength;
                filtered += (Apply(nextFilter, dry) - filtered) * blend;
                if (--fadeRemaining == 0)
                    currentFilter = nextFilter;
            }

            filtered = manual.Normalize(filtered);

            // Shape the voice first, then generate its anticipation and delayed voice.
            var (delayedVoice, reverse) = beforeEnabled || beforeBlend > 0
                ? beforeVoice.Process(filtered) : (0f, new StereoFrame());
            filtered += (delayedVoice - filtered) * beforeBlend;

            // Room reflections and the echo follow the direct voice, even when
            // the reverse reverb is playing earlier.
            StereoFrame space = ambience.Process(filtered);
            StereoFrame anticipation = reverse * (beforeMix * beforeBlend * ambience.DryGain);
            float direct = filtered * ambience.DryGain;
            float leftOutput = Limit((direct + anticipation.Left + space.Left) * outputGain);
            float rightOutput = Limit((direct + anticipation.Right + space.Right) * outputGain);
            samples[i] = right.IsEmpty ? (leftOutput + rightOutput) * .5f : leftOutput;
            if (!right.IsEmpty)
                right[i] = rightOutput;
            outputPeak = Math.Max(outputPeak, Math.Max(Math.Abs(leftOutput), Math.Abs(rightOutput)));
        }
    }

    private float ApplyShared(ManualFilterSettings shared, float dry)
    {
        radioColorBlend += ((shared.RadioColorEnabled ? Math.Clamp(shared.RadioColorMix / 100, 0, 1) : 0) - radioColorBlend) * parameterSmoothing;
        compressionBlend += ((shared.CompressionEnabled ? Math.Clamp(shared.CompressionMix / 100, 0, 1) : 0) - compressionBlend) * parameterSmoothing;
        voiceShiftBlend += ((shared.VoiceShiftEnabled ? Math.Clamp(shared.VoiceShiftMix / 100, 0, 1) : 0) - voiceShiftBlend) * parameterSmoothing;
        float signal = dry;
        if (radioColorBlend > .00001f) signal += (reference.ReadEffect() - signal) * radioColorBlend;
        // Keep the stateful processors warm so enabling them never replays stale audio.
        float compressed = Studio(signal);
        signal += (compressed - signal) * compressionBlend;
        float shifted = anonymous.Process(signal);
        return signal + (shifted - signal) * voiceShiftBlend;
    }

    private float Apply(VoiceFilter filter, float dry)
    {
        float wet = filter switch
        {
            VoiceFilter.Reference => reference.ReadEffect(),
            VoiceFilter.Studio => Studio(dry),
            VoiceFilter.Radio => .58f * MathF.Tanh(2.2f * radioPresence.Transform(radioLowPass.Transform(radioHighPass.Transform(dry)))),
            VoiceFilter.Megaphone => Megaphone(dry),
            VoiceFilter.Anonymous => anonymous.Process(dry),
            _ => dry
        };
        return dry + (wet - dry) * effectMix;
    }

    private float Studio(float sample)
    {
        float tone = studioPresence.Transform(studioLowMid.Transform(studioHighPass.Transform(sample)));
        float level = MathF.Abs(tone);
        studioEnvelope += (level - studioEnvelope) * (level > studioEnvelope ? attack : release);
        const float threshold = .092f;
        float reduction = studioEnvelope > threshold ? MathF.Pow(threshold / studioEnvelope, .62f) : 1;
        return tone * reduction * 1.55f;
    }

    private float Megaphone(float sample)
    {
        float tone = megaphoneResonance.Transform(megaphoneHighPass.Transform(sample));
        return megaphoneLowPass.Transform(MathF.Tanh(5.8f * tone) * .55f);
    }

    private static float Limit(float value)
    {
        const float knee = .91f;
        const float margin = .075f;
        float abs = MathF.Abs(value);
        if (abs <= knee)
            return value;
        return MathF.CopySign(knee + margin * MathF.Tanh((abs - knee) / margin), value);
    }
}
