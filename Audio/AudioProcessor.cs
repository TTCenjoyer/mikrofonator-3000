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
    private readonly BeforeVoiceEcho beforeVoice;
    private readonly VoiceAmbience ambience;
    private readonly NoiseProcessor noise;
    private readonly ManualFilters manual;
    private readonly BasicEffects basic;
    private readonly float parameterSmoothing;
    private readonly int fadeLength;
    private float inputGain,outputGain,beforeBlend,beforeMix;
    private bool beforeEnabled;
    public AudioProcessor(int sampleRate, FilterSettings initial)
    {
        var shared=initial.Manual??VoicePresets.Settings(initial.Tone);
        beforeVoice=new BeforeVoiceEcho(sampleRate,initial.BeforeMilliseconds,initial.Preverb);
        ambience=new VoiceAmbience(sampleRate,initial);noise=new NoiseProcessor(sampleRate,initial.Noise);
        manual=new ManualFilters(sampleRate,shared);basic=new BasicEffects(sampleRate,shared);
        inputGain=initial.InputGain;outputGain=initial.OutputGain;
        beforeEnabled=initial.UsesPreverb;beforeBlend=beforeEnabled?1:0;beforeMix=initial.PreverbMix;
        fadeLength=Math.Max(1,sampleRate*22/1000);parameterSmoothing=1-MathF.Exp(-1f/(sampleRate*.012f));
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
        var shared = settings.Manual ?? VoicePresets.Settings(settings.Tone);
        manual.Update(shared);
        basic.Update(shared);
        if (settings.UsesPreverb && !beforeEnabled)
            beforeVoice.Reset();
        beforeEnabled = settings.UsesPreverb;
        for (int i = 0; i < samples.Length; i++)
        {
            inputGain += (settings.InputGain - inputGain) * parameterSmoothing;
            outputGain += (settings.OutputGain - outputGain) * parameterSmoothing;
            beforeMix += (settings.PreverbMix - beforeMix) * parameterSmoothing;
            beforeBlend = Math.Clamp(beforeBlend + (beforeEnabled ? 1f : -1f) / fadeLength, 0, 1);

            float input = samples[i] * inputGain;
            inputPeak = Math.Max(inputPeak, Math.Abs(input));
            float filtered = basic.Process(manual.Process(noise.Process(input)));

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
