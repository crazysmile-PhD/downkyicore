using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace DownKyi.Services.Download;

internal sealed partial class WindowsOutputArtifactNativeFileSystem
{
    private readonly Action<SafeFileHandle>? _beforeDelete;
    private readonly uint _shareMode;

    internal WindowsOutputArtifactNativeFileSystem()
    {
    }

    internal WindowsOutputArtifactNativeFileSystem(Action<SafeFileHandle> beforeDelete)
    {
        _beforeDelete = beforeDelete ?? throw new ArgumentNullException(nameof(beforeDelete));
        _shareMode = FileShareRead | FileShareWrite | FileShareDelete;
    }

    public OutputArtifactNativeIdentityCaptureResult CaptureIdentity(
        SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!OperatingSystem.IsWindows())
        {
            return OutputArtifactNativeIdentityCaptureResult.Unsupported();
        }

        if (handle.IsClosed || handle.IsInvalid)
        {
            return OutputArtifactNativeIdentityCaptureResult.Failed();
        }

        try
        {
            return OutputArtifactNativeIdentityCaptureResult.Captured(GetFileId(handle));
        }
        catch (IOException)
        {
            return OutputArtifactNativeIdentityCaptureResult.Failed();
        }
        catch (UnauthorizedAccessException)
        {
            return OutputArtifactNativeIdentityCaptureResult.Failed();
        }
        catch (NotSupportedException)
        {
            return OutputArtifactNativeIdentityCaptureResult.Failed();
        }
    }

    public Task<OutputArtifactNativeSafeDeleteResult> DeleteIfIdentityMatchesAsync(
        string path,
        string expectedFilesystemIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFilesystemIdentity);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(OutputArtifactNativeSafeDeleteResult.Unsupported());
        }

        SafeFileHandle? handle = null;
        try
        {
            var status = TryOpen(path, includeDeleteAccess: true, out handle);
            if (status == OutputArtifactNativeOpenStatus.Missing)
            {
                return Task.FromResult(OutputArtifactNativeSafeDeleteResult.Missing());
            }

            if (status == OutputArtifactNativeOpenStatus.Unsupported)
            {
                return Task.FromResult(OutputArtifactNativeSafeDeleteResult.Unsupported());
            }

            if (status != OutputArtifactNativeOpenStatus.Opened || handle is null)
            {
                return Task.FromResult(OutputArtifactNativeSafeDeleteResult.Failed());
            }

            if (!TryAcquireExclusiveWholeFileLock(handle, out var lockState))
            {
                return Task.FromResult(OutputArtifactNativeSafeDeleteResult.Failed());
            }

            try
            {
                if (!string.Equals(
                        GetFileId(handle),
                        expectedFilesystemIdentity,
                        StringComparison.Ordinal))
                {
                    return Task.FromResult(OutputArtifactNativeSafeDeleteResult.Replaced());
                }

                _beforeDelete?.Invoke(handle);
                return Task.FromResult(
                    MarkOpenedFileForDeletion(handle)
                        ? OutputArtifactNativeSafeDeleteResult.Deleted()
                        : OutputArtifactNativeSafeDeleteResult.Failed());
            }
            catch (IOException)
            {
                return Task.FromResult(OutputArtifactNativeSafeDeleteResult.Failed());
            }
            catch (UnauthorizedAccessException)
            {
                return Task.FromResult(OutputArtifactNativeSafeDeleteResult.Failed());
            }
            catch (NotSupportedException)
            {
                return Task.FromResult(OutputArtifactNativeSafeDeleteResult.Failed());
            }
            finally
            {
                ReleaseWholeFileLock(handle, ref lockState);
            }
        }
        finally
        {
            handle?.Dispose();
        }
    }
}
