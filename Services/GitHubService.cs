using System.Net.Http.Headers;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DanLoopServer.Models;
using Microsoft.AspNetCore.DataProtection;

namespace DanLoopServer.Services;

public sealed class GitHubService(HttpClient http, IConfiguration configuration, IDataProtectionProvider protectionProvider)
{
    private readonly IDataProtector _tokenProtector = protectionProvider.CreateProtector("github-access-tokens-v1");
    private readonly string _appId = Value(configuration, "GitHub:AppId", "GITHUB_APP_ID");
    private readonly string _appSlug = Value(configuration, "GitHub:AppSlug", "GITHUB_APP_SLUG");
    private readonly string _clientId = Value(configuration, "GitHub:ClientId", "GITHUB_CLIENT_ID");
    private readonly string _clientSecret = Value(configuration, "GitHub:ClientSecret", "GITHUB_CLIENT_SECRET");
    private readonly string _privateKey = LoadPrivateKey(configuration);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_appId) &&
                                !string.IsNullOrWhiteSpace(_appSlug) &&
                                !string.IsNullOrWhiteSpace(_clientId) &&
                                !string.IsNullOrWhiteSpace(_clientSecret) &&
                                !string.IsNullOrWhiteSpace(_privateKey);

    public string ConfigurationMessage => IsConfigured
        ? "GitHub App is configured."
        : "Configure the GitHub App ID, slug, client ID, client secret, and private key before connecting repositories.";

    public async Task<GitHubConnection> LoadTokenConnectionAsync(string token, CancellationToken cancellationToken)
    {
        token = token.Trim();
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("A GitHub access token is required.");

        using var userRequest = ApiRequest(HttpMethod.Get, "user", token);
        using var userResponse = await http.SendAsync(userRequest, cancellationToken);
        var userBody = await userResponse.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(userResponse, userBody);
        var user = JsonSerializer.Deserialize<GitHubUserResponse>(userBody, JsonOptions())
                   ?? throw new InvalidOperationException("GitHub returned an invalid user response.");

        return new GitHubConnection
        {
            AuthenticationType = GitHubAuthenticationTypes.Token,
            ProtectedToken = _tokenProtector.Protect(token),
            AccountLogin = user.Login,
            AccountType = user.Type,
            SettingsUrl = "https://github.com/settings/personal-access-tokens",
            Repositories = await LoadTokenRepositoriesAsync(token, cancellationToken),
            RefreshedAt = DateTimeOffset.UtcNow
        };
    }

    public async Task<GitHubConnection> RefreshConnectionAsync(GitHubConnection connection, CancellationToken cancellationToken)
    {
        if (connection.AuthenticationType == GitHubAuthenticationTypes.Token)
        {
            var refreshed = await LoadTokenConnectionAsync(UnprotectToken(connection), cancellationToken);
            refreshed.Id = connection.Id;
            refreshed.ConnectedAt = connection.ConnectedAt;
            return refreshed;
        }

        var appConnection = await LoadConnectionAsync(connection.InstallationId, cancellationToken);
        appConnection.Id = connection.Id;
        appConnection.ConnectedAt = connection.ConnectedAt;
        return appConnection;
    }

    public string CreateAuthorizationUrl(string callbackUrl, string state)
    {
        EnsureConfigured();
        return "https://github.com/login/oauth/authorize?client_id=" + Uri.EscapeDataString(_clientId) +
               "&redirect_uri=" + Uri.EscapeDataString(callbackUrl) +
               "&state=" + Uri.EscapeDataString(state);
    }

    public string CreateInstallationUrl(string state)
    {
        EnsureConfigured();
        return $"https://github.com/apps/{Uri.EscapeDataString(_appSlug)}/installations/new?state={Uri.EscapeDataString(state)}";
    }

    public async Task<string> ExchangeOAuthCodeAsync(string code, string callbackUrl, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
        {
            Content = JsonContent.Create(new { client_id = _clientId, client_secret = _clientSecret, code, redirect_uri = callbackUrl })
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, body);
        var token = JsonSerializer.Deserialize<OAuthTokenResponse>(body, JsonOptions());
        if (string.IsNullOrWhiteSpace(token?.AccessToken))
            throw new InvalidOperationException(token?.ErrorDescription ?? token?.Error ?? "GitHub did not return a user access token.");
        return token.AccessToken;
    }

    public async Task<bool> UserCanAccessInstallationAsync(string userToken, long installationId, CancellationToken cancellationToken)
    {
        for (var page = 1; ; page++)
        {
            using var request = ApiRequest(HttpMethod.Get, $"user/installations?per_page=100&page={page}", userToken);
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureSuccess(response, body);
            var result = JsonSerializer.Deserialize<UserInstallationsResponse>(body, JsonOptions());
            if (result?.Installations.Any(x => x.Id == installationId) == true) return true;
            if (result is null || result.Installations.Count < 100) return false;
        }
    }

    public async Task<GitHubConnection> LoadConnectionAsync(long installationId, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var jwt = CreateAppJwt();
        using var installationRequest = ApiRequest(HttpMethod.Get, $"app/installations/{installationId}", jwt);
        using var installationResponse = await http.SendAsync(installationRequest, cancellationToken);
        var installationBody = await installationResponse.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(installationResponse, installationBody);
        var installation = JsonSerializer.Deserialize<InstallationResponse>(installationBody, JsonOptions())
                           ?? throw new InvalidOperationException("GitHub returned an invalid installation response.");

        var repositories = await LoadRepositoriesAsync(installationId, cancellationToken);
        return new GitHubConnection
        {
            AuthenticationType = GitHubAuthenticationTypes.App,
            InstallationId = installationId,
            AccountLogin = installation.Account?.Login ?? "Unknown",
            AccountType = installation.Account?.Type ?? "Account",
            SettingsUrl = installation.HtmlUrl,
            Repositories = repositories,
            RefreshedAt = DateTimeOffset.UtcNow
        };
    }

    public async Task<string> CreateInstallationTokenAsync(long installationId, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        using var request = ApiRequest(HttpMethod.Post, $"app/installations/{installationId}/access_tokens", CreateAppJwt());
        request.Content = JsonContent.Create(new { });
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, body);
        return JsonSerializer.Deserialize<InstallationTokenResponse>(body, JsonOptions())?.Token
               ?? throw new InvalidOperationException("GitHub did not return an installation token.");
    }

    public async Task<string> PushResultFolderAsync(GitHubConnection connection, GitHubRepository repository, string localFolder, string resultName, CancellationToken cancellationToken)
    {
        var token = await AccessTokenAsync(connection, cancellationToken);
        var repo = repository.FullName;
        var branch = Uri.EscapeDataString(repository.DefaultBranch);
        var files = Directory.EnumerateFiles(localFolder, "*", SearchOption.AllDirectories).ToList();
        if (files.Count == 0) throw new InvalidOperationException("The result folder contains no files to push.");

        var commitMessage = $"Add dan-loop results: {resultName}";
        var parentSha = await GetReferenceShaAsync(repo, branch, token, cancellationToken);
        if (parentSha is null)
        {
            parentSha = await InitializeRepositoryAsync(repo, localFolder, resultName, files[0], commitMessage, token, cancellationToken);
            if (files.Count == 1) return $"https://github.com/{repo}/commit/{parentSha}";
        }

        using var commit = await SendJsonAsync(HttpMethod.Get, $"repos/{repo}/git/commits/{parentSha}", token, null, cancellationToken);
        var baseTree = commit.RootElement.GetProperty("tree").GetProperty("sha").GetString()!;
        var treeEntries = new List<object>();
        foreach (var file in files)
        {
            var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
            EnsurePushableSize(file, bytes);
            using var blob = await SendJsonAsync(HttpMethod.Post, $"repos/{repo}/git/blobs", token, new { content = Convert.ToBase64String(bytes), encoding = "base64" }, cancellationToken);
            var sha = blob.RootElement.GetProperty("sha").GetString();
            var relative = Path.GetRelativePath(localFolder, file).Replace('\\', '/');
            treeEntries.Add(new { path = $"dan-loop-results/{resultName}/{relative}", mode = "100644", type = "blob", sha });
        }
        using var tree = await SendJsonAsync(HttpMethod.Post, $"repos/{repo}/git/trees", token, new { base_tree = baseTree, tree = treeEntries }, cancellationToken);
        var treeSha = tree.RootElement.GetProperty("sha").GetString();
        using var newCommit = await SendJsonAsync(HttpMethod.Post, $"repos/{repo}/git/commits", token, new { message = commitMessage, tree = treeSha, parents = new[] { parentSha } }, cancellationToken);
        var commitSha = newCommit.RootElement.GetProperty("sha").GetString()!;
        using var updatedReference = await SendJsonAsync(HttpMethod.Patch, $"repos/{repo}/git/refs/heads/{branch}", token, new { sha = commitSha, force = false }, cancellationToken);
        return $"https://github.com/{repo}/commit/{commitSha}";
    }

    private async Task<string?> GetReferenceShaAsync(string repo, string branch, string token, CancellationToken cancellationToken)
    {
        using var request = ApiRequest(HttpMethod.Get, $"repos/{repo}/git/ref/heads/{branch}", token);
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict && IsEmptyRepositoryResponse(body)) return null;
        EnsureSuccess(response, body);
        using var reference = JsonDocument.Parse(body);
        return reference.RootElement.GetProperty("object").GetProperty("sha").GetString();
    }

    private async Task<string> InitializeRepositoryAsync(string repo, string localFolder, string resultName, string firstFile, string commitMessage, string token, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(firstFile, cancellationToken);
        EnsurePushableSize(firstFile, bytes);
        var relative = Path.GetRelativePath(localFolder, firstFile).Replace('\\', '/');
        var path = EscapeContentPath($"dan-loop-results/{resultName}/{relative}");
        using var initialized = await SendJsonAsync(HttpMethod.Put, $"repos/{repo}/contents/{path}", token, new { message = commitMessage, content = Convert.ToBase64String(bytes) }, cancellationToken);
        return initialized.RootElement.GetProperty("commit").GetProperty("sha").GetString()
               ?? throw new InvalidOperationException("GitHub initialized the repository without returning a commit SHA.");
    }

    private static void EnsurePushableSize(string file, byte[] bytes)
    {
        if (bytes.Length > 50 * 1024 * 1024)
            throw new InvalidOperationException($"Result file {Path.GetFileName(file)} exceeds the 50 MB GitHub push limit.");
    }

    private static string EscapeContentPath(string path) =>
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static bool IsEmptyRepositoryResponse(string body)
    {
        try
        {
            using var error = JsonDocument.Parse(body);
            return error.RootElement.TryGetProperty("message", out var message) &&
                   string.Equals(message.GetString(), "Git Repository is empty.", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return false; }
    }

    public async Task<byte[]> DownloadRepositoryArchiveAsync(GitHubConnection connection, GitHubRepository repository, CancellationToken cancellationToken)
    {
        var token = await AccessTokenAsync(connection, cancellationToken);
        using var request = ApiRequest(HttpMethod.Get, $"repos/{repository.FullName}/zipball/{Uri.EscapeDataString(repository.DefaultBranch)}", token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var length = response.Content.Headers.ContentLength;
        if (length > 250L * 1024 * 1024) throw new InvalidOperationException("Repository archive exceeds the 250 MB import limit.");
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length > 250L * 1024 * 1024) throw new InvalidOperationException("Repository archive exceeds the 250 MB import limit.");
        return buffer.ToArray();
    }

    private async Task<List<GitHubRepository>> LoadRepositoriesAsync(long installationId, CancellationToken cancellationToken)
    {
        var token = await CreateInstallationTokenAsync(installationId, cancellationToken);
        var repositories = new List<GitHubRepository>();
        for (var page = 1; ; page++)
        {
            using var request = ApiRequest(HttpMethod.Get, $"installation/repositories?per_page=100&page={page}", token);
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureSuccess(response, body);
            var result = JsonSerializer.Deserialize<RepositoriesResponse>(body, JsonOptions());
            if (result is null) throw new InvalidOperationException("GitHub returned an invalid repositories response.");
            repositories.AddRange(result.Repositories.Select(x => new GitHubRepository
            {
                Id = x.Id,
                Name = x.Name,
                FullName = x.FullName,
                HtmlUrl = x.HtmlUrl,
                CloneUrl = x.CloneUrl,
                DefaultBranch = x.DefaultBranch,
                IsPrivate = x.Private
            }));
            if (result.Repositories.Count < 100) return repositories;
        }
    }

    private async Task<List<GitHubRepository>> LoadTokenRepositoriesAsync(string token, CancellationToken cancellationToken)
    {
        var repositories = new List<GitHubRepository>();
        for (var page = 1; ; page++)
        {
            using var request = ApiRequest(HttpMethod.Get, $"user/repos?per_page=100&page={page}&sort=full_name&affiliation=owner,collaborator,organization_member", token);
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureSuccess(response, body);
            var result = JsonSerializer.Deserialize<List<RepositoryResponse>>(body, JsonOptions())
                         ?? throw new InvalidOperationException("GitHub returned an invalid repositories response.");
            repositories.AddRange(result.Select(ToRepository));
            if (result.Count < 100) return repositories;
        }
    }

    private async Task<string> AccessTokenAsync(GitHubConnection connection, CancellationToken cancellationToken) =>
        connection.AuthenticationType == GitHubAuthenticationTypes.Token
            ? UnprotectToken(connection)
            : await CreateInstallationTokenAsync(connection.InstallationId, cancellationToken);

    private string UnprotectToken(GitHubConnection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.ProtectedToken))
            throw new InvalidOperationException("This GitHub token connection has no stored credential. Remove it and connect again.");
        try { return _tokenProtector.Unprotect(connection.ProtectedToken); }
        catch { throw new InvalidOperationException("The stored GitHub token cannot be decrypted. Remove the connection and connect again."); }
    }

    private static GitHubRepository ToRepository(RepositoryResponse value) => new()
    {
        Id = value.Id,
        Name = value.Name,
        FullName = value.FullName,
        HtmlUrl = value.HtmlUrl,
        CloneUrl = value.CloneUrl,
        DefaultBranch = value.DefaultBranch,
        IsPrivate = value.Private
    };

    private string CreateAppJwt()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iat = now - 60, exp = now + 540, iss = _appId }));
        var unsignedToken = $"{header}.{payload}";
        using var rsa = RSA.Create();
        rsa.ImportFromPem(_privateKey);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(unsignedToken), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{unsignedToken}.{Base64Url(signature)}";
    }

    private static HttpRequestMessage ApiRequest(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, new Uri(new Uri("https://api.github.com/"), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd("dan-loop-server/1.0");
        return request;
    }

    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string path, string token, object? content, CancellationToken cancellationToken)
    {
        using var request = ApiRequest(method, path, token);
        if (content is not null) request.Content = JsonContent.Create(content);
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, body);
        return JsonDocument.Parse(body);
    }

    private static void EnsureSuccess(HttpResponseMessage response, string body)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 800)]}");
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured) throw new InvalidOperationException(ConfigurationMessage);
    }

    private static string Value(IConfiguration configuration, string key, string environmentKey)
    {
        var configured = configuration[key];
        return !string.IsNullOrWhiteSpace(configured) ? configured : configuration[environmentKey] ?? "";
    }

    private static string LoadPrivateKey(IConfiguration configuration)
    {
        var inline = Value(configuration, "GitHub:PrivateKey", "GITHUB_APP_PRIVATE_KEY");
        if (!string.IsNullOrWhiteSpace(inline)) return inline.Replace("\\n", "\n");
        var path = Value(configuration, "GitHub:PrivateKeyPath", "GITHUB_APP_PRIVATE_KEY_PATH");
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? File.ReadAllText(path) : "";
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static JsonSerializerOptions JsonOptions() => new() { PropertyNameCaseInsensitive = true };

    private sealed record OAuthTokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? ErrorDescription);
    private sealed record InstallationTokenResponse([property: JsonPropertyName("token")] string Token);
    private sealed record UserInstallationsResponse([property: JsonPropertyName("installations")] List<InstallationResponse> Installations);
    private sealed record InstallationResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("account")] GitHubAccountResponse? Account,
        [property: JsonPropertyName("html_url")] string HtmlUrl);
    private sealed record GitHubAccountResponse(
        [property: JsonPropertyName("login")] string Login,
        [property: JsonPropertyName("type")] string Type);
    private sealed record GitHubUserResponse(
        [property: JsonPropertyName("login")] string Login,
        [property: JsonPropertyName("type")] string Type);
    private sealed record RepositoriesResponse([property: JsonPropertyName("repositories")] List<RepositoryResponse> Repositories);
    private sealed record RepositoryResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("full_name")] string FullName,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("clone_url")] string CloneUrl,
        [property: JsonPropertyName("default_branch")] string DefaultBranch,
        [property: JsonPropertyName("private")] bool Private);
}
