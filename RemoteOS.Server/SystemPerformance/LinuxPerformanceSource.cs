using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RemoteOS.Protocol.SystemMonitor;

namespace Server.SystemPerformance;

/// <summary>Linux 原始性能数据源。只读取 /proc、/sys 与 .NET 网络/文件系统 API，不保存差分状态。</summary>
public sealed class LinuxPerformanceSource : ISystemPerformanceSource
{
    public ValueTask<PerformanceInfoDto> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var memory = ReadMemInfo();
        var logicalCount = Environment.ProcessorCount;
        var capabilities = new PerformanceCapabilitiesDto(true, ReadCurrentFrequencyMHz() is not null, true,
            true, false, true, true, false);
        return ValueTask.FromResult(new PerformanceInfoDto(
            new CpuInfoDto(ReadCpuModel(), ReadPhysicalCoreCount(), logicalCount, null, ReadVirtualizationEnabled()),
            new MemoryInfoDto(memory.GetValueOrDefault("MemTotal"), memory.GetValueOrDefault("SwapTotal")),
            ReadFilesystemsInfo(), ReadDiskInfo(), ReadNetworkInfo(), capabilities));
    }

    public ValueTask<RawPerformanceSample> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mem = ReadMemInfo();
        var cpu = ReadCpu();
        var memory = new RawMemory(mem.GetValueOrDefault("MemTotal"), mem.GetValueOrDefault("MemAvailable"),
            ValueOrNull(mem, "Cached"), ValueOrNull(mem, "Buffers"), ValueOrNull(mem, "SwapTotal"), ValueOrNull(mem, "SwapFree"));
        return ValueTask.FromResult(new RawPerformanceSample(DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), cpu, memory,
            ReadFilesystemUsage(), ReadDiskCounters(), ReadNetworkCounters(), Environment.TickCount64 / 1000));
    }

    private static RawCpuTimes ReadCpu()
    {
        RawCpuTimes? aggregate = null;
        var logical = new List<RawCpuTimes>();
        try
        {
            foreach (var line in File.ReadLines("/proc/stat"))
            {
                if (!line.StartsWith("cpu", StringComparison.Ordinal)) continue;
                var values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (values.Length < 5 || (values[0] != "cpu" && !IsLogicalCpuName(values[0]))) continue;
                var counters = values.Skip(1).Select(ParseLong).ToArray();
                var user = At(counters, 0) + At(counters, 1);
                var system = At(counters, 2) + At(counters, 5) + At(counters, 6);
                var idle = At(counters, 3) + At(counters, 4);
                var item = new RawCpuTimes(counters.Sum(), idle, user, system, At(counters, 4), Array.Empty<RawCpuTimes>(), null);
                if (values[0] == "cpu") aggregate = item;
                else logical.Add(item);
            }
        }
        catch { /* source will expose an empty/zero sample rather than leak OS errors. */ }
        aggregate ??= new RawCpuTimes(0, 0, null, null, null, Array.Empty<RawCpuTimes>(), null);
        return aggregate with { LogicalProcessors = logical, CurrentFrequencyMHz = ReadCurrentFrequencyMHz() };
    }

    private static IReadOnlyList<RawFilesystemUsage> ReadFilesystemUsage()
    {
        var result = new List<RawFilesystemUsage>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady || drive.TotalSize <= 0) continue;
                    result.Add(new RawFilesystemUsage(FilesystemId(drive), drive.TotalSize, drive.AvailableFreeSpace));
                }
                catch { }
            }
        }
        catch { }
        return result;
    }

    private static IReadOnlyList<FilesystemInfoDto> ReadFilesystemsInfo()
    {
        var result = new List<FilesystemInfoDto>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    result.Add(new FilesystemInfoDto(FilesystemId(drive), drive.Name, drive.RootDirectory.FullName));
                }
                catch { }
            }
        }
        catch { }
        return result;
    }

    private static IReadOnlyList<DiskInfoDto> ReadDiskInfo()
    {
        var result = new List<DiskInfoDto>();
        try
        {
            foreach (var path in Directory.EnumerateDirectories("/sys/block"))
            {
                var name = Path.GetFileName(path);
                if (IsVirtualBlockDevice(name)) continue;
                result.Add(new DiskInfoDto(DiskId(name), name, ReadText(Path.Combine(path, "device/model")), Array.Empty<string>()));
            }
        }
        catch { }
        return result;
    }

    private static IReadOnlyList<RawDiskCounters> ReadDiskCounters()
    {
        var result = new List<RawDiskCounters>();
        try
        {
            foreach (var line in File.ReadLines("/proc/diskstats"))
            {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 14 || IsVirtualBlockDevice(fields[2])) continue;
                var name = fields[2];
                var sysPath = Path.Combine("/sys/block", name);
                // Partitions appear in diskstats but not under /sys/block; only report top-level devices to avoid double counting.
                if (!Directory.Exists(sysPath)) continue;
                var sectorSize = ParseInt(ReadText(Path.Combine(sysPath, "queue/hw_sector_size"))) is var size && size > 0 ? size : 512;
                result.Add(new RawDiskCounters(DiskId(name), ParseLong(fields[3]), ParseLong(fields[7]), ParseLong(fields[5]),
                    ParseLong(fields[9]), ParseLong(fields[12]), fields.Length > 13 ? ParseLong(fields[13]) : null,
                    ParseLong(fields[6]), ParseLong(fields[10]), sectorSize));
            }
        }
        catch { }
        return result;
    }

    private static IReadOnlyList<NetworkInterfaceInfoDto> ReadNetworkInfo()
    {
        var result = new List<NetworkInterfaceInfoDto>();
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.NetworkInterfaceType == NetworkInterfaceType.Loopback || network.OperationalStatus != OperationalStatus.Up) continue;
                var addresses = network.GetIPProperties().UnicastAddresses
                    .Where(x => (x.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6) && !IPAddress.IsLoopback(x.Address))
                    .Select(x => x.Address.ToString()).ToArray();
                result.Add(new NetworkInterfaceInfoDto(NetworkId(network.Name), network.Name,
                    network.Speed > 0 ? network.Speed : null, addresses));
            }
        }
        catch { }
        return result;
    }

    private static IReadOnlyList<RawNetworkCounters> ReadNetworkCounters()
    {
        var result = new List<RawNetworkCounters>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories("/sys/class/net"))
            {
                var name = Path.GetFileName(directory);
                if (name == "lo") continue;
                var stat = Path.Combine(directory, "statistics");
                result.Add(new RawNetworkCounters(NetworkId(name), ReadLong(stat, "rx_bytes"), ReadLong(stat, "tx_bytes"),
                    ReadLong(stat, "rx_packets"), ReadLong(stat, "tx_packets"), ReadLong(stat, "rx_errors"), ReadLong(stat, "tx_errors"),
                    ReadLong(stat, "rx_dropped"), ReadLong(stat, "tx_dropped")));
            }
        }
        catch { }
        return result;
    }

    private static Dictionary<string, long> ReadMemInfo()
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var key = line[..colon];
                var raw = line[(colon + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (long.TryParse(raw, out var kib)) values[key] = kib * 1024;
            }
        }
        catch { }
        return values;
    }

    private static string? ReadCpuModel()
    {
        try
        {
            return File.ReadLines("/proc/cpuinfo").FirstOrDefault(x => x.StartsWith("model name", StringComparison.Ordinal))?
                .Split(':', 2).ElementAtOrDefault(1)?.Trim();
        }
        catch { return null; }
    }

    private static int? ReadPhysicalCoreCount()
    {
        try
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            string? physical = null;
            string? core = null;
            foreach (var line in File.ReadLines("/proc/cpuinfo").Append(string.Empty))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    if (physical is not null && core is not null) result.Add($"{physical}:{core}");
                    physical = core = null;
                    continue;
                }
                if (line.StartsWith("physical id", StringComparison.Ordinal)) physical = line.Split(':', 2)[1].Trim();
                if (line.StartsWith("core id", StringComparison.Ordinal)) core = line.Split(':', 2)[1].Trim();
            }
            return result.Count > 0 ? result.Count : null;
        }
        catch { return null; }
    }

    private static bool? ReadVirtualizationEnabled()
    {
        try
        {
            var flags = File.ReadLines("/proc/cpuinfo").FirstOrDefault(x => x.StartsWith("flags", StringComparison.Ordinal));
            return flags is null ? null : flags.Contains(" vmx ", StringComparison.Ordinal) || flags.Contains(" svm ", StringComparison.Ordinal);
        }
        catch { return null; }
    }

    private static double? ReadCurrentFrequencyMHz()
    {
        try
        {
            // Do not use AllDirectories on sysfs: cpu topology contains symlinks and can make a one-second sampler
            // unexpectedly traverse a very large graph. Each logical CPU has one known direct cpufreq path.
            var frequencies = Directory.EnumerateDirectories("/sys/devices/system/cpu", "cpu*", SearchOption.TopDirectoryOnly)
                .Select(path => ReadText(Path.Combine(path, "cpufreq", "scaling_cur_freq")))
                .Where(x => double.TryParse(x, out _)).Select(x => double.Parse(x!) / 1000d).ToArray();
            return frequencies.Length == 0 ? null : Math.Round(frequencies.Average(), 1);
        }
        catch { return null; }
    }

    private static bool IsLogicalCpuName(string name) => name.Length > 3 && name.StartsWith("cpu", StringComparison.Ordinal) && name[3..].All(char.IsDigit);
    private static bool IsVirtualBlockDevice(string name) => name.StartsWith("loop", StringComparison.Ordinal) || name.StartsWith("ram", StringComparison.Ordinal) || name.StartsWith("zram", StringComparison.Ordinal) || name.StartsWith("fd", StringComparison.Ordinal) || name.StartsWith("dm-", StringComparison.Ordinal);
    private static string FilesystemId(DriveInfo drive) => $"fs:{drive.RootDirectory.FullName}";
    private static string DiskId(string name) => $"linux-disk:{name}";
    private static string NetworkId(string name) => $"linux-net:{ReadText(Path.Combine("/sys/class/net", name, "ifindex")) ?? name}";
    private static long? ValueOrNull(IReadOnlyDictionary<string, long> values, string key) => values.TryGetValue(key, out var value) ? value : null;
    private static long At(IReadOnlyList<long> values, int index) => index < values.Count ? values[index] : 0;
    private static long ParseLong(string? value) => long.TryParse(value, out var result) ? result : 0;
    private static int ParseInt(string? value) => int.TryParse(value, out var result) ? result : 0;
    private static string? ReadText(string path) { try { return File.ReadAllText(path).Trim() is { Length: > 0 } value ? value : null; } catch { return null; } }
    private static long ReadLong(string directory, string name) => ParseLong(ReadText(Path.Combine(directory, name)));
}
