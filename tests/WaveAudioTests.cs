using System.Buffers.Binary;
using System.Text;
using TextReader;

internal static class WaveAudioTests
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (var (format, bits, channels, streaming) in new[] {
            (1,16,1,false), (1,16,2,false), (1,8,1,false), (3,32,2,false), (1,16,1,true) })
        {
            byte[] original = CreateWave(format, bits, channels, streaming);
            byte[] padded = WaveAudio.AddPlaybackLeadIn(original);
            int start = DataStart(original), align = channels * bits / 8;
            int lead = 24000 * WaveAudio.LeadInMilliseconds / 1000 * align;
            check(padded.AsSpan(start + lead).SequenceEqual(original.AsSpan(start)),
                $"First and remaining speech samples preserved ({format}/{bits}/{channels}, streaming={streaming})");
            byte silence = bits == 8 ? (byte)128 : (byte)0;
            check(padded.AsSpan(start, lead).IndexOfAnyExcept(silence) == -1,
                "Lead-in contains format-correct silence");
            check(BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(4,4)) == padded.Length - 8 &&
                BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(start-4,4)) == 4 * align + lead,
                "WAV file and data lengths describe the padded audio");
            if (format == 3)
                check(BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(44,4)) == 4 + lead / align,
                    "Float WAV sample count includes lead-in");
        }
        byte[] truncated = CreateWave(1,16,1,false)[..^1];
        try { WaveAudio.AddPlaybackLeadIn(truncated); throw new Exception("Truncated WAV accepted"); }
        catch (InvalidDataException) { Console.WriteLine("PASS: Truncated audio rejected"); }
        try { WaveAudio.AddPlaybackLeadIn("not a WAV file"u8.ToArray()); throw new Exception("Invalid WAV accepted"); }
        catch (InvalidDataException) { Console.WriteLine("PASS: Non-WAV response rejected"); }
    }

    internal static int DataStart(byte[] wave)
    {
        for (int p=12; p+8<=wave.Length;)
        {
            if (wave.AsSpan(p,4).SequenceEqual("data"u8)) return p+8;
            int n=checked((int)BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(p+4,4)));
            p+=8+n+(n&1);
        }
        throw new Exception("No data chunk");
    }

    private static byte[] CreateWave(int format,int bits,int channels,bool streaming)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream,Encoding.ASCII,true);
        void Tag(string value) => w.Write(Encoding.ASCII.GetBytes(value));
        int align=channels*bits/8;
        Tag("RIFF");w.Write(0u);Tag("WAVE");Tag("fmt ");w.Write(16u);
        w.Write((ushort)format);w.Write((ushort)channels);w.Write(24000u);
        w.Write((uint)(24000*align));w.Write((ushort)align);w.Write((ushort)bits);
        if (format==3) { Tag("fact");w.Write(4u);w.Write(4u); }
        Tag("JUNK");w.Write(1u);w.Write((byte)7);w.Write((byte)0);
        Tag("data");w.Write(streaming?uint.MaxValue:(uint)(4*align));
        for(int i=0;i<4*align;i++)w.Write((byte)(i+1));
        long end=stream.Position;stream.Position=4;w.Write(streaming?uint.MaxValue:(uint)(end-8));
        return stream.ToArray();
    }
}
