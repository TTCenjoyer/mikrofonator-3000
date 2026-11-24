using System.Buffers.Binary;
using NAudio.Wave;

namespace FiltrMikrofonu.Audio;

internal sealed class SampleFormat
{
    private enum Encoding { Pcm8, Pcm16, Pcm24, Pcm32, Float32, Float64 }

    private readonly Encoding encoding;
    private readonly int bytesPerSample;
    private readonly int channels;

    public SampleFormat(WaveFormat format)
    {
        WaveFormat standard = format.AsStandardWaveFormat();
        encoding = (standard.Encoding, standard.BitsPerSample) switch
        {
            (WaveFormatEncoding.Pcm, 8) => Encoding.Pcm8,
            (WaveFormatEncoding.Pcm, 16) => Encoding.Pcm16,
            (WaveFormatEncoding.Pcm, 24) => Encoding.Pcm24,
            (WaveFormatEncoding.Pcm, 32) => Encoding.Pcm32,
            (WaveFormatEncoding.IeeeFloat, 32) => Encoding.Float32,
            (WaveFormatEncoding.IeeeFloat, 64) => Encoding.Float64,
            _ => throw new NotSupportedException($"Format audio {standard.Encoding} / {standard.BitsPerSample} bitów jest niedostępny.")
        };
        bytesPerSample = format.BitsPerSample / 8;
        channels = format.Channels;
        FrameBytes = format.BlockAlign;
    }

    public int FrameBytes { get; }

    public float ReadMono(ReadOnlySpan<byte> frame)
    {
        float sum = 0;
        for (int channel = 0; channel < channels; channel++)
        {
            ReadOnlySpan<byte> data = frame[(channel * bytesPerSample)..];
            sum += encoding switch
            {
                Encoding.Pcm8 => (data[0] - 128) / 128f,
                Encoding.Pcm16 => BinaryPrimitives.ReadInt16LittleEndian(data) / 32768f,
                Encoding.Pcm24 => ((data[0] << 8) | (data[1] << 16) | (data[2] << 24)) / 2147483648f,
                Encoding.Pcm32 => BinaryPrimitives.ReadInt32LittleEndian(data) / 2147483648f,
                Encoding.Float32 => BinaryPrimitives.ReadSingleLittleEndian(data),
                _ => (float)BinaryPrimitives.ReadDoubleLittleEndian(data)
            };
        }
        return float.IsFinite(sum) ? Math.Clamp(sum / channels, -2, 2) : 0;
    }

    public void Clear(Span<byte> samples)
    {
        if (encoding == Encoding.Pcm8)
            samples.Fill(128);
        else
            samples.Clear();
    }

    public void WriteMono(Span<byte> frame, float value) => WriteStereo(frame, new StereoFrame(value, value));

    public void WriteStereo(Span<byte> frame, StereoFrame value)
    {
        if (channels == 1)
        {
            WriteSample(frame, (value.Left + value.Right) * .5f);
            return;
        }
        WriteSample(frame, value.Left);
        WriteSample(frame[bytesPerSample..], value.Right);
    }

    private void WriteSample(Span<byte> frame, float value)
    {
        switch (encoding)
        {
            case Encoding.Pcm8:
                frame[0] = (byte)Math.Clamp((int)MathF.Round(128 + value * 127), 0, 255);
                break;
            case Encoding.Pcm16:
                BinaryPrimitives.WriteInt16LittleEndian(frame, (short)(value * 32767));
                break;
            case Encoding.Pcm24:
                int value24 = (int)(value * 8388607);
                frame[0] = (byte)value24;
                frame[1] = (byte)(value24 >> 8);
                frame[2] = (byte)(value24 >> 16);
                break;
            case Encoding.Pcm32:
                BinaryPrimitives.WriteInt32LittleEndian(frame, (int)(value * 2147483647.0));
                break;
            case Encoding.Float32:
                BinaryPrimitives.WriteSingleLittleEndian(frame, value);
                break;
            case Encoding.Float64:
                BinaryPrimitives.WriteDoubleLittleEndian(frame, value);
                break;
        }
    }
}
