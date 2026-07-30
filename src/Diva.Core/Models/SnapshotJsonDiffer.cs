namespace Diva.Core.Models;

using System.Text.Json;

/// <summary>
/// Generic, type-agnostic field-level diff between two snapshot JSON documents. Works for any
/// promotable object's SnapshotJson shape without per-type diff logic. Arrays are compared as
/// atomic values (their raw JSON text), not recursively diffed element-by-element.
/// </summary>
public static class SnapshotJsonDiffer
{
    public static IReadOnlyList<SnapshotFieldDiff> Diff(string fromJson, string toJson)
    {
        var diffs = new List<SnapshotFieldDiff>();
        using var fromDoc = JsonDocument.Parse(fromJson);
        using var toDoc = JsonDocument.Parse(toJson);
        DiffElement(string.Empty, fromDoc.RootElement, toDoc.RootElement, diffs);
        return diffs;
    }

    private static void DiffElement(string path, JsonElement? from, JsonElement? to, List<SnapshotFieldDiff> diffs)
    {
        if (from is null && to is null)
        {
            return;
        }

        if (from is null)
        {
            diffs.Add(new SnapshotFieldDiff(path, null, to!.Value.ToString()));
            return;
        }

        if (to is null)
        {
            diffs.Add(new SnapshotFieldDiff(path, from!.Value.ToString(), null));
            return;
        }

        var f = from.Value;
        var t = to.Value;

        if (f.ValueKind == JsonValueKind.Object && t.ValueKind == JsonValueKind.Object)
        {
            var keys = f.EnumerateObject().Select(p => p.Name)
                .Union(t.EnumerateObject().Select(p => p.Name), StringComparer.Ordinal);
            foreach (var key in keys)
            {
                var childPath = path.Length == 0 ? key : $"{path}.{key}";
                JsonElement? fc = f.TryGetProperty(key, out var fv) ? fv : null;
                JsonElement? tc = t.TryGetProperty(key, out var tv) ? tv : null;
                DiffElement(childPath, fc, tc, diffs);
            }

            return;
        }

        var fs = f.ToString();
        var ts = t.ToString();
        if (fs != ts)
        {
            diffs.Add(new SnapshotFieldDiff(path, fs, ts));
        }
    }
}
