using System.Runtime.CompilerServices;
using System.Text.Json;
using Diva.Rag.Abstractions;
using Microsoft.Extensions.Logging;

namespace Diva.Rag.Connectors;

/// <summary>
/// Reads documents from the local file system. Used for local/agent-scoped indexing.
/// Config JSON expects: { "paths": ["/data/docs"], "includePatterns": ["*.md", "*.txt"], "excludePatterns": ["*.tmp"] }
/// </summary>
public sealed class FileDocumentConnector(ILogger<FileDocumentConnector> logger) : IDocumentConnector
{
    public string SourceType => "file";

    public async IAsyncEnumerable<RawDocument> ConnectAsync(
        DocumentSourceConfig config, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var paths = new List<string>();
        var includePatterns = new List<string> { "*.*" };
        var excludePatterns = new List<string>();

        if (config.Config is not null)
        {
            var root = config.Config.RootElement;
            if (root.TryGetProperty("paths", out var pathsProp))
                paths = pathsProp.EnumerateArray().Select(p => p.GetString()!).ToList();
            if (root.TryGetProperty("includePatterns", out var incProp))
                includePatterns = incProp.EnumerateArray().Select(p => p.GetString()!).ToList();
            if (root.TryGetProperty("excludePatterns", out var excProp))
                excludePatterns = excProp.EnumerateArray().Select(p => p.GetString()!).ToList();
        }

        foreach (var basePath in paths)
        {
            if (!Directory.Exists(basePath))
            {
                logger.LogWarning("File connector path does not exist: {Path}", basePath);
                continue;
            }

            foreach (var pattern in includePatterns)
            {
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(basePath, pattern, SearchOption.AllDirectories);
                }
                catch (UnauthorizedAccessException ex)
                {
                    logger.LogWarning(ex, "Cannot access path: {Path}", basePath);
                    continue;
                }

                foreach (var filePath in files)
                {
                    ct.ThrowIfCancellationRequested();

                    if (excludePatterns.Any(ep => MatchesWildcard(Path.GetFileName(filePath), ep)))
                        continue;

                    string content;
                    try
                    {
                        content = await File.ReadAllTextAsync(filePath, ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to read file: {Path}", filePath);
                        continue;
                    }

                    // Skip empty files
                    if (string.IsNullOrWhiteSpace(content))
                        continue;

                    var fileInfo = new FileInfo(filePath);
                    var docId = $"file:{config.SourceId}:{ComputeStableId(filePath)}";

                    yield return new RawDocument
                    {
                        DocumentId = docId,
                        Title = Path.GetFileName(filePath),
                        Uri = filePath,
                        Content = content,
                        ContentType = GetContentType(filePath),
                        ExternalVersion = fileInfo.LastWriteTimeUtc.ToString("O"),
                    };
                }
            }
        }
    }

    private static string ComputeStableId(string filePath)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(filePath.Replace('\\', '/')));
        return Convert.ToHexStringLower(hash)[..16];
    }

    private static string GetContentType(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".md" or ".markdown" => "markdown",
            ".cs" or ".ts" or ".tsx" or ".js" or ".jsx" or ".py" or ".java" or ".go" or ".rs" => "code",
            ".json" or ".yaml" or ".yml" or ".xml" or ".toml" => "config",
            ".html" or ".htm" => "html",
            _ => "text",
        };
    }

    private static bool MatchesWildcard(string fileName, string pattern)
    {
        // Simple wildcard matching for *.ext patterns
        if (pattern.StartsWith('*'))
            return fileName.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        return fileName.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }
}
