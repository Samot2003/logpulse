# API

Back to the [README](../README.md).

## Run it locally

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

## Endpoints

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

Rate limits (per minute, `RateLimiting` section): 10 auth requests per client IP, 600 ingestion requests per agent and 600 queries per user; over the limit the API answers 429 with `Retry-After`. The client IP is taken from `X-Forwarded-For` only when the request comes from a trusted proxy (loopback, or the addresses and networks in `ForwardedHeaders:KnownProxies` / `KnownNetworks`), which is how the dashboard forwards its users' addresses.

Sample payloads are in [samples/](../samples/) (JSON and MessagePack).
