using NAudio.Dsp;
namespace FiltrMikrofonu.Audio;

internal sealed class BasicEffects
{
    private readonly int sampleRate;
    private readonly BiQuadFilter[] lowPass = new BiQuadFilter[4];
    private readonly float[] pitchBuffer;
    private readonly int pitchWindow;
    private readonly float smooth;
    private int write;
    private double phase;
    private float cutoff = -1, lowBlend, stages=1, compressionBlend, pitchBlend, semitones;
    private float envelope, gain=1, detectorAttack, detectorRelease;
    private ManualFilterSettings settings = new();
    public BasicEffects(int sampleRate, ManualFilterSettings initial)
    {
        this.sampleRate=sampleRate; smooth=1-MathF.Exp(-1f/(sampleRate*.02f));
        for(int i=0;i<4;i++)lowPass[i]=BiQuadFilter.LowPassFilter(sampleRate,Math.Min(3500,sampleRate*.45f),.707f);
        pitchWindow=sampleRate*65/1000; pitchBuffer=new float[pitchWindow+4];
        Update(initial); gain=FilterSettings.DecibelsToGain(initial.CompressorMakeup); lowBlend=initial.LowPassEnabled?1:0;
        compressionBlend=initial.CompressionEnabled?initial.CompressionMix/100:0;
        pitchBlend=initial.PitchEnabled && Math.Abs(initial.PitchSemitones)>.0001f?initial.PitchMix/100:0; semitones=initial.PitchSemitones;
        stages=Math.Clamp(initial.LowPassSlope/12,1,4);
    }
    public void Update(ManualFilterSettings next)
    {
        settings=next;
        float hz=Math.Clamp(next.LowPassHz,20,Math.Min(20000,sampleRate*.45f));
        if(hz!=cutoff){cutoff=hz;foreach(var filter in lowPass)filter.SetLowPassFilter(sampleRate,hz,.707f);}
        detectorAttack=1-MathF.Exp(-1f/(sampleRate*Math.Clamp(next.CompressorAttack,1,200)/1000));
        detectorRelease=1-MathF.Exp(-1f/(sampleRate*Math.Clamp(next.CompressorRelease,20,2000)/1000));
    }
    public float Process(float input)
    {
        lowBlend+=((settings.LowPassEnabled?1:0)-lowBlend)*smooth;
        stages+=(Math.Clamp(settings.LowPassSlope/12,1,4)-stages)*smooth;
        float filtered=input, low=input;
        for(int i=0;i<4;i++){filtered=lowPass[i].Transform(filtered);low+=(filtered-low)*Math.Clamp(stages-i,0,1);}
        input+=(low-input)*lowBlend;
        float level=MathF.Abs(input);
        envelope+=(level-envelope)*(level>envelope?detectorAttack:detectorRelease);
        float over=Math.Max(0,20*MathF.Log10(Math.Max(envelope,1e-9f))-Math.Clamp(settings.CompressorThreshold,-60,0));
        float db=over*(1/Math.Clamp(settings.CompressorRatio,1,20)-1)+Math.Clamp(settings.CompressorMakeup,-12,24);
        float target=FilterSettings.DecibelsToGain(db);
        gain+=(target-gain)*(target<gain?detectorAttack:detectorRelease);
        compressionBlend+=((settings.CompressionEnabled?Math.Clamp(settings.CompressionMix/100,0,1):0)-compressionBlend)*smooth;
        input+=(input*gain-input)*compressionBlend;
        pitchBuffer[write]=input;
        semitones+=(Math.Clamp(settings.PitchSemitones,-12,12)-semitones)*smooth;
        double second=phase+.5;second-=Math.Floor(second);
        float window=(float)(.5-.5*Math.Cos(Math.Tau*phase));
        float shifted=Read(2+phase*pitchWindow)*window+Read(2+second*pitchWindow)*(1-window);
        phase+=(1-Math.Pow(2,semitones/12.0))/pitchWindow;phase-=Math.Floor(phase);
        write=(write+1)%pitchBuffer.Length;
        pitchBlend+=((settings.PitchEnabled && Math.Abs(settings.PitchSemitones)>.0001f?Math.Clamp(settings.PitchMix/100,0,1):0)-pitchBlend)*smooth;
        return input+(shifted-input)*pitchBlend;
    }
    private float Read(double delay)
    {
        // Floating-point wrapping can round a tiny negative position up to Length.
        // Use double precision and explicitly wrap that upper boundary back to zero.
        double index=(write-delay)%pitchBuffer.Length;
        if(index<0)index+=pitchBuffer.Length;
        if(index>=pitchBuffer.Length)index=0;
        int first=(int)index;
        float mix=(float)(index-first);
        return pitchBuffer[first]+(pitchBuffer[(first+1)%pitchBuffer.Length]-pitchBuffer[first])*mix;
    }
}
