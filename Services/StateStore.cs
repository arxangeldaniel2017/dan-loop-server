using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using DanLoopServer.Models;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace DanLoopServer.Services;

public sealed class StateStore(IConfiguration configuration, ILogger<StateStore> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _legacyStatePath = configuration["Storage:StatePath"] ?? "/data/state.json";
    private readonly ManagedDatabase? _managedDatabase = ResolveManagedDatabase(configuration);
    private readonly string _sqlitePath = configuration["Storage:SqlitePath"] ?? "/data/dan-loop.db";
    private AppState _state = new();

    public bool IsSetup => _state.Users.Any(x => x.Role == Roles.Admin);

    public async Task InitializeAsync()
    {
        if (_managedDatabase is null)
        {
            var fullPath = Path.GetFullPath(_sqlitePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            logger.LogInformation("No managed database configured; using local SQLite at {Path}", fullPath);
        }
        else
        {
            logger.LogInformation("Using configured managed {Provider} database", _managedDatabase.Provider);
        }

        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync();
            await using (var create = connection.CreateCommand())
            {
                create.CommandText = _managedDatabase?.Provider == DatabaseProvider.MySql
                    ? "CREATE TABLE IF NOT EXISTS app_state (id INT PRIMARY KEY, state_json LONGTEXT NOT NULL)"
                    : "CREATE TABLE IF NOT EXISTS app_state (id INTEGER PRIMARY KEY, state_json TEXT NOT NULL)";
                await create.ExecuteNonQueryAsync();
            }

            await using var select = connection.CreateCommand();
            select.CommandText = "SELECT state_json FROM app_state WHERE id = 1";
            var json = await select.ExecuteScalarAsync() as string;
            if (!string.IsNullOrWhiteSpace(json))
            {
                _state = JsonSerializer.Deserialize<AppState>(json, JsonOptions()) ?? new();
                if (_state.Version < 2)
                {
                    if (_state.AgentRuntime.Settings.MaxOutputTokens <= 2048) _state.AgentRuntime.Settings.MaxOutputTokens = 8192;
                    _state.Version = 2;
                }
                if (_state.Version < 3)
                {
                    _state.AgentRuntime.Settings.AllowGitHubPublish = true;
                    _state.Version = 3;
                }
                if (_state.Version < 4)
                {
                    if (_state.AgentRuntime.Settings.MaxSteps == 12) _state.AgentRuntime.Settings.MaxSteps = 20;
                    if (_state.AgentRuntime.Settings.MaxOutputTokens == 8192) _state.AgentRuntime.Settings.MaxOutputTokens = 16_384;
                    if (_state.AgentRuntime.Settings.MaxToolOutputCharacters == 20_000) _state.AgentRuntime.Settings.MaxToolOutputCharacters = 50_000;
                    foreach (var job in _state.Jobs.Where(job => job.Status == JobStatuses.Running ||
                                 job.Status == JobStatuses.Failed && job.Error == "The operation was canceled." &&
                                 job.CompletedAt is DateTimeOffset completed && DateTimeOffset.UtcNow - completed < TimeSpan.FromMinutes(5)))
                    {
                        job.Status = JobStatuses.Queued;
                        job.StartedAt = null;
                        job.CompletedAt = null;
                        job.Error = "";
                    }
                    _state.Version = 4;
                }
                if (_state.Version < 5)
                {
                    _state.ExecutionPlans ??= [];
                    _state.Version = 5;
                }
                if (_state.Version < 6)
                {
                    foreach (var job in _state.Jobs) job.AiResponses ??= [];
                    _state.Version = 6;
                }
                if (_state.Version < 7)
                {
                    foreach (var project in _state.Projects)
                    {
                        project.RepositoryFolder = ProjectRepositoryFolder(project);
                        project.UserManagedPaths ??= [];
                        project.AutoPublishPendingPaths ??= [];
                    }
                    _state.Version = 7;
                }
                if (_state.Version < 8)
                {
                    foreach (var project in _state.Projects)
                    {
                        var userPaths = project.UserManagedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var pending = project.AutoPublishPendingPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
                        foreach (var path in _state.Jobs
                                     .Where(job => job.ProjectId == project.Id && string.IsNullOrWhiteSpace(job.ProjectCommitUrl))
                                     .SelectMany(job => job.ProjectFilesChanged)
                                     .Where(path => !string.IsNullOrWhiteSpace(path) && !userPaths.Contains(path)))
                            pending.Add(path);
                        project.AutoPublishPendingPaths = pending.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
                        if (project.AutoPublishPendingPaths.Count > 0) project.AutoPublishQueuedAt ??= DateTimeOffset.UtcNow;
                    }
                    _state.Version = 8;
                }
                if (_state.Version < 9)
                {
                    foreach (var job in _state.Jobs)
                        job.PublishedProjectFiles = string.IsNullOrWhiteSpace(job.ProjectCommitUrl)
                            ? []
                            : job.ProjectFilesChanged.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    _state.Version = 9;
                }
                if (_state.Version < 10)
                {
                    _state.AiProviderCatalog ??= [];
                    foreach (var model in _state.Models)
                    {
                        model.Capabilities ??= [];
                        model.SuggestedCapabilities ??= [];
                        if (string.IsNullOrWhiteSpace(model.ProviderId)) model.ProviderId = "custom";
                    }
                    foreach (var job in _state.Jobs) job.FailedModelIds ??= [];
                    _state.Version = 10;
                }
                if (_state.Version < 11)
                {
                    var now = DateTimeOffset.UtcNow;
                    var stoppedLoopIds = _state.Loops.Where(loop => !loop.IsEnabled).Select(loop => loop.Id).ToHashSet();
                    foreach (var job in _state.Jobs.Where(job => job.LoopId is Guid loopId && stoppedLoopIds.Contains(loopId) && job.Status is JobStatuses.Queued or JobStatuses.Running))
                    {
                        job.Status = JobStatuses.Cancelled;
                        job.Error = "Cancelled because the recurring loop was stopped.";
                        job.CompletedAt = now;
                    }
                    foreach (var plan in _state.ExecutionPlans)
                    {
                        foreach (var step in plan.Steps.Where(step => step.Status == PlanStepStatuses.Running))
                        {
                            var activeAttempt = step.Attempts.LastOrDefault(attempt => attempt.Status == PlanStepStatuses.Running);
                            var activeJob = activeAttempt is null ? null : _state.Jobs.FirstOrDefault(job => job.Id == activeAttempt.JobId);
                            if (activeJob?.Status == JobStatuses.Running) continue;
                            if (activeAttempt is not null)
                            {
                                activeAttempt.Status = PlanStepStatuses.Cancelled;
                                activeAttempt.Summary = "Execution was no longer active and was closed during state recovery.";
                                activeAttempt.CompletedAt ??= now;
                            }
                            step.Status = PlanStepStatuses.Paused;
                            plan.UpdatedAt = now;
                        }
                    }
                    _state.Version = 11;
                }
                await SaveUnsafeAsync();
                return;
            }

            if (File.Exists(_legacyStatePath))
            {
                await using var stream = File.OpenRead(_legacyStatePath);
                _state = await JsonSerializer.DeserializeAsync<AppState>(stream, JsonOptions()) ?? new();
                await SaveUnsafeAsync();
                logger.LogInformation("Imported legacy state from {Path} into the database", _legacyStatePath);
            }
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Could not initialize application state storage");
            throw;
        }
    }

    public async Task<T> ReadAsync<T>(Func<AppState, T> reader)
    {
        await _gate.WaitAsync();
        try { return reader(Clone(_state)); }
        finally { _gate.Release(); }
    }

    public async Task<T> UpdateAsync<T>(Func<AppState, T> update)
    {
        await _gate.WaitAsync();
        try
        {
            var result = update(_state);
            await SaveUnsafeAsync();
            return result;
        }
        finally { _gate.Release(); }
    }

    private async Task SaveUnsafeAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = _managedDatabase?.Provider == DatabaseProvider.MySql
            ? "INSERT INTO app_state (id, state_json) VALUES (1, @state) " +
              "ON DUPLICATE KEY UPDATE state_json = @state"
            : "INSERT INTO app_state (id, state_json) VALUES (1, @state) " +
              "ON CONFLICT (id) DO UPDATE SET state_json = EXCLUDED.state_json";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@state";
        parameter.Value = JsonSerializer.Serialize(_state, JsonOptions());
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync();
    }

    public static string ProjectRepositoryFolder(Project project)
    {
        if (!string.IsNullOrWhiteSpace(project.RepositoryFolder)) return project.RepositoryFolder;
        var slug = Regex.Replace(project.Name.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (string.IsNullOrWhiteSpace(slug)) slug = "project";
        return $"projects/{slug}-{project.Id.ToString("N")[..8]}";
    }

    private DbConnection CreateConnection()
    {
        if (_managedDatabase is not null)
        {
            return _managedDatabase.Provider switch
            {
                DatabaseProvider.PostgreSql => new NpgsqlConnection(
                    NormalizePostgresConnectionString(_managedDatabase.ConnectionString)),
                DatabaseProvider.MySql => new MySqlConnection(
                    NormalizeMySqlConnectionString(_managedDatabase.ConnectionString)),
                _ => throw new InvalidOperationException("Unsupported managed database provider.")
            };
        }

        return new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(_sqlitePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
    }

    private static ManagedDatabase? ResolveManagedDatabase(IConfiguration configuration)
    {
        var providerHint = FirstConfigured(
            configuration["Database:Provider"],
            configuration["DATABASE_PROVIDER"],
            configuration["DB_PROVIDER"]);
        var generic = FirstConfigured(
            configuration.GetConnectionString("DefaultConnection"),
            configuration["DATABASE_URL"],
            configuration["DATABASE_CONNECTION_STRING"],
            configuration["DB_CONNECTION_STRING"]);
        var postgres = FirstConfigured(
            configuration.GetConnectionString("PostgreSQL"),
            configuration.GetConnectionString("Postgres"),
            configuration["POSTGRES_URL"],
            configuration["POSTGRESQL_URL"],
            configuration["POSTGRES_CONNECTION_STRING"],
            configuration["POSTGRESQL_CONNECTION_STRING"],
            configuration["PG_CONNECTION_STRING"],
            configuration["PG_SERVICE_URI"]);
        var mysql = FirstConfigured(
            configuration.GetConnectionString("MySql"),
            configuration.GetConnectionString("MySQL"),
            configuration["MYSQL_URL"],
            configuration["MYSQL_DATABASE_URL"],
            configuration["MYSQL_CONNECTION_STRING"],
            configuration["MYSQL_SERVICE_URI"]);

        if (!string.IsNullOrWhiteSpace(providerHint))
        {
            var hintedProvider = ParseProvider(providerHint) ??
                throw new InvalidOperationException(
                    "Database provider must be PostgreSQL, MySQL, or SQLite.");
            if (hintedProvider == DatabaseProvider.Sqlite) return null;

            var hintedConnection = hintedProvider == DatabaseProvider.MySql ? mysql ?? generic : postgres ?? generic;
            if (hintedConnection is null)
                throw new InvalidOperationException(
                    $"Database provider {hintedProvider} is configured, but no connection string was provided.");
            return new ManagedDatabase(hintedProvider, hintedConnection);
        }

        if (generic is not null && DetectProvider(generic) is { } detected)
            return new ManagedDatabase(detected, generic);
        if (postgres is not null) return new ManagedDatabase(DatabaseProvider.PostgreSql, postgres);
        if (mysql is not null) return new ManagedDatabase(DatabaseProvider.MySql, mysql);

        // Preserve compatibility with existing app-platform connection strings that
        // were previously interpreted as PostgreSQL when they had no URL scheme.
        return generic is null ? null : new ManagedDatabase(DatabaseProvider.PostgreSql, generic);
    }

    private static string? FirstConfigured(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static DatabaseProvider? ParseProvider(string value) => value.Trim().ToLowerInvariant() switch
    {
        "postgres" or "postgresql" or "pgsql" => DatabaseProvider.PostgreSql,
        "mysql" or "mariadb" => DatabaseProvider.MySql,
        "sqlite" or "sqlite3" => DatabaseProvider.Sqlite,
        _ => null
    };

    private static DatabaseProvider? DetectProvider(string connectionString)
    {
        if (Uri.TryCreate(connectionString, UriKind.Absolute, out var uri))
        {
            var fromScheme = ParseProvider(uri.Scheme);
            if (fromScheme is not null) return fromScheme;
        }

        if (Regex.IsMatch(connectionString, @"(?:^|;)\s*Port\s*=\s*3306\s*(?:;|$)", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(connectionString, @"(?:^|;)\s*Provider\s*=\s*(?:MySql|MariaDb)\s*(?:;|$)", RegexOptions.IgnoreCase))
            return DatabaseProvider.MySql;
        if (Regex.IsMatch(connectionString, @"(?:^|;)\s*Port\s*=\s*5432\s*(?:;|$)", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(connectionString, @"(?:^|;)\s*Host\s*=", RegexOptions.IgnoreCase))
            return DatabaseProvider.PostgreSql;
        return null;
    }

    private static string NormalizePostgresConnectionString(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("postgres" or "postgresql"))
            return value;

        var credentials = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(credentials[0]),
            Password = credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : "",
            SslMode = SslMode.Require
        };

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0].Equals("sslmode", StringComparison.OrdinalIgnoreCase) &&
                Enum.TryParse<SslMode>(Uri.UnescapeDataString(parts[1]), true, out var sslMode))
                builder.SslMode = sslMode;
        }

        return builder.ConnectionString;
    }

    private static string NormalizeMySqlConnectionString(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("mysql" or "mariadb"))
            return value;

        var credentials = uri.UserInfo.Split(':', 2);
        var builder = new MySqlConnectionStringBuilder
        {
            Server = uri.Host,
            Port = (uint)(uri.IsDefaultPort ? 3306 : uri.Port),
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            UserID = credentials.Length > 0 ? Uri.UnescapeDataString(credentials[0]) : "",
            Password = credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : "",
            SslMode = MySqlSslMode.Required
        };

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2 ||
                parts[0] is not ("sslmode" or "ssl-mode" or "ssl_mode")) continue;

            builder.SslMode = Uri.UnescapeDataString(parts[1]).ToLowerInvariant() switch
            {
                "none" or "disabled" or "disable" => MySqlSslMode.None,
                "preferred" or "prefer" => MySqlSslMode.Preferred,
                "verifyca" or "verify-ca" or "verify_ca" => MySqlSslMode.VerifyCA,
                "verifyfull" or "verify-full" or "verify_identity" => MySqlSslMode.VerifyFull,
                _ => MySqlSslMode.Required
            };
        }

        return builder.ConnectionString;
    }

    private static AppState Clone(AppState value) =>
        JsonSerializer.Deserialize<AppState>(JsonSerializer.Serialize(value, JsonOptions()), JsonOptions())!;

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed record ManagedDatabase(DatabaseProvider Provider, string ConnectionString);

    private enum DatabaseProvider
    {
        Sqlite,
        PostgreSql,
        MySql
    }
}
