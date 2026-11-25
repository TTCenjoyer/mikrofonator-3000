using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FiltrMikrofonu.Audio;

internal sealed class AudioSession : IDisposable
{
    private readonly float[] scratch = new float[1024];
    private readonly float[] rightScratch = new float[1024];
    private MMDevice? input;
    private WasapiRecorder? recorder;
    private RenderRoute? output;
    private RenderRoute? monitor;
    private SampleFormat? captureFormat;
    private AudioProcessor? processor;
    private FilterSettings settings;
    private string? captureError;
    private float inputPeak, outputPeak;
    private bool disposed;

    private AudioSession(FilterSettings settings) => this.settings = settings;

    public int SampleRate { get; private set; }
    public string? Failure => Volatile.Read(ref captureError) ?? output?.Failure;
    public string? MonitorFailure => Volatile.Read(ref monitor)?.Failure;

    public static AudioSession Start(string microphoneId, string outputId, FilterSettings settings)
    {
        var session = new AudioSession(settings);
        try
        {
            using var devices = new MMDeviceEnumerator();
            session.input = devices.GetDevice(microphoneId);
            var builder = new WasapiRecorderBuilder().WithDevice(session.input)
                .WithBufferLength(24).WithLowLatency().WithMmcssThreadPriority("Pro Audio");
            WaveFormat mix;
            using (var client = session.input.CreateAudioClient())
                mix = client.MixFormat;
            if (mix.SampleRate > 48000)
                builder.WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, mix.Channels));

            session.recorder = builder.Build();
            session.SampleRate = session.recorder.WaveFormat.SampleRate;
            session.captureFormat = new SampleFormat(session.recorder.WaveFormat);
            session.processor = new AudioProcessor(session.SampleRate, settings);
            session.output = RenderRoute.Start(outputId, session.SampleRate);
            session.recorder.DataAvailable += session.OnCapture;
            session.recorder.RecordingStopped += session.OnCaptureStopped;
            session.recorder.StartRecording();
            AudioDiagnostics.Write($"Audio started; sampleRate={session.SampleRate}; input={microphoneId}; output={outputId}");
            return session;
        }
        catch(Exception ex)
        {
            AudioDiagnostics.Write("Audio start failed",ex);
            session.Dispose();
            throw;
        }
    }

    public void SetFilter(FilterSettings next) => Volatile.Write(ref settings, next);

    public void SetMonitor(string? deviceId)
    {
        RenderRoute? next = deviceId is null ? null : RenderRoute.Start(deviceId, SampleRate);
        AudioDiagnostics.Close(Interlocked.Exchange(ref monitor,next),"previous monitor");
    }

    public (float In, float Out) TakePeaks() =>
        (Interlocked.Exchange(ref inputPeak, 0), Interlocked.Exchange(ref outputPeak, 0));

    private void OnCapture(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if(Volatile.Read(ref disposed))return;
        try
        {
        int frameBytes = captureFormat!.FrameBytes;
        int count = data.Length / frameBytes;
        var parameters = Volatile.Read(ref settings);
        for (int offset = 0; offset < count; offset += scratch.Length)
        {
            var block = scratch.AsSpan(0, Math.Min(scratch.Length, count - offset));
            for (int frame = 0; frame < block.Length; frame++)
                block[frame] = captureFormat.ReadMono(data[((offset + frame) * frameBytes)..]);

            var right = rightScratch.AsSpan(0, block.Length);
            processor!.ProcessStereo(block, right, parameters, out float before, out float after);
            SetPeak(ref inputPeak, before);
            SetPeak(ref outputPeak, after);
            output!.Buffer.Write(block, right);
            Volatile.Read(ref monitor)?.Buffer.Write(block, right);
        }
        }
        catch(Exception ex)
        {
            if(Volatile.Read(ref disposed))return;
            if(Interlocked.CompareExchange(ref captureError,ex.ToString(),null) is null)
                AudioDiagnostics.Write("Audio capture/processing callback failed",ex);
        }
    }

    private void OnCaptureStopped(object? sender, StoppedEventArgs args)
    {
        if(Volatile.Read(ref disposed))return;
        string failure=args.Exception?.ToString()??"Urządzenie wejściowe przestało przesyłać dźwięk.";
        Volatile.Write(ref captureError,failure);
        AudioDiagnostics.Write("Capture stopped unexpectedly",args.Exception);
    }

    private static void SetPeak(ref float target, float peak)
    {
        float previous = Volatile.Read(ref target);
        while (peak > previous)
        {
            float observed = Interlocked.CompareExchange(ref target, peak, previous);
            if (observed == previous)
                return;
            previous = observed;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        Volatile.Write(ref disposed,true);
        AudioDiagnostics.Write("Audio session disposing");
        if (recorder is { } capture)
        {
            capture.DataAvailable -= OnCapture;
            capture.RecordingStopped -= OnCaptureStopped;
            AudioDiagnostics.Close(capture,"capture");
        }
        AudioDiagnostics.Close(Interlocked.Exchange(ref monitor,null),"monitor");
        AudioDiagnostics.Close(output,"output");
        AudioDiagnostics.Close(input,"input device");
    }
}

internal sealed class RenderRoute : IDisposable
{
    private readonly MMDevice device;
    private readonly WasapiPlayer player;
    private string? failure;

    private RenderRoute(MMDevice device, WasapiPlayer player, RenderBuffer buffer)
    {
        this.device = device;
        this.player = player;
        Buffer = buffer;
    }

    public RenderBuffer Buffer { get; }
    public string? Failure => Volatile.Read(ref failure);

    public static RenderRoute Start(string id, int sampleRate)
    {
        using var devices = new MMDeviceEnumerator();
        var endpoint = devices.GetDevice(id);
        WasapiPlayer? player = null;
        try
        {
            WaveFormat mix;
            using (var client = endpoint.CreateAudioClient())
                mix = client.MixFormat;
            var buffer = new RenderBuffer(sampleRate, mix);
            player = new WasapiPlayerBuilder().WithDevice(endpoint).WithLatency(32)
                .WithLowLatency().WithMmcssThreadPriority("Pro Audio").Build();
            var route = new RenderRoute(endpoint, player, buffer);
            player.PlaybackStopped += route.OnPlaybackStopped;
            player.Init(buffer);
            player.Play();
            return route;
        }
        catch
        {
            AudioDiagnostics.Close(player,"failed playback start");
            AudioDiagnostics.Close(endpoint,"failed playback device");
            throw;
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs args)
    {
        Volatile.Write(ref failure,args.Exception?.ToString()??"Urządzenie wyjściowe przestało odtwarzać dźwięk.");
        AudioDiagnostics.Write("Playback stopped unexpectedly",args.Exception);
    }

    public void Dispose()
    {
        player.PlaybackStopped -= OnPlaybackStopped;
        AudioDiagnostics.Close(player,"playback");
        AudioDiagnostics.Close(device,"playback device");
    }
}
