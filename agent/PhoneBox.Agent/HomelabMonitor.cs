using System.Globalization;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>Servers whose Prometheus node_exporter the agent reads for the Home lab widget.</summary>
public sealed class HomelabConfig
{
    public List<HomelabHost> Hosts { get; set; } = [];
}

public sealed class HomelabHost
{
    public string Name { get; set; } = "";
    /// <summary>node_exporter address, e.g. http://100.64.0.10:9100/metrics (a bare host:port gets /metrics added).</summary>
    public string Url { get; set; } = "";
}

public sealed record HomelabDisk(string Mount, double UsedGb, double TotalGb);

public sealed record HomelabSnapshot(
    string Name, bool Online, string? Error, long Updated,
    string? Os, string? Node, int Cpus, double? CpuPct, double? Load1, double? TempC, long? UptimeSec,
    double? MemUsedGb, double? MemTotalGb, IReadOnlyList<HomelabDisk> Disks,
    double? RxBps, double? TxBps,
    IReadOnlyList<double> CpuHist, IReadOnlyList<double> RxHist, IReadOnlyList<double> TxHist);

/// <summary>
/// Scrapes node_exporter on the configured servers (over Tailscale or the LAN) every <see cref="IntervalSec"/> seconds,
/// only while an enabled page shows the Home lab widget. CPU and network are rates between two scrapes; the first
/// scrape and a counter reset (a reboot) give no rate for that step.
/// </summary>
public sealed class HomelabMonitor(ConfigStore config, ILogger<HomelabMonitor> log) : BackgroundService
{
    public const int IntervalSec = 10;
    private const int HistLen = 90; // 15 minutes
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private sealed class State
    {
        public double CpuBusy, CpuTotal, Rx, Tx; public long At;
        public readonly List<double> CpuHist = [], RxHist = [], TxHist = [];
        public HomelabSnapshot? Last;
    }

    private readonly Dictionary<string, State> _state = [];
    private volatile IReadOnlyList<HomelabSnapshot> _snap = [];

    public IReadOnlyList<HomelabSnapshot> Snapshot() => _snap;

    private bool Shown() => config.Get().Widgets(enabledPagesOnly: true).Any(w => w.Type == "homelab");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var hosts = config.Get().Homelab.Hosts.Where(h => !string.IsNullOrWhiteSpace(h.Url)).ToList();
            if (hosts.Count > 0 && Shown())
            {
                var results = await Task.WhenAll(hosts.Select(h => Scrape(h, ct)));
                foreach (var gone in _state.Keys.Except(hosts.Select(Key)).ToList()) _state.Remove(gone);
                _snap = results;
            }
            else if (hosts.Count == 0) _snap = [];
            await Wake.Delay(TimeSpan.FromSeconds(IntervalSec), ct);
        }
    }

    private static string Key(HomelabHost h) => h.Name + "|" + h.Url;

    internal static string MetricsUrl(string url)
    {
        url = url.Trim();
        if (!url.Contains("://")) url = "http://" + url;
        var u = new Uri(url);
        return u.AbsolutePath is "" or "/" ? new UriBuilder(u) { Path = "/metrics" }.Uri.ToString() : u.ToString();
    }

    private async Task<HomelabSnapshot> Scrape(HomelabHost host, CancellationToken ct)
    {
        var key = Key(host);
        if (!_state.TryGetValue(key, out var st)) _state[key] = st = new State();
        var name = string.IsNullOrWhiteSpace(host.Name) ? host.Url : host.Name.Trim();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            var text = await Http.GetStringAsync(MetricsUrl(host.Url), ct);
            var m = Parse(text);
            var snap = Build(name, m, st, now);
            st.Last = snap;
            return snap;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (st.Last is null || now - st.Last.Updated > 60_000) log.LogDebug("homelab {Name}: {Error}", name, ex.Message);
            var last = st.Last;
            return last is null
                ? new HomelabSnapshot(name, false, "No answer", 0, null, null, 0, null, null, null, null, null, null, [], null, null, [], [], [])
                : last with { Online = false, Error = "No answer" };
        }
    }

    // ---------------------------------------------------------------- the Prometheus text format

    internal sealed record Sample(string Name, Dictionary<string, string> Labels, double Value);

    private static readonly Regex LabelRe = new("(\\w+)=\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

    internal static List<Sample> Parse(string text)
    {
        var list = new List<Sample>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            string name, rest;
            var labels = new Dictionary<string, string>();
            var brace = line.IndexOf('{');
            var space = line.IndexOf(' ');
            if (brace >= 0 && (space < 0 || brace < space))
            {
                var close = line.LastIndexOf('}');
                if (close < brace) continue;
                name = line[..brace];
                foreach (Match lm in LabelRe.Matches(line[(brace + 1)..close])) labels[lm.Groups[1].Value] = lm.Groups[2].Value;
                rest = line[(close + 1)..].Trim();
            }
            else
            {
                if (space < 0) continue;
                name = line[..space];
                rest = line[(space + 1)..].Trim();
            }
            var valueText = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (valueText is null || !double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;
            list.Add(new Sample(name, labels, v));
        }
        return list;
    }

    /// <summary>Real network ports only: bridges, taps, veths, Docker and Tailscale would count the same traffic twice.</summary>
    internal static bool PhysicalNic(string dev) =>
        dev != "lo" && !Regex.IsMatch(dev, "^(veth|tap|fwbr|fwln|fwpr|vmbr|br-|docker|tailscale|virbr|cni|flannel|kube|lxc|wg|tun|zt)");

    private static readonly HashSet<string> RealFs = ["ext4", "ext3", "xfs", "btrfs", "zfs", "f2fs", "apfs", "ntfs"];

    internal static HomelabSnapshot Build(string name, List<Sample> m, object stateObj, long now)
    {
        var st = (State)stateObj;
        double? One(string n) => m.FirstOrDefault(s => s.Name == n)?.Value;

        // CPU: busy = everything but idle and iowait, as a share of all CPU time since the previous scrape
        var cpu = m.Where(s => s.Name == "node_cpu_seconds_total").ToList();
        var cpus = cpu.Select(s => s.Labels.GetValueOrDefault("cpu")).Distinct().Count();
        var total = cpu.Sum(s => s.Value);
        var busy = cpu.Where(s => s.Labels.GetValueOrDefault("mode") is not ("idle" or "iowait")).Sum(s => s.Value);
        double? cpuPct = null;
        if (st.At > 0 && total > st.CpuTotal && busy >= st.CpuBusy)
            cpuPct = Math.Clamp((busy - st.CpuBusy) / (total - st.CpuTotal) * 100, 0, 100);

        var nets = m.Where(s => s.Name is "node_network_receive_bytes_total" or "node_network_transmit_bytes_total"
            && PhysicalNic(s.Labels.GetValueOrDefault("device") ?? "")).ToList();
        var rx = nets.Where(s => s.Name == "node_network_receive_bytes_total").Sum(s => s.Value);
        var tx = nets.Where(s => s.Name == "node_network_transmit_bytes_total").Sum(s => s.Value);
        double? rxBps = null, txBps = null;
        var dt = (now - st.At) / 1000.0;
        if (st.At > 0 && dt > 0.5)
        {
            if (rx >= st.Rx) rxBps = (rx - st.Rx) / dt;
            if (tx >= st.Tx) txBps = (tx - st.Tx) / dt;
        }
        st.CpuBusy = busy; st.CpuTotal = total; st.Rx = rx; st.Tx = tx; st.At = now;
        Push(st.CpuHist, cpuPct); Push(st.RxHist, rxBps); Push(st.TxHist, txBps);

        var memTotal = One("node_memory_MemTotal_bytes");
        var memAvail = One("node_memory_MemAvailable_bytes");
        const double Gb = 1024d * 1024 * 1024;

        var disks = m.Where(s => s.Name == "node_filesystem_size_bytes" && RealFs.Contains(s.Labels.GetValueOrDefault("fstype") ?? "")
                && s.Value > 1 * Gb)
            .Select(s =>
            {
                var mount = s.Labels.GetValueOrDefault("mountpoint") ?? "";
                var avail = m.FirstOrDefault(a => a.Name == "node_filesystem_avail_bytes" && a.Labels.GetValueOrDefault("mountpoint") == mount)?.Value ?? 0;
                return new HomelabDisk(mount, Math.Round((s.Value - avail) / Gb, 1), Math.Round(s.Value / Gb, 1));
            })
            .GroupBy(d => d.Mount).Select(g => g.First())
            .OrderBy(d => d.Mount == "/" ? 0 : 1).ThenByDescending(d => d.TotalGb).Take(3).ToList();

        var temps = m.Where(s => s.Name == "node_hwmon_temp_celsius" && Regex.IsMatch(s.Labels.GetValueOrDefault("chip") ?? "", "coretemp|k10temp|cpu|soc|zenpower")).Select(s => s.Value).ToList();
        if (temps.Count == 0) temps = m.Where(s => s.Name == "node_thermal_zone_temp").Select(s => s.Value).ToList();
        double? temp = temps.Count > 0 ? Math.Round(temps.Max()) : null;

        var bootAt = One("node_boot_time_seconds");
        var nowSec = One("node_time_seconds");
        long? uptime = bootAt is { } b && nowSec is { } n && n > b ? (long)(n - b) : null;

        var osInfo = m.FirstOrDefault(s => s.Name == "node_os_info");
        var os = osInfo is null ? null : osInfo.Labels.GetValueOrDefault("name") ?? osInfo.Labels.GetValueOrDefault("id");
        if (osInfo?.Labels.GetValueOrDefault("pretty_name") is { } pretty && pretty.Contains("Proxmox", StringComparison.OrdinalIgnoreCase)) os = "Proxmox";
        if (m.Any(s => s.Name == "node_uname_info" && (s.Labels.GetValueOrDefault("release") ?? "").EndsWith("-pve"))) os = "Proxmox";
        var node = m.FirstOrDefault(s => s.Name == "node_uname_info")?.Labels.GetValueOrDefault("nodename");

        return new HomelabSnapshot(name, true, null, now, os, node, cpus, Round(cpuPct), Round(One("node_load1"), 2), temp, uptime,
            memTotal is { } t && memAvail is { } a ? Math.Round((t - a) / Gb, 1) : null, memTotal is { } t2 ? Math.Round(t2 / Gb, 1) : null,
            disks, Round(rxBps, 0), Round(txBps, 0), [.. st.CpuHist], [.. st.RxHist], [.. st.TxHist]);
    }

    internal static object NewState() => new State();

    private static double? Round(double? v, int digits = 1) => v is { } x ? Math.Round(x, digits) : null;

    private static void Push(List<double> hist, double? v)
    {
        if (v is null) return;
        hist.Add(Math.Round(v.Value, 1));
        if (hist.Count > HistLen) hist.RemoveAt(0);
    }
}
