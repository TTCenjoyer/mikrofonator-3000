using NAudio.CoreAudioApi;
using NAudio.Wave;
namespace FiltrMikrofonu.Audio;

internal sealed class PlaybackReferenceCapture : IDisposable
{
    private readonly MMDevice device;
    private readonly WasapiRecorder recorder;
    private readonly SampleFormat format;
    private readonly int rate;
    private readonly float[] scratch=new float[4096];
    private bool disposed;
    internal PlaybackReferenceBuffer Buffer { get; }=new();
    internal string DeviceId { get; }
    internal string? Failure { get; private set; }
    internal PlaybackReferenceCapture(string id)
    {
        DeviceId=id;
        using var devices=new MMDeviceEnumerator();device=devices.GetDevice(id);
        try
        {
            recorder=new WasapiRecorderBuilder().WithDevice(device).WithLoopbackCapture().WithBufferLength(20).WithMmcssThreadPriority("Pro Audio").Build();
            format=new SampleFormat(recorder.WaveFormat);rate=recorder.WaveFormat.SampleRate;
            recorder.DataAvailable+=OnData;recorder.RecordingStopped+=OnStopped;recorder.StartRecording();
        }
        catch{AudioDiagnostics.Close(recorder,"failed echo reference recorder");AudioDiagnostics.Close(device,"echo reference device");throw;}
    }
    private void OnData(ReadOnlySpan<byte> data,AudioClientBufferFlags flags,long devicePosition,long qpc)
    {
        if(disposed)return;
        try
        {
            int frames=data.Length/format.FrameBytes;
            for(int offset=0;offset<frames;offset+=scratch.Length)
            {
                int length=Math.Min(scratch.Length,frames-offset);
                for(int i=0;i<length;i++)scratch[i]=(flags&AudioClientBufferFlags.Silent)!=0?0:format.ReadMono(data[((offset+i)*format.FrameBytes)..]);
                Buffer.Write(scratch.AsSpan(0,length),rate,qpc+(long)(offset*10000000.0/rate));
            }
        }
        catch(Exception ex){Failure=ex.Message;AudioDiagnostics.Write("Echo reference capture failed",ex);}
    }
    private void OnStopped(object? sender,StoppedEventArgs args)
    {if(!disposed){Failure=args.Exception?.Message??"Odczyt dźwięku słuchawek został zatrzymany.";AudioDiagnostics.Write("Echo reference stopped unexpectedly",args.Exception);}}
    public void Dispose()
    {
        disposed=true;recorder.DataAvailable-=OnData;recorder.RecordingStopped-=OnStopped;
        AudioDiagnostics.Close(recorder,"echo reference recorder");AudioDiagnostics.Close(device,"echo reference device");
    }
}
