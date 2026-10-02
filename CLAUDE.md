# LogPulse

Mini ServerHealth open source en C#/.NET 8: un agente recoge métricas y logs, una API ASP.NET Core los almacena en SQL Server y un dashboard los muestra en tiempo real. Es un proyecto de portfolio; no incluir código ni datos de Win Systems.

## Idioma
Responder siempre en español. README, commits y comentarios de código en inglés.

## Estructura
- `src/LogPulse.Core`: modelos y contratos compartidos.
- `src/LogPulse.Data`: interfaces + DAOs con Dapper sobre SQL Server (sin EF Core, sin lógica de negocio).
- `src/LogPulse.Api`: Web API, JWT con caducidad + refresh token, Swagger, `/health`.
- `src/LogPulse.Agent`: Worker Service (`BackgroundService`), envía en MessagePack.
- `src/LogPulse.Dashboard`: Blazor + SignalR.
- `tests/LogPulse.Tests`: xUnit (+ Testcontainers para SQL Server).

## Comandos (los mismos que usa el CI)
- Compilar: `dotnet build LogPulse.sln --nologo`
- Tests unitarios: `dotnet test LogPulse.sln --nologo --filter "Category!=Integration"`
- Tests de integración (necesitan Docker): `dotnet test LogPulse.sln --nologo --filter "Category=Integration"`
- Formato: `dotnet format LogPulse.sln --verify-no-changes`
- Entorno completo: `docker compose up --build`

## Convenciones
- Acceso a datos solo a través de interfaces en `Data`; la API depende de las interfaces, no de Dapper.
- Nullable y warnings-as-errors activados.
- Secretos fuera del repo (user-secrets / variables de entorno / GitHub Secrets).
- Hooks de `.claude/settings.json`: formatean los `.cs` editados y ejecutan los tests antes de terminar una tarea.
- Pedir confirmación antes de `git push`, `docker push` o cualquier despliegue.

Plan completo: `C:\Users\samot\.claude\plans\je-vais-pas-mettre-snazzy-sifakis.md`.
