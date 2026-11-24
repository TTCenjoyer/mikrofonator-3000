namespace FiltrMikrofonu.Audio;

internal sealed class VoiceAmbience
{
    public const int DefaultRoomMilliseconds = 450;
    public const int DefaultEchoMilliseconds = 350;
    public const int MinimumEchoMilliseconds = 100;
    public const int MaximumEchoMilliseconds = 1200;

    private readonly int sampleRate;
    private readonly float smoothing, echoDamping;
    private readonly float[] preDelay, echo;
    private readonly float[][] combs = new float[8][];
    private readonly int[] positions = new int[8];
    private readonly float[] damped = new float[8];
    private readonly float[] tapTimes = [25.3f, 29.7f, 33.1f, 37.9f, 41.3f, 43.7f, 47.1f, 51.7f];
    private int prePosition, echoPosition;
    private float size, preTime, decay, damping, low, high, wet, dry, width, echoMix, echoTime;
    private float lowLeft, lowRight, highLeft, highRight, echoFiltered;
    private ReverbSettings target = new();
    private float targetWet, targetDry, targetEchoMix, targetEchoTime;
    private bool initialized;

    public VoiceAmbience(int sampleRate, FilterSettings initial)
    {
        this.sampleRate = sampleRate;
        smoothing = 1 - MathF.Exp(-1f / (sampleRate * .045f));
        echoDamping = 1 - MathF.Exp(-2 * MathF.PI * Math.Min(4400, sampleRate * .4f) / sampleRate);
        preDelay = new float[sampleRate / 5 + 2];
        echo = new float[sampleRate * MaximumEchoMilliseconds / 1000 + 2];
        for (int i = 0; i < combs.Length; i++)
            combs[i] = new float[(int)(sampleRate * tapTimes[i] * 2 / 1000) + 2];
        Update(initial);
    }

    public float DryGain => dry;

    public void Update(FilterSettings settings)
    {
        target = settings.Reverb ?? new ReverbSettings(
            Reverberance: Math.Clamp((settings.RoomMilliseconds - 150) / 13.5f, 0, 100));
        target = target with {
            RoomSize = Math.Clamp(target.RoomSize, 0, 100),
            PreDelay = Math.Clamp(target.PreDelay, 0, 200),
            Reverberance = Math.Clamp(target.Reverberance, 0, 100),
            Damping = Math.Clamp(target.Damping, 0, 100),
            ToneLow = Math.Clamp(target.ToneLow, 0, 100),
            ToneHigh = Math.Clamp(target.ToneHigh, 0, 100),
            StereoWidth = Math.Clamp(target.StereoWidth, 0, 100)
        };
        bool enabled = settings.RoomMix > 0;
        targetWet = enabled ? Math.Clamp(settings.RoomMix, 0, 1) * FilterSettings.DecibelsToGain(Math.Clamp(target.WetGain, -30, 10)) : 0;
        targetDry = enabled ? (target.WetOnly ? 0 : FilterSettings.DecibelsToGain(Math.Clamp(target.DryGain, -30, 10))) : 1;
        targetEchoMix = Math.Clamp(settings.EchoMix, 0, 1);
        targetEchoTime = sampleRate * Math.Clamp(settings.EchoMilliseconds, MinimumEchoMilliseconds, MaximumEchoMilliseconds) / 1000f;
        if (initialized) return;
        size = target.RoomSize;
        preTime = target.PreDelay;
        decay = target.Reverberance;
        damping = target.Damping;
        low = target.ToneLow;
        high = target.ToneHigh;
        width = target.StereoWidth;
        wet = targetWet;
        dry = targetDry;
        echoMix = targetEchoMix;
        echoTime = targetEchoTime;
        initialized = true;
    }

    public StereoFrame Process(float voice)
    {
        Smooth(ref size, target.RoomSize);
        Smooth(ref preTime, target.PreDelay);
        Smooth(ref decay, target.Reverberance);
        Smooth(ref damping, target.Damping);
        Smooth(ref low, target.ToneLow);
        Smooth(ref high, target.ToneHigh);
        Smooth(ref width, target.StereoWidth);
        Smooth(ref wet, targetWet);
        Smooth(ref dry, targetDry);
        Smooth(ref echoMix, targetEchoMix);
        Smooth(ref echoTime, targetEchoTime);

        preDelay[prePosition] = voice;
        float roomInput = Read(preDelay, prePosition, preTime * sampleRate / 1000);
        prePosition = (prePosition + 1) % preDelay.Length;
        float left = 0, right = 0;
        float feedback = .15f + .82f * decay / 100;
        float filter = .90f - .86f * damping / 100;
        for (int i = 0; i < combs.Length; i++)
        {
            float distance = sampleRate * tapTimes[i] * (.5f + size * .015f) / 1000;
            float reflection = Read(combs[i], positions[i], distance);
            damped[i] += (reflection - damped[i]) * filter;
            combs[i][positions[i]] = roomInput * .25f + damped[i] * feedback;
            positions[i] = (positions[i] + 1) % combs[i].Length;
            if (i % 2 == 0) left += reflection;
            else right += reflection;
        }

        left = Tone(left * .5f, ref lowLeft, ref highLeft);
        right = Tone(right * .5f, ref lowRight, ref highRight);
        float mid = (left + right) * .5f;
        float side = (left - right) * .5f * width / 100;
        float repeat = Read(echo, echoPosition, echoTime);
        echoFiltered += (repeat - echoFiltered) * echoDamping;
        echo[echoPosition] = voice + echoFiltered * .34f;
        echoPosition = (echoPosition + 1) % echo.Length;
        return new StereoFrame((mid + side) * wet + repeat * echoMix,
            (mid - side) * wet + repeat * echoMix);
    }

    private float Tone(float sample, ref float lowState, ref float highState)
    {
        lowState += (sample - lowState) * (.06f * 48000 / sampleRate);
        highState += (sample - highState) * Math.Min(.85f, .42f * 48000 / sampleRate);
        return sample + lowState * (low / 100 - 1) + (sample - highState) * (high / 100 - 1);
    }

    private void Smooth(ref float value, float goal) => value += (goal - value) * smoothing;

    private static float Read(float[] buffer, int position, float delay)
    {
        delay = Math.Clamp(delay, 0, buffer.Length - 1);
        int whole = (int)delay;
        float fraction = delay - whole;
        int a = position - whole;
        if (a < 0) a += buffer.Length;
        int b = a == 0 ? buffer.Length - 1 : a - 1;
        return buffer[a] + (buffer[b] - buffer[a]) * fraction;
    }
}
