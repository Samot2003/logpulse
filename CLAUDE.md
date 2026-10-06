# LogPulse

Plataforma de monitorización de servidores open source en C#/.NET 8: un agente recoge métricas y logs, una API ASP.NET Core los almacena en SQL Server y un dashboard los muestra en tiempo real. Es un proyecto de portfolio propio: no incluir código, datos ni nombres internos de empleadores.

## Idioma
Responder siempre en español. README, código, comentarios y commits en inglés. Harness (`CLAUDE.md`, `.claude/agents`, `.claude/skills`) y `docs/ROADMAP.md` en español.

## Estructura
- `src/LogPulse.Core`: modelos y contratos compartidos.
- `src/LogPulse.Data`: interfaces + DAOs con Dapper sobre SQL Server (sin EF Core, sin lógica de negocio).
- `src/LogPulse.Api`: Web API, JWT con caducidad + refresh token, Swagger, `/health`.
- `src/LogPulse.Agent`: Worker Service (`BackgroundService`), envía en MessagePack.
- `src/LogPulse.Dashboard`: Blazor + SignalR.
- `tests/LogPulse.Tests`: xUnit (+ Testcontainers para SQL Server).

## Comandos (los mismos que usa el CI en `.github/workflows/ci.yml`)
- Restaurar (como el CI, falla si cambian los `packages.lock.json`): `dotnet restore LogPulse.sln --locked-mode`. Si añades o actualizas un paquete, haz `dotnet restore` normal y versiona los lock files.
- Compilar: `dotnet build LogPulse.sln --nologo`
- Tests unitarios: `dotnet test LogPulse.sln --nologo --filter "Category!=Integration"`
- Tests de integración (necesitan Docker): `dotnet test LogPulse.sln --nologo --filter "Category=Integration"`
- Formato: `dotnet format LogPulse.sln --verify-no-changes`
- Paquetes vulnerables: `dotnet list LogPulse.sln package --vulnerable --include-transitive` (no debe listar ninguno)
- API en local: SQL Server con `docker run -d --name logpulse-sql -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=LogPulse_Dev_Passw0rd!" -p 127.0.0.1:1433:1433 mcr.microsoft.com/mssql/server:2022-latest` y luego `dotnet run --project src/LogPulse.Api` (http://localhost:5080, credenciales de desarrollo en `appsettings.Development.json`)
- Agente en local (con la API levantada): `dotnet run --project src/LogPulse.Agent`. Envía como `demo-server` y sigue `src/LogPulse.Agent/dev-logs/demo.log` (ignorado por git).
- Publicar el agente para Windows: `dotnet publish src/LogPulse.Agent -p:PublishProfile=win-x64` (un `.exe` self-contained en `artifacts/agent-win-x64`). No debe cambiar ningún `packages.lock.json`.
- Entorno completo (desde H7): `docker compose up --build`

## Convenciones
- Acceso a datos solo a través de interfaces en `Data`; la API depende de las interfaces, no de Dapper.
- Nullable y warnings-as-errors activados.
- Scripts SQL en `src/LogPulse.Data/Schema/` numerados (`00N_*.sql`) e idempotentes. **Excepción hasta `v1.0.0`:** como no hay ninguna base de datos desplegada (solo contenedores de test), se pueden editar los scripts ya publicados. Desde `v1.0.0`, cualquier cambio va en un script nuevo.
- Secretos fuera del repo (user-secrets / variables de entorno / GitHub Secrets).
- Hooks de `.claude/settings.json`: formatean los `.cs` editados y ejecutan los tests unitarios antes de terminar una tarea.
- **Git:** nunca hacer `git commit`, `git merge` ni `git push` sin permiso explícito del usuario para esa acción concreta. Tampoco `docker push` ni despliegues.
- **Una rama por hito:** `hito/hN-nombre` (por ejemplo `hito/h3-ci`), creada desde `main` actualizado. Nunca se trabaja directamente en `main`.

## Flujo de trabajo por hito
Los hitos, sus criterios de aceptación y los agentes que los verifican están en `docs/ROADMAP.md`.
1. Crear la rama del hito: `git switch main`, `git pull`, `git switch -c hito/hN-nombre`.
2. Implementar las casillas del hito. Los hooks formatean y ejecutan los tests unitarios automáticamente.
3. Ejecutar los tests de integración si el hito toca datos o la API.
4. Lanzar `/verificar N`: ejecuta en paralelo los agentes del hito y consolida sus veredictos.
5. Corregir los bloqueantes y repetir `/verificar N` hasta que no quede ningún `FALLA`.
6. Marcar el hito en el roadmap, enseñar al usuario el diff y el mensaje de commit propuesto y **esperar su permiso** antes de hacer commit.
7. Con permiso: push de la rama y PR a `main`. Desde H3, el merge se hace con el CI en verde y también con permiso del usuario.

## Agentes verificadores (`.claude/agents/`)
Solo verifican e informan; nunca modifican archivos. Todos responden con `VEREDICTO: OK | AVISOS | FALLA` y hallazgos con `archivo:línea`.
- `qa-tester`: build, tests, formato y cobertura del cambio.
- `code-reviewer`: bugs y convenciones de este archivo.
- `security-auditor`: SQL, JWT y refresh tokens, autorización, secretos, dependencias.
- `devops-verifier`: workflows, Dockerfiles, compose, coherencia CI ↔ comandos locales.
- `api-tester`: pruebas HTTP reales contra la API levantada.
- `ui-tester`: pruebas E2E del dashboard con Playwright.
- `portfolio-reviewer`: el repo visto por un reclutador técnico.
