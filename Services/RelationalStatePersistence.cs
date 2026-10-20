using System.Data.Common;
using System.Text.Json;
using DanLoopServer.Models;
using MySqlConnector;

namespace DanLoopServer.Services;

internal sealed class RelationalStatePersistence(bool useLongText, JsonSerializerOptions jsonOptions)
{
    private readonly string _jsonType = useLongText ? "LONGTEXT" : "TEXT";

    public async Task EnsureSchemaAsync(DbConnection connection)
    {
        var json = _jsonType;
        var statements = new[]
        {
            $"CREATE TABLE IF NOT EXISTS app_metadata (key_name VARCHAR(100) PRIMARY KEY, value_text {json} NOT NULL)",
            $"CREATE TABLE IF NOT EXISTS app_users (id VARCHAR(36) PRIMARY KEY, username VARCHAR(255) NOT NULL, role VARCHAR(50) NOT NULL, is_enabled INTEGER NOT NULL, created_at VARCHAR(64) NOT NULL, payload_json {json} NOT NULL)",
            $"CREATE TABLE IF NOT EXISTS projects (id VARCHAR(36) PRIMARY KEY, name VARCHAR(500) NOT NULL, created_at VARCHAR(64) NOT NULL, payload_json {json} NOT NULL)",
            $"CREATE TABLE IF NOT EXISTS loops (id VARCHAR(36) PRIMARY KEY, project_id VARCHAR(36) NOT NULL, name VARCHAR(500) NOT NULL, is_enabled INTEGER NOT NULL, payload_json {json} NOT NULL)",
            $"CREATE TABLE IF NOT EXISTS jobs (id VARCHAR(36) PRIMARY KEY, project_id VARCHAR(36) NOT NULL, loop_id VARCHAR(36), status VARCHAR(50) NOT NULL, created_at VARCHAR(64) NOT NULL, payload_json {json} NOT NULL)",
            $"CREATE TABLE IF NOT EXISTS job_responses (id VARCHAR(36) PRIMARY KEY, job_id VARCHAR(36) NOT NULL, step_number INTEGER NOT NULL, kind VARCHAR(100) NOT NULL, created_at VARCHAR(64) NOT NULL, payload_json {json} NOT NULL, FOREIGN KEY(job_id) REFERENCES jobs(id) ON DELETE CASCADE)",
            $"CREATE TABLE IF NOT EXISTS remote_models (id VARCHAR(36) PRIMARY KEY, name VARCHAR(500) NOT NULL, provider_id VARCHAR(200) NOT NULL, model_id VARCHAR(500) NOT NULL, is_enabled INTEGER NOT NULL, priority INTEGER NOT NULL, payload_json {json} NOT NULL)",
            $"CREATE TABLE IF NOT EXISTS ai_provider_catalog (id VARCHAR(300) PRIMARY KEY, name VARCHAR(500) NOT NULL, category VARCHAR(200) NOT NULL, payload_json {json} NOT NULL)",
            $"CREATE TABLE IF NOT EXISTS project_permissions (permission_key VARCHAR(80) PRIMARY KEY, user_id VARCHAR(36) NOT NULL, project_id VARCHAR(36) NOT NULL, can_view INTEGER NOT NULL, can_manage INTEGER NOT NULL, can_run INTEGER NOT NULL, payload_json {json} NOT NULL)",
            $"CREATE TABLE IF NOT EXISTS git_connections (id VARCHAR(36) PRIMARY KEY, provider VARCHAR(100) NOT NULL, account_login VARCHAR(500) NOT NULL, connected_at VARCHAR(64) NOT NULL, payload_json {json} NOT NULL)",
            $"CREATE TABLE IF NOT EXISTS git_repositories (repository_key VARCHAR(100) PRIMARY KEY, connection_id VARCHAR(36) NOT NULL, repository_id VARCHAR(40) NOT NULL, full_name VARCHAR(700) NOT NULL, is_private INTEGER NOT NULL, payload_json {json} NOT NULL, FOREIGN KEY(connection_id) REFERENCES git_connections(id) ON DELETE CASCADE)",
            $"CREATE TABLE IF NOT EXISTS object_storage_connections (id VARCHAR(36) PRIMARY KEY, name VARCHAR(500) NOT NULL, provider VARCHAR(150) NOT NULL, bucket VARCHAR(500) NOT NULL, is_enabled INTEGER NOT NULL, created_at VARCHAR(64) NOT NULL, payload_json {json} NOT NULL)",
            $"CREATE TABLE IF NOT EXISTS project_repositories (repository_key VARCHAR(120) PRIMARY KEY, project_id VARCHAR(36) NOT NULL, connection_id VARCHAR(36) NOT NULL, repository_id VARCHAR(40) NOT NULL, payload_json {json} NOT NULL)",
            $"CREATE TABLE IF NOT EXISTS conversations (id VARCHAR(36) PRIMARY KEY, project_id VARCHAR(36) NOT NULL, title VARCHAR(700) NOT NULL, created_at VARCHAR(64) NOT NULL, updated_at VARCHAR(64) NOT NULL, payload_json {json} NOT NULL)",
            $"CREATE TABLE IF NOT EXISTS conversation_messages (id VARCHAR(36) PRIMARY KEY, conversation_id VARCHAR(36) NOT NULL, job_id VARCHAR(36), role VARCHAR(50) NOT NULL, created_at VARCHAR(64) NOT NULL, payload_json {json} NOT NULL, FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE)",
            $"CREATE TABLE IF NOT EXISTS pending_uploads (id VARCHAR(36) PRIMARY KEY, project_id VARCHAR(36) NOT NULL, user_id VARCHAR(36) NOT NULL, created_at VARCHAR(64) NOT NULL, payload_json {json} NOT NULL)",
            $"CREATE TABLE IF NOT EXISTS pending_upload_items (item_key VARCHAR(100) PRIMARY KEY, upload_id VARCHAR(36) NOT NULL, item_order INTEGER NOT NULL, relative_path VARCHAR(1000) NOT NULL, size_bytes VARCHAR(30) NOT NULL, payload_json {json} NOT NULL, FOREIGN KEY(upload_id) REFERENCES pending_uploads(id) ON DELETE CASCADE)",
            $"CREATE TABLE IF NOT EXISTS app_messages (id VARCHAR(36) PRIMARY KEY, project_id VARCHAR(36), job_id VARCHAR(36), level_name VARCHAR(50) NOT NULL, created_at VARCHAR(64) NOT NULL, payload_json {json} NOT NULL)",
            $"CREATE TABLE IF NOT EXISTS execution_plans (id VARCHAR(36) PRIMARY KEY, project_id VARCHAR(36) NOT NULL, loop_id VARCHAR(36), created_at VARCHAR(64) NOT NULL, updated_at VARCHAR(64) NOT NULL, payload_json {json} NOT NULL)",
            $"CREATE TABLE IF NOT EXISTS execution_plan_steps (id VARCHAR(36) PRIMARY KEY, plan_id VARCHAR(36) NOT NULL, step_order INTEGER NOT NULL, title VARCHAR(1000) NOT NULL, status VARCHAR(50) NOT NULL, is_active INTEGER NOT NULL, payload_json {json} NOT NULL, FOREIGN KEY(plan_id) REFERENCES execution_plans(id) ON DELETE CASCADE)",
            $"CREATE TABLE IF NOT EXISTS plan_step_attempts (id VARCHAR(36) PRIMARY KEY, step_id VARCHAR(36) NOT NULL, job_id VARCHAR(36) NOT NULL, attempt_number INTEGER NOT NULL, status VARCHAR(50) NOT NULL, started_at VARCHAR(64) NOT NULL, payload_json {json} NOT NULL, FOREIGN KEY(step_id) REFERENCES execution_plan_steps(id) ON DELETE CASCADE)",
            $"CREATE TABLE IF NOT EXISTS agent_runtime_settings (id INTEGER PRIMARY KEY, payload_json {json} NOT NULL)",
            $"CREATE TABLE IF NOT EXISTS agent_tool_audit (id VARCHAR(36) PRIMARY KEY, project_id VARCHAR(36) NOT NULL, job_id VARCHAR(36) NOT NULL, step_number INTEGER NOT NULL, tool_name VARCHAR(300) NOT NULL, status VARCHAR(50) NOT NULL, started_at VARCHAR(64) NOT NULL, payload_json {json} NOT NULL)",
            "CREATE INDEX IF NOT EXISTS ix_loops_project ON loops(project_id)",
            "CREATE INDEX IF NOT EXISTS ix_jobs_project_status ON jobs(project_id, status)",
            "CREATE INDEX IF NOT EXISTS ix_jobs_loop ON jobs(loop_id)",
            "CREATE INDEX IF NOT EXISTS ix_job_responses_job ON job_responses(job_id)",
            "CREATE INDEX IF NOT EXISTS ix_permissions_project ON project_permissions(project_id)",
            "CREATE INDEX IF NOT EXISTS ix_conversations_project ON conversations(project_id)",
            "CREATE INDEX IF NOT EXISTS ix_messages_project_created ON app_messages(project_id, created_at)",
            "CREATE INDEX IF NOT EXISTS ix_plans_project ON execution_plans(project_id)",
            "CREATE INDEX IF NOT EXISTS ix_plan_steps_plan_order ON execution_plan_steps(plan_id, step_order)",
            "CREATE INDEX IF NOT EXISTS ix_audit_job ON agent_tool_audit(job_id)"
        };

        foreach (var statement in statements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = useLongText && statement.StartsWith("CREATE INDEX IF NOT EXISTS", StringComparison.Ordinal)
                ? statement.Replace("CREATE INDEX IF NOT EXISTS", "CREATE INDEX", StringComparison.Ordinal)
                : statement;
            try
            {
                await command.ExecuteNonQueryAsync();
            }
            catch (MySqlException ex) when (useLongText && ex.Number == 1061)
            {
                // MySQL has no CREATE INDEX IF NOT EXISTS; an existing index is the desired state.
            }
        }
    }

    public async Task<bool> HasStateAsync(DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value_text FROM app_metadata WHERE key_name = 'storage_format'";
        return string.Equals(await command.ExecuteScalarAsync() as string, "relational-v1", StringComparison.Ordinal);
    }

    public async Task<AppState> LoadAsync(DbConnection connection)
    {
        var state = new AppState
        {
            Users = await LoadRowsAsync<AppUser>(connection, "app_users", "created_at, id"),
            Projects = await LoadRowsAsync<Project>(connection, "projects", "created_at, id"),
            Loops = await LoadRowsAsync<LoopDefinition>(connection, "loops", "project_id, name, id"),
            Jobs = await LoadRowsAsync<Job>(connection, "jobs", "created_at, id"),
            Models = await LoadRowsAsync<RemoteModel>(connection, "remote_models", "priority, name, id"),
            AiProviderCatalog = await LoadRowsAsync<AiProviderCatalogItem>(connection, "ai_provider_catalog", "name, id"),
            Permissions = await LoadRowsAsync<ProjectPermission>(connection, "project_permissions", "project_id, user_id"),
            GitHubConnections = await LoadRowsAsync<GitHubConnection>(connection, "git_connections", "connected_at, id"),
            ObjectStorageConnections = await LoadRowsAsync<ObjectStorageConnection>(connection, "object_storage_connections", "created_at, id"),
            ProjectRepositories = await LoadRowsAsync<ProjectRepository>(connection, "project_repositories", "project_id, repository_id"),
            Conversations = await LoadRowsAsync<Conversation>(connection, "conversations", "created_at, id"),
            PendingUploads = await LoadRowsAsync<PendingUpload>(connection, "pending_uploads", "created_at, id"),
            Messages = await LoadRowsAsync<AppMessage>(connection, "app_messages", "created_at, id"),
            ExecutionPlans = await LoadRowsAsync<ProjectExecutionPlan>(connection, "execution_plans", "created_at, id")
        };

        state.Version = await LoadVersionAsync(connection);
        state.AgentRuntime.Settings = (await LoadRowsAsync<AgentRuntimeSettings>(connection, "agent_runtime_settings", "id")).SingleOrDefault() ?? new();
        state.AgentRuntime.AuditTrail = await LoadRowsAsync<AgentToolAuditEntry>(connection, "agent_tool_audit", "started_at, id");

        var responses = await LoadChildrenAsync<AgentResponseLog>(connection, "job_responses", "job_id", "step_number, created_at, id");
        foreach (var job in state.Jobs) job.AiResponses = responses.GetValueOrDefault(job.Id.ToString()) ?? [];

        var repositories = await LoadChildrenAsync<GitHubRepository>(connection, "git_repositories", "connection_id", "full_name, repository_id");
        foreach (var item in state.GitHubConnections) item.Repositories = repositories.GetValueOrDefault(item.Id.ToString()) ?? [];

        var conversationMessages = await LoadChildrenAsync<ConversationMessage>(connection, "conversation_messages", "conversation_id", "created_at, id");
        foreach (var conversation in state.Conversations) conversation.Messages = conversationMessages.GetValueOrDefault(conversation.Id.ToString()) ?? [];

        var uploadItems = await LoadChildrenAsync<PendingUploadItem>(connection, "pending_upload_items", "upload_id", "item_order");
        foreach (var upload in state.PendingUploads) upload.Items = uploadItems.GetValueOrDefault(upload.Id.ToString()) ?? [];

        var steps = await LoadChildrenAsync<ProjectPlanStep>(connection, "execution_plan_steps", "plan_id", "step_order, id");
        var attempts = await LoadChildrenAsync<PlanStepAttempt>(connection, "plan_step_attempts", "step_id", "attempt_number, started_at, id");
        foreach (var plan in state.ExecutionPlans)
        {
            plan.Steps = steps.GetValueOrDefault(plan.Id.ToString()) ?? [];
            foreach (var step in plan.Steps) step.Attempts = attempts.GetValueOrDefault(step.Id.ToString()) ?? [];
        }

        return state;
    }

    public async Task SaveAsync(DbConnection connection, AppState state)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            foreach (var table in DeleteOrder)
            {
                await using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = $"DELETE FROM {table}";
                await delete.ExecuteNonQueryAsync();
            }

            await InsertRowsAsync(connection, transaction, "app_users", ["id", "username", "role", "is_enabled", "created_at"], state.Users,
                x => [x.Id, x.Username, x.Role, x.IsEnabled, x.CreatedAt]);
            await InsertRowsAsync(connection, transaction, "projects", ["id", "name", "created_at"], state.Projects,
                x => [x.Id, x.Name, x.CreatedAt]);
            await InsertRowsAsync(connection, transaction, "loops", ["id", "project_id", "name", "is_enabled"], state.Loops,
                x => [x.Id, x.ProjectId, x.Name, x.IsEnabled]);

            var jobs = state.Jobs.Select(job => Without(job, clone => clone.AiResponses = [])).ToList();
            await InsertRowsAsync(connection, transaction, "jobs", ["id", "project_id", "loop_id", "status", "created_at"], jobs,
                x => [x.Id, x.ProjectId, x.LoopId, x.Status, x.CreatedAt]);
            await InsertRowsAsync(connection, transaction, "job_responses", ["id", "job_id", "step_number", "kind", "created_at"],
                state.Jobs.SelectMany(job => job.AiResponses.Select(response => new ChildRow<AgentResponseLog>(job.Id, response))),
                x => [x.Value.Id, x.ParentId, x.Value.Step, x.Value.Kind, x.Value.CreatedAt], x => x.Value);

            await InsertRowsAsync(connection, transaction, "remote_models", ["id", "name", "provider_id", "model_id", "is_enabled", "priority"], state.Models,
                x => [x.Id, x.Name, x.ProviderId, x.ModelId, x.IsEnabled, x.Priority]);
            await InsertRowsAsync(connection, transaction, "ai_provider_catalog", ["id", "name", "category"], state.AiProviderCatalog,
                x => [x.Id, x.Name, x.Category]);
            await InsertRowsAsync(connection, transaction, "project_permissions", ["permission_key", "user_id", "project_id", "can_view", "can_manage", "can_run"], state.Permissions,
                x => [$"{x.UserId}:{x.ProjectId}", x.UserId, x.ProjectId, x.CanView, x.CanManage, x.CanRun]);

            var connections = state.GitHubConnections.Select(item => Without(item, clone => clone.Repositories = [])).ToList();
            await InsertRowsAsync(connection, transaction, "git_connections", ["id", "provider", "account_login", "connected_at"], connections,
                x => [x.Id, x.Provider, x.AccountLogin, x.ConnectedAt]);
            await InsertRowsAsync(connection, transaction, "git_repositories", ["repository_key", "connection_id", "repository_id", "full_name", "is_private"],
                state.GitHubConnections.SelectMany(item => item.Repositories.Select(repository => new ChildRow<GitHubRepository>(item.Id, repository))),
                x => [$"{x.ParentId}:{x.Value.Id}", x.ParentId, x.Value.Id, x.Value.FullName, x.Value.IsPrivate], x => x.Value);

            await InsertRowsAsync(connection, transaction, "object_storage_connections", ["id", "name", "provider", "bucket", "is_enabled", "created_at"], state.ObjectStorageConnections,
                x => [x.Id, x.Name, x.Provider, x.Bucket, x.IsEnabled, x.CreatedAt]);
            await InsertRowsAsync(connection, transaction, "project_repositories", ["repository_key", "project_id", "connection_id", "repository_id"], state.ProjectRepositories,
                x => [$"{x.ProjectId}:{x.GitHubConnectionId}:{x.RepositoryId}", x.ProjectId, x.GitHubConnectionId, x.RepositoryId]);

            var conversations = state.Conversations.Select(item => Without(item, clone => clone.Messages = [])).ToList();
            await InsertRowsAsync(connection, transaction, "conversations", ["id", "project_id", "title", "created_at", "updated_at"], conversations,
                x => [x.Id, x.ProjectId, x.Title, x.CreatedAt, x.UpdatedAt]);
            await InsertRowsAsync(connection, transaction, "conversation_messages", ["id", "conversation_id", "job_id", "role", "created_at"],
                state.Conversations.SelectMany(item => item.Messages.Select(message => new ChildRow<ConversationMessage>(item.Id, message))),
                x => [x.Value.Id, x.ParentId, x.Value.JobId, x.Value.Role, x.Value.CreatedAt], x => x.Value);

            var uploads = state.PendingUploads.Select(item => Without(item, clone => clone.Items = [])).ToList();
            await InsertRowsAsync(connection, transaction, "pending_uploads", ["id", "project_id", "user_id", "created_at"], uploads,
                x => [x.Id, x.ProjectId, x.UserId, x.CreatedAt]);
            var pendingItems = state.PendingUploads.SelectMany(upload => upload.Items.Select((item, index) => new OrderedChildRow<PendingUploadItem>(upload.Id, index, item)));
            await InsertRowsAsync(connection, transaction, "pending_upload_items", ["item_key", "upload_id", "item_order", "relative_path", "size_bytes"], pendingItems,
                x => [$"{x.ParentId}:{x.Order}", x.ParentId, x.Order, x.Value.RelativePath, x.Value.Size.ToString()], x => x.Value);

            await InsertRowsAsync(connection, transaction, "app_messages", ["id", "project_id", "job_id", "level_name", "created_at"], state.Messages,
                x => [x.Id, x.ProjectId, x.JobId, x.Level, x.CreatedAt]);

            var plans = state.ExecutionPlans.Select(item => Without(item, clone => clone.Steps = [])).ToList();
            await InsertRowsAsync(connection, transaction, "execution_plans", ["id", "project_id", "loop_id", "created_at", "updated_at"], plans,
                x => [x.Id, x.ProjectId, x.LoopId, x.CreatedAt, x.UpdatedAt]);
            var planSteps = state.ExecutionPlans.SelectMany(plan => plan.Steps.Select(step => new ChildRow<ProjectPlanStep>(plan.Id, Without(step, clone => clone.Attempts = [])))).ToList();
            await InsertRowsAsync(connection, transaction, "execution_plan_steps", ["id", "plan_id", "step_order", "title", "status", "is_active"], planSteps,
                x => [x.Value.Id, x.ParentId, x.Value.Order, x.Value.Title, x.Value.Status, x.Value.IsActive], x => x.Value);
            await InsertRowsAsync(connection, transaction, "plan_step_attempts", ["id", "step_id", "job_id", "attempt_number", "status", "started_at"],
                state.ExecutionPlans.SelectMany(plan => plan.Steps).SelectMany(step => step.Attempts.Select(attempt => new ChildRow<PlanStepAttempt>(step.Id, attempt))),
                x => [x.Value.Id, x.ParentId, x.Value.JobId, x.Value.Number, x.Value.Status, x.Value.StartedAt], x => x.Value);

            await InsertRowsAsync(connection, transaction, "agent_runtime_settings", ["id"], [state.AgentRuntime.Settings], _ => [1]);
            await InsertRowsAsync(connection, transaction, "agent_tool_audit", ["id", "project_id", "job_id", "step_number", "tool_name", "status", "started_at"], state.AgentRuntime.AuditTrail,
                x => [x.Id, x.ProjectId, x.JobId, x.Step, x.Tool, x.Status, x.StartedAt]);

            await InsertMetadataAsync(connection, transaction, "state_version", state.Version.ToString());
            await InsertMetadataAsync(connection, transaction, "storage_format", "relational-v1");
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private async Task<int> LoadVersionAsync(DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value_text FROM app_metadata WHERE key_name = 'state_version'";
        return int.TryParse(await command.ExecuteScalarAsync() as string, out var version) ? version : 1;
    }

    private async Task<List<T>> LoadRowsAsync<T>(DbConnection connection, string table, string orderBy)
    {
        var result = new List<T>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT payload_json FROM {table} ORDER BY {orderBy}";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var value = JsonSerializer.Deserialize<T>(reader.GetString(0), jsonOptions);
            if (value is not null) result.Add(value);
        }
        return result;
    }

    private async Task<Dictionary<string, List<T>>> LoadChildrenAsync<T>(DbConnection connection, string table, string parentColumn, string orderBy)
    {
        var result = new Dictionary<string, List<T>>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {parentColumn}, payload_json FROM {table} ORDER BY {parentColumn}, {orderBy}";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var parent = reader.GetString(0);
            var value = JsonSerializer.Deserialize<T>(reader.GetString(1), jsonOptions);
            if (value is null) continue;
            if (!result.TryGetValue(parent, out var items)) result[parent] = items = [];
            items.Add(value);
        }
        return result;
    }

    private async Task InsertRowsAsync<T>(DbConnection connection, DbTransaction transaction, string table, string[] columns,
        IEnumerable<T> rows, Func<T, object?[]> values, Func<T, object>? payload = null)
    {
        var allColumns = columns.Append("payload_json").ToArray();
        var parameterNames = allColumns.Select((_, index) => $"@p{index}").ToArray();
        foreach (var row in rows)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"INSERT INTO {table} ({string.Join(", ", allColumns)}) VALUES ({string.Join(", ", parameterNames)})";
            var rowValues = values(row);
            for (var index = 0; index < rowValues.Length; index++) AddParameter(command, parameterNames[index], rowValues[index]);
            AddParameter(command, parameterNames[^1], JsonSerializer.Serialize(payload?.Invoke(row) ?? row, jsonOptions));
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task InsertMetadataAsync(DbConnection connection, DbTransaction transaction, string key, string value)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO app_metadata (key_name, value_text) VALUES (@key, @value)";
        AddParameter(command, "@key", key);
        AddParameter(command, "@value", value);
        await command.ExecuteNonQueryAsync();
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value switch
        {
            null => DBNull.Value,
            Guid guid => guid.ToString(),
            DateTimeOffset date => date.ToString("O"),
            bool boolean => boolean ? 1 : 0,
            _ => value
        };
        command.Parameters.Add(parameter);
    }

    private T Without<T>(T value, Action<T> removeChildren)
    {
        var clone = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, jsonOptions), jsonOptions)!;
        removeChildren(clone);
        return clone;
    }

    private static readonly string[] DeleteOrder =
    [
        "job_responses", "git_repositories", "conversation_messages", "pending_upload_items", "plan_step_attempts",
        "execution_plan_steps", "agent_tool_audit", "project_repositories", "project_permissions", "jobs", "loops",
        "conversations", "pending_uploads", "execution_plans", "remote_models", "ai_provider_catalog",
        "git_connections", "object_storage_connections", "projects", "app_users", "agent_runtime_settings", "app_messages",
        "app_metadata"
    ];

    private sealed record ChildRow<T>(Guid ParentId, T Value);
    private sealed record OrderedChildRow<T>(Guid ParentId, int Order, T Value);
}
