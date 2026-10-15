using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
namespace VmPing.Core;

public static class WorkspaceStore
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "vmPing-modern", "workspace.json");
    public static Workspace Load(string path)
    {
        if (!File.Exists(path)) return new();
        var result = JsonSerializer.Deserialize<Workspace>(File.ReadAllText(path)) ?? throw new InvalidDataException("Configuração vazia.");
        if (result.Targets is null || result.Favorites is null || result.Aliases is null ||
            result.IntervalMs is < 100 or > 60000 || result.TimeoutMs is < 100 or > 60000)
            throw new InvalidDataException("Configuração inválida. O arquivo foi preservado.");
        foreach (var target in result.Targets.Concat(result.Favorites.SelectMany(x => x.Targets))) Target.Parse(target.Address);
        result.Aliases = new(result.Aliases, StringComparer.OrdinalIgnoreCase);
        return result;
    }
    public static void Save(string path, Workspace workspace)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(workspace, new JsonSerializerOptions { WriteIndented = true }));
        if (File.Exists(path)) File.Copy(path, path + ".bak", true);
        File.Move(temporary, path, true);
    }
    public static Workspace ImportLegacy(string path)
    {
        // Read-only import; credentials and the original XML are never copied or changed.
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 10_000_000 });
        var root = XDocument.Load(reader).Root;
        if (root?.Name != "vmping") throw new InvalidDataException("Selecione um vmPing.xml válido.");
        var workspace = new Workspace();
        foreach (var node in root.Element("aliases")?.Elements("alias") ?? [])
            if (node.Attribute("host")?.Value is string host) workspace.Aliases[host] = node.Value.Trim();
        foreach (var node in root.Element("favorites")?.Elements("favorite") ?? [])
        {
            var targets = node.Elements("host").Where(x => !string.IsNullOrWhiteSpace(x.Value)).Select(x =>
            {
                var value = x.Value.Trim();
                return Target.Parse(value, workspace.Aliases.GetValueOrDefault(value, ""));
            }).ToList();
            workspace.Favorites.Add(new(node.Attribute("title")?.Value ?? "Importado", targets));
        }
        workspace.Targets = workspace.Favorites.FirstOrDefault()?.Targets.ToList() ?? [];
        var options = root.Element("configuration")?.Elements("option").Where(x => x.Attribute("name") != null)
            .GroupBy(x => x.Attribute("name")!.Value).ToDictionary(x => x.Key, x => x.Last().Value) ?? [];
        if (int.TryParse(options.GetValueOrDefault("PingInterval"), out var interval)) workspace.IntervalMs = Math.Clamp(interval, 100, 60000);
        if (int.TryParse(options.GetValueOrDefault("PingTimeout"), out var timeout)) workspace.TimeoutMs = Math.Clamp(timeout, 100, 60000);
        return workspace;
    }
}
