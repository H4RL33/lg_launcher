using System.Buffers.Binary;
using Concentus;
using Concentus.Oggfile;
using NVorbis;

namespace LGLauncher;

internal static class ThemeAudioImport
{
    private const int OpusSampleRate = 48000;
    private const int MaximumPcmBytes = 256 * 1024 * 1024;

    internal static string StorageExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => ".mp3",
        ".wav" or ".ogg" or ".opus" => ".wav",
        _ => throw new InvalidDataException("Choose an MP3, OGG, Opus, or WAV audio file.")
    };

    internal static bool NeedsDecoding(string path) => Path.GetExtension(path).ToLowerInvariant() is ".ogg" or ".opus";

    internal static void DecodeOgg(string source, string destination, CancellationToken token)
    {
        using var input = File.OpenRead(source);
        Span<byte> ogg = stackalloc byte[27];
        input.ReadExactly(ogg);
        if (!ogg[..4].SequenceEqual("OggS"u8) || ogg[26] == 0)
            throw new InvalidDataException("The Ogg audio header is missing.");
        input.Position += ogg[26];
        Span<byte> codec = stackalloc byte[19];
        input.ReadExactly(codec);
        input.Position = 0;
        if (codec[..8].SequenceEqual("OpusHead"u8))
            DecodeOpus(input, destination, codec[9], BinaryPrimitives.ReadUInt16LittleEndian(codec[10..]),
                BinaryPrimitives.ReadInt16LittleEndian(codec[16..]), token);
        else if (codec[0] == 1 && codec[1..7].SequenceEqual("vorbis"u8))
            DecodeVorbis(input, destination, token);
        else
            throw new InvalidDataException("Choose an Ogg Vorbis or Ogg Opus track.");
    }

    private static void DecodeVorbis(Stream input, string destination, CancellationToken token)
    {
        using var reader = new VorbisReader(input, closeOnDispose: false);
        var channels = reader.Channels;
        var sampleRate = reader.SampleRate;
        if (channels is < 1 or > 2 || sampleRate is < 8000 or > 192000)
            throw new InvalidDataException("Choose a mono or stereo track with a supported sample rate.");
        var frames = reader.TotalSamples;
        if (frames <= 0 || frames > MaximumPcmBytes / (channels * sizeof(short)))
            throw new InvalidDataException("The track has an unsupported decoded length.");

        using var wave = StartWave(destination, sampleRate, channels);
        var buffer = new float[8192];
        var remaining = frames * channels;
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            var count = reader.ReadSamples(buffer, 0, (int)Math.Min(remaining, buffer.Length));
            if (count == 0) break;
            for (var sample = 0; sample < count; sample++)
                wave.Write((short)Math.Round(Math.Clamp(buffer[sample], -1f, 1f) * Int16.MaxValue));
            remaining -= count;
        }
        FinishWave(wave);
    }

    private static void DecodeOpus(Stream input, string destination, int channels, int preSkip, short gainInQ8, CancellationToken token)
    {
        if (channels is < 1 or > 2) throw new InvalidDataException("Choose a mono or stereo Opus track.");
        using var decoder = OpusCodecFactory.CreateDecoder(OpusSampleRate, channels);
        var reader = new OpusOggReadStream(decoder, input);
        try
        {
            // The last Ogg granule identifies the end before encoder padding.
            var frames = reader.GranuleCount - preSkip;
            if (frames <= 0 || frames > MaximumPcmBytes / (channels * sizeof(short)))
                throw new InvalidDataException("The track has an unsupported decoded length.");
            var remaining = frames * channels;
            var skip = preSkip * channels;
            var gain = Math.Pow(10, gainInQ8 / (20 * 256d));
            using var wave = StartWave(destination, OpusSampleRate, channels);
            while (reader.HasNextPacket && remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                var packet = reader.DecodeNextPacket() ?? throw new InvalidDataException("An Opus audio packet could not be decoded.");
                var start = Math.Min(skip, packet.Length);
                skip -= start;
                var count = (int)Math.Min(remaining, packet.Length - start);
                for (var sample = start; sample < start + count; sample++)
                    wave.Write((short)Math.Round(Math.Clamp(packet[sample] * gain, Int16.MinValue, Int16.MaxValue)));
                remaining -= count;
            }
            FinishWave(wave);
        }
        finally { reader.Close(); }
    }

    private static BinaryWriter StartWave(string path, int sampleRate, int channels)
    {
        var wave = new BinaryWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write));
        var blockSize = (short)(channels * sizeof(short));
        try
        {
            wave.Write("RIFF"u8);
            wave.Write(0);
            wave.Write("WAVEfmt "u8);
            wave.Write(16);
            wave.Write((short)1);
            wave.Write((short)channels);
            wave.Write(sampleRate);
            wave.Write(sampleRate * blockSize);
            wave.Write(blockSize);
            wave.Write((short)16);
            wave.Write("data"u8);
            wave.Write(0);
            return wave;
        }
        catch
        {
            wave.Dispose();
            throw;
        }
    }

    private static void FinishWave(BinaryWriter wave)
    {
        var length = (int)wave.BaseStream.Position;
        if (length <= 44) throw new InvalidDataException("The track contains no audio samples.");
        wave.BaseStream.Position = 4;
        wave.Write(length - 8);
        wave.BaseStream.Position = 40;
        wave.Write(length - 44);
    }
}
