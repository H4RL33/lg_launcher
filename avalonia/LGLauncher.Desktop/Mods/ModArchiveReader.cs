using System.IO.Compression;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;

namespace LGLauncher;

internal static class ModArchiveReader
{
    private const long MaximumExpandedBytes = 512L * 1024 * 1024;
    private const int MaximumEntries = 4096;
    // Offset of the appended ZIP in the SHA-256-pinned ReShade 6.8.0 full add-on download.
    private const int ReShadeZipOffset = 153088;

    internal static Dictionary<string, string> Extract(string archivePath, string destination, ModPackageDescriptor package, CancellationToken token)
    {
        var wanted = package.Files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var written = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        int count = 0;

        if (package.Key.Provider == ModProviderId.ReShade)
        {
            var setup = File.ReadAllBytes(archivePath);
            if (setup.Length <= ReShadeZipOffset || !setup.AsSpan(ReShadeZipOffset).StartsWith("PK\u0003\u0004"u8))
                throw new InvalidDataException("The ReShade download does not contain its reviewed ZIP payload.");
            using var stream = new MemoryStream(setup, ReShadeZipOffset, setup.Length - ReShadeZipOffset, writable: false);
            ReadZip(stream);
        }
        else if (package.Address.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(archivePath);
            ReadZip(stream);
        }
        else if (package.Address.AbsolutePath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = SevenZipArchive.OpenArchive(archivePath);
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                var entry = reader.Entry;
                if (entry.IsEncrypted) throw new InvalidDataException("The package contains an encrypted entry.");
                Read(entry.Key, entry.Size, entry.IsDirectory, entry.Attrib ?? 0, entry.LinkTarget, reader.OpenEntryStream);
            }
        }
        else throw new InvalidDataException("Unsupported mod archive format.");

        if (wanted.Count != written.Count) throw new InvalidDataException("The package layout does not match the reviewed version.");
        return written;

        void ReadZip(Stream stream)
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in zip.Entries)
                Read(entry.FullName, entry.Length, entry.Name.Length == 0, entry.ExternalAttributes, null, entry.Open);
        }

        void Read(string? key, long size, bool directory, int attributes, string? link, Func<Stream> open)
        {
            token.ThrowIfCancellationRequested();
            var unixType = (attributes >> 16) & 0xF000;
            if (key == null || link != null || (attributes & (int)FileAttributes.ReparsePoint) != 0 ||
                unixType is not (0 or 0x4000 or 0x8000))
                throw new InvalidDataException("The archive contains an unsupported entry type.");
            var relative = ModPaths.Relative(directory ? key.TrimEnd('/', '\\') : key);
            if (!entries.Add(relative) || ++count > MaximumEntries || size < 0 || size > MaximumExpandedBytes - expanded)
                throw new InvalidDataException("The archive contains duplicate or oversized entries.");
            expanded += size;
            if (directory || !wanted.TryGetValue(relative, out var member)) return;

            var target = ModPaths.Within(destination, member);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            long copied = 0;
            int read;
            while ((read = input.Read(buffer)) != 0)
            {
                token.ThrowIfCancellationRequested();
                copied += read;
                if (copied > size) throw new InvalidDataException("An archive entry is larger than declared.");
                output.Write(buffer, 0, read);
                hash.AppendData(buffer, 0, read);
            }
            if (copied != size) throw new InvalidDataException("An archive entry is incomplete.");
            output.Flush(true);
            written.Add(member, Convert.ToHexString(hash.GetHashAndReset()));
        }
    }
}
