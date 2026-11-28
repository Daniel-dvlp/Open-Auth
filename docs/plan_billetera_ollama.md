# Plan de trabajo — Billetera Yuma + Expediente narrado con Ollama

> Alcance: **solo backend** (lo construimos nosotros). El frontend de la billetera lo hace el equipo de front a partir del contrato de la sección 6.
> Regla: seguir la arquitectura existente (`docs/patrones.md`): vertical slice, `IFeature`, FluentValidation, ProblemDetails, auditoría encadenada, métricas Prometheus, pruebas con `WebApplicationFactory`.

## 0. Decisiones confirmadas (2026-11-27)

| Tema | Decisión |
|---|---|
| Fuente de usuarios | **Solo los CSV del caso**: `clientes.csv` (6.000) y `transacciones.csv`. No hay otro PDF de usuarios. |
| Riesgo medio (`desafiar`) | **Biometría facial simulada** en la app (diagrama oficial "Flujo de autorización"). Si no la supera → caso para el analista. Con cambio de SIM en las 48 h previas **no se acepta OTP** como verificación. |
| Saldo inicial | **Último saldo real** de cada cliente en `transacciones.csv` (`saldo_antes_cop` de su última operación menos el monto). |
| Contexto | Billetera 100 % colombiana: COP enteros, hora de Colombia (UTC-5), departamentos de `clientes.csv`, llave = celular sintético `3` + número del cliente en 9 dígitos (mismo esquema del seed del Escudo), Ley 1581/2012 y Ley 1266/2008 (sin PII en logs ni prompts). |

## 1. Arquitectura objetivo

```
 App billetera (front compañero)
        │  JWT rol=cliente
        ▼
 ┌────────────────────────┐   HTTP + JWT rol=servicio    ┌───────────────────────────┐
 │  YumaBilletera.Api     │ ───────────────────────────▶ │  EscudoYuma.Api (Open-Auth)│
 │  (nuevo proyecto)      │  POST /v1/score/transaccion   │  scoring ONNX, alertas,    │
 │  cuentas, ledger,      │  POST /v1/eventos             │  reputación, auditoría     │
 │  transferencias, OTP   │  GET  /v1/transacciones/{id}  │  + Expediente (nuevo)      │
 └──────────┬─────────────┘    /decision (nuevo)          └──────────┬────────────────┘
            │ Postgres db=yuma_billetera                             │ Postgres db=escudo_yuma
            │                                                        ▼
            │                                               Ollama (qwen2.5:7b) → narrativas
```

**Decisión clave:** la billetera es un **servicio aparte** (`src/YumaBilletera.Api`) en la misma solución, con la misma arquitectura de slices. Motivos: así es un banco real (el core transaccional no comparte proceso con el antifraude), el Escudo no cambia de responsabilidad y el contrato entre ambos ya existe (`/v1/score/transaccion`, `/v1/eventos`). Comparte `Postgres` (otra base) y la red de compose.

**Regla de oro del dinero:** ningún débito se confirma sin decisión del Escudo. Si el Escudo no responde → **fail-closed** (la transferencia queda `en_revision`, no se aprueba por defecto).

## 2. Fases

### Fase 0 — Base del repo (0,5 día)
- [ ] Agregar `.gitignore`, `.dockerignore`, `.env.example` y `ops/tls` (faltan en Open-Auth; `seed/usuarios_prueba.json` NO debe subirse).
- [ ] Crear `src/YumaBilletera.Api` + `src/YumaBilletera.Api.Tests`, añadirlos a `EscudoYuma.sln`.
- [ ] Mover `IFeature`/`AddFeatures`/`MapFeatures`, `ValidacionFiltro`, `ManejadorErrores`, correlación y cabeceras seguras a un proyecto común `src/Yuma.Comun` (lo usan ambas APIs). Sin cambiar comportamiento: los 141 tests del Escudo deben seguir verdes.

### Fase 1 — Núcleo contable de la billetera (2 días)
Modelo de **doble partida** (cada movimiento = débito + crédito, la suma siempre 0). El saldo nunca se edita a mano: se deriva del ledger y se cachea en `Cuenta.SaldoCop` dentro de la misma transacción de BD.

| Entidad | Campos clave |
|---|---|
| `Cliente` | id_cliente (cruza con `clientes.csv`), llave, hash_clave, departamento, fecha_vinculacion, estado |
| `Cuenta` | id_cuenta, id_cliente, saldo_cop, estado (`activa`/`congelada`/`bloqueada`), `xmin` (concurrencia optimista) |
| `Movimiento` | id, id_transferencia, id_cuenta, tipo (`debito`/`credito`), monto_cop, saldo_despues, fecha_utc |
| `Transferencia` | id_transaccion (mismo id que viaja al Escudo), origen, destino, tipo_destino, monto, estado, decision_escudo, puntaje, razones, clave_idempotencia |
| `Destinatario` | inscritos por cliente; `primera_vez_utc` (alimenta `destino_nuevo`) |
| `DesafioOtp` | id, id_transferencia, hash_codigo, intentos, expira_utc |
| `Dispositivo` / `SesionCliente` | id_dispositivo, sistema_operativo, registrado_utc, ultimo_ip_departamento |

- [ ] Seed: una cuenta por cliente de `clientes.csv` con saldo inicial sintético; cuentas `EXT*`/`COM*` como contrapartes externas.
- [ ] Pruebas: invariante de doble partida, concurrencia (dos débitos simultáneos no dejan saldo negativo), idempotencia.

### Fase 2 — Casos de uso del cliente (3 días)
Cada uno es un slice en `Features/<Area>/<Caso>/`.

| Slice | Qué hace | Evento enviado al Escudo |
|---|---|---|
| `Auth/Login` | Valida llave+clave; consulta al Escudo el score de ingreso | `inicio_sesion` (exitoso/fallido) |
| `Auth/VerificarOtp` | Cierra el login cuando el Escudo pidió OTP | — |
| `Dispositivos/Registrar` | Registra dispositivo | `registro_dispositivo` |
| `Seguridad/CambiarClave` | Cambio de clave | `cambio_clave` |
| `Cuentas/ConsultarSaldo` | Saldo y estado | — |
| `Cuentas/ListarMovimientos` | Historial paginado (cursor) | — |
| `Destinatarios/Inscribir`, `Listar` | Libreta de destinos | — |
| `Transferencias/Crear` | **Flujo de la sección 3** | `score/transaccion` |
| `Transferencias/ConfirmarOtp` | Completa una transferencia `desafiar` | — |
| `Transferencias/Consultar` | Estado de una transferencia | — |
| `Pagos/PagarQr`, `Pagos/Servicios`, `Recargas/Celular` | Mismo pipeline con otro `tipo_operacion` | `score/transaccion` |

### Fase 3 — Integración con el Escudo (2 días)
- [ ] `IEscudoCliente` (Adapter, typed `HttpClient`, Polly: timeout 300 ms, 1 reintento idempotente, circuit breaker). Token de rol `servicio` renovado antes de sus 15 min.
- [ ] `ConstructorTransaccionEscudo`: arma el esquema `Transaccion` del openapi con datos **reales** del ledger: `saldo_antes_cop`, `destino_nuevo` (180 días), `dispositivo_nuevo` (<24 h), `cambio_clave_24h`, `cambio_sim_48h` (puerto `IOperadorSim`, simulado), `ip_departamento_distinto`, `antiguedad_cuenta_dias`.
- [ ] `PublicadorEventos`: Channel acotado + BackgroundService que envía lotes a `POST /v1/eventos` (outbox en BD para no perder eventos si el Escudo cae).
- [ ] **En el Escudo** — slice nuevo `Transacciones/ConsultarDecision` y webhook/cola de vuelta: cuando un analista resuelve una alerta (`falso_positivo` → liberar; `fraude_confirmado` → revertir/bloquear), la billetera se entera (`POST {billetera}/v1/internas/resoluciones`, firmado con HMAC).
- [ ] Prueba de contrato: la billetera y el Escudo comparten el mismo fixture JSON de `Transaccion`.

### Fase 4 — Expediente estructurado + Ollama (3 días)
El LLM **nunca decide** ni está en el camino crítico de una transacción. Solo **narra** lo que el motor ya decidió, a partir de un expediente ordenado y verificable.

**4.1 Captura ordenada (Escudo, `Shared/Expediente`)**
Todo hecho relevante se registra como `HechoExpediente` con esquema fijo:

| Campo | Ejemplo |
|---|---|
| tipo | `transaccion`, `evento_app`, `decision`, `regla_activada`, `alerta`, `resolucion`, `cambio_configuracion`, `alteracion_auditoria` |
| fecha_utc, id_cliente, id_transaccion, id_correlacion | — |
| fuente | `billetera`, `escudo`, `analista`, `sistema` |
| datos | JSON tipado (monto, destino, señales 0/1, puntaje, top-3 SHAP con valor) |
| normas | códigos que aplican: reglas R001–R012 (`reglas_vigentes.csv`), umbral de política, regla "bloqueo solo por analista" |
| hash_auditoria | eslabón de la cadena SHA-256 que lo respalda |

- **Alteraciones**: tarea periódica `VerificadorCadena` corre `CadenaHash.Verificar`; si encuentra un eslabón roto genera un hecho `alteracion_auditoria` y alerta crítica.
- **Cambios de configuración/umbral**: se registran como hecho al arrancar (versión de modelo, hash de `umbrales.json`).
- **Logs**: `ILogger` con JSON estructurado (`AddJsonConsole`) + `id_correlacion` en todo; los hechos del expediente salen de los manejadores, no se parsean logs.

**4.2 Narrativa**
- `INarrador` (puerto) → `OllamaNarrador` (adapter, `POST /api/chat`, `format: json`, `temperature: 0.1`, modelo configurable `qwen2.5:7b-instruct`; alternativa liviana `llama3.2:3b`).
- `ConstructorPrompt`: plantilla versionada (`prompts/expediente_v1.md`) + expediente serializado. **Sin PII**: llaves enmascaradas, sin nombres.
- Salida validada contra esquema: `{resumen, linea_de_tiempo[], factores[], normas_aplicadas[], recomendacion_al_analista, confianza}`. Si el JSON no valida o cita datos que no están en el expediente → se descarta y se usa la **narrativa por plantilla** (determinista, ya existe la base en `ExplicadorRazones`).
- `ColaNarrativas` (Channel) + `GeneradorNarrativas` (BackgroundService): al crear una alerta se encola; resultado guardado en `NarrativaAlerta` (texto, modelo, versión de prompt, hash del expediente, latencia).
- Endpoints (rol `analista`):
  - `GET /v1/alertas/{id}/expediente` → hechos ordenados.
  - `GET /v1/alertas/{id}/narrativa` → narrativa (o `202` si está en cola).
  - `POST /v1/alertas/{id}/narrativa/regenerar`.
  - `GET /v1/clientes/{id}/resumen?dias=30` → resumen narrado de la actividad.
- Métricas: `yuma_narrativa_latencia_segundos`, `yuma_narrativa_fallback_total`.
- Pruebas: `FakeNarrador`; prueba de que el texto no contiene PII; prueba de fallback si Ollama cae.

### Fase 5 — Docker, observabilidad y entrega (1,5 días)
- [ ] `docker-compose.yml`: servicios `billetera` (puerto 8082), `ollama` (`ollama/ollama`, volumen `ollama_data`, sin puerto público) y `ollama-init` que hace `ollama pull qwen2.5:7b-instruct`. Perfil `gpu` opcional.
- [ ] Panel Grafana: transferencias por decisión, latencia billetera→Escudo, narrativas y fallbacks.
- [ ] `README` + colección `.http` con el recorrido completo.
- [ ] OpenAPI publicado por la billetera (`/openapi/v1.json`) para el front.

## 3. Flujo de una transferencia

1. `POST /v1/transferencias` con `Idempotency-Key`. Validación (monto > 0, ≤ saldo, destino válido, límites diarios).
2. Se crea `Transferencia` en estado `evaluando` y se **reserva** el monto (retención de saldo).
3. `ConstructorTransaccionEscudo` → `POST /v1/score/transaccion`.
4. Según la decisión:

| Decisión del Escudo | Billetera | Estado al cliente |
|---|---|---|
| `aprobar` | Asienta débito/crédito, libera la reserva | `completada` |
| `desafiar` (verificar/advertir) | Crea `DesafioOtp`; con *advertir* muestra el mensaje anti-ingeniería social | `requiere_otp` |
| `retener` | Mantiene la reserva; alerta para el analista | `en_revision` |
| `rechazar` | Libera la reserva | `rechazada` + razón amigable |
| Escudo caído / timeout | Mantiene la reserva | `en_revision` (fail-closed) |

5. La resolución del analista llega por el webhook firmado y completa o reversa la transferencia.
6. Todo paso queda en la auditoría de la billetera (misma cadena hash) y como hecho del expediente.

## 4. Seguridad (mínimos no negociables)
- JWT de cliente 15 min + refresh rotativo; el cliente solo ve **sus** cuentas (prueba que recorre todas las rutas, como ya hace el Escudo).
- OTP: 6 dígitos, hash, 5 min, 3 intentos; en local se entrega por un buzón simulado (`GET /v1/dev/otp`, solo Development).
- Montos en `long` COP; nada de `double` en dinero.
- Rate limit por cliente en transferencias; idempotencia obligatoria.
- Comunicación entre servicios: JWT de servicio + HMAC en webhooks.

## 5. Pruebas (criterio de "terminado")
- Unitarias por slice + integración con `WebApplicationFactory` para ambas APIs con `FakeEscudo`/`FakeNarrador`.
- E2E en compose: login → transferencia aprobada → transferencia retenida → analista resuelve → saldo final correcto.
- Los 141 tests actuales del Escudo y los 13 de Python siguen verdes.

## 6. Contrato para el frontend (entregable al compañero)

Base `http://localhost:8082`, JSON `snake_case`, errores `ProblemDetails`, `Authorization: Bearer`.

| Método | Ruta | Uso en pantalla |
|---|---|---|
| POST | `/v1/auth/login` | Login → `permitir` (token) / `otp` / `bloqueo_temporal` |
| POST | `/v1/auth/otp` | Completar login con OTP |
| POST | `/v1/auth/refresh` | Renovar sesión |
| GET | `/v1/cuentas/me` | Saldo, estado, número de cuenta |
| GET | `/v1/cuentas/me/movimientos?cursor=&limite=` | Historial |
| GET/POST | `/v1/destinatarios` | Libreta |
| POST | `/v1/transferencias` (`Idempotency-Key`) | Transferir → estado + mensaje |
| POST | `/v1/transferencias/{id}/otp` | Confirmar con OTP |
| GET | `/v1/transferencias/{id}` | Seguimiento (`en_revision` → `completada`/`rechazada`) |
| POST | `/v1/pagos/qr`, `/v1/pagos/servicios`, `/v1/recargas` | Pagos |
| GET/POST | `/v1/dispositivos` | Mis dispositivos |
| POST | `/v1/seguridad/clave` | Cambiar clave |

Para la consola de analista (front actual): `GET /v1/alertas/{id}/expediente` y `GET /v1/alertas/{id}/narrativa` en el Escudo.

## 7. Cronograma estimado (~12 días hábiles)

| Semana | Entrega |
|---|---|
| 1 | Fase 0, 1 y 2 → billetera funcional con `FakeEscudo`; se entrega OpenAPI al front |
| 2 | Fase 3 y 4 → integración real con el Escudo + expediente y narrativas |
| 3 (½) | Fase 5 → compose completo, Grafana, README, demo E2E |

## 8. Riesgos
- **Ollama sin GPU**: 7B tarda 5–20 s por narrativa en CPU → por eso es asíncrono y cacheado; `llama3.2:3b` como respaldo.
- **Alucinaciones**: salida JSON validada + verificación de que cada cifra citada existe en el expediente + fallback por plantilla.
- **Estado en memoria del Escudo** (una réplica): fuera de alcance; se documenta.

## 9. Estado de ejecución (2026-11-27)

| Fase | Estado | Evidencia |
|---|---|---|
| 0 — Base del repo | Hecha | `.gitignore`/`.dockerignore`/`.env.example`; `src/Yuma.Comun`; Dockerfile del Escudo corregido para incluirlo. |
| 1 — Núcleo contable | Hecha | `LibroMayor` (doble partida, concurrencia optimista); seed desde `clientes.csv`/`transacciones.csv`/`eventos_app.csv` (6.000 clientes, 5.960 con saldo real). |
| 2 — Casos de uso del cliente | Hecha | 16 slices en `src/YumaBilletera.Api/Features`. |
| 3 — Integración con el Escudo | Hecha | Adapter HTTP + outbox + reevaluación + conciliación; en el Escudo: `auth/evaluar-ingreso`, `alertas/desafio-fallido`, `transacciones/{id}/decision` y aviso HMAC de resoluciones. |
| 4 — Expediente + Ollama | Hecha y probada con Ollama real | `HechoExpediente`, `CatalogoNormas`, narrador validado con respaldo por plantilla, verificador de la cadena. Probado con `qwen2.5:7b-instruct` en Docker con GPU (RTX 4060): ~17 s por narrativa, salida aceptada por la validación. Prompt `expediente_v2`. |
| 5 — Docker, observabilidad, entrega | Hecha (sin levantar en Docker) | Compose validado (`docker compose config`), `Dockerfile.billetera`, `docker-compose.gpu.yml`, tablero Grafana, OpenAPI, contrato y `.http`. |

Pruebas: Escudo 150/150 · Billetera 50/50 · E2E con los dos servicios reales (modelo ONNX) incluyendo duplicación de SIM → retención → analista confirma fraude con bloqueo → la billetera rechaza y bloquea la cuenta.

### Desviaciones del plan (y por qué)

| Plan | Implementado | Motivo |
|---|---|---|
| `POST /v1/transferencias/{id}/otp` | `POST /v1/transferencias/{id}/biometria` y `POST /v1/auth/biometria` | Decisión del 2026-11-27: riesgo medio = biometría facial (diagrama oficial). |
| Adapter con "1 reintento idempotente" | Sin reintento en calificaciones | `/v1/score/transaccion` actualiza el estado en línea del Escudo: un reintento contaría la operación dos veces. La reevaluación la hace el trabajador de integración cuando el Escudo vuelve. |
| El LLM arma toda la narrativa, incluida la línea de tiempo | La línea de tiempo la arma el sistema (determinista); el LLM redacta resumen, factores, normas y recomendación. Prompt v2 recibe `origen_decision` ya calculado | Probado con Ollama real: el modelo omitía hechos y llamaba "alta probabilidad" a un puntaje de 0,28 cuando la retención venía de un piso de seguridad. |
| Token de servicio dentro de la llamada | Token renovado en segundo plano, fuera del presupuesto de 300 ms | Hallado en la prueba E2E: el login del Escudo (PBKDF2) consumía el presupuesto y forzaba la falla cerrada. |
| Mismo plazo de 300 ms para todo | 300 ms para calificar operaciones; 1.000 ms para el score de ingreso (`Escudo:TimeoutIngresoMs`) | Hallado en la prueba E2E en frío: el ingreso en frío abría el circuito y dejaba en revisión las operaciones siguientes. El requisito de 300 ms del caso es para operaciones. |
| Clave de `servicio01` aleatoria | `Seed:ClaveServicio` opcional en el Escudo | En Docker la billetera necesita conocerla por variable de entorno. |

### Pendientes que requieren decisión
1. **Retiros (corresponsal/cajero) y compra con tarjeta virtual**: están en los datos del caso pero no en el plan; no se implementaron.
2. **Catálogo de entidades receptoras** (`Billetera:EntidadesReceptoras`): vacío por defecto (se acepta el nombre como texto); los datos no lo traen.
3. **Tope diario** (`Billetera:Limites:LimiteDiarioCop`): implementado pero desactivado (0) porque el caso no fija un valor.
4. **Escudo caído durante el ingreso**: se exige verificación reforzada (falla cerrada sin bloquear a todos los clientes).
