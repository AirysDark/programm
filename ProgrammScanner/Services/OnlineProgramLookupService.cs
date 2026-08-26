using ProgrammScanner.Models;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProgrammScanner.Services;

public static class OnlineProgramLookupService
{
    private const int MaxSearchResults = 5;
    private const int MaxCrawlPagesPerResult = 6;

    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(20)
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
    private sealed record PageLink(string Url, string Text);

    public static async Task LookupAsync(InstalledProgram program)
    {
        program.OnlineStatus = "Searching web candidates...";
        program.OfficialWebsite = "";
        program.DownloadUrl = "";
        program.OnlineSource = "";

        string query = $"\"{program.Name}\"";
        if (!string.IsNullOrWhiteSpace(program.Publisher)) query += $" \"{program.Publisher}\"";
        query += " official download windows installer";

        List<SearchResult> results = await SearchGoogleAsync(query);
        if (results.Count == 0) results = await SearchDuckDuckGoAsync(query);
        results = results.Take(MaxSearchResults).ToList();

        if (results.Count == 0)
        {
            program.OnlineStatus = "No web results found";
            return;
        }

        SearchResult website = results
            .OrderByDescending(r => ScoreResult(r, program, false))
            .ThenBy(r => r.Position)
            .First();

        program.OfficialWebsite = website.Url;

        var candidates = new List<DownloadCandidate>();
        int completed = 0;
        foreach (SearchResult result in results)
        {
            completed++;
            program.OnlineStatus = $"Deep checking result {completed}/{results.Count}: {result.Source}";
            candidates.AddRange(await ResolveResultDeepAsync(result, program));
        }

        DownloadCandidate? best = candidates
            .GroupBy(c => c.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Score).First())
            .OrderByDescending(c => c.Score)
            .FirstOrDefault();

        if (best is not null)
        {
            program.DownloadUrl = best.Url;
            program.OnlineSource = best.Source;
            program.OnlineStatus = $"Resolved actual download from {best.Source}";
            return;
        }

        // Never put a search result or ordinary web page into DownloadUrl.
        program.DownloadUrl = "";
        program.OnlineSource = "";
        program.OnlineStatus = "No actual downloadable installer found after deep lookup";
    }

    private static async Task<List<DownloadCandidate>> ResolveResultDeepAsync(SearchResult result, InstalledProgram program)
    {
        var candidates = new List<DownloadCandidate>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Url, int Depth)>();
        queue.Enqueue((result.Url, 0));

        if (IsDirectDownloadUrl(result.Url))
        {
            candidates.Add(new DownloadCandidate(result.Url, result.Source, 160));
        }

        if (TryGetGitHubRepository(result.Url, out string owner, out string repo))
        {
            candidates.AddRange(await GetGitHubReleaseAssetsAsync(owner, repo, program));
        }

        while (queue.Count > 0 && visited.Count < MaxCrawlPagesPerResult)
        {
            (string currentUrl, int depth) = queue.Dequeue();
            if (!visited.Add(currentUrl)) continue;

            PageFetchResult? page = await FetchPageAsync(currentUrl);
            if (page is null) continue;

            if (IsDirectDownloadUrl(page.FinalUrl) || IsInstallerContentType(page.MediaType))
            {
                candidates.Add(new DownloadCandidate(page.FinalUrl, result.Source, 150));
                continue;
            }

            if (!page.IsHtml) continue;

            if (TryGetGitHubRepository(page.FinalUrl, out owner, out repo))
            {
                candidates.AddRange(await GetGitHubReleaseAssetsAsync(owner, repo, program));
            }

            foreach (PageLink link in ExtractLinks(page.Html, page.FinalUrl))
            {
                int score = ScoreDownloadLink(link.Url, link.Text, result, program);

                if (IsDirectDownloadUrl(link.Url))
                {
                    candidates.Add(new DownloadCandidate(link.Url, GetSourceName(GetHost(link.Url)), score + 80));
                    continue;
                }

                if (TryGetGitHubRepository(link.Url, out owner, out repo))
                {
                    candidates.AddRange(await GetGitHubReleaseAssetsAsync(owner, repo, program));
                }

                // Only crawl links that look like a download/release/install page.
                if (depth < 2 && IsLikelyDownloadPage(link.Url, link.Text, score))
                {
                    queue.Enqueue((link.Url, depth + 1));
                }
            }
        }

        return candidates;
    }

    private sealed record PageFetchResult(string FinalUrl, string MediaType, bool IsHtml, string Html);

    private static async Task<PageFetchResult?> FetchPageAsync(string url)
    {
        try
        {
            using HttpRequestMessage request = CreateRequest(url);
            using HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode) return null;

            string finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? url;
            string mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            bool isHtml = mediaType.Contains("html", StringComparison.OrdinalIgnoreCase);

            if (!isHtml)
            {
                return new PageFetchResult(finalUrl, mediaType, false, "");
            }

            string html = await response.Content.ReadAsStringAsync();
            return new PageFetchResult(finalUrl, mediaType, true, html);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<List<DownloadCandidate>> GetGitHubReleaseAssetsAsync(string owner, string repo, InstalledProgram program)
    {
        var candidates = new List<DownloadCandidate>();

        try
        {
            using HttpRequestMessage request = CreateRequest($"https://api.github.com/repos/{owner}/{repo}/releases");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using HttpResponseMessage response = await Client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return candidates;

            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (document.RootElement.ValueKind != JsonValueKind.Array) return candidates;

            foreach (JsonElement release in document.RootElement.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out JsonElement draft) && draft.GetBoolean()) continue;
                if (release.TryGetProperty("prerelease", out JsonElement prerelease) && prerelease.GetBoolean()) continue;
                if (!release.TryGetProperty("assets", out JsonElement assets)) continue;

                foreach (JsonElement asset in assets.EnumerateArray())
                {
                    if (!asset.TryGetProperty("browser_download_url", out JsonElement urlProperty)) continue;
                    string url = urlProperty.GetString() ?? "";
                    string name = asset.TryGetProperty("name", out JsonElement nameProperty) ? nameProperty.GetString() ?? "" : "";
                    int score = ScoreAsset(url, name, program);
                    if (score >= 80)
                    {
                        candidates.Add(new DownloadCandidate(url, "GitHub Release", score));
                    }
                }
            }
        }
        catch
        {
        }

        return candidates;
    }

    private static int ScoreAsset(string url, string name, InstalledProgram program)
    {
        int score = 60;
        string value = (url + " " + name).ToLowerInvariant();

        if (Regex.IsMatch(value, @"\.(exe|msi|msix|msixbundle)(?:$|[?#])", RegexOptions.IgnoreCase)) score += 100;
        else if (Regex.IsMatch(value, @"\.(zip|7z|rar)(?:$|[?#])", RegexOptions.IgnoreCase)) score += 35;

        if (ContainsAny(value, "windows", "win64", "win32", "x64", "x86", "setup", "installer")) score += 25;
        if (ContainsAny(value, "linux", "mac", "darwin", "android", "source", "symbols", "debug", "checksum", "sha256", "sig", "torrent")) score -= 70;
        foreach (string token in Tokens(program.Name)) if (value.Contains(token)) score += 8;
        return score;
    }

    private static IEnumerable<PageLink> ExtractLinks(string html, string baseUrl)
    {
        const string pattern = "<a\\b[^>]*href\\s*=\\s*[\\\"'](?<url>[^\\\"']+)[\\\"'][^>]*>(?<text>.*?)</a>";

        foreach (Match match in Regex.Matches(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            string href = WebUtility.HtmlDecode(match.Groups["url"].Value).Trim();
            if (string.IsNullOrWhiteSpace(href) || href.StartsWith("#") || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseUri)) continue;
            if (!Uri.TryCreate(baseUri, href, out Uri? uri)) continue;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) continue;

            string text = StripHtml(WebUtility.HtmlDecode(match.Groups["text"].Value));
            yield return new PageLink(uri.AbsoluteUri, text);
        }
    }

    private static bool IsLikelyDownloadPage(string url, string text, int score)
    {
        string value = (url + " " + text).ToLowerInvariant();
        return score >= 50 || ContainsAny(value, "download", "downloads", "get", "installer", "install", "setup", "release", "releases", "latest", "windows");
    }

    private static int ScoreDownloadLink(string url, string text, SearchResult result, InstalledProgram program)
    {
        string value = (url + " " + text).ToLowerInvariant();
        int score = ScoreResult(result, program, true);

        if (IsDirectDownloadUrl(url)) score += 100;
        if (ContainsAny(value, "download", "installer", "setup", "install", "get app", "release", "latest", "windows", "win64", "x64", "x86")) score += 30;
        if (ContainsAny(value, "linux", "macos", "darwin", "android", "source code", "sourcecode", "checksum", "signature", "torrent", "debug", "symbols")) score -= 55;
        if (url.Contains("github.com", StringComparison.OrdinalIgnoreCase) && url.Contains("/releases/download/", StringComparison.OrdinalIgnoreCase)) score += 100;
        if (url.Contains("sourceforge.net", StringComparison.OrdinalIgnoreCase) && (url.Contains("/download", StringComparison.OrdinalIgnoreCase) || url.Contains("/files/", StringComparison.OrdinalIgnoreCase))) score += 55;
        foreach (string token in Tokens(program.Name)) if (value.Contains(token)) score += 8;
        return score;
    }

    private static HttpRequestMessage CreateRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("ProgrammScanner/1.1 (+Windows; deep download resolver)");
        request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return request;
    }

    private static bool TryGetGitHubRepository(string url, out string owner, out string repo)
    {
        owner = "";
        repo = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || !uri.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase)) return false;

        string[] parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        owner = parts[0];
        repo = parts[1];
        return true;
    }

    private static bool IsDirectDownloadUrl(string url) => Regex.IsMatch(url, @"\.(exe|msi|msix|msixbundle|zip|7z|rar)(?:$|[?#])", RegexOptions.IgnoreCase) || url.Contains("/releases/download/", StringComparison.OrdinalIgnoreCase);
    private static bool IsInstallerContentType(string mediaType) => mediaType.Contains("application/octet-stream", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("application/x-msdownload", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("application/x-msi", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("application/vnd.microsoft.portable-executable", StringComparison.OrdinalIgnoreCase);
    public static string BuildGoogleSearchUrl(string query) => "https://www.google.com/search?q=" + Uri.EscapeDataString(query);

    private static async Task<List<SearchResult>> SearchGoogleAsync(string query)
    {
        try
        {
            using HttpResponseMessage response = await Client.SendAsync(CreateRequest(BuildGoogleSearchUrl(query) + "&num=5"));
            if (!response.IsSuccessStatusCode) return [];
            string html = await response.Content.ReadAsStringAsync();
            if (html.Contains("unusual traffic", StringComparison.OrdinalIgnoreCase) || html.Contains("consent.google.com", StringComparison.OrdinalIgnoreCase)) return [];
            return ParseSearchResults(html, "Google");
        }
        catch { return []; }
    }

    private static async Task<List<SearchResult>> SearchDuckDuckGoAsync(string query)
    {
        try
        {
            using HttpResponseMessage response = await Client.SendAsync(CreateRequest("https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query)));
            if (!response.IsSuccessStatusCode) return [];
            string html = await response.Content.ReadAsStringAsync();
            var results = new List<SearchResult>();
            const string pattern = "<a[^>]*class=\\\"result__a\\\"[^>]*href=\\\"(?<url>[^\\\"]+)\\\"[^>]*>(?<title>.*?)</a>";
            foreach (Match match in Regex.Matches(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                string target = DecodeDuckDuckGoRedirect(WebUtility.HtmlDecode(match.Groups["url"].Value));
                if (!Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) || !IsAllowedHost(uri.Host)) continue;
                if (results.Any(r => r.Url.Equals(uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))) continue;
                results.Add(new SearchResult(StripHtml(match.Groups["title"].Value), uri.AbsoluteUri, GetSourceName(uri.Host), results.Count + 1));
                if (results.Count == MaxSearchResults) break;
            }
            return results;
        }
        catch { return []; }
    }

    private static List<SearchResult> ParseSearchResults(string html, string searchSource)
    {
        var results = new List<SearchResult>();
        const string pattern = "<a[^>]+href=\\\"(?<url>https?://[^\\\"&]+)[^\\\"]*\\\"[^>]*>\\s*(?:<[^>]+>)*\\s*(?<title>[^<]{2,})";
        foreach (Match match in Regex.Matches(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            string target = WebUtility.HtmlDecode(match.Groups["url"].Value);
            string title = WebUtility.HtmlDecode(match.Groups["title"].Value).Trim();
            if (!Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) || uri.Host.EndsWith("google.com", StringComparison.OrdinalIgnoreCase) || !IsAllowedHost(uri.Host)) continue;
            if (results.Any(r => r.Url.Equals(uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))) continue;
            results.Add(new SearchResult(title, uri.AbsoluteUri, GetSourceName(uri.Host), results.Count + 1));
            if (results.Count == MaxSearchResults) break;
        }
        return results;
    }

    private static int ScoreResult(SearchResult result, InstalledProgram program, bool preferDownload)
    {
        string host = GetHost(result.Url).ToLowerInvariant();
        string text = (result.Title + " " + result.Url).ToLowerInvariant();
        int score = Math.Max(0, 10 - result.Position);

        if (KnownSources.Keys.Any(domain => host.EndsWith(domain, StringComparison.OrdinalIgnoreCase))) score += 35;
        if (host.EndsWith("github.com") && text.Contains("releases")) score += 45;
        if (host.EndsWith("sourceforge.net")) score += 30;
        foreach (string token in Tokens(program.Name)) if (text.Contains(token)) score += 8;
        foreach (string token in Tokens(program.Publisher)) { if (host.Contains(token)) score += 25; else if (text.Contains(token)) score += 5; }
        if (preferDownload && ContainsAny(text, "download", "installer", "setup", "release", "releases", "latest")) score += 35;
        return score;
    }

    private static bool ContainsAny(string value, params string[] terms) => terms.Any(value.Contains);

    private static string GetSourceName(string host)
    {
        foreach (KeyValuePair<string, string> source in KnownSources)
        {
            if (host.EndsWith(source.Key, StringComparison.OrdinalIgnoreCase)) return source.Value;
        }
        return "Website";
    }

    private static bool IsAllowedHost(string host) => !string.IsNullOrWhiteSpace(host) && !host.Contains("softonic", StringComparison.OrdinalIgnoreCase) && !host.Contains("uptodown", StringComparison.OrdinalIgnoreCase) && !host.Contains("cnet.com", StringComparison.OrdinalIgnoreCase) && !host.Contains("download.com", StringComparison.OrdinalIgnoreCase);
    private static IEnumerable<string> Tokens(string value) => Regex.Matches(value ?? "", @"[A-Za-z0-9]{3,}").Select(x => x.Value.ToLowerInvariant()).Where(x => x is not "microsoft" and not "corporation" and not "software" and not "inc" and not "ltd" and not "llc").Distinct();

    private static string DecodeDuckDuckGoRedirect(string href)
    {
        try
        {
            if (!href.Contains("uddg=", StringComparison.OrdinalIgnoreCase)) return href;
            string query = href[(href.IndexOf('?') + 1)..];
            foreach (string pair in query.Split('&'))
            {
                if (pair.StartsWith("uddg=", StringComparison.OrdinalIgnoreCase)) return Uri.UnescapeDataString(pair[5..]);
            }
        }
        catch
        {
        }
        return href;
    }

    private static string StripHtml(string value) => Regex.Replace(value, "<.*?>", " ").Trim();
    private static string GetHost(string url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.Host : "";
}
