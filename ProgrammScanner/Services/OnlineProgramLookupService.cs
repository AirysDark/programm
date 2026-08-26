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

    private static readonly SourceDefinition[] Sources =
    [
        new("GitHub", "github.com", 80, "https://github.com/search?q={0}&type=repositories"),
        new("GitLab", "gitlab.com", 70, "https://gitlab.com/search?search={0}&group_id=&project_id=&snippets=false&repository_ref="),
        new("SourceForge", "sourceforge.net", 65, "https://sourceforge.net/directory/?q={0}"),
        new("FossHub", "fosshub.com", 65, "https://www.fosshub.com/search.html?q={0}"),
        new("MajorGeeks", "majorgeeks.com", 55, "https://www.google.com/search?q=site%3Amajorgeeks.com+{0}"),
        new("FileHippo", "filehippo.com", 50, "https://www.google.com/search?q=site%3Afilehippo.com+{0}"),
        new("Softpedia", "softpedia.com", 50, "https://www.google.com/search?q=site%3Asoftpedia.com+{0}"),
        new("Internet Archive", "archive.org", 40, "https://archive.org/advancedsearch.php?q={0}&output=json"),
        new("Bitbucket", "bitbucket.org", 35, "https://bitbucket.org/repo/all?name={0}")
    ];

    private sealed record SearchResult(string Title, string Url, string Engine, string Source);
    private sealed record SourceDefinition(string Name, string Host, int Priority, string SearchUrl);

    public static async Task LookupAsync(InstalledProgram program)
    {
        program.OnlineStatus = "Searching web and download sources...";
        program.OfficialWebsite = "";
        program.DownloadUrl = "";
        program.OnlineSource = "";

        var results = new List<SearchResult>();
        var queries = BuildQueries(program).ToList();

        // General web search is used for discovery.
        foreach (var query in queries)
        {
            results.AddRange(await SearchGoogleAsync(query));
            if (results.Count >= 20) break;
        }

        if (results.Count == 0)
        {
            foreach (var query in queries)
            {
                results.AddRange(await SearchDuckDuckGoAsync(query));
                if (results.Count >= 20) break;
            }
        }

        // Search every requested download ecosystem using domain-targeted queries.
        foreach (var source in Sources)
        {
            program.OnlineStatus = $"Searching {source.Name}...";
            var sourceQuery = $"site:{source.Host} {program.Name} {program.Publisher} download";
            var sourceResults = await SearchGoogleAsync(sourceQuery);
            if (sourceResults.Count == 0)
                sourceResults = await SearchDuckDuckGoAsync(sourceQuery);

            results.AddRange(sourceResults.Select(r => r with { Source = source.Name }));
        }

        results = results
            .Where(r => Uri.TryCreate(r.Url, UriKind.Absolute, out var uri) && IsAllowedHost(uri.Host))
            .GroupBy(r => r.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(100)
            .ToList();

        if (results.Count == 0)
        {
            program.OnlineStatus = "No automatic result - multi-source search ready";
            program.DownloadUrl = BuildGoogleSearchUrl($"{program.Name} {program.Publisher} download");
            program.OfficialWebsite = BuildGoogleSearchUrl($"{program.Name} {program.Publisher} official website");
            program.OnlineSource = "Google multi-source search";
            return;
        }

        var website = SelectBestResult(results, program, false);
        var download = SelectBestResult(results, program, true, website?.Url);

        if (website != null)
        {
            program.OfficialWebsite = website.Url;
            program.OnlineSource = $"{website.Source}: {GetHost(website.Url)}";
        }

        if (download != null)
        {
            program.DownloadUrl = download.Url;
            if (string.IsNullOrWhiteSpace(program.OnlineSource))
                program.OnlineSource = $"{download.Source}: {GetHost(download.Url)}";
        }

        if (string.IsNullOrWhiteSpace(program.OfficialWebsite))
            program.OfficialWebsite = BuildGoogleSearchUrl($"{program.Name} {program.Publisher} official website");

        if (string.IsNullOrWhiteSpace(program.DownloadUrl))
            program.DownloadUrl = BuildGoogleSearchUrl($"{program.Name} {program.Publisher} download");

        var sourceNames = results.Select(r => r.Source).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        program.OnlineStatus = $"Found {results.Count} results from {string.Join(", ", sourceNames)}";
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
            if (html.Contains("unusual traffic", StringComparison.OrdinalIgnoreCase) || html.Contains("consent.google.com", StringComparison.OrdinalIgnoreCase)) return [];

            var results = new List<SearchResult>();
            var pattern = "<a[^>]+href=\\\"(?<url>https?://[^\\\"&]+)[^\\\"]*\\\"[^>]*>\\s*(?:<[^>]+>)*\\s*(?<title>[^<]{2,})";
            foreach (Match match in Regex.Matches(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var target = WebUtility.HtmlDecode(match.Groups["url"].Value);
                var title = WebUtility.HtmlDecode(match.Groups["title"].Value).Trim();
                if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)) continue;
                if (uri.Host.EndsWith("google.com", StringComparison.OrdinalIgnoreCase)) continue;
                results.Add(new SearchResult(title, uri.AbsoluteUri, "Google", GetSourceName(uri.Host)));
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
            foreach (Match match in Regex.Matches(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var target = DecodeDuckDuckGoRedirect(WebUtility.HtmlDecode(match.Groups["url"].Value));
                var title = StripHtml(WebUtility.HtmlDecode(match.Groups["title"].Value));
                if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)) continue;
                results.Add(new SearchResult(title, uri.AbsoluteUri, "DuckDuckGo", GetSourceName(uri.Host)));
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
            .Select(r => new { Result = r, Score = ScoreResult(r, publisherTokens, programTokens, preferredHost, preferDownload) })
            .OrderByDescending(x => x.Score)
            .Select(x => x.Result)
            .FirstOrDefault();
    }

    private static int ScoreResult(SearchResult result, List<string> publisherTokens, List<string> programTokens, string preferredHost, bool preferDownload)
    {
        var host = GetHost(result.Url).ToLowerInvariant();
        var text = (result.Title + " " + result.Url).ToLowerInvariant();
        var score = GetSourcePriority(host);

        if (!string.IsNullOrWhiteSpace(preferredHost) && host.Equals(preferredHost, StringComparison.OrdinalIgnoreCase)) score += 50;
        foreach (var token in publisherTokens)
        {
            if (host.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 35;
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 8;
        }
        foreach (var token in programTokens)
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 10;

        if (host.EndsWith("github.com") && text.Contains("releases")) score += 35;
        if (host.EndsWith("gitlab.com") && (text.Contains("release") || text.Contains("package"))) score += 30;
        if (host.EndsWith("sourceforge.net")) score += 20;
        if (host.EndsWith("fosshub.com")) score += 20;
        if (text.Contains("official")) score += 15;
        if (preferDownload && (text.Contains("download") || text.Contains("installer") || text.Contains("setup") || text.Contains("release") || text.Contains("releases"))) score += 35;
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

    private static string GetSourceName(string host)
    {
        foreach (var source in Sources)
            if (host.EndsWith(source.Host, StringComparison.OrdinalIgnoreCase)) return source.Name;
        return "Web";
    }

    private static int GetSourcePriority(string host)
    {
        if (host.EndsWith("microsoft.com") || host.EndsWith("visualstudio.com") || host.EndsWith("githubusercontent.com")) return 100;
        foreach (var source in Sources)
            if (host.EndsWith(source.Host, StringComparison.OrdinalIgnoreCase)) return source.Priority;
        return 10;
    }

    private static bool IsAllowedHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        return !host.Contains("softonic", StringComparison.OrdinalIgnoreCase) &&
               !host.Contains("uptodown", StringComparison.OrdinalIgnoreCase) &&
               !host.Contains("cnet.com", StringComparison.OrdinalIgnoreCase) &&
               !host.Contains("download.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string QuoteIfNeeded(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    private static IEnumerable<string> Tokens(string value) =>
        Regex.Matches(value ?? "", @"[A-Za-z0-9]{3,}")
            .Select(x => x.Value.ToLowerInvariant())
            .Where(x => x is not "microsoft" and not "corporation" and not "software" and not "inc" and not "ltd" and not "llc")
            .Distinct();

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
