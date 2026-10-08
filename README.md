# LogPulse

[![CI](https://github.com/Samot2003/logpulse/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Samot2003/logpulse/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Self-hosted server monitoring in C#/.NET 8: a lightweight agent collects metrics and logs, an ASP.NET Core API stores them in SQL Server, and a real-time Blazor dashboard shows them.

> Work in progress. See [docs/ROADMAP.md](docs/ROADMAP.md) for what is done and what comes next.

## Build and test

Requires the .NET 8 SDK. Integration tests also need Docker (they start SQL Server 2022 with Testcontainers).

```bash
dotnet restore LogPulse.sln --locked-mode
dotnet build LogPulse.sln
dotnet test LogPulse.sln --filter "Category!=Integration"   # unit tests
dotnet test LogPulse.sln --filter "Category=Integration"    # integration tests (Docker)
```

## Quick start with Docker

Requires Docker. From the repository root:

```bash
cp .env.example .env          # then change every value in .env
docker compose up --build
```

In about a minute the whole stack is up: open <http://localhost:5090> and log in as `viewer` with the `VIEWER_PASSWORD` from `.env`. The `docker-demo` server appears with its metrics, and its logs arrive live from a small generator that writes a line every two seconds.

| Service | What it is | Address |
|---|---|---|
| `sql` | SQL Server 2022, data in the `sql-data` volume | internal only |
| `api` | The API | <http://localhost:5080/health> |
| `dashboard` | The dashboard | <http://localhost:5090> |
| `agent` | A Linux agent monitoring its own container and following `/var/log/demo/app.log` | — |
| `demo-logs` | Writes realistic log lines for the agent | — |

How it is put together:
- **Images:** multi-stage Dockerfiles (`src/*/Dockerfile`): restore from the lock files, publish in Release, then copy into the slim runtime image. They run as the images' non-root user.
- **Containers:** read-only file systems, no Linux capabilities, `no-new-privileges`.
- **Startup order:** health checks (SQL Server with `sqlcmd`, the API and the dashboard on `/health`) start each service only once the one it needs is ready.
- **Secrets:** they come from `.env`, which git ignores.
- **Exposure:** ports are published on `127.0.0.1` only, so the stack is not reachable from other machines. Inside the compose network the dashboard and the agent talk to the API over plain HTTP, which they only accept because `AllowInsecureHttp` is set explicitly. The dashboard has a fixed address, the only one the API trusts to forward users' IPs.
- **Volumes:** the dashboard's Data Protection keys (which encrypt its cookies) live in the `dashboard-keys` volume; they are stored unencrypted, readable only through that volume. The agent's saved log positions live in `agent-state`.

`docker compose down` stops everything; `docker compose down -v` also deletes the data. CI builds the images and runs this same stack on every push, checking that the demo agent's data reaches the API.

## Run the API locally

Start SQL Server in Docker, then the API (the database and schema are created on first start):

```bash
docker run -d --name logpulse-sql -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=LogPulse_Dev_Passw0rd!" -p 127.0.0.1:1433:1433 mcr.microsoft.com/mssql/server:2022-latest
dotnet run --project src/LogPulse.Api
```

Swagger UI is at <http://localhost:5080/swagger>. The development settings (`appsettings.Development.json`) seed these **development-only** credentials (the API refuses to start outside Development with the development signing key; seeding only creates missing users and agents, it never changes existing ones):

| Who | Credentials | Can |
|---|---|---|
| Admin | `admin` / `dev-admin-password` | read data, create agent keys |
| Viewer | `viewer` / `dev-viewer-password` | read data |
| Agent | server `demo-server`, key `lp_dev_demo_agent_key_not_secret` | ingest data for `demo-server` |

### API overview

| Endpoint | Auth | Purpose |
|---|---|---|
| `POST /api/auth/token` | anonymous | User login → access token (JWT, 15 min) + refresh token (7 days; sessions end 30 days after login) |
| `POST /api/auth/agent-token` | anonymous | Agent login with server name + API key |
| `POST /api/auth/refresh` | anonymous | Rotate a refresh token (single use; reuse revokes the session) |
| `POST /api/auth/revoke` | anonymous | Log out |
| `POST /api/ingest/logs`, `/metrics` | Agent | Batches of up to 1000 items, JSON or MessagePack (`application/x-msgpack`) |
| `GET /api/servers` | Viewer, Admin | Registered servers |
| `GET /api/logs` | Viewer, Admin | Filter by server, minimum severity, time range and text; paged |
| `GET /api/metrics/{serverId}`, `/latest` | Viewer, Admin | Metric history (max 24 h per request) and latest sample per server |
| `POST /api/agents` | Admin | Create or rotate an agent API key (shown once); rotating ends the agent's existing sessions |
| `DELETE /api/agents/{serverName}` | Admin | Revoke an agent key and end its sessions |
| `/hubs/live` (SignalR) | Viewer, Admin | Pushes new logs and metrics to the dashboard as soon as they are stored |
| `GET /health` | anonymous | Liveness including the database |

Rate limits (per minute, `RateLimiting` section): 10 auth requests per client IP, 600 ingestion requests per agent and 600 queries per user; over the limit the API answers 429 with `Retry-After`. The client IP is taken from `X-Forwarded-For` only when the request comes from a trusted proxy (loopback, or the addresses in `ForwardedHeaders:KnownProxies`), which is how the dashboard forwards its users' addresses.

Sample payloads are in [samples/](samples/) (JSON and MessagePack).

## Run the agent

The agent (`src/LogPulse.Agent`) is a .NET worker service that runs on the monitored server. It samples CPU, memory and disk usage, follows log files like `tail -F`, and sends both to the API in MessagePack batches.

With the API running locally, start it with the development settings (it reports as `demo-server` and follows `src/LogPulse.Agent/dev-logs/demo.log`):

```bash
mkdir -p src/LogPulse.Agent/dev-logs
dotnet run --project src/LogPulse.Agent
echo "2026-10-06 12:00:00 ERROR payment timeout" >> src/LogPulse.Agent/dev-logs/demo.log
```

The line shows up in `GET /api/logs` as an `Error`, and `GET /api/metrics/latest` shows the server's latest sample.

How it works:

- **Metrics**: CPU and memory from `/proc/stat` and `/proc/meminfo` on Linux and from the Win32 API (`GetSystemTimes`, `GlobalMemoryStatusEx`) on Windows; disk usage with `DriveInfo`. One sample every 5 s while someone is watching the dashboard, every 30 s otherwise (the API says which in every ingestion response).
- **Logs**: new lines of each configured file, with the severity guessed from the level word (`ERROR`, `WRN`, `fail:`...). Rotation (truncate or replace) is detected with a hash of the file's first bytes. The position confirmed by the API is saved, so a restart resumes where it stopped (at-least-once delivery).
- **Sending**: bounded in-memory buffers, batches split by item count and size, retries with exponential backoff, timeouts and a circuit breaker (`Microsoft.Extensions.Http.Resilience`). A batch that fails is kept and sent again first, so lines keep their order.
- **Auth**: the agent logs in with its API key and renews its token with the refresh token before it expires. Refreshes are never retried automatically (a refresh token works once); if one is refused, the agent logs in again.

### Configuration

Settings live in the `Agent` section of `appsettings.json`, `appsettings.{Environment}.json` (Production when nothing is set) and environment variables (`Agent__ApiKey`, `Agent__LogFiles__0__Path`...). The API key is a secret: never commit it, and keep it where only the agent can read it. In development use `dotnet user-secrets` (loaded only in Development); on a server, a settings file readable only by the service account (see below). Outside Development the agent refuses the development key and plain HTTP to a remote API.

| Setting | Default | Meaning |
|---|---|---|
| `ApiBaseUrl` | — | Address of the API |
| `ServerName`, `ApiKey` | — | Credentials created with `POST /api/agents` |
| `LogFiles` | `[]` | Files to follow: `{ "Path": "...", "Source": "..." }` |
| `MetricsInterval` / `IdleMetricsInterval` | `00:00:05` / `00:00:30` | Sampling with and without dashboard viewers |
| `SendInterval` | `00:00:05` | How often buffered data is sent |
| `LogPollInterval` | `00:00:01` | How often the log files are checked for new lines |
| `MaxBatchItems` / `MaxBatchBytes` | `500` / `524288` | Limits of one request (lower `MaxBatchBytes` if a proxy answers 413) |
| `BufferCapacity` | `10000` | Items kept in memory per buffer while the API is unreachable |
| `DiskPath` | system drive | Drive or mount point reported as disk usage |
| `ReadExistingLogs` | `false` | Also send what a file already contains the first time it is seen |
| `StateDirectory` | `state` | Where the saved log positions are kept |

### Install as a Windows service

Publish without elevation (`dotnet publish src/LogPulse.Agent -p:PublishProfile=win-x64` writes a self-contained `LogPulse.Agent.exe` to `artifacts/agent-win-x64`), then run the block below in an elevated Windows PowerShell, from the repository folder, after setting the three values at its top.

- The agent runs under its own low-privilege virtual account (`NT SERVICE\LogPulseAgent`), not LocalSystem.
- Every folder it uses is writable only by administrators (and, for its state, by the service).
- The block runs as a single unit and stops at the first failing step, even when pasted line by line. The service is set to start automatically only at the very end.
- Groups are given as well-known SIDs (`*S-1-5-32-544` Administrators, `*S-1-5-18` SYSTEM), so the block works on any Windows display language.

```powershell
& {
$ErrorActionPreference = "Stop"
function Assert-Ok { if ($LASTEXITCODE -ne 0) { throw "The previous command failed with exit code $LASTEXITCODE." } }
$apiBaseUrl = "https://logpulse.example.com/"
$serverName = "web-01"
$logFolder  = "D:\AppLogs"   # must be writable only by administrators and the application (see below)

# 1. Only the executable and appsettings.json, copied where only administrators can write.
$app = "C:\Program Files\LogPulse Agent"
New-Item -ItemType Directory $app | Out-Null
Copy-Item artifacts\agent-win-x64\LogPulse.Agent.exe, artifacts\agent-win-x64\appsettings.json $app

# 2. The service, under its own virtual account; manual start until everything else is in place.
New-Service -Name LogPulseAgent -DisplayName "LogPulse Agent" -BinaryPathName "`"$app\LogPulse.Agent.exe`"" -StartupType Manual | Out-Null
sc.exe config LogPulseAgent obj= "NT SERVICE\LogPulseAgent"; Assert-Ok
New-EventLog -LogName Application -Source LogPulse.Agent, LogPulseAgent

# 3. State folder (saved log positions): writable by the service only, and empty (nobody slipped files in).
$data = "C:\ProgramData\LogPulse Agent"
New-Item -ItemType Directory $data | Out-Null
icacls $data /setowner "*S-1-5-32-544"; Assert-Ok
icacls $data /inheritance:r /grant:r "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F" "NT SERVICE\LogPulseAgent:(OI)(CI)M"; Assert-Ok
if (Get-ChildItem -LiteralPath $data -Force) { throw "$data is not empty." }

# 4. Settings with the API key: the file is locked down first, then written. The key is typed hidden.
$settings = "$app\appsettings.Production.json"
New-Item -ItemType File $settings | Out-Null
icacls $settings /inheritance:r /grant:r "*S-1-5-32-544:F" "NT SERVICE\LogPulseAgent:R"; Assert-Ok
$key = (New-Object PSCredential "key", (Read-Host "API key returned by POST /api/agents" -AsSecureString)).GetNetworkCredential().Password
@{ Agent = @{
    ApiBaseUrl = $apiBaseUrl
    ServerName = $serverName
    ApiKey = $key
    StateDirectory = $data
    LogFiles = @(@{ Path = "$logFolder\app.log"; Source = "app" })
} } | ConvertTo-Json -Depth 4 | Set-Content $settings -Encoding utf8

# 5. Read access to the followed logs, then start (and from now on, at every boot).
icacls $logFolder /grant "NT SERVICE\LogPulseAgent:(OI)(CI)R"; Assert-Ok
Set-Service LogPulseAgent -StartupType Automatic
Start-Service LogPulseAgent
}
```

If a step fails, remove what was created before running the block again: `sc.exe delete LogPulseAgent`, then the `C:\Program Files\LogPulse Agent` and `C:\ProgramData\LogPulse Agent` folders.

The folders of the followed logs must be writable only by administrators and the application that writes them. Otherwise a local user could replace a log with a link to another file the service can read, and its content would be sent to the API.

### Install on Linux (systemd)

Publish for Linux (`dotnet publish src/LogPulse.Agent -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o artifacts/agent-linux-x64`) and copy `LogPulse.Agent` and `appsettings.json` to `/opt/logpulse-agent/`. Then, as root:

```bash
useradd --system --no-create-home --shell /usr/sbin/nologin logpulse-agent
install -d -m 750 -o root -g logpulse-agent /etc/logpulse-agent
# The API key is a secret: root writes it, only the agent's group can read it.
install -m 640 -o root -g logpulse-agent /dev/null /etc/logpulse-agent/agent.env
cat > /etc/logpulse-agent/agent.env <<'EOF'
Agent__ApiBaseUrl=https://logpulse.example.com/
Agent__ServerName=web-01
Agent__ApiKey=the key returned by POST /api/agents
Agent__LogFiles__0__Path=/var/log/myapp/app.log
Agent__LogFiles__0__Source=myapp
EOF

cat > /etc/systemd/system/logpulse-agent.service <<'EOF'
[Unit]
Description=LogPulse agent
After=network-online.target
Wants=network-online.target

[Service]
User=logpulse-agent
EnvironmentFile=/etc/logpulse-agent/agent.env
Environment=DOTNET_ENVIRONMENT=Production
Environment=Agent__StateDirectory=/var/lib/logpulse-agent
StateDirectory=logpulse-agent
WorkingDirectory=/opt/logpulse-agent
ExecStart=/opt/logpulse-agent/LogPulse.Agent
Restart=on-failure
# Read-only view of the system; it can only write its own state folder.
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
NoNewPrivileges=true

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now logpulse-agent
```

The `logpulse-agent` user needs read access to the followed log files (for example through their group). As on Windows, their folders must be writable only by root and the application that writes them.

Known limits: every physical line is one log entry (a multi-line stack trace becomes several), and the timestamp is the time the line was read (lines read late, for example after an outage, get that later time).

## Run the dashboard

The dashboard (`src/LogPulse.Dashboard`) is a Blazor Server app. With the API (and ideally the agent) running:

```bash
dotnet run --project src/LogPulse.Dashboard
```

Open <http://localhost:5090> and log in as `viewer` / `dev-viewer-password`. Pages:

- **Servers**: one card per server with online/offline status and its latest CPU, memory and disk usage.
- **Server detail**: CPU, memory and disk charts for the last 15 minutes to 24 hours, plus its recent log entries.
- **Logs**: filters (server, minimum severity, text, time range) kept in the address, so a filtered view is a link you can share; paging, and "Follow live" to reload the newest entries as they arrive.

Everything updates live: the dashboard holds one SignalR connection to the API per open tab, and while any is open the API tells agents to sample every 5 s instead of every 30 s (they notice on their next request, so within about 30 s).

How sign-in works: the dashboard is a backend-for-frontend. The browser only gets an encrypted, `HttpOnly`, `SameSite=Strict` cookie with the id of a server-side session; the JWT and the refresh token stay on the dashboard server, which renews them (refresh tokens work once, so one place owns the rotation). Logging out revokes the refresh token in the API. Sessions are kept in memory: restarting the dashboard logs everyone out, and running several instances would need a shared cache.

Settings (`Dashboard` section): `ApiBaseUrl` (required; outside Development it must be https unless the API is on the same machine), `OfflineAfter` (`00:01:30`, when a silent server is shown offline) and `LiveRefreshInterval` (`00:00:02`, minimum time between automatic reloads of a followed log list). Times are shown in UTC; a "To" date includes its whole minute.

Known limits: a tab closed without logging out keeps counting as a viewer for up to about 3 minutes (Blazor keeps a disconnected circuit that long in case the user comes back), and opening a page queries the API up to three times (to prerender it, when it becomes interactive, and once more when its live connection opens, to catch anything sent in between).

## License

[MIT](LICENSE)
