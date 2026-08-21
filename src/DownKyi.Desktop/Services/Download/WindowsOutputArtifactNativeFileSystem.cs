using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace DownKyi.Services.Download;

internal sealed partial class WindowsOutputArtifactNativeFileSystem
    : IOutputArtifactNativeFileSystem
{
    private const uint GenericRead = 0x80000000;
    private const uint Delete = 0x00010000;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint LockFileFailImmediately = 0x00000001;
    private const uint LockFileExclusiveLock = 0x00000002;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int OpenExisting = 3;

    public bool IsSupported => OperatingSystem.IsWindows();

    public async Task<OutputArtifactNativeCaptureResult> CaptureEvidenceAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OutputArtifactNativeCaptureResult.Unsupported();
        }

        SafeFileHandle? handle = null;
        try
        {
            var status = TryOpen(path, includeDeleteAccess: false, out handle);
            if (status == OutputArtifactNativeOpenStatus.Missing)
            {
                return OutputArtifactNativeCaptureResult.Missing();
            }

            if (status == OutputArtifactNativeOpenStatus.Unsupported)
            {
                return OutputArtifactNativeCaptureResult.Unsupported();
            }

            if (status != OutputArtifactNativeOpenStatus.Opened || handle is null)
            {
                return OutputArtifactNativeCaptureResult.Failed();
            }

            if (!TryAcquireExclusiveWholeFileLock(handle, out var lockState))
            {
                return OutputArtifactNativeCaptureResult.Failed();
            }

            try
            {
                var evidence = await ReadEvidenceAsync(handle, cancellationToken)
                    .ConfigureAwait(false);
                return OutputArtifactNativeCaptureResult.Captured(evidence);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException)
            {
                return OutputArtifactNativeCaptureResult.Failed();
            }
            catch (UnauthorizedAccessException)
            {
                return OutputArtifactNativeCaptureResult.Failed();
            }
            catch (CryptographicException)
            {
                return OutputArtifactNativeCaptureResult.Failed();
            }
            catch (NotSupportedException)
            {
                return OutputArtifactNativeCaptureResult.Failed();
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

    public async Task<OutputArtifactNativeSafeDeleteResult> DeleteIfEvidenceMatchesAsync(
        string path,
        OutputArtifactNativeEvidence expectedEvidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedEvidence);
        if (!OperatingSystem.IsWindows())
        {
            return OutputArtifactNativeSafeDeleteResult.Unsupported();
        }

        SafeFileHandle? handle = null;
        try
        {
            var status = TryOpen(path, includeDeleteAccess: true, out handle);
            if (status == OutputArtifactNativeOpenStatus.Missing)
            {
                return OutputArtifactNativeSafeDeleteResult.Missing();
            }

            if (status == OutputArtifactNativeOpenStatus.Unsupported)
            {
                return OutputArtifactNativeSafeDeleteResult.Unsupported();
            }

            if (status != OutputArtifactNativeOpenStatus.Opened || handle is null)
            {
                return OutputArtifactNativeSafeDeleteResult.Failed();
            }

            if (!TryAcquireExclusiveWholeFileLock(handle, out var lockState))
            {
                return OutputArtifactNativeSafeDeleteResult.Failed();
            }

            try
            {
                var actualEvidence = await ReadEvidenceAsync(handle, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(
                        actualEvidence.FilesystemIdentity,
                        expectedEvidence.FilesystemIdentity,
                        StringComparison.Ordinal))
                {
                    return OutputArtifactNativeSafeDeleteResult.Replaced();
                }

                if (actualEvidence.ByteLength != expectedEvidence.ByteLength
                    || !string.Equals(
                        actualEvidence.Sha256,
                        expectedEvidence.Sha256,
                        StringComparison.Ordinal))
                {
                    return OutputArtifactNativeSafeDeleteResult.Modified();
                }

                _beforeDelete?.Invoke(handle);
                return MarkOpenedFileForDeletion(handle)
                    ? OutputArtifactNativeSafeDeleteResult.Deleted()
                    : OutputArtifactNativeSafeDeleteResult.Failed();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException)
            {
                return OutputArtifactNativeSafeDeleteResult.Failed();
            }
            catch (UnauthorizedAccessException)
            {
                return OutputArtifactNativeSafeDeleteResult.Failed();
            }
            catch (CryptographicException)
            {
                return OutputArtifactNativeSafeDeleteResult.Failed();
            }
            catch (NotSupportedException)
            {
                return OutputArtifactNativeSafeDeleteResult.Failed();
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

    public async Task<bool> VerifyIdentityAndLengthAsync(
        string path,
        OutputArtifactNativeEvidence expectedEvidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedEvidence);
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        SafeFileHandle? handle = null;
        try
        {
            var status = TryOpen(path, includeDeleteAccess: false, out handle);
            if (status != OutputArtifactNativeOpenStatus.Opened || handle is null)
            {
                return false;
            }

            if (!TryAcquireExclusiveWholeFileLock(handle, out var lockState))
            {
                return false;
            }

            try
            {
                var filesystemIdentity = GetFileId(handle);
                return string.Equals(
                           filesystemIdentity,
                           expectedEvidence.FilesystemIdentity,
                           StringComparison.Ordinal)
                       && GetFileLength(handle) == expectedEvidence.ByteLength;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
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

    private static async Task<OutputArtifactNativeEvidence> ReadEvidenceAsync(
        SafeFileHandle handle,
        CancellationToken cancellationToken)
    {
        var filesystemIdentity = GetFileId(handle);
        var lengthBefore = GetFileLength(handle);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long offset = 0;
            while (true)
            {
                var bytesRead = await RandomAccess
                    .ReadAsync(handle, buffer, offset, cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                hasher.AppendData(buffer, 0, bytesRead);
                offset = checked(offset + bytesRead);
            }

            var lengthAfter = GetFileLength(handle);
            if (lengthBefore != lengthAfter || offset != lengthAfter)
            {
                throw new IOException("The output changed while ownership evidence was read.");
            }

            return new OutputArtifactNativeEvidence(
                lengthAfter,
                Convert.ToHexStringLower(hasher.GetHashAndReset()),
                filesystemIdentity);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private OutputArtifactNativeOpenStatus TryOpen(
        string path,
        bool includeDeleteAccess,
        out SafeFileHandle? handle)
    {
        handle = null;
        try
        {
            var openedHandle = WindowsOutputArtifactNativeMethods.CreateFile(
                path,
                includeDeleteAccess ? GenericRead | Delete : GenericRead,
                shareMode: _shareMode,
                IntPtr.Zero,
                OpenExisting,
                FileAttributeNormal | FileFlagOverlapped | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (!openedHandle.IsInvalid)
            {
                handle = openedHandle;
                return OutputArtifactNativeOpenStatus.Opened;
            }

            var error = Marshal.GetLastPInvokeError();
            openedHandle.Dispose();
            return error is ErrorFileNotFound or ErrorPathNotFound
                ? OutputArtifactNativeOpenStatus.Missing
                : OutputArtifactNativeOpenStatus.Failed;
        }
        catch (DllNotFoundException)
        {
            return OutputArtifactNativeOpenStatus.Unsupported;
        }
        catch (EntryPointNotFoundException)
        {
            return OutputArtifactNativeOpenStatus.Unsupported;
        }
        catch (IOException)
        {
            return OutputArtifactNativeOpenStatus.Failed;
        }
        catch (UnauthorizedAccessException)
        {
            return OutputArtifactNativeOpenStatus.Failed;
        }
        catch (ArgumentException)
        {
            return OutputArtifactNativeOpenStatus.Failed;
        }
        catch (NotSupportedException)
        {
            return OutputArtifactNativeOpenStatus.Failed;
        }
    }

    private static bool MarkOpenedFileForDeletion(SafeFileHandle handle)
    {
        var disposition = new FileDispositionInformation
        {
            // FILE_DISPOSITION_INFO.DeleteFile is the one-byte Win32 BOOLEAN.
            DeleteFile = 1
        };
        return WindowsOutputArtifactNativeMethods.SetFileInformationByHandle(
            handle,
            FileInformationByHandleClass.FileDispositionInformation,
            in disposition,
            (uint)Marshal.SizeOf<FileDispositionInformation>());
    }

    private static long GetFileLength(SafeFileHandle handle)
    {
        if (!WindowsOutputArtifactNativeMethods.GetFileSizeEx(handle, out var length))
        {
            throw new IOException("The output length could not be read.");
        }

        return length;
    }

    private static bool TryAcquireExclusiveWholeFileLock(
        SafeFileHandle handle,
        out FileLockOverlapped lockState)
    {
        lockState = default;
        return WindowsOutputArtifactNativeMethods.LockFileEx(
            handle,
            LockFileFailImmediately | LockFileExclusiveLock,
            reserved: 0,
            uint.MaxValue,
            uint.MaxValue,
            ref lockState);
    }

    private static void ReleaseWholeFileLock(
        SafeFileHandle handle,
        ref FileLockOverlapped lockState)
    {
        _ = WindowsOutputArtifactNativeMethods.UnlockFileEx(
            handle,
            reserved: 0,
            uint.MaxValue,
            uint.MaxValue,
            ref lockState);
    }

    private static string GetFileId(SafeFileHandle handle)
    {
        FileIdInformation information;
        if (!WindowsOutputArtifactNativeMethods.GetFileInformationByHandleEx(
                handle,
                FileInformationByHandleClass.FileIdInformation,
                out information,
                (uint)Marshal.SizeOf<FileIdInformation>()))
        {
            throw new IOException("The output filesystem identity could not be read.");
        }

        FileBasicInformation basicInformation;
        if (!WindowsOutputArtifactNativeMethods.GetFileInformationByHandleEx(
                handle,
                FileInformationByHandleClass.FileBasicInformation,
                out basicInformation,
                (uint)Marshal.SizeOf<FileBasicInformation>()))
        {
            throw new IOException("The output filesystem attributes could not be read.");
        }

        if ((basicInformation.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            throw new IOException("Reparse points are not eligible for output ownership evidence.");
        }

        return FormatFileIdForEvidence(
            information.VolumeSerialNumber,
            information.FileIdHigh,
            information.FileIdLow);
    }

    internal static string FormatFileIdForEvidence(
        ulong volumeSerialNumber,
        ulong fileIdHigh,
        ulong fileIdLow)
    {
        if ((fileIdHigh == 0 && fileIdLow == 0)
            || (fileIdHigh == ulong.MaxValue && fileIdLow == ulong.MaxValue))
        {
            throw new IOException("The output filesystem returned a sentinel file identity.");
        }

        var information = new FileIdInformation
        {
            VolumeSerialNumber = volumeSerialNumber,
            FileIdHigh = fileIdHigh,
            FileIdLow = fileIdLow
        };
        return string.Create(
            49,
            information,
            static (destination, source) =>
            {
                source.VolumeSerialNumber.TryFormat(
                    destination[..16],
                    out _,
                    "x16",
                    CultureInfo.InvariantCulture);
                destination[16] = ':';
                source.FileIdHigh.TryFormat(
                    destination.Slice(17, 16),
                    out _,
                    "x16",
                    CultureInfo.InvariantCulture);
                source.FileIdLow.TryFormat(
                    destination.Slice(33, 16),
                    out _,
                    "x16",
                    CultureInfo.InvariantCulture);
            });
    }
}
