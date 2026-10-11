using System.Data.Common;
using System.Text.Json;
using DanLoopServer.Models;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace DanLoopServer.Services;

public sealed class StateStore(IConfiguration configuration, ILogger<StateStore> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _legacyStatePath = configuration["Storage:StatePath"] ?? "/data/state.json";
    private readonly string? _managedConnectionString = ResolveManagedConnectionString(configuration);
    private readonly string _sqlitePath = configuration["Storage:SqlitePath"] ?? "/data/dan-loop.db";
    private AppState _state = new();

    public bool IsSetup => _state.Users.Any(x => x.Role == Roles.Admin);

    public async Task InitializeAsync()
    {
        if (_managedConnectionString is null)
        {
            var fullPath = Path.GetFullPath(_sqlitePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            logger.LogInformation("No managed database configured; using local SQLite at {Path}", fullPath);
        }
        else
        {
            logger.LogInformation("Using configured managed PostgreSQL database");
        }

        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync();
            await using (var create = connection.CreateCommand())
            {
                create.CommandText = "CREATE TABLE IF NOT EXISTS app_state (id INTEGER PRIMARY KEY, state_json TEXT NOT NULL)";
                await create.ExecuteNonQueryAsync();
            }

            await using var select = connection.CreateCommand();
            select.CommandText = "SELECT state_json FROM app_state WHERE id = 1";
            var json = await select.ExecuteScalarAsync() as string;
            if (!string.IsNullOrWhiteSpace(json))
            {
                _state = JsonSerializer.Deserialize<AppState>(json, JsonOptions()) ?? new();
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
        command.CommandText = "INSERT INTO app_state (id, state_json) VALUES (1, @state) " +
                              "ON CONFLICT (id) DO UPDATE SET state_json = EXCLUDED.state_json";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@state";
        parameter.Value = JsonSerializer.Serialize(_state, JsonOptions());
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync();
    }

    private DbConnection CreateConnection()
    {
        if (_managedConnectionString is not null)
            return new NpgsqlConnection(NormalizePostgresConnectionString(_managedConnectionString));

        return new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(_sqlitePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
    }

    private static string? ResolveManagedConnectionString(IConfiguration configuration)
    {
        var value = configuration.GetConnectionString("DefaultConnection")
                    ?? configuration["DATABASE_URL"]
                    ?? configuration["DATABASE_CONNECTION_STRING"]
                    ?? configuration["DB_CONNECTION_STRING"];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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

    private static AppState Clone(AppState value) =>
        JsonSerializer.Deserialize<AppState>(JsonSerializer.Serialize(value, JsonOptions()), JsonOptions())!;

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
