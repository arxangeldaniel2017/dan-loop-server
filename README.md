# dan-loop-server

Local, Docker-first ASP.NET Core control plane for projects, prompts, recurring loops, jobs, remote models and user permissions.

## Run on Docker Desktop for Mac

```bash
docker compose up --build
```

Open <http://localhost:8080>. On first visit, create the administrator account, then optionally configure a model and create a project in the setup wizard. Application state, encryption keys and encrypted remote-model credentials persist in the `dan-loop-data` Docker volume.

When no managed database is configured, the server automatically creates a local SQLite database at `data/dan-loop.db` (or `/data/dan-loop.db` in Docker). Existing `state.json` data is imported automatically when the database is first created.

To use managed PostgreSQL or MySQL, attach the database to the app and set one of these application-platform variables. The provider is detected from the URL or provider-specific variable name:

- `DATABASE_URL` (`postgres://`, `postgresql://`, or `mysql://`)
- `POSTGRES_URL`, `POSTGRESQL_URL`, or `POSTGRES_CONNECTION_STRING`
- `MYSQL_URL`, `MYSQL_DATABASE_URL`, or `MYSQL_CONNECTION_STRING`
- `ConnectionStrings__PostgreSQL` or `ConnectionStrings__MySql`
- `ConnectionStrings__DefaultConnection` (a PostgreSQL or MySQL connection string)
- `DATABASE_CONNECTION_STRING` or `DB_CONNECTION_STRING`

For an ambiguous semicolon-style `DefaultConnection`, set `DB_PROVIDER=postgresql` or `DB_PROVIDER=mysql`. If no managed database variable exists, the app uses local SQLite automatically. If a managed database is configured but unavailable or invalid, startup fails instead of silently switching to a different empty SQLite database.

### S3-compatible project file storage

Project files can be persisted in Amazon S3, DigitalOcean Spaces, MinIO, or another S3-compatible service. The existing local project directory remains a working cache, so previews, editing, AI file tools, downloads, and GitHub publishing continue to work normally. Remote files are restored on startup and local changes are synchronized in the background every 15 seconds. Each project receives an isolated, bucket-safe namespace derived from its project workspace name; spaces and unsupported characters become dashes, non-Latin-only names receive a deterministic English `project-…` name, and name collisions retain the workspace suffix.

Set a bucket plus the relevant connection values:

- `S3_BUCKET`, `S3_BUCKET_NAME`, or `SPACES_BUCKET` (required to enable object storage)
- `S3_ENDPOINT` or `SPACES_ENDPOINT` for Spaces/MinIO; omit it for Amazon S3
- `S3_ACCESS_KEY` and `S3_SECRET_KEY`, or `SPACES_KEY` and `SPACES_SECRET`
- `S3_REGION`, `AWS_REGION`, or `SPACES_REGION` (the Spaces region is inferred from its endpoint when possible)
- `S3_PREFIX` (optional, defaults to `dan-loop/projects`)
- `S3_FORCE_PATH_STYLE=true` when required by a custom endpoint such as some MinIO installations

Standard AWS variables (`AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_SESSION_TOKEN`, `AWS_REGION`, and `AWS_ENDPOINT_URL_S3`) are also recognized. If `S3_BUCKET`/`SPACES_BUCKET` is absent, files remain local and no object-storage connection is attempted. Internal history, trash, staging, download-cache, and `.git` folders are intentionally excluded from synchronization.

To stop the server:

```bash
docker compose down
```

To also permanently erase local server data:

```bash
docker compose down -v
```

## Remote models

The server loads its provider/model defaults from `Data/ai-connections.json` and copies the catalog into SQLite or the configured managed database at startup. Add or update catalog entries in that JSON file when providers change; saved user connections and capability choices remain separate.

Each AI connection can be assigned Planner, Coder, Orchestrator, Tool calling, Generator, Image, Audio, Video, and Files capabilities. Jobs select enabled connections by capability and priority. A failed connection is recorded and the job is re-queued with the next eligible connection. When the user has not selected any capabilities, the catalog defaults (or safe model-name inference for custom models) are used.

The current execution client uses the OpenAI-compatible `POST /chat/completions` API. For a model running directly on the Mac, use a base URL reachable from Docker, for example:

- Ollama OpenAI compatibility: `http://host.docker.internal:11434/v1`
- LM Studio: `http://host.docker.internal:1234/v1`
- OpenAI: `https://api.openai.com/v1`

Configure a model in the **Models** tab, then select it as the project default. A recurring loop only queues when its project has a default model.

## GitHub connections

Projects can be linked to any number of repositories from one or more GitHub accounts. Connections can be added during first-run setup or managed later in the **GitHub** tab.

The default connection method uses a fine-grained personal access token and requires no GitHub App registration, callback URL, or server environment variables. Create a token at <https://github.com/settings/personal-access-tokens/new>, limit it to the required repositories, and grant only the capabilities the server needs. Typical permissions are **Metadata: read**, **Contents: read/write**, **Actions: read/write**, **Workflows: read/write**, **Pull requests: read/write**, **Checks: read/write**, and **Commit statuses: read/write**. Paste the token into the setup wizard or GitHub management tab. The token is encrypted with ASP.NET Core Data Protection before it is stored and is never displayed again.

### Optional GitHub App connection

For multi-user production deployments, a GitHub App can be configured as an additional connection method. The server uses GitHub user authorization to verify installations and generates short-lived installation tokens for repository operations.

Create a GitHub App with these URLs, replacing the host with the server's public URL:

- Callback URL: `https://your-host/github/oauthcallback`
- Setup URL: `https://your-host/github/callback`
- Enable **Redirect on update** for repository-access changes.

Grant the GitHub App the repository permissions needed by the server: Metadata read, Contents read/write, Workflows read/write, Actions read/write, Pull requests read/write, Checks read/write, and Commit statuses read/write. Then configure these application-platform variables:

- `GITHUB_APP_ID`
- `GITHUB_APP_SLUG`
- `GITHUB_CLIENT_ID`
- `GITHUB_CLIENT_SECRET`
- `GITHUB_APP_PRIVATE_KEY` (PEM text; escaped `\\n` line breaks are accepted), or `GITHUB_APP_PRIVATE_KEY_PATH`

Connected repositories can be mapped to several projects. A fresh repository snapshot can then be imported into a project's workspace for reading and editing. Projects can also designate one mapped repository as their result destination; completed job result folders are committed under `dan-loop-results/`. Without a result repository, generated files remain local.

## Project files, conversations, and results

Every project receives a numbered folder under `Storage:ProjectsPath` (`data/projects` locally and `/data/projects` in Docker). The file manager supports file and folder uploads, image/text previews, formatted JSON/XML/text editing, recoverable deletes, and ZIP review. ZIP files are always retained unchanged; extraction requires confirmation and uses the ZIP name as its folder name. Conflicts require an explicit keep-both, override, or cancel decision.

Agent runs can attach project files. Text is added to model context and supported images are sent as image inputs to compatible OpenAI-style chat endpoints. Conversation messages and their related files are retained for later runs. Each job writes `response.md` and any model-generated `file:` or `base64-file:` blocks into a new numbered `results/result-N` folder. The Jobs view supports undoing one generated item or the whole result set.

Uploads are limited to 250 MB per request. ZIP extraction is limited to 5,000 entries and 500 MB expanded data, with path-containment validation.

## Local .NET development

Requires .NET 10 SDK:

```bash
dotnet run
```

Local state is written to `data/`, which is ignored by Git.
