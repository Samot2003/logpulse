# Docker

Back to the [README](../README.md).

`docker compose up --build` (after `cp .env.example .env`) starts the whole stack. In about a minute, open
<http://localhost:5090> and log in as `viewer` with the `VIEWER_PASSWORD` from `.env`. The `docker-demo` server
appears with its metrics, and its logs arrive live from a small generator that writes a line every two seconds.

## Services

| Service | What it is | Address |
|---|---|---|
| `sql` | SQL Server 2022, data in the `sql-data` volume | internal only |
| `api` | The API | <http://localhost:5080/health> |
| `dashboard` | The dashboard | <http://localhost:5090> |
| `agent` | A Linux agent monitoring its own container and following `/var/log/demo/app.log` | — |
| `demo-logs` | Writes realistic log lines for the agent | — |

## How it is put together
- **Images:** multi-stage Dockerfiles (`src/*/Dockerfile`): restore from the lock files, publish in Release, then copy into the slim runtime image. They run as the images' non-root user.
- **Containers:** read-only file systems, no Linux capabilities, `no-new-privileges`.
- **Startup order:** health checks (SQL Server with `sqlcmd`, the API and the dashboard on `/health`) start each service only once the one it needs is ready.
- **Secrets:** they come from `.env`, which git ignores.
- **Exposure:** ports are published on `127.0.0.1` only, so the stack is not reachable from other machines. Inside the compose network the dashboard and the agent talk to the API over plain HTTP, which they only accept because `AllowInsecureHttp` is set explicitly. The dashboard has a fixed address, the only one the API trusts to forward users' IPs.
- **Volumes:** the dashboard's Data Protection keys (which encrypt its cookies) live in the `dashboard-keys` volume; they are stored unencrypted, readable only through that volume. The agent's saved log positions live in `agent-state`.

`docker compose down` stops everything; `docker compose down -v` also deletes the data. CI builds the images and runs this same stack on every push, checking that the demo agent's data reaches the API.
