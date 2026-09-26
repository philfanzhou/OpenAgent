using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Files;

namespace OpenAgent.Core.Capabilities.Web;

/// <summary>
/// web_fetch：抓取公开 HTTP(S) 页面并转成模型可读文本（对标 Claude Code WebFetch 的
/// 去依赖版：HTML→纯文本，不做小模型二次抽取）。复用 FileAssetUrlDownloader 的
/// SSRF 防护（禁环回/内网地址、限重定向、限大小）。15 分钟进程内缓存，
/// 只读网络操作，可与其它只读工具并行。
/// </summary>
internal sealed class WebCapabilitySource(IWebFetcher fetcher) : ICapabilitySource
{
    private const string Name = "web_fetch";
    private const string Description =
        "Fetch a public HTTP(S) page and return its readable text: HTML pages are converted to plain text "
        + "(headings, paragraphs and list items kept as lines; scripts/styles/boilerplate tags removed), "
        + "plain text, JSON and XML pass through as-is. "
        + "Use it to look up documentation, APIs or references you were not sure about. "
        + "Binary content (images, PDF, archives) cannot be fetched as text — use download_file to store it instead. "
        + "Results are cached for 15 minutes per URL. "
        + "This is a plain page fetch: it does not run JavaScript and is not a search engine.";
    private const string Schema =
        """{"type":"object","properties":{"url":{"type":"string","description":"The public HTTP(S) URL of the page to fetch"}},"required":["url"],"additionalProperties":false}""";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);
    private const int MaxCacheEntries = 64;

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.Ordinal);


    public Task<IReadOnlyList<CapabilityDefinition>> DiscoverAsync(
        string agentId,
        AgentConfig config,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CapabilityDefinition> definitions =
        [
            new CapabilityDefinition(
                Name,
                Description,
                Schema,
                AgentResourceType.Tool,
                Name,
                FetchAsync,
                Concurrency: ToolConcurrency.ReadOnly)
        ];
        return Task.FromResult(definitions);
    }

    private async Task<ToolResult> FetchAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        string? url = arguments.TryGetValue("url", out object? value) ? value?.ToString() : null;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return ToolResult.Error(
                "'url' is a required argument and must be an absolute http(s) URL.",
                "invalid_arguments");
        }

        if (Cache.TryGetValue(url, out CacheEntry? cached)
            && DateTimeOffset.UtcNow - cached.FetchedAt < CacheTtl)
        {
            return Text(url, cached.Text, cached.MediaType, fromCache: true);
        }

        DownloadedFile downloaded;
        try
        {
            downloaded = await fetcher.FetchAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ToolResult.Error("The remote address timed out.", "fetch_failed",
                hint: "Retry, or use a more direct URL.");
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            return ToolResult.Error("The remote address is unreachable or rejected the request.",
                "fetch_failed",
                hint: "Verify the URL is public and reachable, then retry.");
        }
        catch (OpenAgent.Contracts.Security.AgentException exception)
        {
            return ToolResult.Error(exception.Message, "invalid_request");
        }

        string mediaType = downloaded.MediaType;
        if (mediaType.Contains(';'))
        {
            mediaType = mediaType[..mediaType.IndexOf(';')].Trim();
        }
        if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mediaType is "application/json" or "application/xml"
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase))
        {
            string text = mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
                ? HtmlTextExtractor.Extract(Encoding.UTF8.GetString(downloaded.Content))
                : Encoding.UTF8.GetString(downloaded.Content);
            if (string.IsNullOrWhiteSpace(text))
            {
                return ToolResult.Error("The page returned no readable text.", "fetch_failed",
                    hint: "It may be a JavaScript-rendered page; try a static or print version of the URL.");
            }
            Store(url, mediaType, text);
            return Text(url, text, mediaType, fromCache: false);
        }
        return ToolResult.Error(
            $"The URL returns binary content ({mediaType}), which cannot be fetched as text.",
            "unsupported_content",
            hint: "Use download_file to store the file, or find an HTML/text version of the page.");
    }

    private static ToolResult Text(string url, string text, string mediaType, bool fromCache) =>
        JsonSerializer.Serialize(new
        {
            url,
            contentType = mediaType,
            chars = text.Length,
            cached = fromCache,
            text
        });

    private static void Store(string url, string mediaType, string text)
    {
        if (Cache.Count >= MaxCacheEntries && !Cache.ContainsKey(url))
        {
            // 满则简单整清：抓取缓存是纯性能优化，重新抓取无害。
            Cache.Clear();
        }
        Cache[url] = new CacheEntry(text, mediaType, DateTimeOffset.UtcNow);
    }

    private sealed record CacheEntry(string Text, string MediaType, DateTimeOffset FetchedAt);
}

/// <summary>抓取适配层：隔离 FileAssetUrlDownloader 便于测试。下载路径复用既有 SSRF 防护。</summary>
internal interface IWebFetcher
{
    Task<DownloadedFile> FetchAsync(string url, CancellationToken cancellationToken);
}

internal sealed class UrlDownloaderWebFetcher(FileAssetUrlDownloader downloader) : IWebFetcher
{
    public Task<DownloadedFile> FetchAsync(string url, CancellationToken cancellationToken) =>
        downloader.DownloadAsync(url, cancellationToken);
}

/// <summary>
/// 无依赖的 HTML→文本转换：去掉 script/style/head 注入噪音，保留标题/段落/列表项的
/// 行结构，标签剥离后做实体解码与空行折叠。
/// </summary>
internal static class HtmlTextExtractor
{
    public static string Extract(string html)
    {
        string withoutNoise = RemoveBlocks(html, "script", "style", "noscript", "svg", "head");
        var builder = new StringBuilder(withoutNoise.Length);
        int index = 0;
        while (index < withoutNoise.Length)
        {
            int open = withoutNoise.IndexOf('<', index);
            if (open < 0)
            {
                builder.Append(withoutNoise[index..]);
                break;
            }
            builder.Append(withoutNoise[index..open]);
            int close = withoutNoise.IndexOf('>', open);
            if (close < 0)
            {
                break;
            }
            string tag = withoutNoise[(open + 1)..close].Trim();
            AppendTagBreak(builder, tag);
            index = close + 1;
        }
        string decoded = WebUtility.HtmlDecode(builder.ToString());
        return CollapseBlankLines(decoded).Trim();
    }

    private static void AppendTagBreak(StringBuilder builder, string tag)
    {
        string name = tag.Split(' ')[0].TrimEnd('/');
        if (name.Length == 0)
        {
            return;
        }
        if (name is "p" or "div" or "section" or "article" or "table" or "tr" or "br")
        {
            builder.AppendLine();
        }
        else if (name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
        {
            builder.AppendLine().AppendLine();
        }
        else if (name is "li")
        {
            builder.Append("- ");
        }
    }

    private static string RemoveBlocks(string html, params string[] tagNames)
    {
        foreach (string tagName in tagNames)
        {
            html = RemoveBlock(html, tagName);
        }
        return html;
    }

    private static string RemoveBlock(string html, string tagName)
    {
        var builder = new StringBuilder(html.Length);
        int index = 0;
        while (index < html.Length)
        {
            int open = html.IndexOf($"<{tagName}", index, StringComparison.OrdinalIgnoreCase);
            if (open < 0)
            {
                builder.Append(html[index..]);
                break;
            }
            builder.Append(html[index..open]);
            int close = html.IndexOf($"</{tagName}", open, StringComparison.OrdinalIgnoreCase);
            if (close < 0)
            {
                break;
            }
            int end = html.IndexOf('>', close);
            if (end < 0)
            {
                break;
            }
            index = end + 1;
        }
        return builder.ToString();
    }

    private static string CollapseBlankLines(string text)
    {
        var builder = new StringBuilder(text.Length);
        bool previousBlank = false;
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r').Trim();
            if (trimmed.Length == 0)
            {
                previousBlank = builder.Length > 0;
                continue;
            }
            if (previousBlank && builder.Length > 0)
            {
                builder.AppendLine();
            }
            builder.AppendLine(trimmed);
            previousBlank = false;
        }
        return builder.ToString();
    }
}
