# Dashboard

Back to the [README](../README.md).

The dashboard (`src/LogPulse.Dashboard`) is a Blazor Server app. With the API (and ideally the agent) running:

```bash
dotnet run --project src/LogPulse.Dashboard
```

## Pages

Open <http://localhost:5090> and log in as `viewer` / `dev-viewer-password`. Pages:

- **Servers**: one card per server with online/offline status and its latest CPU, memory and disk usage.
- **Server detail**: CPU, memory and disk charts for the last 15 minutes to 24 hours, plus its recent log entries.
- **Logs**: filters (server, minimum severity, text, time range) kept in the address, so a filtered view is a link you can share; paging, and "Follow live" to reload the newest entries as they arrive.

Everything updates live: the dashboard holds one SignalR connection to the API per open tab, and while any is open the API tells agents to sample every 5 s instead of every 30 s (they notice on their next request, so within about 30 s).

## How sign-in works

The dashboard is a backend-for-frontend. The browser only gets an encrypted, `HttpOnly`, `SameSite=Strict` cookie with the id of a server-side session; the JWT and the refresh token stay on the dashboard server, which renews them (refresh tokens work once, so one place owns the rotation). Logging out revokes the refresh token in the API. Sessions are kept in memory: restarting the dashboard logs everyone out, and running several instances would need a shared cache.

## Settings

`Dashboard` section: `ApiBaseUrl` (required; outside Development it must be https unless the API is on the same machine), `OfflineAfter` (`00:01:30`, when a silent server is shown offline) and `LiveRefreshInterval` (`00:00:02`, minimum time between automatic reloads of a followed log list). Times are shown in UTC; a "To" date includes its whole minute.

## Known limits

A tab closed without logging out keeps counting as a viewer for up to about 3 minutes (Blazor keeps a disconnected circuit that long in case the user comes back), and opening a page queries the API up to three times (to prerender it, when it becomes interactive, and once more when its live connection opens, to catch anything sent in between).
