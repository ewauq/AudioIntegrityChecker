using System.Collections.Concurrent;
using System.Management;
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
/// Rich description of the physical storage backing a file path: device model,
/// bus (with USB version when available), media kind and capacity. The analysis
/// pipeline uses <see cref="Kind"/> to pick a worker count strategy; the UI uses
/// the rest to render an informative status bar indicator.
/// </summary>
internal sealed record StorageInfo(
    StorageKind Kind,
    string? FriendlyName,
    string BusDisplay,
    long SizeBytes
)
{
    public static StorageInfo Unknown { get; } = new(StorageKind.Unknown, null, "Unknown", 0);
}

/// <summary>
/// Classifies the physical storage backing a file path via a layered resolver:
/// WMI <c>MSFT_PhysicalDisk</c> as the primary source (friendly name, media
/// type, bus type, size), <c>IOCTL_STORAGE_QUERY_PROPERTY</c> and
/// <c>IOCTL_ATA_PASS_THROUGH_EX</c> as fallbacks when WMI cannot determine the
/// media type (older USB bridges). USB version is inferred by walking the PnP
/// parent chain via <c>cfgmgr32</c> until a root hub node is found.
///
/// Results are cached per physical disk number so every resolver runs at most
/// once per disk per process lifetime.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class StorageDetector
{
    private static readonly ConcurrentDictionary<int, StorageInfo> s_infoByDisk = new();
    private static readonly ConcurrentDictionary<string, int> s_diskByVolume = new(
        StringComparer.OrdinalIgnoreCase
    );

    // Win32 constants
    private const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const uint IOCTL_ATA_PASS_THROUGH_EX = 0x0004D02C;
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
    /// Returns the full storage descriptor for an already-resolved physical disk number.
    /// </summary>
    public static StorageInfo GetInfoForDisk(int physicalDiskNumber)
    {
        if (physicalDiskNumber < 0)
            return StorageInfo.Unknown;
        return s_infoByDisk.GetOrAdd(physicalDiskNumber, ResolveInfo);
    }

    /// <summary>
    /// Convenience wrapper for callers that only need the coarse kind.
    /// </summary>
    public static StorageKind GetKindForDisk(int physicalDiskNumber) =>
        GetInfoForDisk(physicalDiskNumber).Kind;

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

    private static StorageInfo ResolveInfo(int diskNumber)
    {
        // Primary source: WMI MSFT_PhysicalDisk. Gives friendly name, bus type,
        // size, and media type in a single query. Windows aggregates multiple
        // lower-level sources for media type, so this is the most reliable path
        // for internal disks and most USB bridges.
        var wmi = TryWmiDetect(diskNumber);
        var kind = wmi?.Kind ?? StorageKind.Unknown;

        // Fallback 1: seek penalty via DeviceIoControl. Sub-millisecond for
        // internal SATA/NVMe if WMI was unavailable for some reason.
        if (kind == StorageKind.Unknown)
        {
            var ioKind = ResolveKindViaIoControl(diskNumber);
            if (ioKind != StorageKind.Unknown)
                kind = ioKind;
        }

        // Fallback 2: direct ATA IDENTIFY DEVICE via SAT pass-through. May
        // succeed for USB bridges where Windows couldn't populate MediaType.
        // Requires FILE_READ_ACCESS so may silently fail without admin rights.
        if (kind == StorageKind.Unknown)
        {
            var ataKind = TryAtaIdentify(diskNumber);
            if (ataKind is not null)
                kind = ataKind.Value;
        }

        string? friendlyName = wmi?.FriendlyName;
        long sizeBytes = wmi?.SizeBytes ?? 0;
        string busDisplay = wmi?.BusDisplay ?? "Unknown";

        // Enrich USB bus with version when possible (e.g. "USB 3.x", "USB 2.0").
        if (busDisplay == "USB")
        {
            var version = TryResolveUsbVersion(diskNumber);
            if (version is not null)
                busDisplay = $"USB {version}";
        }

        return new StorageInfo(kind, friendlyName, busDisplay, sizeBytes);
    }

    private static StorageKind ResolveKindViaIoControl(int diskNumber)
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

    private static StorageKind? TryAtaIdentify(int diskNumber)
    {
        var devicePath = $@"\\.\PhysicalDrive{diskNumber}";
        using var handle = OpenDevice(devicePath);
        if (handle is null || handle.IsInvalid)
            return null;

        // ATA_PASS_THROUGH_EX layout on x64 (48 bytes):
        //   0  : USHORT Length
        //   2  : USHORT AtaFlags          (0x02 = ATA_FLAGS_DATA_IN)
        //   4  : UCHAR PathId / TargetId / Lun / ReservedAsUchar
        //   8  : ULONG DataTransferLength (512 for IDENTIFY DEVICE)
        //   12 : ULONG TimeOutValue       (seconds)
        //   16 : ULONG ReservedAsUlong
        //   20 : padding (ULONG_PTR alignment on 8 bytes)
        //   24 : ULONG_PTR DataBufferOffset (= 48, bytes immediately after struct)
        //   32 : UCHAR PreviousTaskFile[8]
        //   40 : UCHAR CurrentTaskFile[8]  (command byte at offset 46 = 40+6)
        // Followed by 512 bytes of IDENTIFY DEVICE response data.
        const int StructSize = 48;
        const int DataSize = 512;
        var buffer = new byte[StructSize + DataSize];

        BitConverter.GetBytes((ushort)StructSize).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)0x02).CopyTo(buffer, 2); // ATA_FLAGS_DATA_IN
        BitConverter.GetBytes((uint)DataSize).CopyTo(buffer, 8);
        BitConverter.GetBytes((uint)3).CopyTo(buffer, 12); // 3 second timeout
        BitConverter.GetBytes((ulong)StructSize).CopyTo(buffer, 24);
        buffer[46] = 0xEC; // ATA command: IDENTIFY DEVICE

        if (
            !DeviceIoControl(
                handle,
                IOCTL_ATA_PASS_THROUGH_EX,
                buffer,
                (uint)buffer.Length,
                buffer,
                (uint)buffer.Length,
                out _,
                IntPtr.Zero
            )
        )
            return null;

        // IDENTIFY DEVICE word 217 = Nominal Media Rotation Rate.
        // 0x0000 = not reported, 0x0001 = non-rotating (SSD),
        // 0x0401..0xFFFE = nominal rotation rate in RPM (HDD).
        ushort rotationRate = BitConverter.ToUInt16(buffer, StructSize + 217 * 2);

        return rotationRate switch
        {
            0x0001 => StorageKind.SataSsd,
            >= 0x0401 and <= 0xFFFE => StorageKind.Hdd,
            _ => null,
        };
    }

    private static StorageInfo? TryWmiDetect(int diskNumber)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"\\.\root\microsoft\windows\storage",
                $"SELECT FriendlyName, MediaType, BusType, Size FROM MSFT_PhysicalDisk WHERE DeviceId = '{diskNumber}'"
            );
            foreach (var obj in searcher.Get())
            {
                using (obj)
                {
                    var friendlyName = (obj["FriendlyName"] as string)?.Trim();
                    ushort mediaType = obj["MediaType"] is ushort m ? m : (ushort)0;
                    ushort busType = obj["BusType"] is ushort b ? b : (ushort)0;
                    long sizeBytes = obj["Size"] switch
                    {
                        ulong u => (long)u,
                        long l => l,
                        string s when long.TryParse(s, out var parsed) => parsed,
                        _ => 0,
                    };

                    // MSFT_PhysicalDisk.MediaType: 3=HDD, 4=SSD, 5=SCM.
                    var kind = mediaType switch
                    {
                        3 => StorageKind.Hdd,
                        4 => busType == 17 ? StorageKind.Nvme : StorageKind.SataSsd,
                        5 => StorageKind.Nvme,
                        _ => StorageKind.Unknown,
                    };

                    // MSFT_PhysicalDisk.BusType enum (Windows Storage API):
                    // 1 SCSI, 2 ATAPI, 3 ATA, 6 Fibre Channel, 7 USB, 8 RAID,
                    // 9 iSCSI, 10 SAS, 11 SATA, 17 NVMe.
                    var busDisplay = busType switch
                    {
                        1 => "SCSI",
                        2 => "ATAPI",
                        3 => "ATA",
                        6 => "Fibre Channel",
                        7 => "USB",
                        8 => "RAID",
                        9 => "iSCSI",
                        10 => "SAS",
                        11 => "SATA",
                        17 => "NVMe",
                        _ => "Unknown",
                    };

                    return new StorageInfo(kind, friendlyName, busDisplay, sizeBytes);
                }
            }
        }
        catch
        {
            // WMI unavailable (locked-down environment, missing service).
        }
        return null;
    }

    private static string? TryResolveUsbVersion(int diskNumber)
    {
        var pnpDeviceId = GetDiskPnpId(diskNumber);
        if (pnpDeviceId is null)
            return null;

        if (CM_Locate_DevNodeW(out uint devNode, pnpDeviceId, 0) != 0)
            return null;

        var buffer = new char[1024];
        for (int depth = 0; depth < 20; depth++)
        {
            if (CM_Get_Parent(out uint parent, devNode, 0) != 0)
                return null;

            if (CM_Get_Device_IDW(parent, buffer, buffer.Length, 0) != 0)
                return null;

            int nullIdx = Array.IndexOf(buffer, '\0');
            if (nullIdx < 0)
                nullIdx = buffer.Length;
            var parentId = new string(buffer, 0, nullIdx);

            if (parentId.StartsWith(@"USB\ROOT_HUB30", StringComparison.OrdinalIgnoreCase))
                return "3";
            if (parentId.StartsWith(@"USB\ROOT_HUB20", StringComparison.OrdinalIgnoreCase))
                return "2";

            devNode = parent;
        }

        return null;
    }

    private static string? GetDiskPnpId(int diskNumber)
    {
        try
        {
            var devicePath = $@"\\.\PHYSICALDRIVE{diskNumber}";
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, PNPDeviceID FROM Win32_DiskDrive"
            );
            foreach (var obj in searcher.Get())
            {
                using (obj)
                {
                    var deviceId = obj["DeviceID"] as string;
                    if (string.Equals(deviceId, devicePath, StringComparison.OrdinalIgnoreCase))
                        return obj["PNPDeviceID"] as string;
                }
            }
        }
        catch
        {
            // WMI unavailable.
        }
        return null;
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

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int CM_Locate_DevNodeW(
        out uint pdnDevInst,
        string pDeviceID,
        uint ulFlags
    );

    [DllImport("cfgmgr32.dll", SetLastError = true)]
    private static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int CM_Get_Device_IDW(
        uint dnDevInst,
        [Out] char[] buffer,
        int bufferLen,
        uint ulFlags
    );
}
