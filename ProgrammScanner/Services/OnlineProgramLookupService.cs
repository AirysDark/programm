using ProgrammScanner.Models;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace ProgrammScanner.Services;

public static class OnlineProgramLookupService
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private sealed record SearchResult(string Title, string Url);

    public static async Task LookupAsync(InstalledProgram program)
    {
        program.OnlineStatus = "Searching...";

        var query = BuildQuery(program);
        var results = await SearchAsync(query);
        if (results.Count == 0)
        {
            program.OnlineStatus = "No online result found";
            return;
        }

        var official = SelectOfficialResult(results, program);
        if (official != null)
        {
            program.OfficialWebsite = official.Url;
            program.OnlineSource = GetHost(official.Url);
        }

        var downloadQuery = $"{program.Name} {program.Publisher} official download".Trim();
        var downloadResults = await SearchAsync(downloadQuery);
        var download = SelectOfficialResult(downloadResults, program, official?.Url);

        if (download != null)
        {
            program.DownloadUrl = download.Url;
            if (string.IsNullOrWhiteSpace(program.OfficialWebsite))
            {
                program.OfficialWebsite = download.Url;
                program.OnlineSource = GetHost(download.Url);
            }
        }

        if (!string.IsNullOrWhiteSpace(program.OfficialWebsite) || !string.IsNullOrWhiteSpace(program.DownloadUrl))
            program.OnlineStatus = "Found";
        else
            program.OnlineStatus = "No verified official link found";
    }

    private static async Task<List<SearchResult>> SearchAsync(string query)
    {
        try
        {
            var url = "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("ProgrammScanner/1.0 (+https://github.com/AirysDark/programm)");

            using var response = await Client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync();

            var results = new List<SearchResult>();
            var matches = Regex.Matches(
                html,
                "<a[^>]*class=\"result__a\"[^>]*href=\"(?<url>[^\"]+)\"[^>]*>(?<title>.*?)</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            foreach (Match match in matches)
            {
                var href = WebUtility.HtmlDecode(match.Groups["url"].Value);
                var title = StripHtml(WebUtility.HtmlDecode(match.Groups["title"].Value));
                var target = DecodeDuckDuckGoRedirect(href);

                if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
                    uri.Scheme is not ("http" or "https"))
                    continue;

                if (IsBlockedHost(uri.Host)) continue;
                results.Add(new SearchResult(title, uri.AbsoluteUri));
            }

            return results
                .GroupBy(x => x.Url, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .Take(20)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static SearchResult? SelectOfficialResult(
        IEnumerable<SearchResult> results,
        InstalledProgram program,
        string? preferredUrl = null)
    {
        var publisherTokens = Tokens(program.Publisher).ToList();
        var programTokens = Tokens(program.Name).ToList();
        var preferredHost = string.IsNullOrWhiteSpace(preferredUrl) ? "" : GetHost(preferredUrl);

        return results
            .Select(result => new
            {
                Result = result,
                Score = ScoreResult(result, publisherTokens, programTokens, preferredHost)
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Result)
            .FirstOrDefault();
    }

    private static int ScoreResult(SearchResult result, List<string> publisherTokens,
        List<string> programTokens, string preferredHost)
    {
        if (!Uri.TryCreate(result.Url, UriKind.Absolute, out var uri)) return -1000;

        var host = uri.Host.ToLowerInvariant();
        var text = (result.Title + " " + result.Url).ToLowerInvariant();
        var score = 0;

        if (!string.IsNullOrWhiteSpace(preferredHost) && host.Equals(preferredHost, StringComparison.OrdinalIgnoreCase))
            score += 100;

        foreach (var token in publisherTokens)
        {
            if (host.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 40;
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 8;
        }

        foreach (var token in programTokens)
        {
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 8;
        }

        if (text.Contains("official")) score += 15;
        if (text.Contains("download")) score += 12;
        if (host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase) &&
            (text.Contains("releases") || text.Contains("release"))) score += 15;

        if (IsKnownOfficialPlatform(host)) score += 5;
        if (IsBlockedHost(host)) score -= 1000;

        return score;
    }

    private static IEnumerable<string> Tokens(string value)
    {
        return Regex.Matches(value ?? "", @"[A-Za-z0-9]{3,}")
            .Select(x => x.Value.ToLowerInvariant())
            .Where(x => x is not "microsoft" and not "corporation" and not "software" and not "inc" and not "ltd" and not "llc")
            .Distinct();
    }

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
            {
                if (!pair.StartsWith("uddg=", StringComparison.OrdinalIgnoreCase)) continue;
                return Uri.UnescapeDataString(pair[5..]);
            }
        }
        catch { }
        return href;
    }

    private static string StripHtml(string value) => Regex.Replace(value, "<.*?>", " ").Trim();

    private static string GetHost(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "";
    }

    private static string BuildQuery(InstalledProgram program)
    {
        var parts = new[] { program.Name, program.Publisher, "official website" }
            .Where(x => !string.IsNullOrWhiteSpace(x));
        return string.Join(" ", parts);
    }
}
