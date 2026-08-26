using ProgrammScanner.Models;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace ProgrammScanner.Services;

public static class OnlineProgramLookupService
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
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

    public static async Task LookupAsync(InstalledProgram program)
    {
        program.OnlineStatus = "Google search: checking first 5 results...";
        program.OfficialWebsite = "";
        program.DownloadUrl = "";
        program.OnlineSource = "";

        // One fast search only. Do not search every source separately.
        var query = $"\"{program.Name}\"";
        if (!string.IsNullOrWhiteSpace(program.Publisher))
            query += $" \"{program.Publisher}\"";
        query += " download";

        var results = await SearchGoogleAsync(query);
        if (results.Count == 0)
            results = await SearchDuckDuckGoAsync(query);

        // Only evaluate the first five search results for speed.
        results = results.Take(5).ToList();

        if (results.Count == 0)
        {
            program.OnlineStatus = "No results found - Google search link ready";
            program.DownloadUrl = BuildGoogleSearchUrl(query);
            program.OfficialWebsite = BuildGoogleSearchUrl($"\"{program.Name}\" official website");
            program.OnlineSource = "Google Search";
            return;
        }

        var bestDownload = results
            .Select(r => new { Result = r, Score = ScoreResult(r, program, true) })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Result.Position)
            .First().Result;

        var bestWebsite = results
            .Select(r => new { Result = r, Score = ScoreResult(r, program, false) })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Result.Position)
            .First().Result;

        program.DownloadUrl = bestDownload.Url;
        program.OfficialWebsite = bestWebsite.Url;
        program.OnlineSource = $"{bestDownload.Source} (Google result #{bestDownload.Position})";
        program.OnlineStatus = $"Checked first {results.Count} results - download from {bestDownload.Source}";
    }

    public static string BuildGoogleSearchUrl(string query) =>
        "https://www.google.com/search?q=" + Uri.EscapeDataString(query);

    private static async Task<List<SearchResult>> SearchGoogleAsync(string query)
    {
        try
        {
            var url = BuildGoogleSearchUrl(query) + "&num=5";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124 Safari/537.36");
            request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            using var response = await Client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return [];

            var html = await response.Content.ReadAsStringAsync();
            if (html.Contains("unusual traffic", StringComparison.OrdinalIgnoreCase) ||
                html.Contains("consent.google.com", StringComparison.OrdinalIgnoreCase)) return [];

            var results = new List<SearchResult>();
            var pattern = "<a[^>]+href=\\\"(?<url>https?://[^\\\"&]+)[^\\\"]*\\\"[^>]*>\\s*(?:<[^>]+>)*\\s*(?<title>[^<]{2,})";
            foreach (Match match in Regex.Matches(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var target = WebUtility.HtmlDecode(match.Groups["url"].Value);
                var title = WebUtility.HtmlDecode(match.Groups["title"].Value).Trim();
                if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)) continue;
                if (uri.Host.EndsWith("google.com", StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsAllowedHost(uri.Host)) continue;
                if (results.Any(r => r.Url.Equals(uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))) continue;

                results.Add(new SearchResult(title, uri.AbsoluteUri, GetSourceName(uri.Host), results.Count + 1));
                if (results.Count == 5) break;
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
                if (!IsAllowedHost(uri.Host)) continue;
                if (results.Any(r => r.Url.Equals(uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))) continue;

                results.Add(new SearchResult(title, uri.AbsoluteUri, GetSourceName(uri.Host), results.Count + 1));
                if (results.Count == 5) break;
            }
            return results;
        }
        catch { return []; }
    }

    private static int ScoreResult(SearchResult result, InstalledProgram program, bool preferDownload)
    {
        var host = GetHost(result.Url).ToLowerInvariant();
        var text = (result.Title + " " + result.Url).ToLowerInvariant();
        var score = 0;

        // Earlier search results get a small preference, but obvious download sources win.
        score += Math.Max(0, 10 - result.Position);

        if (KnownSources.Keys.Any(domain => host.EndsWith(domain, StringComparison.OrdinalIgnoreCase))) score += 35;
        if (host.EndsWith("github.com") && text.Contains("releases")) score += 45;
        if (host.EndsWith("gitlab.com") && (text.Contains("release") || text.Contains("package"))) score += 40;
        if (host.EndsWith("sourceforge.net")) score += 30;
        if (host.EndsWith("fosshub.com")) score += 30;

        foreach (var token in Tokens(program.Name))
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 8;

        foreach (var token in Tokens(program.Publisher))
        {
            if (host.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 25;
            else if (text.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 5;
        }

        if (preferDownload && ContainsAny(text, "download", "installer", "setup", "release", "releases", "latest")) score += 35;
        if (!preferDownload && ContainsAny(text, "official", "home", "product")) score += 15;

        return score;
    }

    private static bool ContainsAny(string value, params string[] terms) => terms.Any(value.Contains);

    private static string GetSourceName(string host)
    {
        foreach (var source in KnownSources)
            if (host.EndsWith(source.Key, StringComparison.OrdinalIgnoreCase)) return source.Value;
        return "Website";
    }

    private static bool IsAllowedHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        return !host.Contains("softonic", StringComparison.OrdinalIgnoreCase) &&
               !host.Contains("uptodown", StringComparison.OrdinalIgnoreCase) &&
               !host.Contains("cnet.com", StringComparison.OrdinalIgnoreCase) &&
               !host.Contains("download.com", StringComparison.OrdinalIgnoreCase);
    }

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
