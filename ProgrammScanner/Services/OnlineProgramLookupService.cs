using ProgrammScanner.Models;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace ProgrammScanner.Services;

public static class OnlineProgramLookupService
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private sealed record SearchResult(string Title, string Url, string Engine);

    public static async Task LookupAsync(InstalledProgram program)
    {
        program.OnlineStatus = "Searching Google...";
        program.OfficialWebsite = "";
        program.DownloadUrl = "";
        program.OnlineSource = "";

        var queries = BuildQueries(program).ToList();
        var results = new List<SearchResult>();

        foreach (var query in queries)
        {
            results.AddRange(await SearchGoogleAsync(query));
            if (results.Count >= 12) break;
        }

        if (results.Count == 0)
        {
            program.OnlineStatus = "Google unavailable, trying fallback search...";
            foreach (var query in queries)
            {
                results.AddRange(await SearchDuckDuckGoAsync(query));
                if (results.Count >= 12) break;
            }
        }

        results = results
            .Where(r => !IsBlockedHost(GetHost(r.Url)))
            .GroupBy(r => r.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(30)
            .ToList();

        if (results.Count == 0)
        {
            program.OnlineStatus = "No automatic result - Google search ready";
            program.DownloadUrl = BuildGoogleSearchUrl($"{program.Name} {program.Publisher} download");
            program.OfficialWebsite = BuildGoogleSearchUrl($"{program.Name} {program.Publisher} official website");
            program.OnlineSource = "Google search";
            return;
        }

        var website = SelectBestResult(results, program, false);
        var download = SelectBestResult(results, program, true, website?.Url);

        if (website != null)
        {
            program.OfficialWebsite = website.Url;
            program.OnlineSource = $"{website.Engine}: {GetHost(website.Url)}";
        }

        if (download != null) program.DownloadUrl = download.Url;

        if (string.IsNullOrWhiteSpace(program.OfficialWebsite))
        {
            program.OfficialWebsite = results[0].Url;
            program.OnlineSource = $"{results[0].Engine}: {GetHost(results[0].Url)}";
        }

        if (string.IsNullOrWhiteSpace(program.DownloadUrl))
            program.DownloadUrl = BuildGoogleSearchUrl($"{program.Name} {program.Publisher} download");

        program.OnlineStatus = results.Any(r => r.Engine == "Google")
            ? $"Found {results.Count} results via Google"
            : $"Found {results.Count} fallback results";
    }

    public static string BuildGoogleSearchUrl(string query) =>
        "https://www.google.com/search?q=" + Uri.EscapeDataString(query);

    private static async Task<List<SearchResult>> SearchGoogleAsync(string query)
    {
        try
        {
            var url = BuildGoogleSearchUrl(query) + "&num=10";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124 Safari/537.36");
            request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

            using var response = await Client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return [];
            var html = await response.Content.ReadAsStringAsync();

            if (html.Contains("unusual traffic", StringComparison.OrdinalIgnoreCase) ||
                html.Contains("consent.google.com", StringComparison.OrdinalIgnoreCase))
                return [];

            var results = new List<SearchResult>();
            var pattern = "<a[^>]+href=\\\"(?<url>https?://[^\\\"&]+)[^\\\"]*\\\"[^>]*>\\s*(?:<[^>]+>)*\\s*(?<title>[^<]{2,})";
            var matches = Regex.Matches(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

            foreach (Match match in matches)
            {
                var target = WebUtility.HtmlDecode(match.Groups["url"].Value);
                var title = WebUtility.HtmlDecode(match.Groups["title"].Value).Trim();
                if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)) continue;
                if (uri.Host.EndsWith("google.com", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsBlockedHost(uri.Host)) continue;
                results.Add(new SearchResult(title, uri.AbsoluteUri, "Google"));
            }

            return results;
        }
        catch { return []; }
    }

    private static async Task<List<SearchResult>> SearchDuckDuckGoAsync(string query)
    {
        try
        {
            var url = "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

            using var response = await Client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return [];
            var html = await response.Content.ReadAsStringAsync();

            var results = new List<SearchResult>();
            var pattern = "<a[^>]*class=\\\"result__a\\\"[^>]*href=\\\"(?<url>[^\\\"]+)\\\"[^>]*>(?<title>.*?)</a>";
            var matches = Regex.Matches(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

            foreach (Match match in matches)
            {
                var href = WebUtility.HtmlDecode(match.Groups["url"].Value);
                var title = StripHtml(WebUtility.HtmlDecode(match.Groups["title"].Value));
                var target = DecodeDuckDuckGoRedirect(href);
                if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)) continue;
                if (IsBlockedHost(uri.Host)) continue;
                results.Add(new SearchResult(title, uri.AbsoluteUri, "DuckDuckGo"));
            }

            return results;
        }
        catch { return []; }
    }

    private static SearchResult? SelectBestResult(IEnumerable<SearchResult> results, InstalledProgram program, bool preferDownload, string? preferredUrl = null)
    {
        var publisherTokens = Tokens(program.Publisher).ToList();
        var programTokens = Tokens(program.Name).ToList();
        var preferredHost = string.IsNullOrWhiteSpace(preferredUrl) ? "" : GetHost(preferredUrl);

        return results
            .Select(result => new { Result = result, Score = ScoreResult(result, publisherTokens, programTokens, preferredHost, preferDownload) })
            .OrderByDescending(x => x.Score)
            .Select(x => x.Result)
            .FirstOrDefault();
    }

    private static int ScoreResult(SearchResult result, List<string> publisherTokens, List<string> programTokens, string preferredHost, bool preferDownload)
    {
        var host = GetHost(result.Url).ToLowerInvariant();
        var text = (result.Title + " " + result.Url).ToLowerInvariant();
        var score = 0;

        if (IsBlockedHost(host)) return -10000;
        if (!string.IsNullOrWhiteSpace(preferredHost) && host.Equals(preferredHost, StringComparison.OrdinalIgnoreCase)) score += 60;

        foreach (var token in publisherTokens)
        {
            if (host.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 35;
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 8;
        }

        foreach (var token in programTokens)
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 10;

        if (IsKnownOfficialPlatform(host)) score += 20;
        if (text.Contains("official")) score += 10;
        if (preferDownload && (text.Contains("download") || text.Contains("installer") || text.Contains("setup"))) score += 35;
        return score;
    }

    private static IEnumerable<string> BuildQueries(InstalledProgram program)
    {
        var name = QuoteIfNeeded(program.Name);
        var publisher = string.IsNullOrWhiteSpace(program.Publisher) ? "" : QuoteIfNeeded(program.Publisher);
        yield return $"{name} {publisher} official website".Trim();
        yield return $"{name} {publisher} download".Trim();
        yield return $"{name} installer";
        yield return $"{name} latest download";
    }

    private static string QuoteIfNeeded(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    private static IEnumerable<string> Tokens(string value) =>
        Regex.Matches(value ?? "", @"[A-Za-z0-9]{3,}")
            .Select(x => x.Value.ToLowerInvariant())
            .Where(x => x is not "microsoft" and not "corporation" and not "software" and not "inc" and not "ltd" and not "llc")
            .Distinct();

    private static bool IsKnownOfficialPlatform(string host) =>
        host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("microsoft.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("visualstudio.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsBlockedHost(string host) =>
        host.Contains("softonic", StringComparison.OrdinalIgnoreCase) ||
        host.Contains("uptodown", StringComparison.OrdinalIgnoreCase) ||
        host.Contains("filehippo", StringComparison.OrdinalIgnoreCase) ||
        host.Contains("cnet.com", StringComparison.OrdinalIgnoreCase) ||
        host.Contains("download.com", StringComparison.OrdinalIgnoreCase) ||
        host.Contains("majorgeeks", StringComparison.OrdinalIgnoreCase);

    private static string DecodeDuckDuckGoRedirect(string href)
    {
        try
        {
            if (!href.Contains("uddg=", StringComparison.OrdinalIgnoreCase)) return href;
            var query = href[(href.IndexOf('?') + 1)..];
            foreach (var pair in query.Split('&'))
                if (pair.StartsWith("uddg=", StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(pair[5..]);
        }
        catch { }
        return href;
    }

    private static string StripHtml(string value) => Regex.Replace(value, "<.*?>", " ").Trim();
    private static string GetHost(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "";
}
