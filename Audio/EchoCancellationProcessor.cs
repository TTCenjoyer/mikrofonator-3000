using SpeexDSPSharp.Core;
namespace FiltrMikrofonu.Audio;

// Timestamped, bounded reference audio: no file recording and no replay across gaps.
internal sealed class PlaybackReferenceBuffer
{
    private const int Rate=48000, Capacity=131072;
    private readonly float[] audio=new float[Capacity];
    private readonly long[] stamps=Enumerable.Repeat(long.MinValue,Capacity).ToArray();
    private readonly object mutex=new();
    internal void Write(ReadOnlySpan<float> samples,int sourceRate,long qpc)
    {
        if(qpc<=0 || samples.IsEmpty)return;
        long first=(long)Math.Ceiling(qpc*(Rate/10000000.0));
        long end=(long)Math.Ceiling((qpc+samples.Length*10000000.0/sourceRate)*(Rate/10000000.0));
        lock(mutex)
        {
            for(long tick=first;tick<end;tick++)
            {
                double source=(tick*(10000000.0/Rate)-qpc)*sourceRate/10000000.0;
                int a=Math.Clamp((int)source,0,samples.Length-1),b=Math.Min(a+1,samples.Length-1);
                float value=samples[a]+(samples[b]-samples[a])*(float)Math.Clamp(source-a,0,1);
                int index=(int)(tick&(Capacity-1));audio[index]=value;stamps[index]=tick;
            }
        }
    }
    internal int Read(Span<float> destination,int rate,long qpc)
    {
        int available=0;
        lock(mutex)
        {
            for(int i=0;i<destination.Length;i++)
            {
                long stamp=(long)Math.Round((qpc+i*10000000.0/rate)*(Rate/10000000.0));
                int index=(int)(stamp&(Capacity-1));
                if(stamps[index]==stamp){destination[i]=audio[index];available++;}else destination[i]=0;
            }
        }
        return available;
    }
}

internal sealed class EchoCancellationProcessor : IDisposable
{
    private readonly int rate,size;
    private readonly float[] dry,output,reference;
    private readonly short[] capturePcm,referencePcm,cleanPcm;
    private SpeexDSPEchoCanceler? canceller;
    private int position;
    private long frameTime;
    private bool failed;
    internal string? Failure { get; private set; }
    internal EchoCancellationProcessor(int rate)
    {
        this.rate=rate;size=rate/100;
        dry=new float[size];output=new float[size];reference=new float[size];
        capturePcm=new short[size];referencePcm=new short[size];cleanPcm=new short[size];
    }
    internal void Process(Span<float> samples,long qpc,NoiseSettings settings,PlaybackReferenceBuffer? playback)
    {
        for(int i=0;i<samples.Length;i++)
        {
            if(position==0)frameTime=qpc+(long)(i*10000000.0/rate);
            float sample=samples[i];samples[i]=output[position];dry[position]=sample;
            if(++position!=size)continue;
            position=0;
            int available=playback?.Read(reference,rate,frameTime-(long)(Math.Clamp(settings.EchoDelayMs,0,250)*10000))??0;
            if(!settings.EchoCancellationEnabled || playback is null || available<size/2 || failed)
            {dry.CopyTo(output,0);continue;}
            try
            {
                if(canceller is null)
                {
                    canceller=new SpeexDSPEchoCanceler(size,rate/4,false);
                    int samplingRate=rate;canceller.Ctl(EchoCancellationCtl.SPEEX_ECHO_SET_SAMPLING_RATE,ref samplingRate);
                }
                for(int j=0;j<size;j++)
                {
                    capturePcm[j]=(short)Math.Clamp(MathF.Round(dry[j]*32768),short.MinValue,short.MaxValue);
                    referencePcm[j]=(short)Math.Clamp(MathF.Round(reference[j]*32768),short.MinValue,short.MaxValue);
                }
                canceller.EchoCancel(capturePcm,referencePcm,cleanPcm);
                for(int j=0;j<size;j++)output[j]=cleanPcm[j]/32768f;
            }
            catch(Exception ex)
            {
                Failure=ex.Message;failed=true;dry.CopyTo(output,0);
                AudioDiagnostics.Write("Echo cancellation failed; microphone continues without AEC",ex);
            }
        }
    }
    public void Dispose(){canceller?.Dispose();canceller=null;}
}

