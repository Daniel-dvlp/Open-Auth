# Escudo Yuma - Auditoría de seguridad (OWASP ASVS / API Top 10)

Alcance: backend `src/EscudoYuma.Api` (ASP.NET Core 8, vertical slice), Docker/compose, dependencias y pipeline de entrenamiento (`training/`).
Datos 100 % sintéticos (hackatón). Las pruebas que cubren cada hallazgo están en `src/EscudoYuma.Api.Tests/SeguridadTests.cs`
(31 pruebas nuevas; la suite pasa de 97 a 128, `dotnet build` sin avisos).

## 1. Hallazgos

Severidad: C = crítica, A = alta, M = media, B = baja. Estado: **Corregido** o **Aceptado** (con justificación).

| # | Sev. | Hallazgo y evidencia | Estado |
|---|------|----------------------|--------|
| H1 | A | El perfil de carga bajaba las iteraciones PBKDF2 con solo `Seguridad__Hash__Iteraciones` (`PersistenciaExtensions.cs`, antes líneas 27-32): bastaba esa variable en cualquier entorno para debilitar todos los hashes nuevos, sin piso. | **Corregido.** Solo se admite con `Seguridad:PerfilCarga=true` y >= 10.000; si no, la app **no arranca** (`ValidateOnStart`). `docker-compose.yml`/`tls`/`appsettings*` no contienen la variable; solo `docker-compose.carga.yml` activa el perfil. Pruebas: `Bajar_las_iteraciones_sin_el_perfil...`, `Solo_el_perfil_de_carga...`, `El_compose_base_nunca_baja...`. Por defecto: 100.000 (Identity). |
| H2 | A | El seed (cuentas analista/servicio con claves de prueba escritas a disco) se activaba con `Seed__Habilitado=true` también con `ASPNETCORE_ENVIRONMENT=Production` (compose lo hacía, `InicializadorBd`). | **Corregido.** Fuera de Development exige `Seed:PermitirFueraDeDesarrollo=true` (solo el compose de demo); si falta, el arranque falla. Prueba `El_seed_fuera_de_Development_exige_permiso_explicito`. |
| H3 | A | Redis sin contraseña (`docker-compose.yml`). No publicaba puertos, pero cualquier contenedor de la red lo alcanzaba. | **Corregido.** `--requirepass` desde `REDIS_PASSWORD` (obligatoria, `.env.example` actualizado); conexión con `password=`. |
| H4 | M | `/metrics` anónimo en el mismo puerto publicado que la API (`Program.cs`, TODO). | **Corregido.** Se sirve solo por el puerto interno `Seguridad:Metricas:Puerto` (9091, no publicado) y ese puerto solo sirve `/metrics`; Prometheus raspa `api:9091`. Prueba `Metricas_solo_por_el_puerto_interno...`. |
| H5 | M | BOLA por rol desconocido: `ObtenerReputacion.cs` solo restringía `rol == cliente`; cualquier otro valor del claim veía cuentas ajenas. | **Corregido.** Deniega por defecto: solo analista/servicio ven cualquier cuenta; el resto solo la propia (comparación exacta). Prueba `Un_rol_desconocido_solo_ve_su_propia_reputacion...`. |
| H6 | M | JWT sin algoritmo fijado (`Seguridad.cs`): aceptaba HS384/HS512 con la misma clave; clave trivial de 32+ caracteres permitida en prod. | **Corregido.** `ValidAlgorithms=[HS256]`, `RequireSignedTokens`, `RequireExpirationTime`; fuera de Development se rechazan claves con < 12 caracteres distintos o ausentes. Política de respaldo "autenticado" para todo endpoint sin política. Pruebas `JwtSeguridadTests` (alg `none`, HS512, clave/aud/iss/expiración). |
| H7 | M | Estado en línea envenenable: `fecha_hora` sin rango (un evento `cambio_sim` en 2999 queda "vigente" siempre y sube el score del cliente); montos hasta `long.Max` desbordan las sumas `long` de las ventanas (`EstadoEnLinea.cs`); `null` en `eventos` y `sistema_operativo` ilimitado. | **Corregido.** Fecha entre 2020 y ahora+1 día; monto/saldo <= 10^12; antigüedad <= 36.500; elementos no nulos; `sistema_operativo` <= 16. Un lote con un evento inválido se rechaza completo y no toca el estado. Pruebas de `ValidacionEntradaTests`. |
| H8 | M | `/v1/eventos` (rol servicio) no dejaba rastro de auditoría de quién alteró el estado en línea. | **Corregido.** Registro `eventos_ingeridos` (actor, nº de eventos y clientes; sin ids de cliente). |
| H9 | M | Cuerpo sin tope (30 MB por defecto), JSON mal formado/profundo respondía 500 en Development (`BadHttpRequestException` caía en `ManejadorErrores`). | **Corregido.** Tope 1 MiB (413 por `Content-Length` + límite de Kestrel), `ManejadorErrores` devuelve 4xx para cuerpos ilegibles; cabeceras con timeout/tamaño; sin cabecera `Server`. Prueba `Cuerpos_enormes_json_profundo...`. |
| H10 | M | Limitador en memoria: tabla sin cota (llaves arbitrarias agotan memoria) y purga O(n) en cada petición cuando está llena (CPU). | **Corregido.** Tope de 200.000 claves, purga <= 1/s, falla cerrado al saturarse y se recupera al vencer ventanas. Prueba `La_tabla_esta_acotada...`. |
| H11 | M | Dos analistas podían resolver la misma alerta a la vez (sin token de concurrencia): doble ajuste de reputación y bloqueo duplicado. | **Corregido.** `Alerta.Estado` es token de concurrencia; el segundo guardado recibe 409. Prueba `Dos_analistas_no_pueden_resolver...`. |
| H12 | M | `X-Forwarded-For` / IP real detrás de un balanceador: sin soporte, todos los clientes compartirían el límite por IP. | **Corregido (opt-in seguro).** Por defecto la cabecera se **ignora** (no evadible). `Seguridad:ProxiesConfiables` (IP/CIDR) activa `UseForwardedHeaders` solo para esos proxies. Pruebas `X_Forwarded_For_*`. |
| H13 | B | `BloqueoDefinitivo`/`BloqueadaPor` con setter público. | **Corregido.** Setter privado; solo `MarcarBloqueoDefinitivo` (única llamada: `ResolverAlerta`). Prueba `El_bloqueo_definitivo_solo_se_activa...` y la existente `Ninguna_ruta_distinta_de_ResolverAlerta...`. |
| H14 | B | Registro de dispositivos sin tope por cliente. | **Corregido.** Máx. 20 (409) y formato `[A-Za-z0-9._:-]`. |
| H15 | B | Imágenes con etiqueta flotante (`postgres:16-alpine`, `redis:7-alpine`, `python:3.12-slim`, `sdk:8.0`, `aspnet:8.0`); observabilidad publicada en 0.0.0.0. | **Corregido.** Versiones fijas (verificadas con `docker manifest inspect`), Prometheus/Grafana/sandbox en `127.0.0.1`, `no-new-privileges` y `cap_drop: ALL` en la API, Grafana sin registro ni acceso anónimo. |
| A1 | M | Compromiso total de la BD permite recalcular toda la cadena de auditoría (SHA-256 sin clave, sin ancla externa). | **Aceptado.** La cadena detecta alteraciones parciales y la tabla es append-only desde la aplicación. Mejora: HMAC con clave en KMS y publicar periódicamente el último hash. |
| A2 | M | Una caída del proceso entre guardar el cambio de negocio y confirmar su registro de auditoría deja un hueco (los llamadores esperan el commit, pero el orden es "cambio, luego auditoría"). | **Aceptado.** Ventana de milisegundos; afecta a la evidencia, no a la integridad de datos. Mejora: outbox transaccional. |
| A3 | M | Estado en línea, limitador y `RegistroBloqueos` son por proceso: con varias réplicas el límite se multiplica y un bloqueo no se propaga de inmediato. | **Aceptado** (despliegue de una réplica en el hackatón). Mejora: mover a Redis (ya hay contraseña). |
| A4 | B | Bloqueo por abuso de login (5 intentos/5 min por llave) permite a un tercero impedir temporalmente el ingreso de una víctima; los fallidos también suben su score de ingreso. | **Aceptado**: es el compromiso entre fuerza bruta y disponibilidad; el efecto es temporal (ventana) y auditado. |
| A5 | B | Cuentas de servicio y analista se autentican por el mismo `/v1/auth/login` público (con límites); sin revocación de tokens (vigencia 15 min). | **Aceptado**. Mejora: mTLS / cliente OAuth para servicio y lista de revocación por `jti`/`sid`. |
| A6 | B | PBKDF2-HMAC-SHA512 con 100.000 iteraciones (< 210.000 de OWASP). | **Aceptado.** Subirlo sin re-hash haría que el hash ficticio (anti-enumeración por tiempo) tardara más que los hashes ya guardados y filtraría la existencia de la llave; se recomienda subir ambos a la vez (re-hash en el siguiente login). |
| A7 | B | `AllowedHosts: "*"`, `/health/ready` anónimo (consulta la BD), `ReplayHabilitado` reinicia el estado en línea si un analista lo activa. | **Aceptado**: sin datos sensibles (devuelve solo `Healthy`), el replay requiere rol analista **y** bandera (409 por defecto). En producción fijar `AllowedHosts` y limitar el health en el ingress. |
| A8 | B | La llave enmascarada de 10 dígitos conserva 6 (`300****567`). | **Aceptado**: pseudonimización suficiente para soporte; una prueba existente fija ese formato. |

## 2. Controles existentes (verificados)

* **Autenticación**: JWT HS256, emisor/audiencia/vencimiento validados, tolerancia 30 s, vigencia 15 min, clave >= 32 caracteres solo por `Jwt__Clave` (obligatoria fuera de Development; en Development, clave efímera por proceso; compose fija `ASPNETCORE_ENVIRONMENT=Production`).
* **Autorización**: política de respaldo autenticada + políticas por rol. Analista: cola, resolver, replay. Servicio: eventos. Servicio o analista: score. Reputación: propia, o cualquiera si analista/servicio. Matriz probada en `Matriz_de_roles_por_endpoint...`.
* **Login**: mismo mensaje y mismo costo (hash ficticio) exista o no la llave; límite por llave (5/5 min) y por IP (30/5 min); escalada por score de ingreso (OTP / bloqueo temporal) sin token; PBKDF2 de Identity; claves máx. 128 caracteres.
* **Entrada**: FluentValidation en cada endpoint con cuerpo; enums por lista cerrada; longitudes máximas; 0/1 en señales; tope de 1.000 eventos por lote.
* **Auditoría**: cadena SHA-256 con secuencia única (no bifurca con réplicas), commit de grupo con espera por registro, tabla append-only para la app, llaves enmascaradas, sin claves ni tokens; fallos de commit descartan y propagan error (no se pierden en silencio). Prueba de concurrencia existente.
* **Regla crítica**: solo `ResolverAlerta` (rol analista, `fraude_confirmado` + `bloquear_cuenta`) llama `MarcarBloqueoDefinitivo`; el pipeline de scoring nunca lo hace (prueba que recorre todas las rutas).
* **Transporte y cabeceras**: `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, CSP `default-src 'none'`, `Cache-Control: no-store`, `Permissions-Policy`; HSTS (1 año) y redirección con `Seguridad:ForzarHttps` (true por defecto; el compose de demo lo desactiva porque TLS lo termina el ingress; hay compose TLS). Sin CORS (ningún origen de navegador autorizado). Sin Swagger. Errores 500 como ProblemDetails con id de correlación, sin traza; el id de correlación del cliente se sanea.
* **Contenedores**: usuario no root (`USER $APP_UID`), secretos por entorno (`${VAR:?}`), `.env`, `*.pfx` y `seed/` ignorados por git y por `.dockerignore`, Postgres/Redis sin puertos publicados.

## 3. Dependencias

* `dotnet list package --vulnerable --include-transitive`: **sin vulnerabilidades** (API y pruebas).
* `pip-audit` (en `training/.venv`): `pip` 25.2 tenía 12 avisos (herramienta, no código de la app); se actualizó pip en el venv y el resultado es **"No known vulnerabilities found"**.

## 4. Fairness / no discriminación

`departamento`, `rango_edad` y `tipo_cliente` (y etiquetas, `id_*`, `departamento_ip`) están en `CatalogoAtributos.Prohibidos` / `features.PROHIBIDOS`; ningún atributo del modelo desplegado (`training/artefactos/atributos.json`, 57) los contiene (prueba `Los_atributos_sensibles_no_entran...`, que además exige que el catálogo coincida con el artefacto). La política (`ContextoPolitica`) solo usa señales de la transacción y de la sesión. `Usuario.Departamento` se guarda pero no se lee en el scoring. Únicas señales geográficas: `ip_departamento_distinto` y, en el score de ingreso, "IP distinta a la habitual" (comparación con el propio historial del cliente, no con una categoría demográfica). `training/fairness.py` audita tasas por grupo solo para informe.

## 5. Mapeo normativo (resumen)

| Marco | Requisito | Control |
|-------|-----------|---------|
| Ley 1581 de 2012 (hábeas data) | Principios de finalidad, seguridad y confidencialidad; minimización | Datos sintéticos; auditoría con llaves enmascaradas y sin claves; claves con PBKDF2; no se guardan datos demográficos en el modelo; acceso a reputación por titular (anti-BOLA); explicaciones por decisión (razones legibles) |
| Ley 1266 de 2008 (información financiera/crediticia) | Calidad, actualización y derecho a corrección | La reputación es interna (no se reporta a centrales); toda decisión de bloqueo es humana (analista), con comentario y registro; falso positivo revierte el puntaje |
| PCI DSS | No se procesan ni almacenan PAN ni CVV; el alcance PCI no aplica | Esquema de transacción sin PAN; si se agregara, tokenizar. Controles afines ya presentes: cifrado en tránsito (TLS en ingress), contraseñas robustas, registro inalterable, mínimo privilegio |
| SFC (Circular Básica Jurídica, Parte I Tít. II cap. I / CE 007 de 2018 y ss.: seguridad y calidad de la información, ciberseguridad) | Trazabilidad, control de acceso por roles, segregación de funciones, no repudio, registro de eventos | Roles analista/servicio/cliente; bloqueo definitivo solo por analista; cadena de auditoría encadenada; límites de intentos; métricas internas; gestión de vulnerabilidades (auditorías de dependencias en esta sección) |

## 6. Pendientes recomendados antes de producción

HMAC/ancla externa de la cadena de auditoría; outbox transaccional; estado/limitador/bloqueos en Redis para varias réplicas; mTLS o credenciales OAuth para cuentas de servicio; revocación de tokens; subir PBKDF2 con re-hash; `AllowedHosts` explícito; WAF/límites en el ingress; reemplazar `EnsureCreated` por migraciones; escaneo de imágenes en CI.
