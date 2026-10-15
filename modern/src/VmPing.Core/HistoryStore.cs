using System.Text;
namespace VmPing.Core;

// Append-only event log next to the workspace. Rotates to history.log.1 so it never grows without bound.
public sealed class HistoryStore(string path, long maxBytes = 5_000_000)
{
    public string Path { get; } = path;
    public void Append(string line)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        if (File.Exists(Path) && new FileInfo(Path).Length > maxBytes) File.Move(Path, Path + ".1", true);
        File.AppendAllText(Path, line.ReplaceLineEndings(" ") + "\n", Encoding.UTF8);
    }
    // Newest first, matching the order shown in the timeline.
    public List<string> LoadRecent(int count)
    {
        if (!File.Exists(Path)) return [];
        var lines = new Queue<string>(count);
        foreach (var line in File.ReadLines(Path, Encoding.UTF8))
        {
            if (line.Length == 0) continue;
            if (lines.Count == count) lines.Dequeue();
            lines.Enqueue(line);
        }
        return lines.Reverse().ToList();
    }
}
