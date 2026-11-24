# Patrones y ciclos de vida (Escudo Yuma)

Regla de oro: un patrón solo aparece donde hay **más de una implementación real** o un **recurso costoso** que compartir.

## Vertical slice
Cada caso de uso vive en `Features/<Area>/<CasoDeUso>/` con request/response, validador, manejador y endpoint juntos
(clase `*Feature : IFeature`). `Program.cs` solo compone: `AddFeatures()` descubre las features por reflexión y `MapFeatures()` mapea los endpoints.
Agregar un caso de uso = agregar una carpeta; no se toca código compartido. Lo transversal vive en `Shared/`.

## Lifetimes

| Componente | Lifetime | Por qué |
|---|---|---|
| Sesión ONNX (`InferenceSession` dentro de `OnnxModeloAdapter`) / `IModeloRiesgoFactory` | Singleton | Crear la sesión es costoso y `Run` es thread-safe; se crea una vez por proceso y se calienta al arrancar (`PrecargaMotor`). |
| `PoliticaDecision` + `UmbralesDecision` | Singleton | Sin estado mutable; umbrales de `training/artefactos/umbrales.json` fijos al arrancar (calibrados fuera de línea). |
| `ExplicadorRazones`, `ConstructorAtributos` | Singleton | Tablas inmutables (explicacion.json) / lógica pura sobre una vista del estado. |
| `ColaReputacion` + `EscritorReputacion` | Singleton + Hosted | Channel acotado; el pipeline solo encola y un BackgroundService agrupa y persiste `ReputacionCuenta` (su propio scope por lote). |
| `RegistroBloqueos` | Singleton | Cuentas con bloqueo definitivo en memoria (evita una consulta por transacción); solo `ResolverAlerta` la actualiza. |
| `EstadoCadenaAuditoria` | Singleton | Candado y último eslabón de la cadena de hashes por instancia de app (O(1) por registro). |
| `ServicioReplay` | Singleton | Una sola ejecución del replay a la vez; cada lote usa su scope. |
| `CatalogoReglas` + `IEnumerable<IRegla>` | Singleton | Reglas inmutables, sin dependencias de request. |
| `IConnectionMultiplexer` (Redis) | Singleton | Una conexión multiplexada por proceso; crear una por request agota sockets. Solo se registra si `Redis:Conexion` existe. |
| `IEstadoEnLinea` (`EstadoEnLinea`) | Singleton | Port 1:1 de `training/features.py`: ventanas por cliente y destino, eventos de la app, score de ingreso por sesión. Un único candado: cada operación dura microsegundos y el orden importa. Hoy en memoria; con varias réplicas se movería a Redis. |
| `JwtEmisor`, `LimitadorVentana`, `IScoreIngreso`, `IConstructorAtributos` | Singleton | Sin estado por request o con estado intencionalmente compartido (limitador). |
| `EscudoDbContext` | Scoped | No es thread-safe; una unidad de trabajo por request. |
| Manejadores (`Manejador*`), validadores | Scoped | Usan el DbContext; viven lo que dura el request. |
| `ContextoAuditoria`, `ServicioAuditoria` | Scoped | Correlación e id de actor son por request. |
| `SandboxClient` | Typed HttpClient | `IHttpClientFactory` gestiona el pool de conexiones y el reciclado de DNS. |
| `SandboxReplayService` | Hosted (BackgroundService) | Solo corre con `Sandbox:ReplayHabilitado=true` y `Sandbox:EjecutarAlIniciar=true` (modo consola: termina la app al acabar); delega en `ServicioReplay`. |

Un Singleton nunca depende de un Scoped (p. ej. `ManejadorErrores` obtiene `ContextoAuditoria` desde `HttpContext.RequestServices`).

## Patrones
- **Factory** — `IModeloRiesgoFactory.Crear(TipoModelo, version)`: hay dos modelos (ingreso vs. transacción) y varias versiones; la factory los entrega y cachea (son costosos).
- **Strategy** — `IRegla`: cada regla vigente (R001–R012) es una estrategia registrada en el contenedor; `CatalogoReglas` las recorre. Agregar una regla = registrar otra implementación.
- **Adapter** — `SandboxClient` (JSON snake_case en español del openapi ↔ dominio), `OnnxModeloAdapter` (tensor ONNX float32 [1,57] ↔ `AtributosTransaccion`; salida `probabilities[:,1]`) y, a futuro, `IOperadorSimAdapter` (consulta simulada de cambio de SIM; hoy la señal ya llega en la transacción como `cambio_sim_48h`, por eso no hay segunda implementación todavía).
- **Puerto/implementación intercambiable** — `IScoreIngreso` (`ScoreIngresoLogin`, port exacto de `training/score_login.py`), `IEstadoEnLinea` e `IConstructorAtributos`.
- **Pipeline** — `ManejadorEvaluarTransaccion`: estado (atributos + actualización atómica) → modelo ONNX → política → razones → reputación (cola asíncrona) → auditoría → alerta. Las reglas vigentes R001-R012 (Strategy) ya no puntúan: son 12 señales 0/1 dentro del vector de atributos.

## Transversales (middleware/filtros, no dentro de los casos de uso)
- Correlación (`X-Correlation-Id`) y latencia por endpoint: `CorrelacionMetricasMiddleware`.
- Errores: `IExceptionHandler` → `ProblemDetails` sin detalles internos.
- Validación: `ValidacionFiltro<T>` (FluentValidation) como `IEndpointFilter`.
- Seguridad: JWT (15 min, claims `sub`, `sid`, `rol`, `score_ingreso`), políticas por rol, cabeceras seguras, HSTS/HTTPS configurable, limitador por llave e IP.
- Métricas: `/metrics` (Prometheus): `yuma_http_latencia_segundos`, `yuma_decisiones_total`, `yuma_scoring_latencia_ms`, `yuma_login_total`.
- Auditoría: bitácora append-only con hash SHA-256 encadenado (`Shared/Auditoria`); `CadenaHash.Verificar` detecta modificación, borrado o reordenamiento.

## Garantía de negocio
Solo `ResolverAlerta` (rol `analista`, acción `bloquear_cuenta` con `fraude_confirmado`) llama a `ReputacionCuenta.MarcarBloqueoDefinitivo`.
El pipeline de scoring únicamente *respeta* un bloqueo ya decidido (rechaza). Un test recorre todas las rutas mapeadas y falla si aparece una ruta nueva sin cubrir.

## Motor integrado (antes `TODO(INTEGRACION-*)`)

- **Atributos**: `Shared/Estado/EstadoEnLinea` + `Shared/Scoring/ConstructorAtributos` reproducen `training/features.py` (57 atributos en el orden de `atributos.json`, validado al arrancar). Paridad: `training/exportar_fixture_atributos.py` genera `fixture_atributos.csv` (2.000 tx) y la prueba `ParidadAtributosTests` repite el streaming sobre `03_Datos` (variable `YUMA_DATOS`; se salta sin datos) con tolerancia 1e-6.
- **Modelo**: `OnnxModeloAdapter` (una `InferenceSession`) creado por `ModeloRiesgoFactory`; si falta el modelo se aborta el arranque (un stub aprobaría todo). Paridad con `paridad.csv` (|dif| < 1e-4, mismas decisiones).
- **Política** (`umbrales.json`): `p < 0,461` aprobar · `0,461 <= p < 0,977` desafiar · `p >= 0,977` retener · rechazar si además fan-in destino >= 3 y reputación destino <= 30. Niveles internos: *advertir* (desafiar con destino nuevo y monto >= 80 % del saldo, sin señales de cuenta: mensaje anti-ingeniería social) y *verificar* (resto de desafíos; señales de dispositivo/SIM/clave/login). Cuenta bloqueada por un analista: rechazar.
- **Razones**: aproximación aditiva de SHAP (`explicacion.json`): top 3 con contribución > 0,15, plantillas con valores concretos; si ninguna supera el umbral, razón genérica coherente con la decisión. Siempre >= 1.
- **Score de ingreso**: `ScoreIngresoLogin` (pesos de `score_login.py`). Umbrales de resultado (el script no define): permitir < 0,40 · otp en [0,40; 0,70) · bloqueo_temporal >= 0,70. Un dispositivo no visto solo suma 0,30 (permitir); hacen falta más señales para OTP. Los dispositivos de la BD cuentan como conocidos solo para este score. El score se guarda en el estado (por sesión y por cliente, vigencia 60 min) y **NO es atributo del modelo**: escala la política (score >= 0,40 y decisión *aprobar* con p >= 0,2 -> *verificar*); nunca baja una decisión.
- **Eventos**: `POST /v1/eventos` (rol servicio) ingiere eventos tipo `eventos_app.csv`; Login (exitoso/fallido) y RegistrarDispositivo escriben su evento en el mismo estado. Un login con OTP/bloqueo no cuenta como "dispositivo visto".
- **Reputación**: tras cada tx se encola (Channel) la reputación de origen y destino; `EscritorReputacion` la persiste sin bloquear. `ReputacionCuenta.Puntaje` es **riesgo** (0 = confiable) = (100 - reputación) + ajuste de analistas. `GET /v1/cuentas/{id}/reputacion` devuelve puntaje, reputación 0-100 y señales.
- **Replay**: `Features/Sandbox/Replay` (ver más abajo).

## Replay contra el sandbox

`Sandbox:ReplayHabilitado=true` habilita `POST /v1/sandbox/replay` (analista; avance en `GET`) y, con `Sandbox:EjecutarAlIniciar=true`, corre al arrancar y termina la app (modo consola). Reinicia el sandbox y el estado, alimenta el estado con los eventos de `eventos_app.csv` con fecha < la de cada tx, califica con el mismo `ManejadorEvaluarTransaccion`, publica a `/v1/decisiones` cada lote pequeño apenas se califica y escribe `reports/replay_metricas.json` (métricas del sandbox, métricas locales sep-oct cruzadas con `transacciones.csv`, latencias internas y diferencias frente a `training/artefactos/reporte_validacion.json`).
```
training/.venv/Scripts/python ../Caso_de_estudio_Yuma_Pay/04_Sandbox_API/mock_api.py --puerto 8090
cd src/EscudoYuma.Api   # las rutas relativas parten del directorio de contenido
Sandbox__UrlBase=http://127.0.0.1:8090 Sandbox__ReplayHabilitado=true Sandbox__EjecutarAlIniciar=true Persistencia__Proveedor=memoria Jwt__Clave=<32+ caracteres> Seguridad__ForzarHttps=false dotnet run -c Release
```
**Latencia que mide el sandbox** (entrega -> decisión): `Sandbox:TamanoLote` por defecto 10 (antes 200; con lotes de 500 el p50 era 342 ms y el p99 564 ms porque cada decisión esperaba al lote completo). `CanalLotes` (`Replay.cs`, con prueba unitaria) pide un lote, lo califica y lo publica antes de pedir el siguiente; el orden cronológico del estado se conserva. `Sandbox:PrefetchLotes` >= 1 activa un pipeline productor/consumidor (canal acotado FIFO, un solo consumidor): sube el rendimiento a costa de latencia, por eso el valor por defecto es 0. Reutilizar conexión no es posible: el mock responde HTTP/1.0 y cierra cada conexión. Resultado: p50 2,3-5 ms y p99 12-22 ms (antes 342 / 564 ms) con detección 95,6 % y FP 0 %.
El cliente HTTP del sandbox envía `Connection: close` y cuerpo con `Content-Length` (el mock es HTTP/1.0 y no lee cuerpos *chunked*).

## Auditoría con commit de grupo
`ServicioAuditoria` calcula el eslabón (secuencia + hash) bajo un candado de microsegundos y encola el registro; un escritor en segundo plano (scope propio) confirma en una sola transacción todos los registros pendientes, y cada llamador espera a que el suyo esté confirmado. Antes el candado cubría el commit a Postgres y toda la API quedaba limitada a ~1/(latencia del commit) por segundo. Si un commit falla se descarta lo encadenado sobre él y se relee el último eslabón de la BD. Los llamadores deben guardar sus propios cambios antes de auditar (el commit usa otro `DbContext`).

## Docker
```
cp .env.example .env        # completar secretos
docker compose up -d --build   # api, postgres, redis, sandbox, prometheus, grafana
```
- `GET /health/ready` devuelve `Healthy` con Postgres y Redis reales (con Redis detenido responde `Degraded`).
- Los usuarios de prueba se siembran al primer arranque en `seed/usuarios_prueba.json` (ignorado por git). **El esquema se crea con `EnsureCreated`: no migra.** Si el modelo de datos cambia (columnas nuevas) hay que recrear el volumen de desarrollo: `docker compose down -v` (pierde la base de dev) y volver a levantar.
- Grafana (`http://localhost:3000`, usuario `admin` y `GRAFANA_PASSWORD`): dashboard provisionado "Escudo Yuma - Operación" (`ops/grafana/dashboards`): latencia p50/p99 por endpoint, decisiones por tipo, tasa de retenciones, scoring interno y logins. Prometheus: `http://localhost:9090`.
- **TLS de desarrollo**: `dotnet dev-certs https -ep ops/tls/escudo-dev.pfx -p <clave aleatoria>`; guardar la clave como `TLS_PFX_PASSWORD` en `.env` y activar `docker-compose.tls.yml` (`COMPOSE_PATH_SEPARATOR=:` y `COMPOSE_FILE=docker-compose.yml:docker-compose.tls.yml` en `.env`, o `-f` explícitos). La API queda en `https://localhost:8443` (certificado autofirmado: `curl -k`); el http 8080 se mantiene para el scrape de Prometheus dentro de la red. En producción: certificado real en el ingress y `Seguridad__ForzarHttps=true`.

## Prueba de carga (k6)
`load/k6.js`: `constant-arrival-rate` 150 it/s x 60 s + pico de 300 it/s x 15 s (desde el segundo 65); 90 % `POST /v1/score/transaccion` (4.000 transacciones de `transacciones.csv` sin `es_fraude`/`modalidad_fraude`, `load/transacciones_carga.json`) y 10 % `POST /v1/auth/login` (1.000 llaves). Umbrales: `http_req_duration p(99)<300` y `http_req_failed rate<0.01`. Resumen en `reports/k6_resumen.json`.
```
docker compose -f docker-compose.yml -f docker-compose.tls.yml -f docker-compose.carga.yml up -d --build api   # perfil de carga (BD aparte: escudo_yuma_carga)
training/.venv/Scripts/python load/preparar_datos.py --seed seed/usuarios_carga_seed.json   # genera load/*.json (credenciales: ignoradas por git)
docker run --rm --network escudo-yuma_default -v ./load:/load:ro -v ./reports:/reports -e BASE_URL=http://api:8080 grafana/k6:0.54.0 run /load/k6.js
```
**Perfil de carga (`docker-compose.carga.yml`, nunca producción)**: (1) tope de login por IP muy alto, porque k6 envía todo desde una IP (el tope por llave sigue activo y se reparten los logins entre 1.000 llaves; no relanzar la prueba antes de 5 min o las llaves agotan su cupo); (2) `Seguridad__Hash__Iteraciones=10000` en una base aparte con usuarios sintéticos. Motivo: con el PBKDF2 por defecto (100.000 iteraciones, ~110 ms de CPU por login en esta máquina) el p99 global lo fija la CPU de los logins en el pico (p99 ~330 ms, login p99 ~470 ms; el scoring daba p99 ~80 ms). Con el costo de producción la fase sostenida pasa y el pico no: es un límite de CPU del hash, no de la API.
Otros hallazgos de la carga: el candado de auditoría serializaba un commit por petición (arreglado con commit de grupo) y ONNX Runtime dejaba hilos girando (`allow_spinning=0`; no se fija el número de hilos porque con 1 hilo un caso límite del replay cambia de decisión).

## Decisiones de hackatón
- Esquema con `EnsureCreated` (sin migraciones EF); estado en línea en memoria; `/metrics` sin autenticación (restringir por red en producción).
- Claves de prueba del seeder: `seed/usuarios_prueba.json` (ignorado por git); nunca se escriben en logs.
