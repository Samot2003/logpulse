# LogPulse

Mini ServerHealth open source en C#/.NET 8: un agente recoge métricas y logs, una API ASP.NET Core los almacena en SQL Server y un dashboard los muestra en tiempo real. Es un proyecto de portfolio; no incluir código ni datos de Win Systems.

## Idioma
Responder siempre en español. README, código, comentarios y commits en inglés. Harness (`CLAUDE.md`, `.claude/agents`, `.claude/skills`) y `docs/ROADMAP.md` en español.

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
- Scripts SQL en `src/LogPulse.Data/Schema/` numerados (`00N_*.sql`) e idempotentes. **Excepción hasta `v1.0.0`:** como no hay ninguna base de datos desplegada (solo contenedores de test), se pueden editar los scripts ya publicados. Desde `v1.0.0`, cualquier cambio va en un script nuevo.
- Secretos fuera del repo (user-secrets / variables de entorno / GitHub Secrets).
- Hooks de `.claude/settings.json`: formatean los `.cs` editados y ejecutan los tests unitarios antes de terminar una tarea.
- Pedir confirmación antes de `git push`, `docker push` o cualquier despliegue.

## Flujo de trabajo por hito
Los hitos, sus criterios de aceptación y los agentes que los verifican están en `docs/ROADMAP.md`.
1. Implementar las casillas del hito. Los hooks formatean y ejecutan los tests unitarios automáticamente.
2. Ejecutar los tests de integración si el hito toca datos o la API.
3. Lanzar `/verificar N`: ejecuta en paralelo los agentes del hito y consolida sus veredictos.
4. Corregir los bloqueantes y repetir `/verificar N` hasta que no quede ningún `FALLA`.
5. Marcar el hito en el roadmap y hacer commit.

## Agentes verificadores (`.claude/agents/`)
Solo verifican e informan; nunca modifican archivos. Todos responden con `VEREDICTO: OK | AVISOS | FALLA` y hallazgos con `archivo:línea`.
- `qa-tester`: build, tests, formato y cobertura del cambio.
- `code-reviewer`: bugs y convenciones de este archivo.
- `security-auditor`: SQL, JWT y refresh tokens, autorización, secretos, dependencias.
- `devops-verifier`: workflows, Dockerfiles, compose, coherencia CI ↔ comandos locales.
- `api-tester`: pruebas HTTP reales contra la API levantada.
- `ui-tester`: pruebas E2E del dashboard con Playwright.
- `portfolio-reviewer`: el repo visto por un reclutador técnico.
