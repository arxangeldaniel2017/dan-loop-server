using System.Security.Claims;
using System.Text.Json;
using DanLoopServer.Models;
using DanLoopServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace DanLoopServer.Controllers;

[Authorize(Roles = Roles.Admin)]
public sealed class GitHubController(
    StateStore store,
    GitHubService gitHub,
    GitProviderService gitProviders,
    WorkspaceService workspace,
    IDataProtectionProvider protectionProvider,
    ILogger<GitHubController> logger) : Controller
{
    private const string AuthCookie = "dan-loop.github-auth";
    private readonly IDataProtector _protector = protectionProvider.CreateProtector("github-install-flow-v1");

    [HttpGet]
    public IActionResult Setup() => View(gitHub);

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ConnectToken(string token, string? provider, string? baseUrl, string? username,
        string? namespaceName, string? region, string? repositoryUrl, string? returnUrl, CancellationToken cancellationToken)
    {
        var destination = Url.IsLocalUrl(returnUrl) ? returnUrl! : "/#connections";
        try
        {
            var connection = await gitProviders.ConnectAsync(provider ?? GitProviders.GitHub, token, baseUrl, username, namespaceName, region, repositoryUrl, cancellationToken);
            await store.UpdateAsync(state =>
            {
                state.GitHubConnections.Add(connection);
                return true;
            });
            return Back($"Connected {connection.Provider} account {connection.AccountLogin} with {connection.Repositories.Count} accessible repositories.", destination);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not connect {Provider}", provider);
            return Back($"Could not connect {provider ?? GitProviders.GitHub}: {ex.Message}", destination);
        }
    }

    [HttpGet]
    public IActionResult Connect(string? returnUrl = null)
    {
        if (!gitHub.IsConfigured) return Back(gitHub.ConfigurationMessage, returnUrl);
        var destination = Url.IsLocalUrl(returnUrl) ? returnUrl! : "/#connections";
        var state = Protect(new FlowState(CurrentUserId(), destination, DateTimeOffset.UtcNow.AddMinutes(15)));
        var callback = Url.ActionLink(nameof(OAuthCallback), "GitHub", protocol: Request.Scheme)!;
        return Redirect(gitHub.CreateAuthorizationUrl(callback, state));
    }

    [HttpGet]
    public async Task<IActionResult> OAuthCallback(string? code, string? state, string? error, CancellationToken cancellationToken)
    {
        var flow = Unprotect(state);
        if (flow is null) return Back("The GitHub authorization request expired or is invalid.", "/#connections");
        if (!string.IsNullOrEmpty(error) || string.IsNullOrWhiteSpace(code))
            return Back("GitHub authorization was cancelled.", flow.ReturnUrl);

        try
        {
            var callback = Url.ActionLink(nameof(OAuthCallback), "GitHub", protocol: Request.Scheme)!;
            var userToken = await gitHub.ExchangeOAuthCodeAsync(code, callback, cancellationToken);
            var cookieValue = Protect(new PendingInstallation(CurrentUserId(), userToken, flow.ReturnUrl, DateTimeOffset.UtcNow.AddMinutes(15)));
            Response.Cookies.Append(AuthCookie, cookieValue, new CookieOptions
            {
                HttpOnly = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromMinutes(15),
                IsEssential = true
            });
            var installState = Protect(new FlowState(CurrentUserId(), flow.ReturnUrl, DateTimeOffset.UtcNow.AddMinutes(15)));
            return Redirect(gitHub.CreateInstallationUrl(installState));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GitHub user authorization failed");
            return Back("GitHub authorization failed. Check the GitHub App configuration and try again.", flow.ReturnUrl);
        }
    }

    [HttpGet]
    public async Task<IActionResult> Callback(long? installation_id, string? state, CancellationToken cancellationToken)
    {
        var flow = Unprotect(state);
        var pending = Request.Cookies.TryGetValue(AuthCookie, out var cookie) ? UnprotectPending(cookie) : null;
        Response.Cookies.Delete(AuthCookie);
        if (flow is null || pending is null || flow.UserId != pending.UserId || installation_id is null)
        {
            if (installation_id is not null && await RefreshExistingInstallationAsync(installation_id.Value, cancellationToken))
                return Back("GitHub repository access updated.", "/#connections");
            return Back("The GitHub installation request expired or is invalid.", "/#connections");
        }

        try
        {
            if (!await gitHub.UserCanAccessInstallationAsync(pending.UserToken, installation_id.Value, cancellationToken))
                return Back("The GitHub installation could not be verified for your account.", pending.ReturnUrl);

            var connection = await gitHub.LoadConnectionAsync(installation_id.Value, cancellationToken);
            await store.UpdateAsync(stateValue =>
            {
                var existing = stateValue.GitHubConnections.FirstOrDefault(x => x.InstallationId == connection.InstallationId);
                if (existing is null)
                {
                    stateValue.GitHubConnections.Add(connection);
                }
                else
                {
                    existing.AccountLogin = connection.AccountLogin;
                    existing.AccountType = connection.AccountType;
                    existing.SettingsUrl = connection.SettingsUrl;
                    existing.Repositories = connection.Repositories;
                    existing.RefreshedAt = connection.RefreshedAt;
                    RemoveUnavailableMappings(stateValue, existing);
                }
                return true;
            });
            TempData["Message"] = $"Connected GitHub account {connection.AccountLogin} with {connection.Repositories.Count} repositories.";
            return LocalRedirect(pending.ReturnUrl);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not complete GitHub App installation {InstallationId}", installation_id);
            return Back("GitHub was connected, but its repositories could not be loaded. Check the app permissions and try again.", pending.ReturnUrl);
        }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Refresh(Guid id, CancellationToken cancellationToken)
    {
        var current = await store.ReadAsync(state => state.GitHubConnections.FirstOrDefault(x => x.Id == id));
        if (current is null) return NotFound();
        try
        {
            var refreshed = await gitProviders.RefreshAsync(current, cancellationToken);
            await store.UpdateAsync(state =>
            {
                var connection = state.GitHubConnections.First(x => x.Id == id);
                connection.AuthenticationType = refreshed.AuthenticationType;
                connection.Provider = refreshed.Provider;
                connection.BaseUrl = refreshed.BaseUrl;
                connection.Namespace = refreshed.Namespace;
                connection.Region = refreshed.Region;
                connection.Username = refreshed.Username;
                connection.ProtectedToken = refreshed.ProtectedToken;
                connection.InstallationId = refreshed.InstallationId;
                connection.AccountLogin = refreshed.AccountLogin;
                connection.AccountType = refreshed.AccountType;
                connection.SettingsUrl = refreshed.SettingsUrl;
                connection.Repositories = refreshed.Repositories;
                connection.RefreshedAt = refreshed.RefreshedAt;
                RemoveUnavailableMappings(state, connection);
                return true;
            });
            return Back($"{current.Provider} repositories refreshed.", "/#connections");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not refresh GitHub connection {ConnectionId}", id);
            return Back($"Could not refresh the {current.Provider} connection: {ex.Message}", "/#connections");
        }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetProjectRepositories(Guid projectId, Guid connectionId, long[]? repositoryIds)
    {
        await store.UpdateAsync(state =>
        {
            var connection = state.GitHubConnections.FirstOrDefault(x => x.Id == connectionId);
            if (connection is null || state.Projects.All(x => x.Id != projectId)) return false;
            var allowed = connection.Repositories.Select(x => x.Id).ToHashSet();
            state.ProjectRepositories.RemoveAll(x => x.ProjectId == projectId && x.GitHubConnectionId == connectionId);
            foreach (var repositoryId in (repositoryIds ?? []).Distinct().Where(allowed.Contains))
                state.ProjectRepositories.Add(new ProjectRepository { ProjectId = projectId, GitHubConnectionId = connectionId, RepositoryId = repositoryId });
            CleanupResultRepositories(state);
            return true;
        });
        return Back("Project repositories updated.", "/#connections");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetResultsRepository(Guid projectId, long? repositoryId)
    {
        await store.UpdateAsync(state =>
        {
            var project = state.Projects.FirstOrDefault(x => x.Id == projectId);
            if (project is null) return false;
            var allowed = repositoryId is null || state.ProjectRepositories.Any(x => x.ProjectId == projectId && x.RepositoryId == repositoryId);
            if (allowed) project.ResultsRepositoryId = repositoryId;
            return allowed;
        });
        return Back(repositoryId is null ? "Results will remain local." : "Results repository updated.", "/#connections");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportRepository(Guid projectId, long repositoryId, CancellationToken cancellationToken)
    {
        var target = await store.ReadAsync(state => state.GitHubConnections
            .SelectMany(connection => connection.Repositories.Select(repository => new { Connection = connection, Repository = repository }))
            .FirstOrDefault(x => x.Repository.Id == repositoryId && state.ProjectRepositories.Any(link => link.ProjectId == projectId && link.GitHubConnectionId == x.Connection.Id && link.RepositoryId == repositoryId)));
        if (target is null) return NotFound();
        try
        {
            var archive = await gitProviders.DownloadRepositoryArchiveAsync(target.Connection, target.Repository, cancellationToken);
            var folder = await workspace.ImportRepositoryAsync(projectId, target.Repository.FullName, archive);
            return Back($"Imported {target.Repository.FullName} into {folder}.", $"/Files?projectId={projectId}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not import Git repository {RepositoryId}", repositoryId);
            return Back("Could not import the Git repository: " + ex.Message, "/#connections");
        }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        await store.UpdateAsync(state =>
        {
            var removedRepositoryIds = state.GitHubConnections.Where(x => x.Id == id).SelectMany(x => x.Repositories).Select(x => x.Id).ToHashSet();
            state.GitHubConnections.RemoveAll(x => x.Id == id);
            state.ProjectRepositories.RemoveAll(x => x.GitHubConnectionId == id);
            foreach (var project in state.Projects.Where(x => x.ResultsRepositoryId is not null && removedRepositoryIds.Contains(x.ResultsRepositoryId.Value))) project.ResultsRepositoryId = null;
            return true;
        });
        return Back("Git provider connection and its stored credential were removed from this server.", "/#connections");
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private string Protect<T>(T value) => _protector.Protect(JsonSerializer.Serialize(value));

    private FlowState? Unprotect(string? value)
    {
        try
        {
            var result = JsonSerializer.Deserialize<FlowState>(_protector.Unprotect(value ?? ""));
            return result is not null && result.UserId == CurrentUserId() && result.ExpiresAt > DateTimeOffset.UtcNow ? result : null;
        }
        catch { return null; }
    }

    private PendingInstallation? UnprotectPending(string value)
    {
        try
        {
            var result = JsonSerializer.Deserialize<PendingInstallation>(_protector.Unprotect(value));
            return result is not null && result.UserId == CurrentUserId() && result.ExpiresAt > DateTimeOffset.UtcNow ? result : null;
        }
        catch { return null; }
    }

    private static void RemoveUnavailableMappings(AppState state, GitHubConnection connection)
    {
        var available = connection.Repositories.Select(x => x.Id).ToHashSet();
        state.ProjectRepositories.RemoveAll(x => x.GitHubConnectionId == connection.Id && !available.Contains(x.RepositoryId));
        CleanupResultRepositories(state);
    }

    private static void CleanupResultRepositories(AppState state)
    {
        foreach (var project in state.Projects.Where(x => x.ResultsRepositoryId is not null && !state.ProjectRepositories.Any(link => link.ProjectId == x.Id && link.RepositoryId == x.ResultsRepositoryId)))
            project.ResultsRepositoryId = null;
    }

    private async Task<bool> RefreshExistingInstallationAsync(long installationId, CancellationToken cancellationToken)
    {
        if (!await store.ReadAsync(state => state.GitHubConnections.Any(x => x.InstallationId == installationId))) return false;
        try
        {
            var refreshed = await gitHub.LoadConnectionAsync(installationId, cancellationToken);
            await store.UpdateAsync(state =>
            {
                var connection = state.GitHubConnections.First(x => x.InstallationId == installationId);
                connection.AccountLogin = refreshed.AccountLogin;
                connection.AccountType = refreshed.AccountType;
                connection.SettingsUrl = refreshed.SettingsUrl;
                connection.Repositories = refreshed.Repositories;
                connection.RefreshedAt = refreshed.RefreshedAt;
                RemoveUnavailableMappings(state, connection);
                return true;
            });
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not refresh updated GitHub installation {InstallationId}", installationId);
            return false;
        }
    }

    private IActionResult Back(string message, string? returnUrl)
    {
        TempData["Message"] = message;
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/#connections");
    }

    private sealed record FlowState(Guid UserId, string ReturnUrl, DateTimeOffset ExpiresAt);
    private sealed record PendingInstallation(Guid UserId, string UserToken, string ReturnUrl, DateTimeOffset ExpiresAt);
}
