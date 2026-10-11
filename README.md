# dan-loop-server

Local, Docker-first ASP.NET Core control plane for projects, prompts, recurring loops, jobs, remote models and user permissions.

## Run on Docker Desktop for Mac

```bash
docker compose up --build
```

Open <http://localhost:8080>. On first visit, create the administrator account, then optionally configure a model and create a project in the setup wizard. Application state, encryption keys and encrypted remote-model credentials persist in the `dan-loop-data` Docker volume.

When no managed database is configured, the server automatically creates a local SQLite database at `data/dan-loop.db` (or `/data/dan-loop.db` in Docker). Existing `state.json` data is imported automatically when the database is first created.

To use managed PostgreSQL, set one of these application-platform variables:

- `DATABASE_URL` (a `postgres://` or `postgresql://` URL)
- `ConnectionStrings__DefaultConnection` (an Npgsql connection string)
- `DATABASE_CONNECTION_STRING` or `DB_CONNECTION_STRING`

To stop the server:

```bash
docker compose down
```

To also permanently erase local server data:

```bash
docker compose down -v
```

## Remote models

The server uses the OpenAI-compatible `POST /chat/completions` API. For a model running directly on the Mac, use a base URL reachable from Docker, for example:

- Ollama OpenAI compatibility: `http://host.docker.internal:11434/v1`
- LM Studio: `http://host.docker.internal:1234/v1`
- OpenAI: `https://api.openai.com/v1`

Configure a model in the **Models** tab, then select it as the project default. A recurring loop only queues when its project has a default model.

## Local .NET development

Requires .NET 10 SDK:

```bash
dotnet run
```

Local state is written to `data/`, which is ignored by Git.
