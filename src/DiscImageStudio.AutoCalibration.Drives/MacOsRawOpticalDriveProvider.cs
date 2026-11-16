using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiscImageStudio.AutoCalibration.Drives;

public sealed class MacOsOpticalDriveProvider : IOpticalDriveProvider
{
    public IReadOnlyList<CalibrationDriveInfo> ListDrives(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureMacOs();
        return MacOpticalMedia.Enumerate(cancellationToken)
            .Select(static media => media.Info)
            .ToArray();
    }

    public ITimedOpticalDrive Open(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureMacOs();
        MacOpticalMedia? media = MacOpticalMedia.Enumerate(cancellationToken)
            .FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.Info.Id, id));
        if (media is null)
        {
            throw new ArgumentException(
                "The ID is not a currently enumerated optical medium. Only IOKit-identified optical media may be opened.",
                nameof(id));
        }

        return MacRawOpticalDrive.Open(media, cancellationToken);
    }

    private static void EnsureMacOs()
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("The macOS raw optical-drive adapter is available only on macOS.");
        }
    }
}

internal sealed record MacOpticalMedia(CalibrationDriveInfo Info, string MediaClass)
{
    private static readonly string[] OpticalClasses = ["IOCDMedia", "IODVDMedia", "IOBDMedia"];

    public static IReadOnlyList<MacOpticalMedia> Enumerate(CancellationToken cancellationToken)
    {
        IntPtr matching = Iokit.IOServiceMatching("IOMedia");
        if (matching == IntPtr.Zero)
        {
            throw new IOException("IOKit could not create an IOMedia matching dictionary.");
        }

        int result = Iokit.IOServiceGetMatchingServices(0, matching, out uint iterator);
        if (result != 0)
        {
            throw new IOException($"IOKit media enumeration failed with 0x{result:X8}.");
        }

        var drives = new List<MacOpticalMedia>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            uint service;
            while ((service = Iokit.IOIteratorNext(iterator)) != 0)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? mediaClass = FindOpticalMediaClass(service);
                    if (mediaClass is null || !ReadBooleanProperty(service, "Whole"))
                    {
                        continue;
                    }

                    string? bsdName = ReadStringProperty(service, "BSD Name");
                    if (string.IsNullOrWhiteSpace(bsdName)
                        || !IsDiskName(bsdName)
                        || !seen.Add(bsdName))
                    {
                        continue;
                    }

                    ulong byteSize = ReadNumberProperty(service, "Size");
                    ulong sectors64 = byteSize / 2048;
                    uint sectors = sectors64 > uint.MaxValue ? uint.MaxValue : (uint)sectors64;
                    string mediaType = MediaTypeFor(mediaClass);
                    string vendor = ReadAncestorStringProperty(service, "Vendor Identification");
                    string product = ReadAncestorStringProperty(service, "Product Identification");
                    string revision = ReadAncestorStringProperty(service, "Product Revision Level");
                    string mediaId = $"{mediaClass}:{bsdName}:{byteSize}";

                    drives.Add(new MacOpticalMedia(
                        new CalibrationDriveInfo(
                            bsdName,
                            vendor,
                            product,
                            revision,
                            MediaPresent: true,
                            mediaType,
                            mediaId,
                            sectors,
                            IsBlank: false,
                            Simulated: false,
                            Supported: CalibrationMediaProfile.IsSupported(mediaType)),
                        mediaClass));
                }
                finally
                {
                    Iokit.IOObjectRelease(service);
                }
            }
        }
        finally
        {
            Iokit.IOObjectRelease(iterator);
        }

        return drives;
    }

    private static string? FindOpticalMediaClass(uint service)
    {
        uint current = service;
        bool releaseCurrent = false;
        try
        {
            for (int depth = 0; depth < 16; depth++)
            {
                string className = Iokit.GetClassName(current);
                if (OpticalClasses.Contains(className, StringComparer.Ordinal))
                {
                    return className;
                }

                if (Iokit.IORegistryEntryGetParentEntry(current, "IOService", out uint parent) != 0 || parent == 0)
                {
                    return null;
                }

                if (releaseCurrent)
                {
                    Iokit.IOObjectRelease(current);
                }

                current = parent;
                releaseCurrent = true;
            }

            return null;
        }
        finally
        {
            if (releaseCurrent)
            {
                Iokit.IOObjectRelease(current);
            }
        }
    }

    private static string ReadAncestorStringProperty(uint service, string key)
    {
        uint current = service;
        bool releaseCurrent = false;
        try
        {
            for (int depth = 0; depth < 16; depth++)
            {
                string value = ReadStringProperty(current, key);
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }

                if (Iokit.IORegistryEntryGetParentEntry(current, "IOService", out uint parent) != 0 || parent == 0)
                {
                    return string.Empty;
                }

                if (releaseCurrent)
                {
                    Iokit.IOObjectRelease(current);
                }

                current = parent;
                releaseCurrent = true;
            }

            return string.Empty;
        }
        finally
        {
            if (releaseCurrent)
            {
                Iokit.IOObjectRelease(current);
            }
        }
    }

    private static string ReadStringProperty(uint service, string key)
    {
        IntPtr propertyKey = Iokit.CFStringCreateWithCString(IntPtr.Zero, key, Iokit.Utf8Encoding);
        if (propertyKey == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            IntPtr property = Iokit.IORegistryEntryCreateCFProperty(service, propertyKey, IntPtr.Zero, 0);
            if (property == IntPtr.Zero)
            {
                return string.Empty;
            }

            try
            {
                if (Iokit.CFGetTypeID(property) != Iokit.CFStringGetTypeID())
                {
                    return string.Empty;
                }

                byte[] buffer = new byte[1024];
                if (!Iokit.CFStringGetCString(property, buffer, buffer.Length, Iokit.Utf8Encoding))
                {
                    return string.Empty;
                }

                int terminator = Array.IndexOf(buffer, (byte)0);
                return System.Text.Encoding.UTF8.GetString(buffer, 0, terminator < 0 ? buffer.Length : terminator);
            }
            finally
            {
                Iokit.CFRelease(property);
            }
        }
        finally
        {
            Iokit.CFRelease(propertyKey);
        }
    }

    private static bool ReadBooleanProperty(uint service, string key)
    {
        IntPtr propertyKey = Iokit.CFStringCreateWithCString(IntPtr.Zero, key, Iokit.Utf8Encoding);
        if (propertyKey == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            IntPtr property = Iokit.IORegistryEntryCreateCFProperty(service, propertyKey, IntPtr.Zero, 0);
            if (property == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                if (Iokit.CFGetTypeID(property) == Iokit.CFBooleanGetTypeID())
                {
                    return Iokit.CFBooleanGetValue(property) != 0;
                }

                return Iokit.CFGetTypeID(property) == Iokit.CFNumberGetTypeID()
                    && Iokit.CFNumberGetValue(property, Iokit.CfNumberSInt64Type, out long value)
                    && value != 0;
            }
            finally
            {
                Iokit.CFRelease(property);
            }
        }
        finally
        {
            Iokit.CFRelease(propertyKey);
        }
    }

    private static ulong ReadNumberProperty(uint service, string key)
    {
        IntPtr propertyKey = Iokit.CFStringCreateWithCString(IntPtr.Zero, key, Iokit.Utf8Encoding);
        if (propertyKey == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            IntPtr property = Iokit.IORegistryEntryCreateCFProperty(service, propertyKey, IntPtr.Zero, 0);
            if (property == IntPtr.Zero)
            {
                return 0;
            }

            try
            {
                return Iokit.CFGetTypeID(property) == Iokit.CFNumberGetTypeID()
                    && Iokit.CFNumberGetValue(property, Iokit.CfNumberSInt64Type, out long value)
                    && value >= 0
                    ? (ulong)value
                    : 0;
            }
            finally
            {
                Iokit.CFRelease(property);
            }
        }
        finally
        {
            Iokit.CFRelease(propertyKey);
        }
    }

    private static bool IsDiskName(string name) =>
        name.StartsWith("disk", StringComparison.Ordinal)
        && name.Length > 4
        && name.AsSpan(4).ToString().All(static character => character is >= '0' and <= '9');

    private static string MediaTypeFor(string mediaClass) => mediaClass switch
    {
        "IOCDMedia" => "CD",
        "IODVDMedia" => "DVD",
        _ => "Blu-ray",
    };

    private static class Iokit
    {
        public const uint Utf8Encoding = 0x08000100;
        public const int CfNumberSInt64Type = 4;

        [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit", CharSet = CharSet.Ansi)]
        public static extern IntPtr IOServiceMatching([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
        public static extern int IOServiceGetMatchingServices(uint mainPort, IntPtr matching, out uint iterator);

        [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
        public static extern uint IOIteratorNext(uint iterator);

        [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit", CharSet = CharSet.Ansi)]
        public static extern int IORegistryEntryGetParentEntry(
            uint entry,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string plane,
            out uint parent);

        [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
        public static extern IntPtr IORegistryEntryCreateCFProperty(uint entry, IntPtr key, IntPtr allocator, uint options);

        [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
        public static extern int IOObjectGetClass(uint entry, [Out] byte[] className);

        [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
        public static extern int IOObjectRelease(uint entry);

        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", CharSet = CharSet.Ansi)]
        public static extern IntPtr CFStringCreateWithCString(
            IntPtr allocator,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string value,
            uint encoding);

        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool CFStringGetCString(IntPtr value, [Out] byte[] buffer, nint bufferSize, uint encoding);

        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        public static extern byte CFBooleanGetValue(IntPtr value);

        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool CFNumberGetValue(IntPtr number, int numberType, out long value);

        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        public static extern void CFRelease(IntPtr value);

        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        public static extern nint CFGetTypeID(IntPtr value);

        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        public static extern nint CFStringGetTypeID();

        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        public static extern nint CFBooleanGetTypeID();

        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        public static extern nint CFNumberGetTypeID();

        public static string GetClassName(uint entry)
        {
            byte[] name = new byte[128];
            if (IOObjectGetClass(entry, name) != 0)
            {
                return string.Empty;
            }

            int terminator = Array.IndexOf(name, (byte)0);
            return System.Text.Encoding.UTF8.GetString(name, 0, terminator < 0 ? name.Length : terminator);
        }
    }
}

internal sealed class MacRawOpticalDrive : ITimedOpticalDrive
{
    private const int FNoCache = 48;
    private const ulong DkIoGetBlockCount = 0x4008_6419;
    private const ulong DkIoGetBlockSize = 0x4004_6418;
    private const int OpenReadOnly = 0;
    private const int SectorBytes = 2048;
    private readonly SafeFileHandle _handle;
    private readonly uint _reportedCapacity;
    private bool _disposed;

    private MacRawOpticalDrive(SafeFileHandle handle, uint reportedCapacity)
    {
        _handle = handle;
        _reportedCapacity = reportedCapacity;
    }

    public bool SupportsForceUnitAccess => false;

    public static MacRawOpticalDrive Open(MacOpticalMedia media, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string suffix = media.Info.Id[4..];
        string rawDevice = $"/dev/rdisk{suffix}";
        string[] candidates = media.MediaClass == "IOCDMedia"
            ? [$"{rawDevice}s0", rawDevice]
            : [rawDevice];
        var failures = new List<string>();

        foreach (string path in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SafeFileHandle? handle = null;
            try
            {
                handle = OpenRawDevice(path);
                if (NativeMethods.fcntl(handle, FNoCache, 1) < 0)
                {
                    throw NativeFailure("F_NOCACHE could not be enabled for the optical device.");
                }

                uint capacity = ReadCapacity(handle, media.Info.TotalSectors);
                return new MacRawOpticalDrive(handle, capacity);
            }
            catch (Exception exception) when (exception is IOException or NotSupportedException)
            {
                failures.Add($"{path}: {exception.Message}");
                handle?.Dispose();
            }
        }

        throw new IOException(
            "Could not open an IOKit-identified optical device without caching. " + string.Join(" | ", failures));
    }

    public TimedSectorRead ReadTimed(uint lba, bool forceUnitAccess, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (forceUnitAccess)
        {
            throw new NotSupportedException(
                "The /dev/rdisk fallback can disable the macOS host cache, but cannot bypass the drive's internal cache (FUA).");
        }

        Span<byte> sector = stackalloc byte[SectorBytes];
        ReadExact(lba, sector, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new TimedSectorRead(ToNanoseconds(Stopwatch.GetTimestamp()));
    }

    public uint ReadCapacity(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return ReadCapacity(_handle, _reportedCapacity);
    }

    public void ReadSector(uint lba, Span<byte> destination, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (destination.Length < SectorBytes)
        {
            throw new ArgumentException("A user-data sector destination must contain at least 2048 bytes.", nameof(destination));
        }

        ReadExact(lba, destination[..SectorBytes], cancellationToken);
    }

    public bool TryReadRawSector(uint lba, Span<byte> destination, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (destination.Length < 2352)
        {
            throw new ArgumentException("A raw CD-sector destination must contain at least 2352 bytes.", nameof(destination));
        }

        return false;
    }

    public void SetReadSpeed(ushort kilobytesPerSecond, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
    }

    public void Wait(TimeSpan duration, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (duration != TimeSpan.Zero && cancellationToken.WaitHandle.WaitOne(duration))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
    }

    private void ReadExact(uint lba, Span<byte> destination, CancellationToken cancellationToken)
    {
        long offset = checked((long)lba * SectorBytes);
        int total = 0;
        while (total < destination.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = RandomAccess.Read(_handle, destination[total..], checked(offset + total));
            if (read == 0)
            {
                throw new EndOfStreamException($"The macOS optical device returned a short read at LBA {lba}.");
            }

            total += read;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static SafeFileHandle OpenRawDevice(string path)
    {
        int descriptor = NativeMethods.open(path, OpenReadOnly);
        if (descriptor < 0)
        {
            throw NativeFailure($"Could not open optical device {path}.");
        }

        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    private static uint ReadCapacity(SafeFileHandle handle, uint fallback)
    {
        ulong blockCount = 0;
        uint blockSize = 0;
        if (NativeMethods.ioctl(handle, new UIntPtr(DkIoGetBlockCount), ref blockCount) == 0
            && NativeMethods.ioctl(handle, new UIntPtr(DkIoGetBlockSize), ref blockSize) == 0
            && blockSize > 0)
        {
            if (blockSize > SectorBytes || SectorBytes % blockSize != 0)
            {
                throw new NotSupportedException(
                    $"The raw device reports {blockSize}-byte blocks, which cannot be read as 2048-byte user sectors.");
            }

            if (blockCount > ulong.MaxValue / blockSize)
            {
                throw new IOException("The macOS optical device reported an invalid capacity.");
            }

            ulong sectors = blockCount * blockSize / SectorBytes;
            return sectors > uint.MaxValue ? uint.MaxValue : (uint)sectors;
        }

        if (fallback > 0)
        {
            return fallback;
        }

        throw NativeFailure("The macOS optical device did not report its capacity.");
    }

    private static IOException NativeFailure(string message)
    {
        int error = Marshal.GetLastWin32Error();
        return new IOException(message, new System.ComponentModel.Win32Exception(error));
    }

    private static double ToNanoseconds(long timestamp) =>
        timestamp * (1_000_000_000.0 / Stopwatch.Frequency);

    private static class NativeMethods
    {
        [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
        public static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
        public static extern int fcntl(SafeFileHandle fileDescriptor, int command, int value);

        [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
        public static extern int ioctl(SafeFileHandle fileDescriptor, UIntPtr request, ref ulong value);

        [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
        public static extern int ioctl(SafeFileHandle fileDescriptor, UIntPtr request, ref uint value);
    }
}
