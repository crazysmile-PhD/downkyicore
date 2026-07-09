using System;
using System.IO;
using System.Text;

namespace DownKyi.Services.Download;

internal static class DownloadFileIntegrity
{
    public static DownloadFileIntegrityResult Check(
        string? file,
        long expectedBytes = 0,
        long receivedBytes = 0,
        long totalBytesToReceive = 0)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            return DownloadFileIntegrityResult.Invalid("File is missing.");
        }

        var path = file;
        if (File.Exists($"{path}.aria2") || File.Exists($"{path}.download"))
        {
            return DownloadFileIntegrityResult.Invalid("Downloaded media file still has an unfinished sidecar.");
        }

        var actualBytes = new FileInfo(path).Length;
        var requiredBytes = expectedBytes > 0 ? expectedBytes : totalBytesToReceive;
        if (actualBytes <= 0 ||
            requiredBytes > 0 && actualBytes < requiredBytes ||
            requiredBytes > 0 && receivedBytes > 0 && receivedBytes < requiredBytes)
        {
            return DownloadFileIntegrityResult.Invalid(
                $"Downloaded media file is incomplete. expectedBytes={requiredBytes}; receivedBytes={receivedBytes}; actualBytes={actualBytes}");
        }

        if (LooksLikeErrorPayload(path))
        {
            return DownloadFileIntegrityResult.Invalid("Downloaded media file looks like an error payload instead of media.");
        }

        return DownloadFileIntegrityResult.Valid();
    }

    public static bool IsUsable(
        string? file,
        long expectedBytes = 0,
        long receivedBytes = 0,
        long totalBytesToReceive = 0)
    {
        return Check(file, expectedBytes, receivedBytes, totalBytesToReceive).IsUsable;
    }

    private static bool LooksLikeErrorPayload(string file)
    {
        try
        {
            var buffer = new byte[256];
            using var stream = File.OpenRead(file);
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                return true;
            }

            var sample = Encoding.UTF8.GetString(buffer, 0, read)
                .TrimStart('\uFEFF', ' ', '\t', '\r', '\n');

            return sample.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
                   sample.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
                   sample.StartsWith("{\"code\"", StringComparison.OrdinalIgnoreCase) ||
                   sample.StartsWith("{\"error\"", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

internal readonly record struct DownloadFileIntegrityResult(bool IsUsable, string? Reason)
{
    public static DownloadFileIntegrityResult Valid()
    {
        return new DownloadFileIntegrityResult(true, null);
    }

    public static DownloadFileIntegrityResult Invalid(string reason)
    {
        return new DownloadFileIntegrityResult(false, reason);
    }
}
