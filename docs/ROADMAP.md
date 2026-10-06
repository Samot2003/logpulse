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

**Aceptación:** run verde en GitHub y badge funcionando. ✅ Comprobado con la API pública de GitHub (push a `main` `595bc56` en verde, badge "passing", `main` protegida).

**Verificación (`/verificar h3`):** qa-tester OK, devops-verifier AVISOS (con el repo aún privado no podía ver el CI remoto; después se comprobó con la API pública).

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

**`/verificar 4`, primera pasada:** api-tester AVISOS (~40 escenarios E2E en verde contra la API real), qa-tester AVISOS, code-reviewer FALLA, security-auditor FALLA. Bloqueantes corregidos:
- [x] Carrera en la rotación de refresh tokens: el token rotado solo se inserta si su familia no está revocada (`TryInsertRotatedAsync`, comprobación e inserción atómicas con `UPDLOCK, HOLDLOCK`); tras el compare-and-set ya no se cancela a medias.
- [x] Rotar la clave de un agente revoca sus sesiones; `DELETE /api/agents/{serverName}` revoca la credencial; sesiones con caducidad absoluta (`Jwt:MaxSessionDays`, 30 días).

Avisos corregidos en la misma pasada: lote con elementos `null` → 400; líneas vacías aceptadas; timestamps futuros acotados a la hora de recepción; cuerpo de más de 4 MB → 413; `Retention:Interval` y `BatchSize` validados (lotes por debajo del umbral de escalado de bloqueos); opciones validadas antes de tocar la base de datos; la API no arranca fuera de Development con la clave JWT de desarrollo; timeout del applock; carrera en `CREATE DATABASE`; guardas en `SqlBatch`; constante de lote en el contrato de Data; test dependiente del orden; fakes con la semántica de la collation de SQL Server; `AgentsController` en su propio archivo; SQL Server publicado solo en `127.0.0.1` en la documentación.

**`/verificar 4`, segunda pasada:** api-tester AVISOS, qa-tester AVISOS, security-auditor AVISOS (bloqueantes anteriores confirmados como corregidos), code-reviewer FALLA. Corregido:
- [x] Bloqueante: carrera entre el login de un agente y la rotación de su clave. Primer intento con horas (`FamilyCreatedAt > CreatedAt`), descartado en la tercera pasada (ver abajo).
- [x] MessagePack malformado → 400 (formatter propio en lugar del paquete `MessagePack.AspNetCoreMvcFormatter`).
- [x] Perder el compare-and-set contra una revocación ya no se registra como reutilización.
- [x] `/api/auth/*`: solo JSON y cuerpo de 16 KB como máximo.
- [x] `serverName` de agentes restringido a caracteres seguros para URL.
- [x] Cadena de conexión de desarrollo con `127.0.0.1` y reintentos al conectar con SQL Server durante el arranque (hasta ~1 min).
- [x] Tests del 413, de opciones inválidas al arrancar, de timestamps futuros en métricas y de la carrera login/rotación.

**`/verificar 4`, tercera pasada:** security-auditor AVISOS, code-reviewer FALLA. Corregido:
- [x] Bloqueante: comparar horas no cerraba la carrera login/rotación (la rotación toma su hora antes de bloquear la fila). Ahora cada rotación incrementa atómicamente `AgentCredentials.KeyVersion`, la sesión guarda la versión de la fila cuya clave verificó (`RefreshTokens.SubjectVersion`) y el refresh exige que coincidan. No depende de relojes, de la precisión ni del nivel de aislamiento.
- [x] DoS: el límite de 1000 elementos se aplica **durante** la deserialización (converter JSON y formatter MessagePack sobre el lote completo), no después de deserializar y validar millones de elementos. Cuarta pasada: también se rechazan las claves repetidas (`"entries"` dos veces) y los lotes con más de 8 claves, que permitían multiplicar el presupuesto de una petición.
- [x] `/api/auth/*` sin `[Consumes]` (un content-type distinto da 415, no un 401 confuso); el formatter de MessagePack solo lee los lotes de ingesta.
- [x] El seed valida nombres y roles con las mismas reglas que la API; los reintentos de arranque cubren también la base de datos de la aplicación, con un plazo total de 2 minutos.

**`/verificar 4`, cuarta pasada:** code-reviewer AVISOS, security-auditor AVISOS. **Sin bloqueantes: H4 cerrado.** Corregido además:
- [x] Claves repetidas en el lote (multiplicaban el presupuesto de elementos) y lotes con más de 8 claves → 400.
- [x] MessagePack con claves en camelCase → 400 (antes 200 con entradas vacías: pérdida silenciosa de datos).
- [x] MessagePack de más de 4 MB → 413 (la librería envolvía la excepción de Kestrel); un payload truncado sigue siendo 400.
- [x] La ruta de los errores JSON vuelve a indicar qué elemento del lote falla (`$.entries[3].severity`).
- [x] El seed valida el nombre del agente con el mismo atributo que la API (sin aceptar un salto de línea final).
- [x] `TokenSubject.Version` obligatorio; el test de revocación aísla `IsActive`; tests de los nuevos límites y del formatter.

**Aceptación:** flujo completo de auth, ingesta y consulta verificado por el api-tester contra la API levantada (pasadas 1 y 2, unos 40 escenarios). Los cambios posteriores están cubiertos por los tests de integración (107 tests en total).

**Avisos que pasan a otros hitos:**
- H5: el agente debe truncar con `FieldLimits` y partir los lotes por bytes (`IngestLimits.MaxRequestBytes`); sin reintentos automáticos en `/api/auth/refresh`.
- H6: rate limit en la ingesta por agente (hoy solo limita el tamaño de cada petición).
- H7: limitar los reintentos de arranque a errores transitorios (hoy reintenta cualquier `DbException` durante ~2 min, también un error de sintaxis en un script).
- H6/H7: `UseForwardedHeaders` y partición del rate limit detrás de proxy o del dashboard (hoy por IP y compartido por todos los endpoints de auth); valorar límite por cuenta.
- H6: ventana temporal por defecto o rate limit en `GET /api/logs` para consultas caras sin filtros.
- H8: al desplegar, login de SQL con permisos mínimos (hoy la API crea la base de datos y aplica DDL al arrancar) y seed desactivado en producción.

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

**`/verificar 5`, primera pasada:** qa-tester FALLA, code-reviewer FALLA, security-auditor FALLA. Bloqueantes corregidos:
- [x] `.gitignore` ignoraba la carpeta de código `Logs/` (la regla `logs/` del log de desarrollo coincide sin distinguir mayúsculas en Windows): el log de desarrollo pasa a `dev-logs/`.
- [x] La posición inicial de un archivo sin checkpoint no se guardaba: un reinicio antes del primer lote confirmado saltaba lo escrito mientras tanto. Ahora se guarda al abrirlo (o en el byte 0 si aún no existe), sin pisar una posición conocida.
- [x] Instalación como servicio del README insegura (API key en el registro, legible por usuarios locales; LocalSystem en una carpeta modificable por cualquier usuario autenticado). Ahora: `Program Files`, cuenta virtual `NT SERVICE\LogPulseAgent`, estado en `ProgramData` con ACL propia y la clave en `appsettings.Production.json` legible solo por el servicio y los administradores.

Avisos corregidos en la misma pasada: ninguna excepción inesperada para el host (envío, archivo ilegible, checkpoint manipulado); 413 parte el lote por la mitad en lugar de descartarlo y 415 se reintenta; las métricas salen antes de cada lote de logs; timeouts de Polly a medida de lotes grandes y `MaxBatchBytes` por defecto de 512 KB (por debajo del límite habitual de nginx); la publicación solo incluye `appsettings.json` (lista blanca); cabecera `Authorization` redactada en los logs de `HttpClient`; respuesta 200 que no es el JSON de la API → reintentar; sin `!` injustificados; arrancar por el final no corta una línea a medio escribir; checkpoint escrito sin seguir enlaces plantados en el temporal. Tests nuevos: posición inicial, alineación de línea, checkpoint manipulado, regla de muestreo ociosa, partición por 413, prioridad de métricas y el cableado real de DI (401 → renovación y reenvío; login y refresh sin reintentos).

**`/verificar 5`, segunda pasada:** qa-tester AVISOS, code-reviewer AVISOS, security-auditor FALLA (bloqueantes de la primera pasada confirmados como resueltos). Corregido:
- [x] Bloqueante: el script del README daba a `icacls` nombres de grupo en inglés (`Administrators`), que no existen en un Windows en otro idioma; el comando fallaba en silencio y la clave quedaba legible. Ahora usa SIDs (`*S-1-5-32-544`, `*S-1-5-18`), se detiene en el primer fallo (`$ErrorActionPreference` y `$LASTEXITCODE`), restringe el archivo antes de escribir la clave (pedida con `Read-Host`, fuera del historial), fija el propietario de la carpeta de estado, registra el origen del Event Log y exige que las carpetas de los logs solo sean modificables por administradores.

Avisos corregidos: las métricas aceptadas ya no reinician el contador de fallos mientras un lote de logs sigue fallando; checkpoint con `Flush(flushToDisk: true)` antes del rename; como mucho 1 MB por archivo y sondeo (un backlog no bloquea los demás archivos); el cliente de ingesta deja los timeouts a Polly (`HttpClient.Timeout` infinito); una carpeta inexistente (volumen sin montar) no se trata como archivo nuevo; un fallo de disco envía el último valor conocido en lugar de perder CPU y memoria; fallos de muestreo registrados una vez por racha; excepciones inesperadas del envío siempre registradas; `mkdir` de `dev-logs` en el README. Tests nuevos: bucles reales del tailer (reparto entre archivos, posiciones iniciales) y del sampler (sigue tras un fallo), carpeta inexistente, contador de fallos y bordes de `Truncate`.

**`/verificar 5`, tercera pasada:** qa-tester OK, code-reviewer AVISOS, security-auditor AVISOS. **Sin bloqueantes: H5 cerrado.** El security-auditor comprobó en este Windows es-ES que los SID funcionan donde `"Administrators"` falla (exit 1332) y que `Set-Content` conserva la ACL del archivo ya restringido. Corregido además:
- [x] El tope de 1 MB por archivo y sondeo limitaba la velocidad a 1 MB/s por archivo: mientras quede backlog se hace otra ronda inmediata (round-robin) en vez de esperar al siguiente tick.
- [x] `HttpClient.Timeout` del cliente de ingesta finito (5 min, por encima del peor caso de Polly): la lectura del cuerpo de la respuesta queda fuera de la pipeline y no puede colgarse para siempre.
- [x] Un archivo de posiciones ilegible ya no impide arrancar (se trata como dañado).
- [x] Script de instalación: un único bloque `& { }` (se detiene en el primer fallo aunque se pegue línea a línea), servicio en Manual hasta el final, comprobación de que la carpeta de estado está vacía, clave con `Read-Host -AsSecureString`, solo se copian el `.exe` y `appsettings.json` (publicado sin elevar), pasos para deshacer una instalación a medias. Sintaxis comprobada con el parser de PowerShell 5.1.
- [x] Nombre del servicio coherente (`LogPulseAgent`) y origen del Event Log registrado para él; ajustes que faltaban en la tabla del README.

**Avisos que pasan a otros hitos:**
- H6: `IngestResult.ViewersOnline` dirá a cualquier token de agente si alguien mira el dashboard; valorar si es aceptable al conectarlo a SignalR. Sigue pendiente el rate limit de ingesta por agente.
- H7: en compose el agente hablará con `http://api:...`, que no es loopback, y `AgentStartupChecks` lo rechazará fuera de Development: decidir entre TLS interno o un entorno propio. Documentar la instalación del agente en Linux.
- Pendiente (sin hito concreto): validar `DiskPath` al arrancar (si el disco no se puede leer desde la primera muestra no se envían métricas); comprobar en el tailer que la ruta final del handle coincide con la configurada (hoy se documenta que las carpetas de logs no deben ser modificables por usuarios); test del `catch` de `IngestSender.ExecuteAsync`. Límites documentados: una entrada por línea física y hora de lectura como timestamp.

**Aceptación:** un agente local aparece en `/api/servers` y sus logs y métricas se pueden consultar. ✅ Comprobado en local con la API y el agente en Development: `demo-server` aparece, las líneas añadidas a `dev-logs/demo.log` llegan con su severidad (Error, Warning) y hay una muestra de métricas cada 5 s.

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
