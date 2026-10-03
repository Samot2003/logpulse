# Roadmap de LogPulse

Fuente de verdad de los hitos. La skill `/verificar N` lee de aquí la definición de terminado y los agentes requeridos de cada hito. Un hito solo se marca `[x]` cuando `/verificar N` no devuelve ningún `FALLA` ni hallazgo bloqueante.

## Definición de terminado común
Se aplica a todos los hitos, además de sus criterios propios:
- `dotnet build LogPulse.sln --nologo` sin errores ni warnings.
- Tests unitarios y de integración en verde.
- `dotnet format LogPulse.sln --verify-no-changes` limpio.
- `/verificar N` sin `FALLA`.
- Trabajo en la rama `hito/hN-nombre`; commits en inglés y solo con permiso explícito del usuario.
- A partir de H3: CI en verde en GitHub.

---

## [x] H0 — Harness base
**Agentes:** —
- [x] `CLAUDE.md` con estructura, comandos y convenciones.
- [x] `.claude/settings.json` con permisos y hooks (`format-cs.ps1`, `test-on-stop.ps1`).

## [x] H1 — Core + Data
**Agentes:** qa-tester, code-reviewer, security-auditor
- [x] Modelos en `src/LogPulse.Core` (`Server`, `LogEntry`, `MetricSample`, `LogSeverity`, `LogQuery`, `PagedResult<T>`).
- [x] DAOs con Dapper detrás de interfaces en `src/LogPulse.Data/Daos`.
- [x] Esquema idempotente embebido (`Schema/001_initial.sql`) aplicado por `DatabaseInitializer`.
- [x] Tests unitarios + integración con Testcontainers (SQL Server 2022).

**Verificación (`/verificar 1`, 3 pasadas):** qa-tester AVISOS, security-auditor OK, code-reviewer AVISOS. Bloqueantes corregidos:
- Desbordamiento del offset de paginación (ahora `LogQuery.Offset` en `long`).
- Dependencias transitivas vulnerables de xunit 2.5.3 (actualizado a 2.9.3).
- Búsqueda recortada que daba falsos positivos (ya no se recorta).
- Edición de `001_initial.sql` publicado (excepción hasta `v1.0.0` decidida por el usuario y documentada en `CLAUDE.md`).

## [x] H2 — Harness de agentes verificadores
**Agentes:** qa-tester, code-reviewer, security-auditor
- [x] 7 agentes en `.claude/agents/` (qa-tester, code-reviewer, security-auditor, devops-verifier, api-tester, ui-tester, portfolio-reviewer).
- [x] Skill `/verificar` en `.claude/skills/verificar/SKILL.md`.
- [x] Este roadmap y el flujo de trabajo por hito en `CLAUDE.md`.
- [x] `/verificar 1` ejecutado sobre el código existente y bloqueantes corregidos.

**Aceptación:** `/agents` lista los 7 agentes; `/verificar 1` devuelve OK o AVISOS.

## [ ] H3 — CI en GitHub Actions
**Agentes:** devops-verifier, qa-tester
- [x] Repo creado y primer push (lo hizo el usuario como `Samot2003/Proyecto-c-`).
- [x] Renombrar el repo a `logpulse` en GitHub (usuario) y actualizar el remoto con `git remote set-url`.
- [x] Lock files de NuGet (`packages.lock.json`) y restauración en `--locked-mode` en el CI.
- [x] `.github/workflows/ci.yml` en push y PR a `main`, con `concurrency` que cancela los runs antiguos.
  - [x] Job `build`: `setup-dotnet` con `global-json-file`, caché NuGet, `dotnet format --verify-no-changes`, `dotnet build` (warnings como errores vía `Directory.Build.props`).
  - [x] Job `unit-tests`.
  - [x] Job `integration-tests` (Testcontainers en `ubuntu-latest`).
  - [x] Job `coverage`: une la cobertura de unitarios e integración con ReportGenerator y la publica en el job summary.
  - [x] Job `security`: `dotnet list package --vulnerable --include-transitive` falla si encuentra vulnerabilidades.
- [x] `.github/dependabot.yml` (nuget + github-actions, semanal, agrupado).
- [x] README mínimo con el badge de CI (se amplía en H9).
- [ ] Protección de la rama `main` exigiendo CI.

**Aceptación:** run verde en GitHub y badge funcionando.

## [ ] H4 — API ASP.NET Core
**Agentes:** qa-tester, code-reviewer, security-auditor, api-tester
- [ ] `src/LogPulse.Api` con controllers, `[ApiController]`, ProblemDetails, Swagger con Bearer y `/health` con check de SQL Server.
- [ ] `Schema/002_auth.sql`: `Users`, `AgentCredentials`, `RefreshTokens`.
- [ ] Auth de agentes (nombre de servidor + API key hasheada) → JWT rol `Agent` (15 min) + refresh token.
- [ ] Auth de usuarios (usuario/contraseña con `PasswordHasher`, admin inicial desde configuración) → JWT rol `Viewer`/`Admin`.
- [ ] Refresh tokens rotativos guardados como hash SHA-256, con detección de reutilización que revoca toda la familia.
- [ ] Endpoints `POST /api/auth/token`, `/api/auth/refresh` y `/api/auth/revoke`, con rate limiting.
- [ ] Ingesta (rol `Agent`) `POST /api/ingest/logs` y `/metrics`, en JSON y MessagePack, con límite de lote. El servidor sale del token.
- [ ] Consulta (rol `Viewer`): `GET /api/servers`, `/api/logs`, `/api/metrics/{serverId}`, `/api/metrics/latest`.
- [ ] `RetentionService` (`BackgroundService`) que purga los datos antiguos.
- [ ] Tests unitarios de `TokenService` y de integración con `WebApplicationFactory` + `SqlServerFixture`.
- [ ] Muestras de ingesta en `samples/` (JSON y `.msgpack`) para las pruebas E2E del api-tester.

**Avisos heredados de `/verificar 1` que se resuelven aquí:**
- [ ] Límites de longitud de campos expuestos en Core (`Server.Name` 128, `Source` 256, `Message` 4000, tope para `Exception`); la ingesta valida o trunca antes del DAO para que un campo largo no tumbe el lote entero.
- [ ] Tamaño máximo de lote en la ingesta y `ServerId` sobrescrito con el del token.
- [ ] La API valida la longitud de `search` con un 400. Tener en cuenta que `EscapeLike` puede duplicarla y que el patrón de `LIKE` admite unos 4000 caracteres.
- [ ] La API acota `page` y el rango de `GET /api/metrics/{serverId}` (rango máximo o agregación) para no cargar todo el histórico en memoria.
- [ ] `RetentionService` borra por lotes (`DELETE TOP (N)` en bucle) para no superar el timeout ni bloquear la tabla.
- [ ] Ingesta masiva eficiente (`SqlBulkCopy`, TVP o `INSERT` multi-fila) en lugar de un `INSERT` por fila.
- [ ] `seenAt` del upsert de servidores = hora de recepción en la API, nunca el reloj del agente.
- [ ] `DatabaseInitializer` seguro con varias instancias a la vez (`sp_getapplock`) y limitado a los recursos `Schema.*.sql`.
- [ ] Valorar renombrar `SqlServerDao` (se confunde con "SQL Server").

**Aceptación:** flujo completo de auth, ingesta y consulta verificado por api-tester contra la API levantada.

## [ ] H5 — Agent (Worker Service)
**Agentes:** qa-tester, code-reviewer, security-auditor
- [ ] `src/LogPulse.Agent` con colectores `IMetricsCollector` para Windows y Linux, y disco con `DriveInfo`.
- [ ] Tail de archivos de log que guarda el offset y soporta la rotación de archivos.
- [ ] `Channel` acotado, envío por lotes en MessagePack y reintentos con `Microsoft.Extensions.Http.Resilience`.
- [ ] `DelegatingHandler` con renovación automática del token.
- [ ] Baja la frecuencia de envío cuando no hay viewers conectados.
- [ ] Publish self-contained `win-x64` y soporte de servicio de Windows.
- [ ] Tests de parsers, tailer, batching y handler de tokens; integración agente → API.

**Aceptación:** un agente local aparece en `/api/servers` y sus logs y métricas se pueden consultar.

## [ ] H6 — Dashboard en tiempo real
**Agentes:** qa-tester, code-reviewer, ui-tester
- [ ] `src/LogPulse.Dashboard` (Blazor Server) que consume la API con JWT.
- [ ] Hub SignalR `/hubs/live` (rol `Viewer`) y `ViewerTracker`.
- [ ] Páginas: login, resumen de servidores, detalle con gráficas y visor de logs con filtros, paginación y modo "seguir en vivo".
- [ ] Tests de componentes con bUnit.

**Aceptación:** ui-tester ve llegar datos en vivo sin errores en la consola.

## [ ] H7 — Docker
**Agentes:** devops-verifier, api-tester, ui-tester
- [ ] Dockerfiles multi-stage (API, Dashboard, Agent Linux), con usuario no root y `.dockerignore`.
- [ ] `docker-compose.yml` con SQL Server (healthcheck), API, Dashboard y Agent de demo, más `.env.example`.
- [ ] Job de CI que construye las imágenes y hace smoke test de `compose up` + `/health`.

**Aceptación:** `docker compose up --build` muestra datos en vivo en el dashboard en menos de 2 minutos.

## [ ] H8 — CD
**Agentes:** devops-verifier, security-auditor
- [ ] `.github/workflows/cd.yml`: con push a `main` publica imágenes en GHCR (`sha` + `latest`).
- [ ] Con tag `v*`: tags semver y GitHub Release con el Agent `win-x64` en zip.
- [ ] Despliegue a un entorno gratuito (se decide al llegar), con environment `production`, secretos en GitHub Secrets y smoke test a `/health`.
- [ ] Antes del primer despliegue, decidir con el usuario si se cierra ya la excepción de edición de scripts SQL publicados. A partir de ese momento sí habrá una base de datos real, y las guardas por nombre no aplican cambios a objetos que ya existen (aviso del code-reviewer en `/verificar 1`).

**Aceptación:** push a `main` publica en GHCR, tag crea la Release y el deploy pasa el smoke test. Se pide confirmación antes de cada push de imágenes y de cada despliegue.

## [ ] H9 — README y pulido
**Agentes:** portfolio-reviewer, code-reviewer
- [ ] README en inglés con diagrama Mermaid, quick start en 2 comandos, capturas, badges y sección "Design decisions".
- [ ] Repo fijado en el perfil de GitHub y enlazado desde el CV y `samot2003.github.io`.
- [ ] Tag `v1.0.0`.

**Aceptación:** portfolio-reviewer devuelve OK.
