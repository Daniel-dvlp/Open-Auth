# Escudo Yuma

Escudo Yuma es un motor antifraude en tiempo real para **Yuma Pay**, billetera digital ficticia del caso de estudio de la hackatón Estud-IA (equipo Open-Auth). Califica cada operación **antes** de autorizarla y devuelve una decisión (`aprobar`, `desafiar`, `retener` o `rechazar`) con razones legibles.

Todos los datos del proyecto son sintéticos. Es un prototipo de hackatón: no está listo para producción sin los pendientes de la sección 11.

## 1. Problema y objetivo

El motor actual tiene unas 140 reglas estáticas que se ejecutan por lotes cada 15 minutos, **después** de que la transacción ya fue autorizada. En la muestra analizada, ese motor genera 89,0 % de falsos positivos y solo alerta el 62,6 % del fraude.

Escudo Yuma busca:

- calificar cada operación en línea, antes de autorizarla, con latencias de milisegundos;
- reducir los falsos positivos y aumentar la detección de fraude;
- explicar cada decisión con razones legibles;
- no usar departamento, rango de edad ni tipo de cliente para decidir.

## 2. Arquitectura

### Flujo de una decisión

```
App Yuma Pay
    |  evento de transaccion o de ingreso
    v
Orquestador (cuenta de servicio, rol "servicio")
    |  POST /v1/score/transaccion
    v
Escudo Yuma
    |  estado en linea -> 57 atributos -> modelo ONNX -> politica -> razones
    v
Decision: aprobar | desafiar | retener | rechazar
```

### Estructura del repositorio

```
escudo-yuma/
  src/
    EscudoYuma.Api/            API ASP.NET Core 8 (Minimal APIs)
      Features/                Un caso de uso por carpeta (vertical slice)
        Alertas/               ListarCola, ResolverAlerta
        Auth/                  Login, RegistrarDispositivo
        Cuentas/               ObtenerReputacion
        Eventos/               IngerirEventos
        Salud/                 Salud, health checks
        Sandbox/Replay/        Replay contra el sandbox
        Scoring/               EvaluarTransaccion
      Shared/                  Lo transversal: estado en linea, scoring, seguridad,
                               auditoria, persistencia, observabilidad, reglas
    EscudoYuma.Api.Tests/      Pruebas xUnit
  training/                    Entrenamiento en Python (XGBoost -> ONNX, SHAP, Fairlearn) y pytest
  load/                        Prueba de carga con k6
  ops/                         Prometheus, Grafana, sandbox y TLS de desarrollo
  docs/                        patrones.md y seguridad.md
  seed/                        Usuarios de prueba generados al primer arranque (ignorado por git)
  reports/                     Salidas del replay y de k6
  docker-compose.yml          Entorno local completo
  docker-compose.tls.yml      Capa opcional con TLS de desarrollo
  docker-compose.carga.yml    Capa con el perfil de carga
  EscudoYuma.sln
```

Agregar un caso de uso equivale a agregar una carpeta dentro de `Features/`. El código compartido vive en `Shared/`.

## 3. Los tres puntajes

| Puntaje | Dónde se calcula | Rango | Qué mide |
|---|---|---|---|
| Score de ingreso | `POST /v1/auth/login` | 0 a 1 | Riesgo del ingreso a la sesión: dispositivo no visto, intentos fallidos, cambio de SIM o de clave, registro reciente de dispositivo, IP de otro departamento, madrugada. No es atributo del modelo; escala la política de transacciones. |
| Score de transacción | `POST /v1/score/transaccion` | Probabilidad 0 a 1 | Modelo XGBoost exportado a ONNX sobre 57 atributos calculados en streaming, sin información del futuro, más las 12 reglas vigentes como señales 0/1. |
| Reputación de cuenta | `GET /v1/cuentas/{idCuenta}/reputacion` | 0 a 100 | Fan-in de víctimas, antigüedad de la cuenta y patrón de retiro tras recibir dinero. Sirve para discriminar cuentas mula. El endpoint devuelve el `puntaje` de riesgo (0 = confiable) y la `reputacion` (100 - puntaje). |

### Política de decisión

Los umbrales del modelo están en `training/artefactos/umbrales.json`: desafiar desde p = 0,461 y retener desde p = 0,977.

| Nivel interno | Decisión del sandbox | Condición |
|---|---|---|
| `aprobar` | `aprobar` | p < 0,461 y sin escalamiento por ingreso riesgoso |
| `advertir` | `desafiar` | Destino nuevo y monto mayor o igual al 80 % del saldo, sin señales de cuenta. El mensaje advierte sobre ingeniería social. |
| `verificar` | `desafiar` | Resto de los desafíos (0,461 <= p < 0,977). Se pide un OTP. |
| `retener` | `retener` | p >= 0,977. La operación queda en la cola del analista. |
| `rechazar` | `rechazar` | Solo para destino mula confirmado: fan-in del destino >= 3 y reputación del destino <= 30. También se rechaza toda cuenta con bloqueo definitivo. |

Reglas adicionales:

- **Piso de reglas de seguridad**, independiente del modelo: dispositivo nuevo con cambio de SIM o de clave, vaciado de saldo hacia un destino nuevo, y destino de reputación baja con fan-in alto.
- **Escalamiento por ingreso**: si el score de ingreso es mayor o igual a 0,40 y la decisión era `aprobar` con p >= 0,2, la decisión sube a `verificar`. Nunca baja una decisión.
- **Imputación sin feed de eventos**: si falta el feed de eventos de la app, los atributos se imputan desde las banderas de la propia transacción. Así no se aprueba por defecto cuando el feed se cae.
- **Bloqueo definitivo**: solo lo puede hacer un analista mediante `ResolverAlerta`. El pipeline de scoring únicamente respeta ese bloqueo.
- **Razones**: se calcula una aproximación aditiva de SHAP con el top 3 de contribuciones, con plantillas que incluyen valores concretos. Siempre hay al menos una razón.

## 4. Patrones y ciclo de vida

El detalle está en `docs/patrones.md`. Los principales:

- **Singleton**: sesión ONNX (se calienta al arrancar), política y umbrales, catálogo de reglas, estado en línea, conexión a Redis, limitador de intentos, cadena de auditoría y cola de reputación.
- **Scoped**: `DbContext`, manejadores de casos de uso y validadores. Cada request tiene su unidad de trabajo.
- **Factory**: `IModeloRiesgoFactory` entrega y cachea los modelos por tipo y versión.
- **Strategy**: cada regla vigente es una implementación de `IRegla` registrada en el contenedor.
- **Adapter**: `SandboxClient` traduce el contrato del sandbox al dominio, y `OnnxModeloAdapter` traduce los atributos al tensor de entrada del modelo.
- **Pipeline**: estado -> modelo -> política -> razones -> reputación (cola asíncrona) -> auditoría -> alerta.
- **Transversales**: correlación (`X-Correlation-Id`), errores como `ProblemDetails`, validación con FluentValidation, JWT, limitación de intentos, métricas Prometheus y auditoría con cadena de hashes SHA-256.

## 5. Endpoints

Las rutas protegidas exigen el encabezado `Authorization: Bearer <token>`. El token dura 15 minutos y contiene los claims `sub`, `sid`, `rol` y `score_ingreso`.

| Método | Ruta | Rol | Descripción |
|---|---|---|---|
| POST | `/v1/auth/login` | Anónimo, con límite de intentos | Valida llave y clave. Devuelve `resultado` (`permitir`, `otp` o `bloqueo_temporal`), `score_ingreso`, `razones` y, si se permite, un token. |
| POST | `/v1/auth/dispositivos` | Autenticado | Registra un dispositivo conocido del cliente (máximo 20 por cliente). |
| POST | `/v1/score/transaccion` | `servicio` o `analista` | Califica una transacción antes de autorizarla. |
| POST | `/v1/eventos` | `servicio` | Ingesta lotes de 1 a 1.000 eventos de la app (inicio de sesión, registro de dispositivo, cambio de clave, cambio de SIM). |
| GET | `/v1/cuentas/{idCuenta}/reputacion` | Cuenta propia; `analista` o `servicio` cualquier cuenta | Puntaje de riesgo, reputación 0-100, señales y bandera de bloqueo definitivo. |
| GET | `/v1/alertas` | `analista` | Cola de alertas ordenada por puntaje x monto. Acepta `estado` (`pendiente` por defecto o `resuelta`) y `limite` (1 a 200). |
| POST | `/v1/alertas/{idAlerta}/resolver` | `analista` | Resuelve una alerta. Es el único camino que puede bloquear una cuenta de forma definitiva. |
| POST | `/v1/sandbox/replay` | `analista`, con `Sandbox:ReplayHabilitado=true` | Inicia el replay contra el sandbox. Responde 202. |
| GET | `/v1/sandbox/replay` | `analista` | Consulta el avance del replay. |
| GET | `/v1/salud` | Anónimo | Estado del servicio. |
| GET | `/health/live` | Anónimo | Liveness. |
| GET | `/health/ready` | Anónimo | Readiness: verifica PostgreSQL y Redis. |
| GET | `/metrics` | Puerto interno 9091 | Métricas de Prometheus. No se publica. |

### Ejemplo: login

Petición (valores ficticios):

```json
{
  "llave": "usuario-demo-001",
  "clave": "clave-ficticia",
  "id_dispositivo": "DISP-0001",
  "departamento_ip": "Antioquia"
}
```

Respuesta (ilustrativa):

```json
{
  "resultado": "permitir",
  "score_ingreso": 0.0,
  "razones": ["Sin senales de riesgo en el ingreso"],
  "token": "eyJhbGciOi...",
  "expira_utc": "2026-11-24T15:15:00Z"
}
```

### Ejemplo: score de transacción

Petición (valores ficticios):

```json
{
  "id_transaccion": "TX-EJEMPLO-000001",
  "fecha_hora": "2026-10-15 22:41:07",
  "id_cliente": "CLI-EJEMPLO-01",
  "tipo_operacion": "transferencia_inmediata",
  "canal": "app",
  "monto_cop": 850000,
  "saldo_antes_cop": 900000,
  "id_destino": "DEST-EJEMPLO-77",
  "tipo_destino": "cuenta_yuma",
  "destino_nuevo": 1,
  "id_dispositivo": "DISP-0001",
  "dispositivo_nuevo": 0,
  "cambio_clave_24h": 0,
  "cambio_sim_48h": 0,
  "departamento_ip": "Antioquia",
  "ip_departamento_distinto": 0,
  "antiguedad_cuenta_dias": 420
}
```

Respuesta (ilustrativa):

```json
{
  "decision": "desafiar",
  "decision_interna": "advertir",
  "puntaje": 0.512345,
  "razones": [
    "Advertencia: confirme con el beneficiario por otro canal antes de continuar; las estafas suelen pedir vaciar el saldo hacia una cuenta nueva."
  ],
  "latencia_ms": 2.815,
  "version_modelo": "xgb-onnx-1",
  "latencia_total_ms": 3.102
}
```

Los valores de `tipo_operacion` son `transferencia_inmediata`, `pago_qr`, `retiro_corresponsal`, `retiro_cajero`, `pago_servicios`, `recarga_celular` y `compra_tarjeta_virtual`. El `canal` admite `app`, `corresponsal` o `cajero`. Las banderas (`0` o `1`) no admiten otros valores.

## 6. Requisitos

- SDK de .NET 8.0.
- Docker Desktop con Docker Compose v2.
- Python 3.13 para entrenar el modelo, ejecutar las pruebas de Python y correr los scripts de `training/`.
- PowerShell 7 (los comandos de esta guía están escritos para PowerShell en Windows).
- Carpeta de datos del caso, **no incluida en este repositorio**, en `..\Caso_de_estudio_Yuma_Pay` (carpeta hermana), con `03_Datos` y `04_Sandbox_API`. Los CSV se esperan en `03_Datos`. Para las pruebas de paridad se puede usar la variable `YUMA_DATOS` con la ruta a esa carpeta; si no hay datos, esas pruebas se saltan.

## 7. Cómo ejecutar

Todos los comandos se ejecutan desde la raíz del repositorio salvo que se indique otra carpeta.

### 7.1 Configurar el archivo .env

```powershell
cd C:\Users\Daniel\Documents\Yuma_Pay\escudo-yuma
Copy-Item .env.example .env
```

Complete estas variables en `.env` (está ignorado por git; no lo versione):

- `JWT_CLAVE`: al menos 32 caracteres.
- `POSTGRES_PASSWORD`.
- `REDIS_PASSWORD`: obligatoria. Compose rechaza un valor vacío.
- `GRAFANA_PASSWORD`.

Para generar un secreto aleatorio de 48 caracteres:

```powershell
-join ((48..57) + (65..90) + (97..122) | Get-Random -Count 48 | ForEach-Object { [char]$_ })
```

### 7.2 Levantar todo con Docker Compose

```powershell
docker compose up -d --build
docker compose ps
```

Esto levanta la API, PostgreSQL, Redis, el sandbox, Prometheus y Grafana. Para verificar que la API está lista:

```powershell
Invoke-RestMethod http://localhost:8080/health/ready
```

| Servicio | URL o puerto | Nota |
|---|---|---|
| API | `http://localhost:8080` | HTTP. En el compose de demo `ForzarHttps` está desactivado. |
| API con TLS | `https://localhost:8443` | Solo con la capa TLS (sección 7.8). |
| Sandbox | `http://127.0.0.1:8081` | Solo escucha en localhost. |
| Prometheus | `http://localhost:9090` | Solo localhost. |
| Grafana | `http://localhost:3000` | Usuario `admin` y la clave `GRAFANA_PASSWORD`. Dashboard "Escudo Yuma - Operación". |
| Métricas | Puerto 9091, interno | No publicado; solo lo raspa Prometheus dentro de la red de compose. |

PostgreSQL y Redis no publican puertos. Los usuarios de prueba se crean en `seed/usuarios_prueba.json` en el primer arranque.

Para detener el entorno:

```powershell
docker compose down
```

`docker compose down -v` borra también el volumen de la base de desarrollo. El esquema se crea con `EnsureCreated` y no migra: si cambia el modelo de datos, hay que recrear el volumen.

### 7.3 Ejecutar la API localmente sin Docker

```powershell
cd C:\Users\Daniel\Documents\Yuma_Pay\escudo-yuma\src\EscudoYuma.Api
$env:Persistencia__Proveedor = "memoria"
$env:Jwt__Clave = "<clave de al menos 32 caracteres>"
$env:Seguridad__ForzarHttps = "false"
dotnet run -c Release
```

Las rutas relativas de la configuración se resuelven desde el directorio de contenido (`src/EscudoYuma.Api`). Revise la URL que imprime `dotnet run`. Fuera de Development la clave de JWT es obligatoria.

### 7.4 Entrenar el modelo

Las dependencias con versión fija están en `training/requirements.txt`.

```powershell
cd C:\Users\Daniel\Documents\Yuma_Pay\escudo-yuma\training
py -3.13 -m venv .venv
.\.venv\Scripts\python.exe -m pip install --upgrade pip
.\.venv\Scripts\python.exe -m pip install -r requirements.txt

$env:YUMA_DATOS = "C:\Users\Daniel\Documents\Yuma_Pay\Caso_de_estudio_Yuma_Pay\03_Datos"
.\.venv\Scripts\python.exe train.py
.\.venv\Scripts\python.exe evaluar.py
.\.venv\Scripts\python.exe fairness.py
```

- `train.py` calcula los atributos en streaming, entrena XGBoost, exporta a ONNX, selecciona umbrales, verifica la paridad y genera la explicación para .NET. Las dos primeras semanas (1 al 14 de mayo) solo calientan el estado y no se entrenan.
- `evaluar.py` compara la línea base contra el modelo con la política en septiembre y octubre, y escribe `artefactos/reporte_validacion.json`.
- `fairness.py` audita las tasas por grupo y escribe `artefactos/fairness.json`. Es solo para informe.

Los artefactos quedan en `training/artefactos/`: `modelo_tx.onnx`, `modelo_tx.json`, `atributos.json`, `umbrales.json`, `paridad.csv`, `explicacion.json`, `reporte_validacion.json` y `fairness.json`.

### 7.5 Ejecutar las pruebas

```powershell
cd C:\Users\Daniel\Documents\Yuma_Pay\escudo-yuma
dotnet test EscudoYuma.sln

cd training
.\.venv\Scripts\python.exe -m pytest tests -q
```

Las pruebas de paridad de atributos entre Python y .NET necesitan `YUMA_DATOS`; si no está configurada, se saltan.

### 7.6 Levantar el sandbox y ejecutar el replay

El replay envía las 120.972 transacciones al sandbox, las califica con el mismo pipeline del endpoint y guarda las métricas en `reports/replay_metricas.json`. Reinicia el estado en línea, por eso está deshabilitado por defecto.

Terminal 1, desde la raíz del repositorio:

```powershell
.\training\.venv\Scripts\python.exe ..\Caso_de_estudio_Yuma_Pay\04_Sandbox_API\mock_api.py --puerto 8090
```

Terminal 2:

```powershell
cd C:\Users\Daniel\Documents\Yuma_Pay\escudo-yuma\src\EscudoYuma.Api
$env:Sandbox__UrlBase = "http://127.0.0.1:8090"
$env:Sandbox__ReplayHabilitado = "true"
$env:Sandbox__EjecutarAlIniciar = "true"
$env:Persistencia__Proveedor = "memoria"
$env:Jwt__Clave = "<clave de al menos 32 caracteres>"
$env:Seguridad__ForzarHttps = "false"
dotnet run -c Release
```

Con `EjecutarAlIniciar=true` la aplicación termina al acabar el replay. Sin esa bandera, el replay también se puede lanzar con `POST /v1/sandbox/replay` usando un token de analista, y su avance se consulta con `GET /v1/sandbox/replay`.

### 7.7 Prueba de carga con k6

La prueba envía 150 peticiones por segundo durante 60 segundos, seguidas de un pico de 300 por segundo durante 15 segundos. La mezcla es 90 % de scoring y 10 % de login. Requiere el perfil de carga, que **nunca debe usarse en producción**.

```powershell
cd C:\Users\Daniel\Documents\Yuma_Pay\escudo-yuma
docker compose -f docker-compose.yml -f docker-compose.tls.yml -f docker-compose.carga.yml up -d --build api

training\.venv\Scripts\python.exe load\preparar_datos.py --seed seed\usuarios_carga_seed.json

docker run --rm --network escudo-yuma_default -v "${PWD}/load:/load:ro" -v "${PWD}/reports:/reports" -e BASE_URL=http://api:8080 grafana/k6:0.54.0 run /load/k6.js
```

- El perfil de carga usa una base aparte (`escudo_yuma_carga`) y un costo de hash menor (10.000 iteraciones de PBKDF2). Así no se altera la base de desarrollo.
- El nombre de la red (`escudo-yuma_default`) depende del nombre de la carpeta del proyecto.
- Las llaves de login se reparten entre 1.000 clientes. No vuelva a lanzar la prueba antes de 5 minutos, o las llaves agotan su cupo.
- El resumen se guarda en `reports/k6_resumen.json`. Los umbrales son p99 menor que 300 ms y tasa de error menor que 1 %.

### 7.8 Activar TLS de desarrollo

El certificado es autofirmado y solo sirve para desarrollo.

```powershell
cd C:\Users\Daniel\Documents\Yuma_Pay\escudo-yuma
dotnet dev-certs https -ep ops\tls\escudo-dev.pfx -p "<clave aleatoria>"
```

Luego, en `.env`:

1. Asigne `TLS_PFX_PASSWORD` con la misma clave.
2. Descomente `COMPOSE_PATH_SEPARATOR=:` y `COMPOSE_FILE=docker-compose.yml:docker-compose.tls.yml`.

```powershell
docker compose up -d --build
curl.exe -k https://localhost:8443/v1/salud
```

El puerto 8080 HTTP se mantiene para que Prometheus pueda raspar las métricas dentro de la red. En producción, el certificado real va en el ingress y se usa `Seguridad__ForzarHttps=true`.

## 8. Resultados

Todas las cifras corresponden a los datos sintéticos del caso de estudio.

### 8.1 Replay contra el sandbox oficial (120.972 transacciones)

| Indicador | Resultado |
|---|---|
| Fraude detectado | 94,66 % |
| Monto protegido | 97,34 % |
| Falsos positivos | 0,06 % |
| Legítimos bloqueados | 0,0 % |
| Desafíos | 721 |
| Latencia medida por el sandbox (entrega a decisión), p50 | 2,8 ms |
| Latencia medida por el sandbox, p99 | 11,6 ms |

### 8.2 Validación septiembre a octubre (meses no vistos al entrenar)

| Indicador | Línea base | Escudo Yuma |
|---|---|---|
| Falsos positivos (entre bloqueadas) | 91,7 % | 0 % |
| Detección de casos | 56,5 % | 90,7 % |
| Detección por monto | 80,1 % | 93,7 % |
| Toma de cuenta | 58,6 % | 98,7 % |
| Sim swapping | 86,0 % | 98,2 % |
| Ingeniería social | 12,7 % | 75,9 % |
| Engaño por QR | 30,0 % | 42,5 % |
| Cuenta mula | 86,1 % | 100 % |
| Legítimas desafiadas | n/a | 0,37 % |
| Legítimas retenidas | n/a | 0 % |

Con el feed de eventos de la app caído, la detección se mantiene en 90,5 % y los falsos positivos en 0 %.

### 8.3 Prueba de carga (k6)

Perfil: 150 it/s durante 60 s, pico de 300 it/s durante 15 s, mezcla 90 % scoring y 10 % login. Corre con el perfil `docker-compose.carga.yml`.

| Indicador | Resultado |
|---|---|
| Latencia p50 | 7,1 ms |
| Latencia p95 | 22,1 ms |
| Latencia p99 | 42,8 ms |
| Errores | 0 % |

Con el costo de hash de producción (100.000 iteraciones), la fase sostenida pasa, pero el pico de login supera 300 ms por la CPU del hash. Es un límite del hash, no de la API.

### 8.4 Pruebas

- 141 pruebas xUnit en verde.
- 13 pruebas pytest en verde.
- Cubren, entre otras cosas, que no haya fuga de información futura, la paridad de atributos entre Python y .NET y la paridad de la inferencia ONNX.

## 9. Seguridad y cumplimiento

El detalle está en `docs/seguridad.md`. Resumen:

- **Autenticación**: JWT HS256 con emisor, audiencia y vencimiento validados, y vigencia de 15 minutos. Fuera de Development se exige una clave de al menos 32 caracteres.
- **Autorización**: políticas por rol (`cliente`, `analista`, `servicio`). Un rol desconocido solo ve su propia cuenta.
- **Login**: mismo mensaje y mismo costo de CPU exista o no la llave. Límites de 5 intentos por llave y 30 por IP en 5 minutos.
- **Entrada**: validación en cada endpoint con FluentValidation, tope de 1 MiB por cuerpo y listas cerradas para los valores.
- **Auditoría**: bitácora append-only con cadena de hashes SHA-256 y llaves enmascaradas. No se guardan claves ni tokens.
- **Bloqueo definitivo**: solo un analista puede aplicarlo, mediante `ResolverAlerta`.
- **Contenedores**: usuario no root, secretos por variables de entorno, `no-new-privileges` y `cap_drop: ALL` en la API. Postgres y Redis no publican puertos.
- **Dependencias**: sin vulnerabilidades conocidas en `dotnet list package --vulnerable` y en `pip-audit`.
- **Hallazgos**: se corrigieron 15 hallazgos (H1 a H15) y se aceptaron 8 riesgos (A1 a A8), con su justificación en `docs/seguridad.md`.

### Marco normativo (resumen)

- **Ley 1581 de 2012 (hábeas data)**: finalidad, seguridad, confidencialidad y minimización. El modelo no usa datos demográficos.
- **Ley 1266 de 2008 (información financiera)**: la reputación es interna y no se reporta a centrales. Toda decisión de bloqueo es humana y queda registrada.
- **SFC (Circular Básica Jurídica, ciberseguridad)**: trazabilidad, control de acceso por roles, segregación de funciones y registro de eventos.
- **PCI DSS**: el sistema no procesa ni almacena PAN ni CVV, por lo que el alcance PCI no aplica. Si se agregara, se tokenizaría.

## 10. Fairness

Los atributos `departamento`, `rango_edad` y `tipo_cliente` están prohibidos para el modelo y se verifican con una prueba. La política solo usa señales de la transacción y de la sesión. La auditoría de fairness está en `training/artefactos/fairness.json`.

## 11. Limitaciones y próximos pasos

### Limitaciones

- **Datos sintéticos muy separables** (AUC 0,9998). El 0 % de falsos positivos en validación no se extrapola a producción. Con cero falsos positivos observados, el intervalo de confianza del 95 % para legítimas retenidas llega a unos 0,7 %.
- **Umbrales elegidos y evaluados en el mismo periodo**. Verificados fuera de muestra (umbral elegido con septiembre, evaluado en octubre): 0 % de falsos positivos y 89,3 % de detección. El engaño por QR cae a 25 % frente a 37,5 % de la línea base, con solo 8 casos.
- **Engaño por QR** es la modalidad más débil del modelo.
- **Equidad**: algunas razones de tasa de desafío superan 1,25 en grupos pequeños. Hay que revisarlas antes de producción.
- **Explicaciones**: el SHAP para .NET es una aproximación por tablas. El top 1 cae dentro del top 3 aproximado en el 78 % de los casos.
- **Arranque en frío**: en producción el estado en línea debe precargarse con al menos 14 días de historia.
- **Una sola réplica**: el estado en línea, el limitador y los bloqueos viven en memoria del proceso.
- **Esquema con `EnsureCreated`**: no hay migraciones de EF Core.

### Próximos pasos antes de producción

- Ancla externa o HMAC con clave en KMS para la cadena de auditoría, y outbox transaccional.
- Estado en línea, limitador y bloqueos en Redis, para varias réplicas.
- mTLS o credenciales OAuth para las cuentas de servicio, y revocación de tokens por `jti` o `sid`.
- Subir el costo de PBKDF2 con re-hash en el siguiente login.
- Fijar `AllowedHosts`, limitar `/health/ready` en el ingress y agregar WAF y límites.
- Reemplazar `EnsureCreated` por migraciones, y agregar escaneo de imágenes en CI.
