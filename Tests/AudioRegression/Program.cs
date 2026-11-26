using FiltrMikrofonu.Audio;
if(args.Contains("--reference-smoke"))
{
    var headphones=AudioDevices.Read().Outputs.First(device=>device.CablePair is null && device.Name.Contains("MAONO AU-AM200",StringComparison.OrdinalIgnoreCase));
    using var capture=new PlaybackReferenceCapture(headphones.Id);
    await Task.Delay(1000);
    if(capture.Failure is not null)throw new Exception(capture.Failure);
    Console.WriteLine("PASS read-only playback reference initialized: "+headphones.Name);
    return;
}
const int rate=48000;
void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS "+message);}
void StablePitch(int sampleRate,ManualFilterSettings settings,int seconds)
{
    var effects=new BasicEffects(sampleRate,settings);
    for(int i=0;i<sampleRate*seconds;i++)
    {
        float output=effects.Process(.01f*MathF.Sin(i*.13f));
        if(!float.IsFinite(output))throw new Exception($"Nonfinite pitch output at sample {i}");
    }
}
StablePitch(rate,new(),120);
Check(true,"disabled pitch remains stable for 120 seconds (old code crashed at 12.777 seconds)");
foreach(float pitch in new[]{-12f,-5,-.01f,0,.01f,5,12})StablePitch(rate,new(PitchEnabled:true,PitchSemitones:pitch),30);
foreach(int sampleRate in new[]{16000,44100,96000})
    foreach(float pitch in new[]{-12f,-5,12})StablePitch(sampleRate,new(PitchEnabled:true,PitchSemitones:pitch),15);
Check(true,"pitch remains finite across positive/negative shifts and sample rates");
var changing=new BasicEffects(rate,new());
for(int block=0;block<600;block++)
{
    changing.Update(new(PitchEnabled:block%2==0,PitchSemitones:block%25-12));
    for(int i=0;i<rate/10;i++)if(!float.IsFinite(changing.Process(.01f)))throw new Exception("Nonfinite output during toggle");
}
Check(true,"pitch remains stable through rapid live parameter changes");
var normalizer=new ManualFilters(rate,new(NormalizerEnabled:true,MaxGainDb:12));
foreach(float amplitude in new[]{.02f,.006f,.002f,.0002f})
{
    double inPower=0,outPower=0;
    for(int i=0;i<rate*6;i++)
    {
        float input=amplitude*MathF.Sin(2*MathF.PI*1000*i/rate);
        float output=normalizer.Normalize(input);
        if(i>=rate*5){inPower+=input*input;outPower+=output*output;}
    }
    double boost=10*Math.Log10(outPower/inPower);
    Check(boost>11.8 && boost<12.05,$"quiet voice amplitude {amplitude} keeps its 12 dB boost");
}
for(int i=0;i<rate*3;i++)CheckSilent(normalizer.Normalize(0));
void CheckSilent(float value){if(value!=0||!float.IsFinite(value))throw new Exception("Silence became nonzero/nonfinite");}
Check(true,"normalizer leaves silence silent");
var parameters=new FilterSettings(VoiceFilter.Clean,1,1,1,Manual:new(NormalizerEnabled:true),Noise:new());
var processor=new AudioProcessor(rate,parameters);var left=new float[480];var right=new float[480];
for(int block=0;block<3000;block++)
{
    for(int i=0;i<left.Length;i++)left[i]=.002f*MathF.Sin((block*480+i)*.13f);
    processor.ProcessStereo(left,right,parameters,out _,out _);
    if(left.Any(x=>!float.IsFinite(x))||right.Any(x=>!float.IsFinite(x)))throw new Exception("Full audio chain nonfinite");
}
Check(true,"full audio chain with quiet voice runs 30 seconds without failure");

var random=new Random(731);
using(var ai=new NeuralNoiseReducer(rate))
{
    var noiseSettings=new NoiseSettings(DenoiseEnabled:true,NeuralEnabled:true,NeuralStrength:100,AutomaticVoiceGate:true);
    double before=0,after=0;
    var inputHistory=new float[rate*5];var outputHistory=new float[rate*5];
    for(int i=0;i<rate*5;i++)
    {
        float input=(float)(random.NextDouble()*2-1)*.025f;
        float value=ai.Process(input,noiseSettings,rate);
        inputHistory[i]=input;outputHistory[i]=value;
        if(!float.IsFinite(value))throw new Exception("RNNoise produced nonfinite audio");
        if(i>=rate*4){before+=input*input;after+=value*value;}
    }
    Check(ai.Failure is null,"bundled RNNoise native library loads and processes frames");
    double reduction=10*Math.Log10(Math.Max(after,1e-30)/before);
    Console.WriteLine($"Discord-style RNNoise + automatic sensitivity noise reduction: {reduction:0.0} dB");
    Check(reduction< -10,"Discord-style neural profile attenuates synthetic broadband noise");
    double Correlation(int lag){double value=0;for(int i=rate*4;i<rate*5;i+=8)value+=outputHistory[i]*inputHistory[i-lag];return value;}
    int neuralLag=Enumerable.Range(600,601).MaxBy(Correlation);
    Console.WriteLine($"Measured RNNoise streaming delay: {neuralLag} samples");
    Check(Math.Abs(neuralLag-960)<=1,"neural wet output and dry bypass are time-aligned");
}
// Bypass must have the same fixed latency and retain floating-point precision.
using(var ai=new NeuralNoiseReducer(rate))
{
    for(int i=0;i<2400;i++)
    {
        float actual=ai.Process(i==0?.001234567f:0,new(),rate);
        if(Math.Abs(actual-(i==960?.001234567f:0))>1e-9)throw new Exception("Neural bypass changed delay or amplitude");
    }
    Check(true,"neural bypass keeps aligned dry audio without quantization");
}
const long epoch=1000000000;
var referenceBuffer=new PlaybackReferenceBuffer();
foreach(int echoDelay in new[]{0,20})
using(var echo=new EchoCancellationProcessor(rate))
{
    int total=rate*10;var far=new float[total];var near=new float[total];
    for(int i=0;i<total;i++)
    {
        far[i]=(float)(random.NextDouble()*2-1)*.15f;
        near[i]=i>=rate*7?(float)(.06*Math.Sin(2*Math.PI*177*i/rate)+.03*Math.Sin(2*Math.PI*354*i/rate)):0;
    }
    var frame=new float[480];double residual=0,rawPower=0,nearOut=0,nearPower=0;
    var aec=new NoiseSettings(EchoCancellationEnabled:true,EchoDelayMs:echoDelay);
    for(int offset=0;offset<total;offset+=480)
    {
        referenceBuffer.Write(far.AsSpan(offset,480),rate,epoch+(long)(offset*10000000.0/rate));
        for(int i=0;i<480;i++)frame[i]=(offset+i>=1440?.4f*far[offset+i-1440]:0)+near[offset+i];
        echo.Process(frame,epoch+(long)(offset*10000000.0/rate),aec,referenceBuffer);
        for(int i=0;i<480;i++)
        {
            if(!float.IsFinite(frame[i]))throw new Exception("AEC produced nonfinite audio");
            if(offset>=rate*5 && offset<rate*7){residual+=frame[i]*frame[i];float raw=.4f*far[offset+i-1920];rawPower+=raw*raw;}
            if(offset>=rate*8){float own=near[offset+i-480];nearOut+=frame[i]*own;nearPower+=own*own;}
        }
    }
    Check(echo.Failure is null,"bundled echo cancellation library loads and processes frames");
    double echoDb=10*Math.Log10(residual/rawPower);
    Console.WriteLine($"Synthetic delayed echo (reference offset {echoDelay} ms) residual: {echoDb:0.0} dB; own-voice correlation gain: {nearOut/nearPower:0.00}");
    Check(echoDb< -15,"AEC attenuates synthetic delayed far-end leakage");
    Check(nearOut/nearPower>.6,"AEC retains independent near-end voice during double-talk");
}
var missing=new float[480];referenceBuffer.Read(missing,rate,epoch+100000000);
Check(missing.All(x=>x==0),"missing/stale playback reference becomes silence instead of old speech");
