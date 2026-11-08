using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WillOPatchwisp;

public enum GitHubLookupStatus
{
    Loading,
    Found,
    NotFound,
    Failed,
}

/// <param name="Source">Short label of where the text came from, e.g. "GitHub release v1.2.3".</param>
/// <param name="Url">Link to open on GitHub.</param>
public sealed record GitHubLookup(GitHubLookupStatus Status, string? Text = null, string? Source = null, string? Url = null);

/// <summary>
/// Fallback for plugins that don't ship a changelog in their manifest: looks on the plugin's GitHub repository for
/// 1) a release whose tag matches the version, 2) the latest release, 3) the most recent commit messages.
/// Lookups only happen on demand (when a card is opened) and are cached, to stay well within GitHub's
/// limit of 60 unauthenticated requests per hour.
/// </summary>
public sealed class GitHubChangelogs : IDisposable
{
    private static readonly Regex RepoRegex = new(@"github\.com[/:]([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)", RegexOptions.IgnoreCase);
    private static readonly Regex VersionRegex = new(@"\d+(\.\d+)+");

    private readonly HttpClient http;
    private readonly CancellationTokenSource cts = new();
    private readonly ConcurrentDictionary<string, GitHubLookup> cache = new();

    public GitHubChangelogs()
    {
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Will-o-Patchwisp", "2.1"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public void Dispose()
    {
        cts.Cancel();
        http.Dispose();
        cts.Dispose();
    }

    /// <summary>Returns (owner, repo) when the URL points at a GitHub repository.</summary>
    public static (string Owner, string Repo)? ParseRepo(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return null;
        var m = RepoRegex.Match(url);
        if (!m.Success)
            return null;
        var repo = m.Groups[2].Value;
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repo = repo[..^4];
        return (m.Groups[1].Value, repo);
    }

    /// <summary>Gets the cached result, starting a lookup the first time it's asked for.</summary>
    public GitHubLookup Get(string repoUrl, string version)
    {
        var parsed = ParseRepo(repoUrl);
        if (parsed == null)
            return new GitHubLookup(GitHubLookupStatus.NotFound);

        var (owner, repo) = parsed.Value;
        var key = $"{owner}/{repo}@{version}".ToLowerInvariant();

        if (cache.TryGetValue(key, out var existing))
            return existing;

        var loading = new GitHubLookup(GitHubLookupStatus.Loading);
        if (cache.TryAdd(key, loading))
        {
            Task.Run(async () =>
            {
                GitHubLookup result;
                try
                {
                    result = await LookupWithRetryAsync(owner, repo, version, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning(ex, $"GitHub lookup for {owner}/{repo} failed");
                    result = new GitHubLookup(GitHubLookupStatus.Failed, Describe(ex));
                }

                cache[key] = result;
            });
        }

        return loading;
    }

    /// <summary>
    /// GitHub sometimes has short outages (502/503/504) or slow responses. Try up to 3 times with a
    /// growing pause before giving up; rate limits (403/429) are not retried.
    /// </summary>
    private async Task<GitHubLookup> LookupWithRetryAsync(string owner, string repo, string version, CancellationToken token)
    {
        var delays = new[] { 2, 6 };
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await LookupAsync(owner, repo, version, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < delays.Length && IsTemporary(ex, token))
            {
                Plugin.Log.Debug($"GitHub lookup for {owner}/{repo} failed ({Describe(ex)}), retrying in {delays[attempt]}s");
                await Task.Delay(TimeSpan.FromSeconds(delays[attempt]), token).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTemporary(Exception ex, CancellationToken token) => ex switch
    {
        HttpRequestException { StatusCode: null } => true,                               // connection problem
        HttpRequestException { StatusCode: { } c } => (int)c >= 500,                     // GitHub-side error
        TaskCanceledException when !token.IsCancellationRequested => true,                // timed out
        _ => false,
    };

    /// <summary>A short, honest explanation for the card.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden or (HttpStatusCode)429 } =>
            "GitHub's hourly lookup limit was reached. Try again later.",
        HttpRequestException { StatusCode: { } c } when (int)c >= 500 =>
            $"GitHub is having problems right now (error {(int)c}). Try again in a little while.",
        HttpRequestException { StatusCode: { } c } =>
            $"GitHub answered with error {(int)c}.",
        HttpRequestException => "Couldn't connect to GitHub. Check your internet connection.",
        TaskCanceledException => "GitHub took too long to answer. Try again in a little while.",
        _ => "Something went wrong while reading GitHub's answer.",
    };

    /// <summary>Forget a failed lookup so it's tried again.</summary>
    public void Retry(string repoUrl, string version)
    {
        if (ParseRepo(repoUrl) is { } p)
            cache.TryRemove($"{p.Owner}/{p.Repo}@{version}".ToLowerInvariant(), out _);
    }

    private async Task<GitHubLookup> LookupAsync(string owner, string repo, string version, CancellationToken token)
    {
        var baseUrl = $"https://api.github.com/repos/{owner}/{repo}";
        var repoPage = $"https://github.com/{owner}/{repo}";

        // 1 + 2: releases
        var releases = await GetJsonAsync($"{baseUrl}/releases?per_page=30", token).ConfigureAwait(false);
        if (releases is { ValueKind: JsonValueKind.Array } && releases.Value.GetArrayLength() > 0)
        {
            var list = releases.Value.EnumerateArray()
                .Where(r => !r.TryGetProperty("draft", out var d) || !d.GetBoolean())
                .ToList();

            var wanted = Normalize(version);
            foreach (var r in list)
            {
                var tag = Str(r, "tag_name") ?? string.Empty;
                var name = Str(r, "name") ?? string.Empty;
                if (wanted != null && (Normalize(tag) == wanted || Normalize(name) == wanted))
                {
                    var body = Clean(Str(r, "body"));
                    if (body.Length > 0)
                        return new GitHubLookup(GitHubLookupStatus.Found, body, $"GitHub release {tag}", Str(r, "html_url") ?? repoPage);
                }
            }

            var latest = list.FirstOrDefault(r => Clean(Str(r, "body")).Length > 0);
            if (latest.ValueKind == JsonValueKind.Object)
            {
                var tag = Str(latest, "tag_name");
                return new GitHubLookup(
                    GitHubLookupStatus.Found,
                    Clean(Str(latest, "body")),
                    $"Latest GitHub release ({tag}). No release matches this exact version",
                    Str(latest, "html_url") ?? repoPage);
            }
        }

        // 3: recent commits
        var commits = await GetJsonAsync($"{baseUrl}/commits?per_page=10", token).ConfigureAwait(false);
        if (commits is { ValueKind: JsonValueKind.Array } && commits.Value.GetArrayLength() > 0)
        {
            var sb = new StringBuilder();
            foreach (var c in commits.Value.EnumerateArray())
            {
                if (!c.TryGetProperty("commit", out var commit))
                    continue;
                var msg = (Str(commit, "message") ?? string.Empty).Split('\n')[0].Trim();
                if (msg.Length == 0 || msg.StartsWith("Merge ", StringComparison.OrdinalIgnoreCase))
                    continue;

                var date = commit.TryGetProperty("author", out var a) && Str(a, "date") is { } ds && DateTime.TryParse(ds, out var dt)
                    ? dt.ToLocalTime().ToString("d MMM") + "  "
                    : string.Empty;
                sb.Append("- ").Append(date).AppendLine(msg);
            }

            if (sb.Length > 0)
                return new GitHubLookup(GitHubLookupStatus.Found, sb.ToString().TrimEnd(), "No release notes on GitHub. Recent commits", $"{repoPage}/commits");
        }

        return new GitHubLookup(GitHubLookupStatus.NotFound);
    }

    private async Task<JsonElement?> GetJsonAsync(string url, CancellationToken token)
    {
        using var resp = await http.GetAsync(url, token).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>"v1.2.3" / "1.2.3.0" / "Release 1.2.3" -> "1.2.3" (trailing .0 parts removed).</summary>
    private static string? Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return null;
        var m = VersionRegex.Match(s);
        if (!m.Success)
            return null;
        var parts = m.Value.Split('.').Select(p => p.TrimStart('0') is { Length: > 0 } t ? t : "0").ToList();
        while (parts.Count > 1 && parts[^1] == "0")
            parts.RemoveAt(parts.Count - 1);
        return string.Join('.', parts);
    }

    /// <summary>Light markdown cleanup so release notes read well as plain text.</summary>
    private static string Clean(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;

        var s = body.Replace("\r", string.Empty);
        s = Regex.Replace(s, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);  // html comments
        s = Regex.Replace(s, @"!\[[^\]]*\]\([^)]*\)", string.Empty);                  // images
        s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]*\)", "$1");                         // links -> text
        s = Regex.Replace(s, @"^#{1,6}\s*", string.Empty, RegexOptions.Multiline);    // headings
        s = Regex.Replace(s, @"^\s*[*+]\s+", "- ", RegexOptions.Multiline);           // bullets
        s = s.Replace("**", string.Empty).Replace("__", string.Empty).Replace("`", string.Empty);
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        s = s.Trim();

        const int max = 4000;
        return s.Length > max ? s[..max].TrimEnd() + "\n..." : s;
    }
}
