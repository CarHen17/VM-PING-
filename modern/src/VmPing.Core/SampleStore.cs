using System.Globalization;
using System.Text;
namespace VmPing.Core;

public record SampleBucket(DateTimeOffset Start, DateTimeOffset End, int Sent, int Received, int Errors, double? Average, double? Maximum, double? Minimum)
{
    public double LossPercent => Sent - Errors <= 0 ? 0 : 100d * (Sent - Errors - Received) / (Sent - Errors);
}
// Every probe result per host, one CSV file per host per day: "unix_ms,value" where value is the RTT,
// -1 for a lost probe and -2 for a local error. Writes are buffered and flushed periodically.
public sealed class SampleStore(string directory)
{
    public string Directory { get; } = directory;
    private readonly Lock gate = new();
    private readonly Dictionary<string, StringBuilder> pending = [];
    public void Add(Target target, ProbeResult result)
    {
        var value = result.Kind switch
        {
            ResultKind.Success => (result.Milliseconds ?? 0).ToString("0.###", CultureInfo.InvariantCulture),
            ResultKind.LocalError => "-2",
            _ => "-1"
        };
        var file = FileFor(target, result.Time);
        lock (gate)
        {
            if (!pending.TryGetValue(file, out var buffer)) pending[file] = buffer = new();
            buffer.Append(result.Time.ToUnixTimeMilliseconds()).Append(',').Append(value).Append('\n');
        }
    }
    public void Flush()
    {
        KeyValuePair<string, StringBuilder>[] work;
        lock (gate) { work = [.. pending]; pending.Clear(); }
        foreach (var (file, buffer) in work)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.AppendAllText(file, buffer.ToString(), Encoding.ASCII);
        }
    }
    public void Cleanup(int retentionDays, DateTimeOffset now)
    {
        if (!System.IO.Directory.Exists(Directory)) return;
        var oldest = now.UtcDateTime.Date.AddDays(-retentionDays);
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.csv", SearchOption.AllDirectories))
            if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day) && day < oldest) File.Delete(file);
    }
    // Aggregates the range into at most `buckets` time slots. Empty slots are omitted.
    public List<SampleBucket> Load(Target target, DateTimeOffset from, DateTimeOffset to, int buckets)
    {
        Flush();
        var span = (to - from).Ticks / Math.Max(1, buckets);
        if (span <= 0) return [];
        var slots = new (int Sent, int Received, int Errors, double Total, double Max, double Min)[buckets];
        for (var day = from.UtcDateTime.Date; day <= to.UtcDateTime.Date; day = day.AddDays(1))
        {
            var file = FileFor(target, new DateTimeOffset(day, TimeSpan.Zero));
            if (!File.Exists(file)) continue;
            foreach (var line in File.ReadLines(file))
            {
                var comma = line.IndexOf(',');
                if (comma <= 0 || !long.TryParse(line.AsSpan(0, comma), out var unix) ||
                    !double.TryParse(line.AsSpan(comma + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) continue;
                var time = DateTimeOffset.FromUnixTimeMilliseconds(unix);
                if (time < from || time >= to) continue;
                var index = (int)Math.Min(buckets - 1, (time - from).Ticks / span);
                ref var slot = ref slots[index];
                if (slot.Sent == 0) slot.Min = double.PositiveInfinity;
                slot.Sent++;
                if (value == -2) slot.Errors++;
                else if (value >= 0) { slot.Received++; slot.Total += value; slot.Max = Math.Max(slot.Max, value); slot.Min = Math.Min(slot.Min, value); }
            }
        }
        var result = new List<SampleBucket>();
        for (var i = 0; i < buckets; i++)
        {
            var slot = slots[i];
            if (slot.Sent == 0) continue;
            var start = from + TimeSpan.FromTicks(span * i);
            result.Add(new(start, start + TimeSpan.FromTicks(span), slot.Sent, slot.Received, slot.Errors,
                slot.Received == 0 ? null : slot.Total / slot.Received, slot.Received == 0 ? null : slot.Max, slot.Received == 0 ? null : slot.Min));
        }
        return result;
    }
    private string FileFor(Target target, DateTimeOffset time)
    {
        var name = new StringBuilder();
        foreach (var c in $"{target.Kind}_{target.Address}") name.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_');
        return Path.Combine(Directory, name.ToString(), time.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv");
    }
}
