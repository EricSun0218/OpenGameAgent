using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GameAgent.Persistence;

internal enum LocalSkillFileReadStage
{
    PrimaryOpened
}

internal interface ILocalSkillPackageFileObserver
{
    void OnFileRead(
        LocalSkillFileReadStage stage,
        string sourceId,
        string relativePath);
}

internal sealed class LocalSkillFileException : IOException
{
    public LocalSkillFileException(string reasonCode, string message)
        : base(message)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

internal static class SecureLocalSkillFiles
{
    private const int ReadBufferBytes = 16 * 1024;

    private static readonly bool IsWindows =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static readonly bool IsLinux =
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    private static readonly StringComparison PathComparison =
        IsWindows
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public static string OpenCanonicalRoot(string path)
    {
        EnsureSupportedPlatform();
        var expected = NormalizeFullPath(path);
        if (!Directory.Exists(expected))
        {
            throw Error(
                SkillPackageDiagnosticCodes.RootUnavailable,
                "The configured skill-package root is unavailable.");
        }

        using var handle = OpenDirectoryNoFollow(expected);
        var identity = CaptureIdentity(handle);
        EnsureExpectedFinalPath(expected, expected, identity.FinalPath);
        EnsureNotReparsePoint(expected);
        return identity.FinalPath;
    }

    public static void ValidateDirectory(string root, string path)
    {
        EnsureContained(root, path);
        ValidatePathComponents(root, path);
        using var handle = OpenDirectoryNoFollow(path);
        var identity = CaptureIdentity(handle);
        EnsureExpectedFinalPath(root, path, identity.FinalPath);
    }

    public static byte[] ReadFile(
        string root,
        string path,
        int maximumBytes,
        string sourceId,
        string relativePath,
        ILocalSkillPackageFileObserver? observer,
        CancellationToken cancellationToken)
    {
        if (maximumBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        cancellationToken.ThrowIfCancellationRequested();
        EnsureContained(root, path);
        ValidatePathComponents(root, path);

        using var primary = OpenFileNoFollow(path);
        using var stream = new FileStream(
            primary,
            FileAccess.Read,
            ReadBufferBytes,
            isAsync: false);
        var openedIdentity = CaptureIdentity(primary);
        EnsureExpectedFinalPath(root, path, openedIdentity.FinalPath);

        observer?.OnFileRead(
            LocalSkillFileReadStage.PrimaryOpened,
            sourceId,
            relativePath);

        cancellationToken.ThrowIfCancellationRequested();
        EnsurePathStillNamesHandle(root, path, primary, openedIdentity);
        var bytes = ReadBounded(
            stream,
            maximumBytes,
            cancellationToken);
        EnsurePathStillNamesHandle(root, path, primary, openedIdentity);
        return bytes;
    }

    public static string RelativePath(string root, string path)
    {
        EnsureContained(root, path);
        return Path.GetRelativePath(root, path)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    public static string CombineRelative(
        string root,
        string relativePath)
    {
        var platformPath = relativePath.Replace(
            '/',
            Path.DirectorySeparatorChar);
        var combined = NormalizeFullPath(Path.Combine(root, platformPath));
        EnsureContained(root, combined);
        return combined;
    }

    public static void EnsureContained(string root, string path)
    {
        var normalizedRoot = NormalizeFullPath(root);
        var normalizedPath = NormalizeFullPath(path);
        if (string.Equals(
                normalizedRoot,
                normalizedPath,
                PathComparison))
        {
            return;
        }

        var prefix = normalizedRoot.EndsWith(
            Path.DirectorySeparatorChar.ToString(),
            StringComparison.Ordinal)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        if (!normalizedPath.StartsWith(prefix, PathComparison))
        {
            throw Error(
                SkillPackageDiagnosticCodes.PathEscapesRoot,
                "A skill-package path escapes its configured root.");
        }
    }

    private static byte[] ReadBounded(
        FileStream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(
            Math.Min(maximumBytes, ReadBufferBytes));
        var buffer = new byte[ReadBufferBytes];
        var total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = maximumBytes - total;
            var requested = Math.Min(
                buffer.Length,
                checked(remaining + 1));
            var read = stream.Read(buffer, 0, requested);
            if (read == 0)
            {
                break;
            }

            if (read > remaining)
            {
                throw Error(
                    SkillPackageDiagnosticCodes.FileBytesExceeded,
                    "A skill-package file exceeds its byte limit.");
            }

            output.Write(buffer, 0, read);
            total = checked(total + read);
        }

        return output.ToArray();
    }

    private static void EnsurePathStillNamesHandle(
        string root,
        string path,
        SafeFileHandle primary,
        FileIdentity openedIdentity)
    {
        var currentPrimary = CaptureIdentity(primary);
        EnsureExpectedFinalPath(root, path, currentPrimary.FinalPath);
        if (!openedIdentity.SameFile(currentPrimary))
        {
            throw Error(
                SkillPackageDiagnosticCodes.FileIdentityChanged,
                "A skill-package file identity changed while it was open.");
        }

        ValidatePathComponents(root, path);
        using var validation = OpenFileNoFollow(path);
        var validationIdentity = CaptureIdentity(validation);
        EnsureExpectedFinalPath(root, path, validationIdentity.FinalPath);
        if (!openedIdentity.SameFile(validationIdentity))
        {
            throw Error(
                SkillPackageDiagnosticCodes.FileIdentityChanged,
                "A skill-package path changed identity while it was read.");
        }
    }

    private static void ValidatePathComponents(string root, string path)
    {
        EnsureContained(root, path);
        EnsureNotReparsePoint(root);
        var relative = Path.GetRelativePath(root, path);
        if (string.Equals(relative, ".", StringComparison.Ordinal))
        {
            return;
        }

        var current = root;
        foreach (var component in relative.Split(
                     new[]
                     {
                         Path.DirectorySeparatorChar,
                         Path.AltDirectorySeparatorChar
                     },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (component is "." or "..")
            {
                throw Error(
                    SkillPackageDiagnosticCodes.PathEscapesRoot,
                    "A skill-package path is not canonical.");
            }

            current = Path.Combine(current, component);
            EnsureNotReparsePoint(current);
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception exception)
            when (exception is IOException
                  or UnauthorizedAccessException)
        {
            throw Error(
                SkillPackageDiagnosticCodes.PathUnavailable,
                "A skill-package path became unavailable.");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw Error(
                SkillPackageDiagnosticCodes.LinkRejected,
                "Skill-package links, junctions, and reparse points are rejected.");
        }
    }

    private static SafeFileHandle OpenDirectoryNoFollow(string path)
    {
        try
        {
            return IsWindows
                ? WindowsNative.OpenDirectory(path)
                : LinuxNative.OpenDirectory(path);
        }
        catch (LocalSkillFileException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException
                  or UnauthorizedAccessException)
        {
            throw Error(
                SkillPackageDiagnosticCodes.PathUnavailable,
                "A skill-package directory could not be opened safely.");
        }
    }

    private static SafeFileHandle OpenFileNoFollow(string path)
    {
        try
        {
            return IsWindows
                ? WindowsNative.OpenFile(path)
                : LinuxNative.OpenFile(path);
        }
        catch (LocalSkillFileException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException
                  or UnauthorizedAccessException)
        {
            throw Error(
                SkillPackageDiagnosticCodes.PathUnavailable,
                "A skill-package file could not be opened safely.");
        }
    }

    private static FileIdentity CaptureIdentity(SafeFileHandle handle)
    {
        if (handle.IsInvalid || handle.IsClosed)
        {
            throw Error(
                SkillPackageDiagnosticCodes.FileIdentityUnavailable,
                "A skill-package file identity is unavailable.");
        }

        return IsWindows
            ? WindowsNative.CaptureIdentity(handle)
            : LinuxNative.CaptureIdentity(handle);
    }

    private static void EnsureExpectedFinalPath(
        string root,
        string expected,
        string finalPath)
    {
        var normalizedExpected = NormalizeFullPath(expected);
        var normalizedFinal = NormalizeFullPath(finalPath);
        EnsureContained(root, normalizedFinal);
        if (!string.Equals(
                normalizedExpected,
                normalizedFinal,
                PathComparison))
        {
            throw Error(
                SkillPackageDiagnosticCodes.FileIdentityChanged,
                "A skill-package handle does not identify its expected path.");
        }
    }

    private static string NormalizeFullPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (root is not null
            && !string.Equals(full, root, PathComparison))
        {
            full = full.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }

        return full;
    }

    private static void EnsureSupportedPlatform()
    {
        if (!IsWindows && !IsLinux)
        {
            throw Error(
                SkillPackageDiagnosticCodes.PlatformUnsupported,
                "Secure local skill-package loading supports Windows and Linux.");
        }
    }

    private static LocalSkillFileException Error(
        string reasonCode,
        string message) =>
        new(reasonCode, message);

    private readonly struct FileIdentity
    {
        public FileIdentity(
            string finalPath,
            ulong device,
            ulong file)
        {
            FinalPath = finalPath;
            Device = device;
            File = file;
        }

        public string FinalPath { get; }

        public ulong Device { get; }

        public ulong File { get; }

        public bool SameFile(FileIdentity other) =>
            Device == other.Device && File == other.File;
    }

    private static class WindowsNative
    {
        private const uint GenericRead = 0x80000000;
        private const uint FileReadAttributes = 0x00000080;
        private const uint ShareRead = 0x00000001;
        private const uint ShareWrite = 0x00000002;
        private const uint ShareDelete = 0x00000004;
        private const uint OpenExisting = 3;
        private const uint FlagOpenReparsePoint = 0x00200000;
        private const uint FlagBackupSemantics = 0x02000000;
        private const uint FlagSequentialScan = 0x08000000;
        private const uint AttributeReparsePoint = 0x00000400;

        public static SafeFileHandle OpenDirectory(string path) =>
            Open(
                path,
                FileReadAttributes,
                FlagOpenReparsePoint | FlagBackupSemantics);

        public static SafeFileHandle OpenFile(string path) =>
            Open(
                path,
                GenericRead | FileReadAttributes,
                FlagOpenReparsePoint | FlagSequentialScan);

        public static FileIdentity CaptureIdentity(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandle(handle, out var information))
            {
                throw NativeError(
                    SkillPackageDiagnosticCodes.FileIdentityUnavailable,
                    "A Windows skill-package handle identity is unavailable.");
            }

            if ((information.FileAttributes & AttributeReparsePoint) != 0)
            {
                throw Error(
                    SkillPackageDiagnosticCodes.LinkRejected,
                    "Skill-package links, junctions, and reparse points are rejected.");
            }

            var file = ((ulong)information.FileIndexHigh << 32)
                       | information.FileIndexLow;
            if (file == 0)
            {
                throw Error(
                    SkillPackageDiagnosticCodes.FileIdentityUnavailable,
                    "A Windows skill-package file ID is unavailable.");
            }

            return new FileIdentity(
                FinalPath(handle),
                information.VolumeSerialNumber,
                file);
        }

        private static SafeFileHandle Open(
            string path,
            uint access,
            uint flags)
        {
            var handle = CreateFile(
                path,
                access,
                ShareRead | ShareWrite | ShareDelete,
                IntPtr.Zero,
                OpenExisting,
                flags,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw NativeError(
                    SkillPackageDiagnosticCodes.PathUnavailable,
                    "A Windows skill-package path could not be opened safely.");
            }

            return handle;
        }

        private static string FinalPath(SafeFileHandle handle)
        {
            var capacity = 512;
            while (capacity <= 32_768)
            {
                var buffer = new StringBuilder(capacity);
                var length = GetFinalPathNameByHandle(
                    handle,
                    buffer,
                    (uint)buffer.Capacity,
                    0);
                if (length == 0)
                {
                    throw NativeError(
                        SkillPackageDiagnosticCodes.FileIdentityUnavailable,
                        "A Windows skill-package final path is unavailable.");
                }

                if (length < buffer.Capacity)
                {
                    return NormalizeWindowsDevicePath(buffer.ToString());
                }

                capacity = checked((int)length + 1);
            }

            throw Error(
                SkillPackageDiagnosticCodes.PathBytesExceeded,
                "A skill-package native path is too long.");
        }

        private static string NormalizeWindowsDevicePath(string path)
        {
            const string uncPrefix = @"\\?\UNC\";
            const string devicePrefix = @"\\?\";
            if (path.StartsWith(
                    uncPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                return @"\\" + path.Substring(uncPrefix.Length);
            }

            return path.StartsWith(
                devicePrefix,
                StringComparison.OrdinalIgnoreCase)
                ? path.Substring(devicePrefix.Length)
                : path;
        }

        private static LocalSkillFileException NativeError(
            string reasonCode,
            string message) =>
            Error(
                reasonCode,
                message + " Native error "
                + Marshal.GetLastWin32Error().ToString(
                    CultureInfo.InvariantCulture)
                + ".");

        [DllImport(
            "kernel32.dll",
            EntryPoint = "CreateFileW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation fileInformation);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "GetFinalPathNameByHandleW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            StringBuilder filePath,
            uint filePathLength,
            uint flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public FileTime CreationTime;
            public FileTime LastAccessTime;
            public FileTime LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime
        {
            public uint LowDateTime;
            public uint HighDateTime;
        }
    }

    private static class LinuxNative
    {
        private const int ReadOnly = 0;
        private const int NonBlocking = 0x00000800;
        private const int CloseOnExec = 0x00080000;
        private const int Directory = 0x00010000;
        private const int NoFollow = 0x00020000;

        private static readonly UTF8Encoding StrictUtf8 = new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

        public static SafeFileHandle OpenDirectory(string path) =>
            Open(path, ReadOnly | CloseOnExec | Directory | NoFollow);

        public static SafeFileHandle OpenFile(string path) =>
            Open(path, ReadOnly | NonBlocking | CloseOnExec | NoFollow);

        public static FileIdentity CaptureIdentity(SafeFileHandle handle)
        {
            var descriptor = checked((int)handle.DangerousGetHandle());
            var finalPath = ReadLink(
                "/proc/self/fd/"
                + descriptor.ToString(CultureInfo.InvariantCulture));
            if (!Path.IsPathRooted(finalPath))
            {
                throw Error(
                    SkillPackageDiagnosticCodes.FileIdentityUnavailable,
                    "A Linux skill-package handle is not a regular filesystem path.");
            }

            var mount = (ulong?)null;
            var inode = (ulong?)null;
            try
            {
                foreach (var line in File.ReadLines(
                             "/proc/self/fdinfo/"
                             + descriptor.ToString(
                                 CultureInfo.InvariantCulture)))
                {
                    if (line.StartsWith("mnt_id:", StringComparison.Ordinal))
                    {
                        mount = ParseUnsigned(line.Substring("mnt_id:".Length));
                    }
                    else if (line.StartsWith("ino:", StringComparison.Ordinal))
                    {
                        inode = ParseUnsigned(line.Substring("ino:".Length));
                    }
                }
            }
            catch (Exception exception)
                when (exception is IOException
                      or UnauthorizedAccessException
                      or FormatException
                      or OverflowException)
            {
                throw Error(
                    SkillPackageDiagnosticCodes.FileIdentityUnavailable,
                    "A Linux skill-package handle identity is unavailable.");
            }

            if (!mount.HasValue
                || !inode.HasValue
                || inode.Value == 0)
            {
                throw Error(
                    SkillPackageDiagnosticCodes.FileIdentityUnavailable,
                    "A Linux skill-package mount or inode identity is unavailable.");
            }

            return new FileIdentity(
                finalPath,
                mount.Value,
                inode.Value);
        }

        private static SafeFileHandle Open(string path, int flags)
        {
            var descriptor = open(path, flags);
            if (descriptor < 0)
            {
                throw Error(
                    SkillPackageDiagnosticCodes.PathUnavailable,
                    "A Linux skill-package path could not be opened safely. Native error "
                    + Marshal.GetLastWin32Error().ToString(
                        CultureInfo.InvariantCulture)
                    + ".");
            }

            return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        }

        private static string ReadLink(string path)
        {
            var capacity = 512;
            while (capacity <= 32_768)
            {
                var buffer = new byte[capacity];
                var result = readlink(
                    path,
                    buffer,
                    (UIntPtr)buffer.Length);
                var length = result.ToInt64();
                if (length < 0)
                {
                    throw Error(
                        SkillPackageDiagnosticCodes.FileIdentityUnavailable,
                        "A Linux skill-package final path is unavailable.");
                }

                if (length < buffer.Length)
                {
                    try
                    {
                        return StrictUtf8.GetString(
                            buffer,
                            0,
                            checked((int)length));
                    }
                    catch (DecoderFallbackException)
                    {
                        throw Error(
                            SkillPackageDiagnosticCodes.StrictUtf8Required,
                            "A skill-package native path is not strict UTF-8.");
                    }
                }

                capacity = checked(capacity * 2);
            }

            throw Error(
                SkillPackageDiagnosticCodes.PathBytesExceeded,
                "A skill-package native path is too long.");
        }

        private static ulong ParseUnsigned(string value) =>
            ulong.Parse(
                value.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture);

        [DllImport(
            "libc",
            EntryPoint = "open",
            CharSet = CharSet.Ansi,
            SetLastError = true)]
        private static extern int open(string path, int flags);

        [DllImport(
            "libc",
            EntryPoint = "readlink",
            CharSet = CharSet.Ansi,
            SetLastError = true)]
        private static extern IntPtr readlink(
            string path,
            byte[] buffer,
            UIntPtr bufferSize);
    }
}
