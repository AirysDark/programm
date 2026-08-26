using ProgrammScanner.Models;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProgrammScanner.Services;

public static class OnlineProgramLookupService
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly Dictionary<string, string> KnownSources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["github.com"] = "GitHub",
        ["gitlab.com"] = "GitLab",
        ["sourceforge.net"] = "SourceForge",
        ["softpedia.com"] = "Softpedia",
        ["filehippo.com"] = "FileHippo",
        ["majorgeeks.com"] = "MajorGeeks",
        ["fosshub.com"] = "FossHub",
        ["archive.org"] = "Internet Archive",
        ["bitbucket.org"] = "Bitbucket"
    };

    private sealed record SearchResult(string Title, string Url, string Source, int Position);
    private sealed record DownloadCandidate(string Url, string Source, int Score);

    public static async Task LookupAsync(InstalledProgram program)
    {
        program.OnlineStatus = "Searching web candidates...";
        program.OfficialWebsite = "";
        program.DownloadUrl = "";
        program.OnlineSource = "";

        var query = $"\"{program.Name}\"";
        if (!string.IsNullOrWhiteSpace(program.Publisher)) query += $" \"{program.Publisher}\"";
        query += " download official";

        var results = await SearchGoogleAsync(query);
        if (results.Count == 0) results = await SearchDuckDuckGoAsync(query);
        results = results.Take(5).ToList();

        if (results.Count == 0)
        {
            program.OnlineStatus = "No web results found";
            return;
        }

        var website = results.OrderByDescending(r => ScoreResult(r, program, false)).ThenBy(r => r.Position).First();
        program.OfficialWebsite = website.Url;

        var candidates = new List<DownloadCandidate>();
        foreach (var result in results)
        {
            program.OnlineStatus = $"Checking result {result.Position}/{results.Count}: {result.Source}";
            candidates.AddRange(await ResolveResultAsync(result, program));
        }

        var best = candidates
            .GroupBy(c => c.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Score).First())
            .OrderByDescending(c => c.Score)
            .FirstOrDefault();

        if (best is not null)
        {
            program.DownloadUrl = best.Url;
            program.OnlineSource = best.Source;
            program.OnlineStatus = $"Resolved installer from {best.Source}";
        }
        else
        {
            var fallback = results.OrderByDescending(r => ScoreResult(r, program, true)).ThenBy(r => r.Position).First();
            program.DownloadUrl = fallback.Url;
            program.OnlineSource = $"{fallback.Source} download page";
            program.OnlineStatus = "No direct installer found; saved best download page";
        }
    }

    private static async Task<List<DownloadCandidate>> ResolveResultAsync(SearchResult result, InstalledProgram program)
    {
        var candidates = new List<DownloadCandidate>();
        if (IsDownloadFile(result.Url)) candidates.Add(new(result.Url, result.Source, 120));

        if (TryGetGitHubRepository(result.Url, out var owner, out var repo))
        {
            candidates.AddRange(await GetGitHubReleaseAssetsAsync(owner, repo, program));
        }

        try
        {
            using var request = CreateRequest(result.Url);
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode) return candidates;

            var finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? result.Url;
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (IsDownloadFile(finalUrl) || IsInstallerContentType(mediaType))
            {
                candidates.Add(new(finalUrl, result.Source, 115));
                return candidates;
            }

            if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)) return candidates;
            var html = await response.Content.ReadAsStringAsync();
            foreach (var link in ExtractLinks(html, finalUrl))
            {
                var score = ScoreDownloadLink(link.Url, link.Text, result, program);
                if (score >= 55) candidates.Add(new(link.Url, result.Source, score));
            }
        }
        catch { }

        return candidates;
    }

    private static async Task<List<DownloadCandidate>> GetGitHubReleaseAssetsAsync(string owner, string repo, InstalledProgram program)
    {
        var candidates = new List<DownloadCandidate>();
        try
        {
            using var request = CreateRequest($"https://api.github.com/repos/{owner}/{repo}/releases/latest");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return candidates;

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!document.RootElement.TryGetProperty("assets", out var assets)) return candidates;
            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("browser_download_url", out var urlProperty)) continue;
                var url = urlProperty.GetString() ?? "";
                var name = asset.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() ?? "" : "";
                var score = ScoreDownloadLink(url, name, new SearchResult(name, url, "GitHub Release", 0), program) + 50;
                if (IsDownloadFile(url) || score >= 85) candidates.Add(new(url, "GitHub Release", score));
            }
        }
        catch { }
        return candidates;
    }

    private static IEnumerable<(string Url, string Text)> ExtractLinks(string html, string baseUrl)
    {
        foreach (Match match in Regex.Matches(html, "<a\\b[^>]*href\\s*=\\s*[\\\"'](?<url>[^\\\"']+)[\\\"'][^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var href = WebUtility.HtmlDecode(match.Groups["url"].Value).Trim();
            if (string.IsNullOrWhiteSpace(href) || href.StartsWith("#") || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || !Uri.TryCreate(baseUri, href, out var uri)) continue;
            if (uri.Scheme is not "http" and not "https") continue;
            yield return (uri.AbsoluteUri, StripHtml(WebUtility.HtmlDecode(match.Groups["text"].Value)));
        }
    }

    private static int ScoreDownloadLink(string url, string text, SearchResult result, InstalledProgram program)
    {
        var value = (url + " " + text).ToLowerInvariant();
        var score = ScoreResult(result, program, true);
        if (IsDownloadFile(url)) score += 100;
        if (ContainsAny(value, "download", "installer", "setup", "install", "release", "latest", "windows", "win64", "x64", "x86")) score += 25;
        if (ContainsAny(value, "linux", "macos", "android", "source code", "sourcecode", "checksum", "signature", "torrent")) score -= 35;
        if (url.Contains("github.com", StringComparison.OrdinalIgnoreCase) && url.Contains("/releases/download/", StringComparison.OrdinalIgnoreCase)) score += 80;
        if (url.Contains("sourceforge.net", StringComparison.OrdinalIgnoreCase) && (url.Contains("/download", StringComparison.OrdinalIgnoreCase) || url.Contains("/files/", StringComparison.OrdinalIgnoreCase))) score += 45;
        foreach (var token in Tokens(program.Name)) if (value.Contains(token)) score += 8;
        return score;
    }

    private static HttpRequestMessage CreateRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("ProgrammScanner/1.0 (+Windows; download resolver)");
        request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return request;
    }

    private static bool TryGetGitHubRepository(string url, out string owner, out string repo)
    {
        owner = ""; repo = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        owner = parts[0]; repo = parts[1];
        return true;
    }

    private static bool IsDownloadFile(string url) => Regex.IsMatch(url, @"\.(exe|msi|msix|msixbundle|zip|7z|rar)(?:$|[?#])", RegexOptions.IgnoreCase);
    private static bool IsInstallerContentType(string mediaType) => mediaType.Contains("application/octet-stream", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("application/x-msdownload", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("application/x-msi", StringComparison.OrdinalIgnoreCase);
    public static string BuildGoogleSearchUrl(string query) => "https://www.google.com/search?q=" + Uri.EscapeDataString(query);

    private static async Task<List<SearchResult>> SearchGoogleAsync(string query)
    {
        try
        {
            using var response = await Client.SendAsync(CreateRequest(BuildGoogleSearchUrl(query) + "&num=5"));
            if (!response.IsSuccessStatusCode) return [];
            var html = await response.Content.ReadAsStringAsync();
            if (html.Contains("unusual traffic", StringComparison.OrdinalIgnoreCase) || html.Contains("consent.google.com", StringComparison.OrdinalIgnoreCase)) return [];
            return ParseSearchResults(html, "Google");
        }
        catch { return []; }
    }

    private static async Task<List<SearchResult>> SearchDuckDuckGoAsync(string query)
    {
        try
        {
            using var response = await Client.SendAsync(CreateRequest("https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query)));
            if (!response.IsSuccessStatusCode) return [];
            var html = await response.Content.ReadAsStringAsync();
            var results = new List<SearchResult>();
            var pattern = "<a[^>]*class=\\\"result__a\\\"[^>]*href=\\\"(?<url>[^\\\"]+)\\\"[^>]*>(?<title>.*?)</a>";
            foreach (Match match in Regex.Matches(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var target = DecodeDuckDuckGoRedirect(WebUtility.HtmlDecode(match.Groups["url"].Value));
                if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || !IsAllowedHost(uri.Host)) continue;
                if (results.Any(r => r.Url.Equals(uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))) continue;
                results.Add(new(StripHtml(match.Groups["title"].Value), uri.AbsoluteUri, GetSourceName(uri.Host), results.Count + 1));
                if (results.Count == 5) break;
            }
            return results;
        }
        catch { return []; }
    }

    private static List<SearchResult> ParseSearchResults(string html, string searchSource)
    {
        var results = new List<SearchResult>();
        var pattern = "<a[^>]+href=\\\"(?<url>https?://[^\\\"&]+)[^\\\"]*\\\"[^>]*>\\s*(?:<[^>]+>)*\\s*(?<title>[^<]{2,})";
        foreach (Match match in Regex.Matches(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var target = WebUtility.HtmlDecode(match.Groups["url"].Value);
            var title = WebUtility.HtmlDecode(match.Groups["title"].Value).Trim();
            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Host.EndsWith("google.com", StringComparison.OrdinalIgnoreCase) || !IsAllowedHost(uri.Host)) continue;
            if (results.Any(r => r.Url.Equals(uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))) continue;
            results.Add(new(title, uri.AbsoluteUri, GetSourceName(uri.Host), results.Count + 1));
            if (results.Count == 5) break;
        }
        return results;
    }

    private static int ScoreResult(SearchResult result, InstalledProgram program, bool preferDownload)
    {
        var host = GetHost(result.Url).ToLowerInvariant();
        var text = (result.Title + " " + result.Url).ToLowerInvariant();
        var score = Math.Max(0, 10 - result.Position);
        if (KnownSources.Keys.Any(domain => host.EndsWith(domain, StringComparison.OrdinalIgnoreCase))) score += 35;
        if (host.EndsWith("github.com") && text.Contains("releases")) score += 45;
        if (host.EndsWith("sourceforge.net")) score += 30;
        foreach (var token in Tokens(program.Name)) if (text.Contains(token)) score += 8;
        foreach (var token in Tokens(program.Publisher)) { if (host.Contains(token)) score += 25; else if (text.Contains(token)) score += 5; }
        if (preferDownload && ContainsAny(text, "download", "installer", "setup", "release", "releases", "latest")) score += 35;
        return score;
    }

    private static bool ContainsAny(string value, params string[] terms) => terms.Any(value.Contains);
    private static string GetSourceName(string host) { foreach (var source in KnownSources) if (host.EndsWith(source.Key, StringComparison.OrdinalIgnoreCase)) return source.Value; return "Website"; }
    private static bool IsAllowedHost(string host) => !string.IsNullOrWhiteSpace(host) && !host.Contains("softonic", StringComparison.OrdinalIgnoreCase) && !host.Contains("uptodown", StringComparison.OrdinalIgnoreCase) && !host.Contains("cnet.com", StringComparison.OrdinalIgnoreCase) && !host.Contains("download.com", StringComparison.OrdinalIgnoreCase);
    private static IEnumerable<string> Tokens(string value) => Regex.Matches(value ?? "", @"[A-Za-z0-9]{3,}").Select(x => x.Value.ToLowerInvariant()).Where(x => x is not "microsoft" and not "corporation" and not "software" and not "inc" and not "ltd" and not "llc").Distinct();
    private static string DecodeDuckDuckGoRedirect(string href) { try { if (!href.Contains("uddg=", StringComparison.OrdinalIgnoreCase)) return href; var query = href[(href.IndexOf('?') + 1)..]; foreach (var pair in query.Split('&')) if (pair.StartsWith("uddg=", StringComparison.OrdinalIgnoreCase)) return Uri.UnescapeDataString(pair[5..]); } catch { } return href; }
    private static string StripHtml(string value) => Regex.Replace(value, "<.*?>", " ").Trim();
    private static string GetHost(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "";
}
