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

**Historial de verificación:** [docs/verification/H1.md](verification/H1.md).

## [x] H2 — Harness de agentes verificadores
**Agentes:** qa-tester, code-reviewer, security-auditor
- [x] 7 agentes en `.claude/agents/` (qa-tester, code-reviewer, security-auditor, devops-verifier, api-tester, ui-tester, portfolio-reviewer).
- [x] Skill `/verificar` en `.claude/skills/verificar/SKILL.md`.
- [x] Este roadmap y el flujo de trabajo por hito en `CLAUDE.md`.
- [x] `/verificar 1` ejecutado sobre el código existente y bloqueantes corregidos.

**Aceptación:** `/agents` lista los 7 agentes; `/verificar 1` devuelve OK o AVISOS.

## [x] H3 — CI en GitHub Actions
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
- [x] Primer run en verde: PR #1 y push a `main` (`b38e188`), confirmado por el usuario en la pestaña Actions.
- [x] Dependabot funcionando: abrió su primer PR (actualización del grupo de GitHub Actions).
- [x] Revisión del historial antes de publicar: sin secretos; menciones a empleadores neutralizadas; email noreply de GitHub para los commits nuevos.
- [x] Repo público.
- [x] Badge visible en el README ("passing").
- [x] Protección de `main` con un ruleset: PR obligatorio, los 5 checks del CI obligatorios (ligados a GitHub Actions), rama al día, sin borrado ni force push.

**Historial de verificación:** [docs/verification/H3.md](verification/H3.md).

**Aceptación:** run verde en GitHub y badge funcionando. ✅ Comprobado con la API pública de GitHub (push a `main` `595bc56` en verde, badge "passing", `main` protegida).

## [x] H4 — API ASP.NET Core
**Agentes:** qa-tester, code-reviewer, security-auditor, api-tester
- [x] `src/LogPulse.Api` con controllers, `[ApiController]`, ProblemDetails, Swagger con Bearer (solo en Development) y `/health` con check de SQL Server.
- [x] `Schema/002_auth.sql`: `Users`, `AgentCredentials`, `RefreshTokens`.
- [x] Auth de agentes (nombre de servidor + API key hasheada) → JWT rol `Agent` (15 min) + refresh token. `POST /api/agents` (Admin) crea o rota claves.
- [x] Auth de usuarios (usuario/contraseña con `PasswordHasher`, usuarios iniciales desde configuración) → JWT rol `Viewer`/`Admin`.
- [x] Refresh tokens rotativos guardados como hash SHA-256, con detección de reutilización que revoca toda la familia.
- [x] Endpoints `POST /api/auth/token` (usuarios), `/api/auth/agent-token` (agentes), `/api/auth/refresh` y `/api/auth/revoke`, con rate limiting por IP.
- [x] Ingesta (rol `Agent`) `POST /api/ingest/logs` y `/metrics`, en JSON y MessagePack, con límite de lote. El servidor sale del token.
- [x] Consulta (roles `Viewer` y `Admin`): `GET /api/servers`, `/api/logs`, `/api/metrics/{serverId}`, `/api/metrics/latest`.
- [x] `RetentionService` (`BackgroundService`) que purga los datos antiguos y los refresh tokens caducados.
- [x] Tests unitarios (tokens, auth, ingesta, retención, validación) y de integración con `WebApplicationFactory` + `SqlServerFixture`.
- [x] Muestras de ingesta en `samples/` (JSON y `.msgpack`), con un test que comprueba que coinciden.

**Avisos heredados de `/verificar 1` que se resuelven aquí:**
- [x] Límites de longitud de campos expuestos en Core (`FieldLimits`); la ingesta trunca antes del DAO para que un campo largo no tumbe el lote entero.
- [x] Tamaño máximo de lote en la ingesta (1000) y `ServerId` siempre el del token.
- [x] La API valida la longitud de `search` (500) con un 400.
- [x] La API acota `page` (10000) y el rango de `GET /api/metrics/{serverId}` (24 h, y el DAO devuelve como mucho 20000 filas).
- [x] `RetentionService` borra por lotes (`DELETE TOP (N)` en bucle).
- [x] Ingesta masiva con `INSERT` multi-fila por bloques (`SqlBatch`).
- [x] `seenAt` del upsert de servidores = hora de recepción en la API.
- [x] `DatabaseInitializer` con `sp_getapplock` y limitado a los recursos `Schema.*.sql`.
- [x] Renombrar `SqlServerDao`: **se mantiene**, porque es coherente con `SqlLogDao` y `SqlMetricDao` (prefijo `Sql` = implementación SQL Server) y la interfaz `IServerDao` deja claro el dominio.

**Avisos que pasan a otros hitos:**
- H5: el agente debe truncar con `FieldLimits` y partir los lotes por bytes (`IngestLimits.MaxRequestBytes`); sin reintentos automáticos en `/api/auth/refresh`.
- H6: rate limit en la ingesta por agente (hoy solo limita el tamaño de cada petición).
- H7: limitar los reintentos de arranque a errores transitorios (hoy reintenta cualquier `DbException` durante ~2 min, también un error de sintaxis en un script).
- H6/H7: `UseForwardedHeaders` y partición del rate limit detrás de proxy o del dashboard (hoy por IP y compartido por todos los endpoints de auth); valorar límite por cuenta.
- H6: ventana temporal por defecto o rate limit en `GET /api/logs` para consultas caras sin filtros.
- H8: al desplegar, login de SQL con permisos mínimos (hoy la API crea la base de datos y aplica DDL al arrancar) y seed desactivado en producción.

**Historial de verificación:** [docs/verification/H4.md](verification/H4.md).

**Aceptación:** flujo completo de auth, ingesta y consulta verificado por api-tester contra la API levantada.

## [x] H5 — Agent (Worker Service)
**Agentes:** qa-tester, code-reviewer, security-auditor
- [x] `src/LogPulse.Agent` con colectores `IMetricsCollector`: Windows (`GetSystemTimes`, `GlobalMemoryStatusEx`) y Linux (`/proc/stat`, `/proc/meminfo`); disco con `DriveInfo` (misma fórmula que `df`).
- [x] Tail de archivos de log: posición confirmada guardada en disco (entrega al menos una vez), rotación por truncado o sustitución detectada con un hash de los primeros bytes, BOM y CRLF, severidad deducida de la palabra de nivel.
- [x] Dos `Channel` acotados (logs: espera; métricas: descarta las más antiguas), lotes en MessagePack partidos por número y por bytes, reintentos con `Microsoft.Extensions.Http.Resilience`; un lote fallido se reenvía primero (mantiene el orden), un 413 lo parte por la mitad y solo se descartan los rechazados por payload (400/422).
- [x] `DelegatingHandler` con renovación automática del token: refresh antes de caducar sin reintentos automáticos, login de nuevo si el refresh se rechaza, renovaciones serializadas y caducidad anclada al reloj local con la cabecera `Date`.
- [x] Baja la frecuencia de métricas sin viewers: `IngestResult.ViewersOnline` en cada respuesta de ingesta (por ahora siempre `true`; H6 lo conectará al contador de SignalR).
- [x] Publish self-contained `win-x64` en un solo `.exe` (perfil `win-x64`, sin ajustes de desarrollo ni lock file) y soporte de servicio de Windows (`AddWindowsService`).
- [x] Tests de parsers, tailer, checkpoints, lotes, envío, tokens y opciones; integración del host real del agente contra la API y SQL Server.

**Avisos de H4 resueltos aquí:**
- [x] El agente trunca con `FieldLimits` (`FieldLimits.Truncate`, compartido con la API) y parte los lotes por bytes (estimación con cota superior comprobada en tests).
- [x] Sin reintentos automáticos en `/api/auth/refresh`.

**Avisos que pasan a otros hitos:**
- H6: `IngestResult.ViewersOnline` dirá a cualquier token de agente si alguien mira el dashboard; valorar si es aceptable al conectarlo a SignalR. Sigue pendiente el rate limit de ingesta por agente.
- H7: en compose el agente hablará con `http://api:...`, que no es loopback, y `AgentStartupChecks` lo rechazará fuera de Development: decidir entre TLS interno o un entorno propio. Documentar la instalación del agente en Linux.
- Pendiente (sin hito concreto): validar `DiskPath` al arrancar (si el disco no se puede leer desde la primera muestra no se envían métricas); comprobar en el tailer que la ruta final del handle coincide con la configurada (hoy se documenta que las carpetas de logs no deben ser modificables por usuarios); test del `catch` de `IngestSender.ExecuteAsync`. Límites documentados: una entrada por línea física y hora de lectura como timestamp.

**Historial de verificación:** [docs/verification/H5.md](verification/H5.md).

**Aceptación:** un agente local aparece en `/api/servers` y sus logs y métricas se pueden consultar. ✅ Comprobado en local con la API y el agente en Development: `demo-server` aparece, las líneas añadidas a `dev-logs/demo.log` llegan con su severidad (Error, Warning) y hay una muestra de métricas cada 5 s.

## [x] H6 — Dashboard en tiempo real
**Agentes:** qa-tester, code-reviewer, ui-tester
- [x] `src/LogPulse.Dashboard` (Blazor Server) que consume la API con JWT: backend-for-frontend, con el JWT y el refresh token en una sesión del servidor (el navegador solo recibe una cookie cifrada `HttpOnly`, `SameSite=Strict`, con el id de sesión), renovación única por sesión (los refresh tokens son de un solo uso) y logout con antiforgery que revoca el refresh token.
- [x] Hub SignalR `/hubs/live` (rol `Viewer`) y `ViewerTracker`: avisa de cada lote guardado (resumen de logs, métricas completas) y alimenta `IngestResult.ViewersOnline`. Las conexiones se cierran al caducar su token.
- [x] Páginas: login, resumen de servidores, detalle con gráficas y visor de logs con filtros, paginación y modo "seguir en vivo".
- [x] Tests de componentes con bUnit.

**Avisos de H4/H5 que se resuelven aquí:**
- [x] Rate limit de ingesta por agente y de consultas por usuario (429 con `Retry-After`).
- [x] `X-Forwarded-For` desde proxies de confianza: el dashboard reenvía la IP de cada usuario y el rate limit de auth deja de ser compartido por todos.
- [x] `IngestResult.ViewersOnline` conectado a SignalR. Exposición aceptada: es un solo bit, solo lo reciben agentes autenticados y es lo que permite bajar la frecuencia de muestreo.

**Avisos que pasan a otros hitos:**
- H7: el dashboard en compose hablará con `http://api:...` (como el agente, `DashboardStartupChecks` lo rechazará fuera de Development); añadir la IP del dashboard a `ForwardedHeaders:KnownProxies` de la API; claves de Data Protection persistentes en el contenedor.
- Pendiente (sin hito concreto): tests directos de la reconexión de `LiveFeed` (backoff tras `StartAsync` fallido, `Closed` por caducidad del token, token rechazado) con un hub caído y `FakeTimeProvider`; tests bUnit de la página de login (429, API caída, validación); tests de integración con sondeo y plazo fijo de 15 s; `PersistentComponentState` para no repetir consultas al prerenderizar; dejar de contar como viewer una pestaña cerrada sin logout antes de los ~3 min de retención de circuitos de Blazor (`CircuitHandler`).

**Historial de verificación:** [docs/verification/H6.md](verification/H6.md).

**Aceptación:** ui-tester ve llegar datos en vivo sin errores en la consola. ✅ Primera pasada: datos en vivo correctos y consola limpia, con el bloqueante de las fechas; segunda pasada: todo OK. Comprobado también en local con la API, el agente y el dashboard: el agente registra "Dashboard viewers online: True" con el dashboard abierto y vuelve a False al cerrar sesión.

## [x] H7 — Docker
**Agentes:** devops-verifier, api-tester, ui-tester
- [x] Dockerfiles multi-stage (API, Dashboard, Agent Linux), con usuario no root y `.dockerignore`.
- [x] `docker-compose.yml` con SQL Server (healthcheck), API, Dashboard y Agent de demo, más `.env.example`.
- [x] Job de CI que construye las imágenes y hace smoke test de `compose up` + `/health`.

**Avisos de H4–H6 que se resuelven aquí:**
- [x] Reintentos de arranque de la API solo ante errores de "servidor aún no listo" (`SqlStartupErrors`); un script roto falla al momento.
- [x] Agente y dashboard hablan con `http://api:8080` dentro de compose con un opt-in explícito (`AllowInsecureHttp`, desactivado por defecto).
- [x] IP fija del dashboard en `ForwardedHeaders:KnownProxies` de la API; claves de Data Protection del dashboard en un volumen.
- [x] Instalación del agente en Linux (systemd) documentada en el README.

**`/verificar 7`:** devops-verifier AVISOS, api-tester AVISOS, ui-tester OK, en una sola pasada. Historial: [docs/verification/H7.md](verification/H7.md).

**Avisos que pasan a otros hitos:**
- H8: fijar las acciones de terceros por SHA en el workflow de CD; valorar `healthcheck` del agente y hardening del contenedor de SQL Server si se despliega con compose.
- H9: la tarjeta del resumen solo navega desde el nombre del servidor.

**Aceptación:** `docker compose up --build` muestra datos en vivo en el dashboard en menos de 2 minutos. ✅ Stack sano en 21–30 s; el ui-tester vio `docker-demo` con métricas y logs llegando en vivo y la consola limpia.

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
