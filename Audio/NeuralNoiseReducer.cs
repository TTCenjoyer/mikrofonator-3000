using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FiltrMikrofonu.Audio;

internal sealed class NeuralNoiseReducer : IDisposable
{
    private const int FrameSize=480, Delay=960;
    private readonly float[] input=new float[FrameSize], output=new float[FrameSize], dry=new float[Delay];
    private readonly float smoothing;
    private RnState? state;
    private bool unavailable;
    private int position,dryPosition;
    private float mix,voiceProbability,voiceGain=1;
    private int voiceHold;
    internal float VoiceProbability=>voiceProbability;
    internal string? Failure { get; private set; }
    internal NeuralNoiseReducer(int rate)=>smoothing=1-MathF.Exp(-1f/(rate*.03f));
    internal float Process(float sample,NoiseSettings settings,int rate)
    {
        float delayed=dry[dryPosition];dry[dryPosition]=sample;dryPosition=(dryPosition+1)%Delay;
        float wet=output[position]/32768f;
        input[position]=(float.IsFinite(sample)?Math.Clamp(sample,-1,1):0)*32768f;
        bool enabled=rate==48000 && settings.DenoiseEnabled && settings.NeuralEnabled;
        mix+=((enabled && state is not null ? Math.Clamp(settings.NeuralStrength/100,0,1):0)-mix)*smoothing;
        float result=delayed+(wet-delayed)*mix;
        if(++position==FrameSize)
        {
            position=0;
            if(enabled && state is null && !unavailable)
            {
                try {state=Create(nint.Zero);if(state.IsInvalid)throw new InvalidOperationException("RNNoise nie utworzył stanu.");}
                catch(Exception ex) when(ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or InvalidOperationException)
                {Failure=ex.Message;unavailable=true;AudioDiagnostics.Write("RNNoise initialization failed; legacy suppression remains available",ex);}
            }
            if(state is not null)voiceProbability=ProcessFrame(state,output,input);
            else input.CopyTo(output,0);
        }
        if(enabled && settings.AutomaticVoiceGate && state is not null)
        {
            if(voiceProbability>=.35f)voiceHold=rate*300/1000;
            else if(voiceHold>0)voiceHold--;
            float target=voiceHold>0?1:.08f;
            voiceGain+=(target-voiceGain)*(target>voiceGain?.02f:.00015f);
        }
        else voiceGain+=(1-voiceGain)*smoothing;
        return result*(1+(voiceGain-1)*mix);
    }
    public void Dispose(){state?.Dispose();state=null;}
    private sealed class RnState : SafeHandleZeroOrMinusOneIsInvalid
    {
        public RnState():base(true){}
        protected override bool ReleaseHandle(){Destroy(handle);return true;}
    }
    [DllImport("rnnoise",EntryPoint="rnnoise_create",CallingConvention=CallingConvention.Cdecl)]private static extern RnState Create(nint model);
    [DllImport("rnnoise",EntryPoint="rnnoise_destroy",CallingConvention=CallingConvention.Cdecl)]private static extern void Destroy(nint state);
    [DllImport("rnnoise",EntryPoint="rnnoise_process_frame",CallingConvention=CallingConvention.Cdecl)]private static extern float ProcessFrame(RnState state,[Out]float[] output,[In]float[] input);
}
