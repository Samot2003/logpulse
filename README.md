# LogPulse

[![CI](https://github.com/Samot2003/logpulse/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Samot2003/logpulse/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Self-hosted server monitoring in C#/.NET 8: a lightweight agent collects metrics and logs, an ASP.NET Core API stores them in SQL Server, and a real-time dashboard shows them.

> Work in progress. See [docs/ROADMAP.md](docs/ROADMAP.md) for what is done and what comes next.

## Build and test

Requires the .NET 8 SDK. Integration tests also need Docker (they start SQL Server 2022 with Testcontainers).

```bash
dotnet restore LogPulse.sln --locked-mode
dotnet build LogPulse.sln
dotnet test LogPulse.sln --filter "Category!=Integration"   # unit tests
dotnet test LogPulse.sln --filter "Category=Integration"    # integration tests (Docker)
```

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
| `GET /health` | anonymous | Liveness including the database |

Sample payloads are in [samples/](samples/) (JSON and MessagePack).

## License

[MIT](LICENSE)
