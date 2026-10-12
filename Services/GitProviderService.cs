using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DanLoopServer.Models;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using Microsoft.AspNetCore.DataProtection;

namespace DanLoopServer.Services;

/// <summary>Provider-neutral repository discovery and Git transport. GitHub keeps its richer App/API path;
/// every other provider uses its repository API for discovery and authenticated smart-HTTP for content.</summary>
public sealed class GitProviderService(HttpClient http, GitHubService gitHub, IDataProtectionProvider protectionProvider)
{
    private readonly IDataProtector _protector = protectionProvider.CreateProtector("git-provider-access-tokens-v1");

    public async Task<GitHubConnection> ConnectAsync(string provider, string token, string? baseUrl, string? username,
        string? namespaceName, string? region, string? repositoryUrl, CancellationToken cancellationToken)
    {
        provider = GitProviders.All.FirstOrDefault(x => x.Equals(provider, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("Choose a supported Git provider.");
        if (provider == GitProviders.GitHub && string.IsNullOrWhiteSpace(repositoryUrl))
            return await gitHub.LoadTokenConnectionAsync(token, cancellationToken);
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException($"A {provider} access token or Git password is required.");

        var connection = new GitHubConnection
        {
            Provider = provider,
            BaseUrl = NormalizeBaseUrl(baseUrl, provider),
            Namespace = namespaceName?.Trim() ?? "",
            Region = region?.Trim() ?? "",
            Username = username?.Trim() ?? "",
            AuthenticationType = GitHubAuthenticationTypes.Token,
            ProtectedToken = _protector.Protect(token.Trim()),
            AccountLogin = username?.Trim() ?? namespaceName?.Trim() ?? provider,
            AccountType = "Git provider",
            SettingsUrl = NormalizeBaseUrl(baseUrl, provider),
            RefreshedAt = DateTimeOffset.UtcNow
        };

        if (!string.IsNullOrWhiteSpace(repositoryUrl))
        {
            connection.Repositories = [RepositoryFromUrl(repositoryUrl, provider)];
            await VerifyRepositoryAsync(connection, connection.Repositories[0], cancellationToken);
        }
        else
        {
            connection.Repositories = await DiscoverAsync(connection, token.Trim(), cancellationToken);
            foreach (var repository in connection.Repositories)
                repository.Id = StableId($"{provider}|{connection.BaseUrl}|{repository.ExternalId}");
            if (connection.Repositories.Count == 0)
                throw new InvalidOperationException($"{provider} returned no repositories. Supply a repository HTTPS URL to connect it directly.");
        }
        return connection;
    }

    public async Task<GitHubConnection> RefreshAsync(GitHubConnection connection, CancellationToken cancellationToken)
    {
        if (connection.Repositories.Count > 0 && connection.Repositories.All(repo => repo.ExternalId.StartsWith(connection.Provider + ":", StringComparison.Ordinal)))
        {
            foreach (var repository in connection.Repositories) await VerifyRepositoryAsync(connection, repository, cancellationToken);
            connection.RefreshedAt = DateTimeOffset.UtcNow;
            return connection;
        }
        if (UsesGitHubApi(connection)) return await gitHub.RefreshConnectionAsync(connection, cancellationToken);
        var token = Token(connection);
        var refreshed = await ConnectAsync(connection.Provider, token, connection.BaseUrl, connection.Username,
            connection.Namespace, connection.Region, null, cancellationToken);
        refreshed.Id = connection.Id;
        refreshed.ConnectedAt = connection.ConnectedAt;
        return refreshed;
    }

    public async Task<string> PushProjectFilesAsync(GitHubConnection connection, GitHubRepository repository,
        string projectRoot, IReadOnlyList<string> relativePaths, string projectFolder, string commitMessage, CancellationToken cancellationToken)
    {
        if (UsesGitHubApi(connection))
            return await gitHub.PushProjectFilesAsync(connection, repository, projectRoot, relativePaths, projectFolder, commitMessage, cancellationToken);
        if (relativePaths.Count == 0) throw new InvalidOperationException("Choose at least one project file to publish.");
        return await Task.Run(() => CommitFiles(connection, repository, projectRoot,
            relativePaths.Select(path => (Source: Path.Combine(projectRoot, path.Replace('/', Path.DirectorySeparatorChar)), Target: $"{projectFolder}/{path.Replace('\\', '/')}")).ToList(),
            commitMessage, cancellationToken), cancellationToken);
    }

    public async Task<string> PushResultFolderAsync(GitHubConnection connection, GitHubRepository repository,
        string localFolder, string projectFolder, string resultName, CancellationToken cancellationToken)
    {
        if (UsesGitHubApi(connection))
            return await gitHub.PushResultFolderAsync(connection, repository, localFolder, projectFolder, resultName, cancellationToken);
        var files = Directory.EnumerateFiles(localFolder, "*", SearchOption.AllDirectories)
            .Select(path => (Source: path, Target: $"{projectFolder}/results/{resultName}/{Path.GetRelativePath(localFolder, path).Replace('\\', '/')}")).ToList();
        if (files.Count == 0) throw new InvalidOperationException("The result folder contains no files to push.");
        return await Task.Run(() => CommitFiles(connection, repository, localFolder, files, $"Add dan-loop results: {resultName}", cancellationToken), cancellationToken);
    }

    public async Task<byte[]> DownloadRepositoryArchiveAsync(GitHubConnection connection, GitHubRepository repository, CancellationToken cancellationToken)
    {
        if (UsesGitHubApi(connection)) return await gitHub.DownloadRepositoryArchiveAsync(connection, repository, cancellationToken);
        return await Task.Run(() =>
        {
            var temporary = TemporaryDirectory();
            try
            {
                Clone(connection, repository, temporary, cancellationToken);
                using var output = new MemoryStream();
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    foreach (var file in Directory.EnumerateFiles(temporary, "*", SearchOption.AllDirectories)
                                 .Where(path => !Path.GetRelativePath(temporary, path).Split(Path.DirectorySeparatorChar).Contains(".git")))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var info = new FileInfo(file);
                        if (info.Length > 250L * 1024 * 1024) throw new InvalidOperationException("Repository contains a file larger than the 250 MB import limit.");
                        zip.CreateEntryFromFile(file, Path.GetRelativePath(temporary, file).Replace('\\', '/'), CompressionLevel.Fastest);
                    }
                }
                if (output.Length > 250L * 1024 * 1024) throw new InvalidOperationException("Repository archive exceeds the 250 MB import limit.");
                return output.ToArray();
            }
            finally { DeleteDirectory(temporary); }
        }, cancellationToken);
    }

    private string CommitFiles(GitHubConnection connection, GitHubRepository repository, string sourceRoot,
        IReadOnlyList<(string Source, string Target)> files, string message, CancellationToken cancellationToken)
    {
        var temporary = TemporaryDirectory();
        try
        {
            Clone(connection, repository, temporary, cancellationToken);
            using var repo = new Repository(temporary);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Path.GetFullPath(file.Source);
                if (!File.Exists(source)) throw new InvalidOperationException($"Project file '{Path.GetRelativePath(sourceRoot, source)}' was not found.");
                if (new FileInfo(source).Length > 100L * 1024 * 1024) throw new InvalidOperationException($"{Path.GetFileName(source)} exceeds the 100 MB publish limit.");
                var target = Path.GetFullPath(Path.Combine(temporary, file.Target.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(Path.GetFullPath(temporary) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidOperationException("A publish path escaped the repository workspace.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, true);
                Commands.Stage(repo, file.Target);
            }
            if (!repo.RetrieveStatus().IsDirty) throw new InvalidOperationException("The selected files already match the repository.");
            var signature = new Signature("dan-loop", "dan-loop@localhost", DateTimeOffset.UtcNow);
            var commit = repo.Commit(message, signature, signature);
            var remote = repo.Network.Remotes["origin"];
            repo.Network.Push(remote, $"refs/heads/{repo.Head.FriendlyName}", new PushOptions { CredentialsProvider = Credentials(connection) });
            return CommitUrl(connection, repository, commit.Sha);
        }
        catch (LibGit2SharpException ex) { throw new InvalidOperationException($"{connection.Provider} Git operation failed: {ex.Message}", ex); }
        finally { DeleteDirectory(temporary); }
    }

    private void Clone(GitHubConnection connection, GitHubRepository repository, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Repository.Clone(repository.CloneUrl, path, new CloneOptions
        {
            BranchName = string.IsNullOrWhiteSpace(repository.DefaultBranch) ? null : repository.DefaultBranch,
            FetchOptions = { CredentialsProvider = Credentials(connection) }
        });
    }

    private CredentialsHandler Credentials(GitHubConnection connection) => (_, _, _) => new UsernamePasswordCredentials
    {
        Username = GitUsername(connection), Password = Token(connection)
    };

    private static string GitUsername(GitHubConnection connection) => !string.IsNullOrWhiteSpace(connection.Username) ? connection.Username : connection.Provider switch
    {
        GitProviders.GitLab => "oauth2",
        GitProviders.Gitee => "oauth2",
        GitProviders.GoogleSecureSourceManager => "oauth2",
        _ => "x-token-auth"
    };

    private async Task VerifyRepositoryAsync(GitHubConnection connection, GitHubRepository repository, CancellationToken cancellationToken)
    {
        var path = TemporaryDirectory();
        try { await Task.Run(() => Clone(connection, repository, path, cancellationToken), cancellationToken); }
        catch (LibGit2SharpException ex) { throw new InvalidOperationException($"Could not access that {connection.Provider} repository with the supplied credentials: {ex.Message}", ex); }
        finally { DeleteDirectory(path); }
    }

    private async Task<List<GitHubRepository>> DiscoverAsync(GitHubConnection connection, string token, CancellationToken cancellationToken) => connection.Provider switch
    {
        GitProviders.GitLab => await DiscoverGitLabAsync(connection, token, cancellationToken),
        GitProviders.Bitbucket => await DiscoverBitbucketAsync(connection, token, cancellationToken),
        GitProviders.Codeberg or GitProviders.Gitea => await DiscoverGiteaAsync(connection, token, cancellationToken),
        GitProviders.Gitee => await DiscoverGiteeAsync(connection, token, cancellationToken),
        GitProviders.AzureDevOps => await DiscoverAzureAsync(connection, token, cancellationToken),
        GitProviders.SourceHut => await DiscoverSourceHutAsync(connection, token, cancellationToken),
        GitProviders.AwsCodeCommit => throw ManualUrlRequired(connection.Provider, "Use the HTTPS clone URL (not HTTPS GRC) and IAM-generated Git username/password."),
        GitProviders.GoogleSecureSourceManager => throw ManualUrlRequired(connection.Provider, "Use the repository HTTPS clone URL and an OAuth access token."),
        _ => throw ManualUrlRequired(connection.Provider, "Supply the repository HTTPS clone URL.")
    };

    private async Task<List<GitHubRepository>> DiscoverGitLabAsync(GitHubConnection c, string token, CancellationToken ct)
    {
        using var user = await JsonAsync(c.BaseUrl + "/api/v4/user", token, "PRIVATE-TOKEN", ct);
        c.AccountLogin = Text(user.RootElement, "username", c.AccountLogin);
        c.AccountType = "GitLab account";
        c.SettingsUrl = c.BaseUrl + "/-/profile/personal_access_tokens";
        using var projects = await JsonAsync(c.BaseUrl + "/api/v4/projects?membership=true&simple=true&per_page=100&order_by=last_activity_at", token, "PRIVATE-TOKEN", ct);
        return projects.RootElement.EnumerateArray().Select(x => FromJson(x, "id", "name", "path_with_namespace", "web_url", "http_url_to_repo", "default_branch", "visibility")).ToList();
    }

    private async Task<List<GitHubRepository>> DiscoverGiteaAsync(GitHubConnection c, string token, CancellationToken ct)
    {
        using var user = await JsonAsync(c.BaseUrl + "/api/v1/user", token, "Authorization", ct, "token ");
        c.AccountLogin = Text(user.RootElement, "login", c.AccountLogin);
        c.AccountType = c.Provider + " account";
        using var repos = await JsonAsync(c.BaseUrl + "/api/v1/user/repos?limit=100", token, "Authorization", ct, "token ");
        return repos.RootElement.EnumerateArray().Select(x => FromJson(x, "id", "name", "full_name", "html_url", "clone_url", "default_branch", "private")).ToList();
    }

    private async Task<List<GitHubRepository>> DiscoverGiteeAsync(GitHubConnection c, string token, CancellationToken ct)
    {
        using var user = await JsonAsync(c.BaseUrl + "/api/v5/user", token, "Authorization", ct, "token ");
        c.AccountLogin = Text(user.RootElement, "login", c.AccountLogin);
        c.AccountType = "Gitee account";
        using var repos = await JsonAsync(c.BaseUrl + "/api/v5/user/repos?type=all&sort=updated&per_page=100", token, "Authorization", ct, "token ");
        return repos.RootElement.EnumerateArray().Select(x => FromJson(x, "id", "name", "full_name", "html_url", "clone_url", "default_branch", "private")).ToList();
    }

    private async Task<List<GitHubRepository>> DiscoverBitbucketAsync(GitHubConnection c, string token, CancellationToken ct)
    {
        var apiCredential = string.IsNullOrWhiteSpace(c.Username) ? token : Convert.ToBase64String(Encoding.UTF8.GetBytes(c.Username + ":" + token));
        var apiPrefix = string.IsNullOrWhiteSpace(c.Username) ? "Bearer " : "Basic ";
        using var user = await JsonAsync("https://api.bitbucket.org/2.0/user", apiCredential, "Authorization", ct, apiPrefix);
        c.AccountLogin = Text(user.RootElement, "display_name", c.AccountLogin);
        c.AccountType = "Bitbucket account";
        var workspace = string.IsNullOrWhiteSpace(c.Namespace) ? Text(user.RootElement, "username", "") : c.Namespace;
        if (string.IsNullOrWhiteSpace(workspace)) throw new InvalidOperationException("Bitbucket requires a workspace name.");
        using var repos = await JsonAsync($"https://api.bitbucket.org/2.0/repositories/{Uri.EscapeDataString(workspace)}?role=member&pagelen=100", apiCredential, "Authorization", ct, apiPrefix);
        return repos.RootElement.GetProperty("values").EnumerateArray().Select(x =>
        {
            var full = Text(x, "full_name", ""); var clone = x.GetProperty("links").GetProperty("clone").EnumerateArray().First(v => Text(v, "name", "") == "https");
            return NewRepository(Text(x, "uuid", full), Text(x, "name", full), full, x.GetProperty("links").GetProperty("html").GetProperty("href").GetString() ?? "", Text(clone, "href", ""), x.TryGetProperty("mainbranch", out var b) ? Text(b, "name", "main") : "main", x.TryGetProperty("is_private", out var p) && p.GetBoolean());
        }).ToList();
    }

    private async Task<List<GitHubRepository>> DiscoverAzureAsync(GitHubConnection c, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.Namespace)) throw new InvalidOperationException("Azure DevOps requires the organization name in Namespace/workspace.");
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + token));
        using var repos = await JsonAsync($"{c.BaseUrl}/{Uri.EscapeDataString(c.Namespace)}/_apis/git/repositories?api-version=7.1", auth, "Authorization", ct, "Basic ");
        c.AccountLogin = c.Namespace; c.AccountType = "Azure DevOps organization"; c.SettingsUrl = $"{c.BaseUrl}/{c.Namespace}/_settings/repositories";
        return repos.RootElement.GetProperty("value").EnumerateArray().Select(x =>
        {
            var project = x.GetProperty("project"); var full = Text(project, "name", "") + "/" + Text(x, "name", "");
            return NewRepository(Text(x, "id", full), Text(x, "name", full), full, Text(x, "webUrl", ""), Text(x, "remoteUrl", ""), Text(x, "defaultBranch", "refs/heads/main").Replace("refs/heads/", ""), true);
        }).ToList();
    }

    private async Task<List<GitHubRepository>> DiscoverSourceHutAsync(GitHubConnection c, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.Username) && string.IsNullOrWhiteSpace(c.Namespace))
            throw new InvalidOperationException("SourceHut requires your username.");
        var owner = (string.IsNullOrWhiteSpace(c.Username) ? c.Namespace : c.Username).TrimStart('~');
        c.AccountLogin = "~" + owner; c.AccountType = "SourceHut account"; c.SettingsUrl = "https://meta.sr.ht/oauth2/personal-token";
        using var repos = await JsonAsync($"https://git.sr.ht/api/~{Uri.EscapeDataString(owner)}/repos", token, "Authorization", ct, "token ");
        var values = repos.RootElement.ValueKind == JsonValueKind.Array ? repos.RootElement : repos.RootElement.GetProperty("results");
        return values.EnumerateArray().Select(x =>
        {
            var name = Text(x, "name", ""); var full = "~" + owner + "/" + name; var url = "https://git.sr.ht/" + full;
            return NewRepository(Text(x, "id", full), name, full, url, url, Text(x, "default_branch", "master"), Text(x, "visibility", "public") != "public");
        }).ToList();
    }

    private async Task<JsonDocument> JsonAsync(string url, string token, string header, CancellationToken ct, string prefix = "")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation(header, prefix + token);
        request.Headers.UserAgent.ParseAdd("dan-loop-server/1.0");
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Provider API returned {(int)response.StatusCode}: {body[..Math.Min(500, body.Length)]}");
        return JsonDocument.Parse(body);
    }

    private string Token(GitHubConnection connection)
    {
        try { return _protector.Unprotect(connection.ProtectedToken); }
        catch { throw new InvalidOperationException($"The stored {connection.Provider} credential cannot be decrypted. Remove the connection and connect it again."); }
    }

    private static bool UsesGitHubApi(GitHubConnection c) => (string.IsNullOrWhiteSpace(c.Provider) || c.Provider == GitProviders.GitHub) &&
        (c.Repositories.Count == 0 || c.Repositories.All(repo => string.IsNullOrWhiteSpace(repo.ExternalId)));
    private static string NormalizeBaseUrl(string? value, string provider)
    {
        var normalized = (string.IsNullOrWhiteSpace(value) ? GitProviders.DefaultBaseUrl(provider) : value).Trim().TrimEnd('/');
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Provider server URL must be an absolute HTTP or HTTPS URL.");
        return normalized;
    }
    private static Exception ManualUrlRequired(string provider, string guidance) => new InvalidOperationException($"{provider} repository discovery requires a repository URL. {guidance}");
    private static GitHubRepository RepositoryFromUrl(string url, string provider)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Repository URL must be an HTTPS clone URL.");
        var full = uri.AbsolutePath.Trim('/').Replace(".git", "", StringComparison.OrdinalIgnoreCase);
        var repository = NewRepository(provider + ":" + url, full.Split('/').LastOrDefault() ?? "repository", full, url.Replace(".git", "", StringComparison.OrdinalIgnoreCase), url, "main", true);
        repository.DefaultBranch = ""; // Let the remote advertise its default branch.
        return repository;
    }
    private static GitHubRepository FromJson(JsonElement x, string id, string name, string full, string html, string clone, string branch, string privacy)
    {
        var isPrivate = x.TryGetProperty(privacy, out var p) && (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.String && p.GetString() != "public");
        return NewRepository(x.GetProperty(id).ToString(), Text(x, name, ""), Text(x, full, ""), Text(x, html, ""), Text(x, clone, ""), Text(x, branch, "main"), isPrivate);
    }
    private static GitHubRepository NewRepository(string externalId, string name, string full, string html, string clone, string branch, bool isPrivate) => new()
    {
        Id = StableId(externalId), ExternalId = externalId, Name = name, FullName = full, HtmlUrl = html, CloneUrl = clone,
        DefaultBranch = string.IsNullOrWhiteSpace(branch) ? "main" : branch, IsPrivate = isPrivate
    };
    private static long StableId(string value) => BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(value)), 0) & long.MaxValue;
    private static string Text(JsonElement element, string property, string fallback) => element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : fallback;
    private static string TemporaryDirectory() { var path = Path.Combine(Path.GetTempPath(), "dan-loop-git", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static void DeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
    private static string CommitUrl(GitHubConnection connection, GitHubRepository repository, string sha) => connection.Provider switch
    {
        GitProviders.GitLab => $"{repository.HtmlUrl}/-/commit/{sha}",
        GitProviders.Bitbucket => $"{repository.HtmlUrl}/commits/{sha}",
        GitProviders.SourceHut => $"{repository.HtmlUrl}/commit/{sha}",
        GitProviders.AzureDevOps => $"{repository.HtmlUrl}/commit/{sha}",
        _ => $"{repository.HtmlUrl}/commit/{sha}"
    };
}
