using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace AudioIntegrityChecker.Pipeline;

internal enum StorageKind
{
    Unknown,
    Hdd,
    SataSsd,
    Nvme,
}

/// <summary>
/// Classifies the physical storage backing a file path as HDD, SATA SSD, or NVMe
/// via DeviceIoControl. The tuning of the analysis pipeline (worker count, I/O
/// strategy, prefetch) depends on this classification: parallelism that helps on
/// NVMe destroys throughput on a mechanical disk and vice versa.
///
/// Results are cached per physical disk number so the DeviceIoControl calls happen
/// at most once per disk per process lifetime, not once per file.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class StorageDetector
{
    private static readonly ConcurrentDictionary<int, StorageKind> s_kindByDisk = new();
    private static readonly ConcurrentDictionary<string, int> s_diskByVolume = new(
        StringComparer.OrdinalIgnoreCase
    );

    // Win32 constants
    private const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int StorageAdapterProperty = 1;
    private const int PropertyStandardQuery = 0;
    private const int BusTypeNvme = 17;
    private const uint FILE_SHARE_READ_WRITE = 0x00000003;
    private const uint OPEN_EXISTING = 3;

    /// <summary>
    /// Returns the physical disk number that hosts the volume of the given file path,
    /// or -1 if the lookup fails (network share, permission denied, virtual FS).
    /// </summary>
    public static int GetPhysicalDiskNumber(string filePath)
    {
        var volume = GetVolumePathName(filePath);
        if (volume is null)
            return -1;
        return s_diskByVolume.GetOrAdd(volume, ResolvePhysicalDisk);
    }

    /// <summary>
    /// Returns the storage kind (HDD, SATA SSD, NVMe) for the file path, falling back
    /// to <see cref="StorageKind.Unknown"/> on any failure.
    /// </summary>
    public static StorageKind GetKind(string filePath)
    {
        int disk = GetPhysicalDiskNumber(filePath);
        if (disk < 0)
            return StorageKind.Unknown;
        return s_kindByDisk.GetOrAdd(disk, ResolveKind);
    }

    /// <summary>
    /// Returns the storage kind for an already-resolved physical disk number.
    /// </summary>
    public static StorageKind GetKindForDisk(int physicalDiskNumber)
    {
        if (physicalDiskNumber < 0)
            return StorageKind.Unknown;
        return s_kindByDisk.GetOrAdd(physicalDiskNumber, ResolveKind);
    }

    private static string? GetVolumePathName(string filePath)
    {
        var buffer = new char[261];
        if (!GetVolumePathNameW(filePath, buffer, (uint)buffer.Length))
            return null;

        int len = Array.IndexOf(buffer, '\0');
        if (len <= 0)
            return null;
        return new string(buffer, 0, len);
    }

    private static int ResolvePhysicalDisk(string volumePath)
    {
        var devicePath = @"\\.\" + volumePath.TrimEnd('\\');

        using var handle = OpenDevice(devicePath);
        if (handle is null || handle.IsInvalid)
            return -1;

        // VOLUME_DISK_EXTENTS layout (x64, 24 bytes per extent, aligned on 8):
        //   offset 0  : DWORD NumberOfDiskExtents
        //   offset 4  : padding (DISK_EXTENT contains LARGE_INTEGER, aligned on 8)
        //   offset 8  : DWORD DiskNumber (first extent)
        //   offset 16 : LARGE_INTEGER StartingOffset
        //   offset 24 : LARGE_INTEGER ExtentLength
        var output = new byte[1024];
        if (
            !DeviceIoControl(
                handle,
                IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                null,
                0,
                output,
                (uint)output.Length,
                out _,
                IntPtr.Zero
            )
        )
            return -1;

        int numberOfExtents = BitConverter.ToInt32(output, 0);
        if (numberOfExtents <= 0)
            return -1;

        return BitConverter.ToInt32(output, 8);
    }

    private static StorageKind ResolveKind(int diskNumber)
    {
        var devicePath = $@"\\.\PhysicalDrive{diskNumber}";
        using var handle = OpenDevice(devicePath);
        if (handle is null || handle.IsInvalid)
            return StorageKind.Unknown;

        bool? seekPenalty = QuerySeekPenalty(handle);
        if (seekPenalty is true)
            return StorageKind.Hdd;

        int busType = QueryBusType(handle);
        if (busType == BusTypeNvme)
            return StorageKind.Nvme;

        if (seekPenalty is false)
            return StorageKind.SataSsd;

        return StorageKind.Unknown;
    }

    private static bool? QuerySeekPenalty(SafeFileHandle handle)
    {
        // STORAGE_PROPERTY_QUERY: 8 bytes header + 1 byte AdditionalParameters, padded to 12.
        var input = BuildPropertyQuery(StorageDeviceSeekPenaltyProperty);

        // DEVICE_SEEK_PENALTY_DESCRIPTOR:
        //   offset 0 : DWORD Version
        //   offset 4 : DWORD Size
        //   offset 8 : BOOLEAN IncursSeekPenalty
        var output = new byte[12];
        if (
            !DeviceIoControl(
                handle,
                IOCTL_STORAGE_QUERY_PROPERTY,
                input,
                (uint)input.Length,
                output,
                (uint)output.Length,
                out _,
                IntPtr.Zero
            )
        )
            return null;

        return output[8] != 0;
    }

    private static int QueryBusType(SafeFileHandle handle)
    {
        var input = BuildPropertyQuery(StorageAdapterProperty);

        // STORAGE_ADAPTER_DESCRIPTOR (only BusType matters here, at offset 24):
        //   0  : DWORD Version
        //   4  : DWORD Size
        //   8  : DWORD MaximumTransferLength
        //   12 : DWORD MaximumPhysicalPages
        //   16 : DWORD AlignmentMask
        //   20 : 4 × BOOLEAN (AdapterUsesPio, AdapterScansDown, CommandQueueing, AcceleratedTransfer)
        //   24 : BYTE BusType
        var output = new byte[64];
        if (
            !DeviceIoControl(
                handle,
                IOCTL_STORAGE_QUERY_PROPERTY,
                input,
                (uint)input.Length,
                output,
                (uint)output.Length,
                out _,
                IntPtr.Zero
            )
        )
            return -1;

        return output[24];
    }

    private static byte[] BuildPropertyQuery(int propertyId)
    {
        var query = new byte[12];
        BitConverter.GetBytes(propertyId).CopyTo(query, 0);
        BitConverter.GetBytes(PropertyStandardQuery).CopyTo(query, 4);
        return query;
    }

    private static SafeFileHandle? OpenDevice(string devicePath)
    {
        try
        {
            var handle = CreateFileW(
                devicePath,
                0,
                FILE_SHARE_READ_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero
            );
            return handle;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(
        string lpszFileName,
        [Out] char[] lpszVolumePathName,
        uint cchBufferLength
    );

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[]? lpInBuffer,
        uint nInBufferSize,
        byte[]? lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped
    );
}
