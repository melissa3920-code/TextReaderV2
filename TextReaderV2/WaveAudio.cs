using System.Buffers.Binary;
using System.IO;

namespace TextReader;

internal static class WaveAudio
{
    internal const int DefaultLeadInMilliseconds = 100;

    internal static byte[] AddPlaybackLeadIn(byte[] wave, int leadInMilliseconds = DefaultLeadInMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(leadInMilliseconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(leadInMilliseconds, 1000);
        if (wave.Length < 12 || !wave.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !wave.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("The speech server did not return a WAV file.");

        int format = 0, bits = 0, blockAlign = 0, sampleRate = 0;
        List<int> factOffsets = new();
        for (int offset = 12; offset <= wave.Length - 8;)
        {
            uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(offset + 4, 4));
            int payload = offset + 8;
            bool isData = wave.AsSpan(offset, 4).SequenceEqual("data"u8);
            // Streaming WAV encoders can leave RIFF/data lengths unspecified, even in a buffered response.
            long length = isData && chunkLength == uint.MaxValue ? wave.Length - payload : chunkLength;
            if (length > wave.Length - payload)
                throw new InvalidDataException("The speech WAV file is incomplete.");

            if (wave.AsSpan(offset, 4).SequenceEqual("fmt "u8) && length >= 16)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(wave.AsSpan(payload, 2));
                uint rate = BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(payload + 4, 4));
                if (rate > int.MaxValue) throw new InvalidDataException("Invalid WAV sample rate.");
                sampleRate = (int)rate;
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(wave.AsSpan(payload + 12, 2));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(wave.AsSpan(payload + 14, 2));
                if (format == 0xfffe && length >= 40)
                    format = BinaryPrimitives.ReadUInt16LittleEndian(wave.AsSpan(payload + 24, 2));
            }
            else if (wave.AsSpan(offset, 4).SequenceEqual("fact"u8) && length >= 4)
                factOffsets.Add(payload);

            if (isData)
            {
                if (sampleRate <= 0 || blockAlign <= 0 || length % blockAlign != 0)
                    throw new InvalidDataException("Invalid WAV audio format.");
                if (format != 1 && format != 3) return wave;
                long silenceFrames = ((long)sampleRate * leadInMilliseconds / 1000 + 1) & ~1L;
                int silenceLength = checked((int)(silenceFrames * blockAlign));
                byte[] result = new byte[checked(wave.Length + silenceLength)];
                wave.AsSpan(0, payload).CopyTo(result);
                if (format == 1 && bits == 8)
                    result.AsSpan(payload, silenceLength).Fill(128);
                wave.AsSpan(payload).CopyTo(result.AsSpan(payload + silenceLength));
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), checked((uint)(result.Length - 8)));
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset + 4, 4), checked((uint)(length + silenceLength)));
                foreach (int fact in factOffsets)
                    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(fact, 4), checked((uint)((length + silenceLength) / blockAlign)));
                return result;
            }
            long next = payload + length + (length & 1);
            if (next > wave.Length) break;
            offset = (int)next;
        }
        throw new InvalidDataException("The speech WAV file has no audio data.");
    }
}
