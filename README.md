# dan-loop-server

Local, Docker-first ASP.NET Core control plane for projects, prompts, recurring loops, jobs, remote models and user permissions.

## Run on Docker Desktop for Mac

```bash
docker compose up --build
```

Open <http://localhost:8080>. On first visit, create the required administrator account, then optionally create a project and configure connections. Both optional steps can be skipped and completed later. Application state, encryption keys and encrypted remote-model credentials persist in the `dan-loop-data` Docker volume.

When no managed database is configured, the server automatically creates a local SQLite database at `data/dan-loop.db` (or `/data/dan-loop.db` in Docker). Existing `state.json` data is imported automatically when the database is first created.

To use managed PostgreSQL or MySQL, attach the database to the app and set one of these application-platform variables. The provider is detected from the URL or provider-specific variable name:

- `DATABASE_URL` (`postgres://`, `postgresql://`, or `mysql://`)
- `POSTGRES_URL`, `POSTGRESQL_URL`, or `POSTGRES_CONNECTION_STRING`
- `MYSQL_URL`, `MYSQL_DATABASE_URL`, or `MYSQL_CONNECTION_STRING`
- `ConnectionStrings__PostgreSQL` or `ConnectionStrings__MySql`
- `ConnectionStrings__DefaultConnection` (a PostgreSQL or MySQL connection string)
- `DATABASE_CONNECTION_STRING` or `DB_CONNECTION_STRING`

For an ambiguous semicolon-style `DefaultConnection`, set `DB_PROVIDER=postgresql` or `DB_PROVIDER=mysql`. If no managed database variable exists, the app uses local SQLite automatically. If a managed database is configured but unavailable or invalid, startup fails instead of silently switching to a different empty SQLite database.

### Cloud object storage

Project files can be persisted in Amazon S3, DigitalOcean Spaces, Cloudflare R2, Backblaze B2, Wasabi, Google Cloud Storage, Azure Blob Storage, Oracle Object Storage, IBM Cloud Object Storage, Storj, IDrive e2, Vultr Object Storage, Linode/Akamai Object Storage, Scaleway Object Storage, OVHcloud Object Storage, Hetzner Object Storage, Tigris Data, Bunny Storage, or any custom S3-compatible service such as MinIO. Add and test connections from the dashboard's **Connections** tab. Credentials are encrypted in the application database and are not read from deployment configuration. Several connections can be saved, with one active storage destination at a time.

S3-compatible providers share the AWS Signature V4 adapter and receive provider-aware endpoint defaults. Cloudflare R2, Oracle Object Storage, and IDrive e2 require the account-specific endpoint shown by their console. Google Cloud Storage uses the official XML interoperability endpoint and requires an HMAC access ID and secret—not a JSON service-account key. Azure uses the native Blob SDK with the storage account name and account key. Bunny uses its native Storage API with the storage-zone name and storage-zone API password. A custom endpoint can override any inferred endpoint.

The existing local project directory remains a working cache, so previews, editing, AI file tools, downloads, and Git-provider publishing continue to work normally. Remote files are restored when a connection is activated and local changes are synchronized in the background every 15 seconds. The same manual `sync_project_storage` agent tool works with every provider. Each project receives an isolated, provider-safe namespace derived from its project workspace name; spaces and unsupported characters become dashes, non-Latin-only names receive a deterministic English `project-…` name, and name collisions retain the workspace suffix. Internal history, trash, staging, download-cache, and `.git` folders are intentionally excluded from synchronization.

To stop the server:

```bash
docker compose down
```

To also permanently erase local server data:

```bash
docker compose down -v
```

## Sign-in security

The sign-in and first-run administrator forms use ASP.NET Core anti-forgery tokens. Login requests are limited to 12 per minute per source IP, repeated failures receive a progressive delay, and an account is temporarily locked for 15 minutes after 8 failed attempts. Errors remain deliberately generic so the form does not reveal whether an account exists. Administrators can clear a temporary lock from **Users**.

Cloudflare Turnstile is optional and appears adaptively after 3 suspicious failures. Configure both values to enable it:

- `Security__Turnstile__SiteKey`
- `Security__Turnstile__SecretKey`

Authentication cookies are HTTP-only and SameSite, are secure-only outside development, and expire after 12 hours. Passwords use salted PBKDF2-SHA256 with 210,000 iterations and fixed-time verification. Production responses enable HSTS, HTTPS redirection, clickjacking protection, MIME-sniffing protection, a same-origin referrer policy, and a restrictive browser permissions policy. Failed and successful sign-ins are recorded in server logs without logging passwords or submitted credentials.

For a public deployment, keep the application controls enabled and also configure request limiting at the hosting platform or reverse proxy. Edge limiting rejects abusive traffic before it consumes application resources; the in-application limiter protects requests that reach the server.

## Remote models

The server loads its provider/model defaults from `Data/ai-connections.json` and copies the catalog into SQLite or the configured managed database at startup. Add or update catalog entries in that JSON file when providers change; saved user connections and capability choices remain separate.

Each AI connection can be assigned Planner, Coder, Orchestrator, Tool calling, Generator, Image, Audio, Video, and Files capabilities. Jobs select enabled connections by capability and priority. A failed connection is recorded and the job is re-queued with the next eligible connection. When the user has not selected any capabilities, the catalog defaults (or safe model-name inference for custom models) are used.

The connection runtime supports OpenAI-compatible chat/tool APIs, native Anthropic Messages tool use, and native asset generation through Replicate, fal.ai, Stability AI, and ElevenLabs. Hosted services such as RunPod, Modal, Vast.ai, Lambda, CoreWeave, GMI Cloud, Lepton AI, Paperspace, Baseten, Databricks, DigitalOcean Gradient, Vertex AI, Azure Foundry, OCI, and watsonx use the deployment's user-supplied compatible endpoint. The `generate_asset` project tool selects enabled Image, Audio, Video, or Files connections by priority, falls back after provider errors, saves the result within the project workspace, and queues the generated file for normal background storage and Git synchronization.

The current execution client uses the OpenAI-compatible `POST /chat/completions` API. For a model running directly on the Mac, use a base URL reachable from Docker, for example:

- Ollama OpenAI compatibility: `http://host.docker.internal:11434/v1`
- LM Studio: `http://host.docker.internal:1234/v1`
- OpenAI: `https://api.openai.com/v1`

Configure a model in the **Models** tab, then select it as the project default. A recurring loop only queues when its project has a default model.

## Git provider connections

Projects can be linked to any number of repositories across GitHub, GitLab, Bitbucket Cloud, Codeberg, Gitea Cloud or self-hosted Gitea, SourceHut, AWS CodeCommit, Azure DevOps Repos, Google Cloud Secure Source Manager, and Gitee. Connections can be added during first-run setup or managed later in **Connections**.

GitHub, GitLab, Bitbucket, Codeberg/Gitea, SourceHut, Azure DevOps, and Gitee can discover repositories through their account APIs. Enter the provider token plus any requested username, workspace, or organization. Self-hosted GitLab and Gitea instances accept a custom server URL. A direct HTTPS clone URL can connect one repository on any provider and is required for AWS CodeCommit and Google Secure Source Manager. For CodeCommit use the IAM-generated HTTPS Git username/password; for Secure Source Manager use the repository HTTPS URL with an OAuth credential accepted by its Git endpoint.

The credential needs repository read/write access. It is encrypted with ASP.NET Core Data Protection before storage and is never displayed again. Imports, result commits, explicitly selected file publishing, and automatic AI-created-file synchronization all use the selected provider connection. Existing GitHub connections and project mappings are migrated in place.

For GitHub, the default discovery method uses a fine-grained personal access token and requires no GitHub App registration, callback URL, or server environment variables. Create a token at <https://github.com/settings/personal-access-tokens/new>, limit it to the required repositories, and grant only the capabilities the server needs. Typical permissions are **Metadata: read** and **Contents: read/write**, plus any Actions, Workflows, Pull requests, Checks, or Commit statuses permissions used outside the core file flow.

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

Connected repositories can be mapped to several projects. A fresh repository snapshot can then be imported into a project's workspace for reading and editing. Projects can also designate one mapped repository as their result destination; completed job result folders are committed under that project's isolated repository folder. Without a result repository, generated files remain local.

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
