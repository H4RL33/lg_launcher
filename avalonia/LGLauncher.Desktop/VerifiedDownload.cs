using System.Security.Cryptography;

namespace LGLauncher;

internal static class VerifiedDownload
{
    internal static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    internal static bool MatchesSha256(byte[] hash, string expected) =>
        String.Equals(Convert.ToHexString(hash), expected, StringComparison.OrdinalIgnoreCase);

    internal static async Task CopyAsync(HttpContent content, Stream output, long expectedLength, string expectedDigest,
        IProgress<int>? progress, CancellationToken token)
    {
        if (content.Headers.ContentLength is long length && length != expectedLength)
            throw new InvalidDataException("The download has an unexpected size.");

        await using var input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        var total = 0L;
        var lastProgress = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            total += read;
            if (total > expectedLength)
                throw new InvalidDataException("The download is larger than expected.");

            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            if (progress == null) continue;
            var percent = (int)(total * 100 / expectedLength);
            if (percent <= lastProgress) continue;
            progress.Report(percent);
            lastProgress = percent;
        }

        if (total != expectedLength || !MatchesSha256(hash.GetHashAndReset(), expectedDigest))
            throw new InvalidDataException("The download did not match the expected size or SHA-256 checksum.");
    }
}
