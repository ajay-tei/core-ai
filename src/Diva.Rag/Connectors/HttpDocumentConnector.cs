using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Diva.Rag.Abstractions;
using Microsoft.Extensions.Logging;

namespace Diva.Rag.Connectors;

/// <summary>
/// Crawls web pages and extracts text content. Uses HtmlAgilityPack for parsing.
/// Config JSON: { "urls": ["https://..."], "maxDepth": 0 }
/// </summary>
public sealed class HttpDocumentConnector(
    IHttpClientFactory httpFactory,
    ILogger<HttpDocumentConnector> logger) : IDocumentConnector
{
    public string SourceType => "http";

    public async IAsyncEnumerable<RawDocument> ConnectAsync(
        DocumentSourceConfig config, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var urls = new List<string>();

        if (config.Config is not null)
        {
            var root = config.Config.RootElement;
            if (root.TryGetProperty("urls", out var urlsProp))
                urls = urlsProp.EnumerateArray().Select(u => u.GetString()!).ToList();
        }

        if (urls.Count == 0)
        {
            logger.LogWarning("HTTP connector has no URLs configured for source {SourceId}", config.SourceId);
            yield break;
        }

        using var http = httpFactory.CreateClient("RagConnector");
        http.Timeout = TimeSpan.FromSeconds(30);

        foreach (var url in urls)
        {
            ct.ThrowIfCancellationRequested();

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                logger.LogWarning("Invalid URL skipped: {Url}", url);
                continue;
            }

            string html;
            try
            {
                html = await http.GetStringAsync(uri, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fetch URL: {Url}", url);
                continue;
            }

            var content = ExtractText(html);
            if (string.IsNullOrWhiteSpace(content))
                continue;

            var docId = $"http:{config.SourceId}:{ComputeStableId(url)}";
            yield return new RawDocument
            {
                DocumentId = docId,
                Title = ExtractTitle(html) ?? new Uri(url).AbsolutePath,
                Uri = url,
                Content = content,
                ContentType = "html",
            };
        }
    }

    private static string ExtractText(string html)
    {
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(html);

        // Remove script, style, nav, footer, header elements
        var nodesToRemove = doc.DocumentNode.SelectNodes(
            "//script|//style|//nav|//footer|//header|//aside|//noscript");
        if (nodesToRemove != null)
        {
            foreach (var node in nodesToRemove)
                node.Remove();
        }

        // Get main content or body
        var main = doc.DocumentNode.SelectSingleNode("//main") ??
                   doc.DocumentNode.SelectSingleNode("//article") ??
                   doc.DocumentNode.SelectSingleNode("//body");

        return main?.InnerText.Trim() ?? string.Empty;
    }

    private static string? ExtractTitle(string html)
    {
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(html);
        return doc.DocumentNode.SelectSingleNode("//title")?.InnerText?.Trim();
    }

    private static string ComputeStableId(string url)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(url));
        return Convert.ToHexStringLower(hash)[..16];
    }
}
