using FiltrMikrofonu.Audio;
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
