namespace FiltrMikrofonu.Audio;
internal readonly record struct StereoFrame(float Left, float Right)
{
    public static StereoFrame operator +(StereoFrame a, StereoFrame b) => new(a.Left + b.Left, a.Right + b.Right);
    public static StereoFrame operator -(StereoFrame a, StereoFrame b) => new(a.Left - b.Left, a.Right - b.Right);
    public static StereoFrame operator *(StereoFrame a, float gain) => new(a.Left * gain, a.Right * gain);
}
