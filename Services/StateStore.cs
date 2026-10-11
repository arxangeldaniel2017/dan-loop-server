using System.Text.Json;
using DanLoopServer.Models;

namespace DanLoopServer.Services;

public sealed class StateStore(IConfiguration configuration, ILogger<StateStore> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = configuration["Storage:StatePath"] ?? "/data/state.json";
    private AppState _state = new();

    public bool IsSetup => _state.Users.Any(x => x.Role == Roles.Admin);

    public async Task InitializeAsync()
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        if (!File.Exists(_path)) return;
        try
        {
            await using var stream = File.OpenRead(_path);
            _state = await JsonSerializer.DeserializeAsync<AppState>(stream, JsonOptions()) ?? new();
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Could not load state from {Path}", _path);
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
        var temp = _path + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, _state, JsonOptions());
        File.Move(temp, _path, true);
    }

    private static AppState Clone(AppState value) =>
        JsonSerializer.Deserialize<AppState>(JsonSerializer.Serialize(value, JsonOptions()), JsonOptions())!;

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
