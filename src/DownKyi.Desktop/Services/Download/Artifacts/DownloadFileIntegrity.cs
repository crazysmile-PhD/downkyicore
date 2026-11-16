using System;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

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
        if (File.Exists($"{path}.download"))
        {
            return DownloadFileIntegrityResult.Invalid("Downloaded media file still has an unfinished sidecar.");
        }

        var actualBytes = new FileInfo(path).Length;
        var requiredBytes = expectedBytes > 0 ? expectedBytes : totalBytesToReceive;
        if (actualBytes <= 0 ||
            requiredBytes > 0 && actualBytes != requiredBytes ||
            requiredBytes > 0 && receivedBytes > 0 && receivedBytes != requiredBytes)
        {
            return DownloadFileIntegrityResult.Invalid(
                $"Downloaded media file length does not match the server. expectedBytes={requiredBytes}; receivedBytes={receivedBytes}; actualBytes={actualBytes}");
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

    public static DownloadFileIntegrityResult CheckXml(string? file, XName expectedRoot)
    {
        ArgumentNullException.ThrowIfNull(expectedRoot);
        var basicIntegrity = Check(file);
        if (!basicIntegrity.IsUsable)
        {
            return basicIntegrity;
        }

        try
        {
            using var stream = File.OpenRead(file!);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            var document = XDocument.Load(reader, LoadOptions.None);
            return document.Root?.Name == expectedRoot
                ? DownloadFileIntegrityResult.Valid()
                : DownloadFileIntegrityResult.Invalid("XML file has an unexpected root element.");
        }
        catch (XmlException)
        {
            return DownloadFileIntegrityResult.Invalid("XML file is malformed.");
        }
        catch (IOException)
        {
            return DownloadFileIntegrityResult.Invalid("XML file could not be read.");
        }
        catch (UnauthorizedAccessException)
        {
            return DownloadFileIntegrityResult.Invalid("XML file could not be read.");
        }
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
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
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
