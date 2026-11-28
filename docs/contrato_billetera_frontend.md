# Contrato de la Billetera Yuma para el frontend

Billetera digital **100 % colombiana**: montos en **pesos colombianos enteros (COP)**, fechas para mostrar en **hora de Colombia (UTC-5)**, llaves = **celular de 10 dígitos que empieza por 3**. Datos 100 % sintéticos del caso de estudio.

- **Base URL**: `http://localhost:8082` (Docker Compose, o `dotnet run` en `src/YumaBilletera.Api`).
- **Documento OpenAPI**: `docs/openapi-billetera.json` (también en vivo en `GET /openapi/v1.json`).
- **JSON en `snake_case`**. Errores como **ProblemDetails** (`title`, `status`, `detail`; en errores de negocio también `codigo`).
- **Autenticación**: `Authorization: Bearer <token>` (JWT de 15 min) + `refresh_token` rotativo.
- **Toda operación que mueve dinero exige la cabecera `Idempotency-Key`** (1-64 caracteres `[A-Za-z0-9_-]`, un valor nuevo por operación; si la app reintenta con la misma clave, recibe la misma operación y el dinero no sale dos veces).
- **Correlación**: cada respuesta trae `X-Correlation-Id`; muéstralo en pantallas de error ("cita este código al reportar").

> La billetera **no muestra puntajes, reglas ni umbrales** del Escudo (el caso prohíbe publicarlos). Muestra el campo `mensaje` que ya viene redactado para el cliente.

---

## 1. Ingreso

### `POST /v1/auth/login`
```json
{ "llave": "300 000 0001", "clave": "…", "id_dispositivo": "D19CE9F2", "departamento_ip": "Antioquia", "sistema_operativo": "Android" }
```
- `departamento_ip`: departamento estimado de la IP (en producción lo resuelve el gateway; en la demo lo envía la app).
- Respuesta `200`, según `resultado`:

| `resultado` | Qué hace la app |
|---|---|
| `permitir` | Guarda `token`, `expira_utc`, `refresh_token`, `refresh_expira_utc` y entra. |
| `verificacion_requerida` | Muestra la pantalla del método en `verificacion.metodo`: **`otp`** (código de 6 dígitos por SMS) o **`biometria`** (verificación facial; se usa cuando hubo cambio de SIM en las últimas 48 h, porque el SMS podría llegar a la SIM duplicada). Guarda `verificacion.id_verificacion` (vence en `verificacion.expira_utc`, 5 min). |

```json
{ "resultado": "verificacion_requerida", "mensaje": "Te enviamos un código de 6 dígitos por SMS. Ingrésalo para continuar.",
  "token": null, "expira_utc": null, "refresh_token": null, "refresh_expira_utc": null,
  "verificacion": { "id_verificacion": "3f6c…", "metodo": "otp", "expira_utc": "2026-11-27T21:05:00Z" } }
```
- `403` con `resultado: "bloqueo_temporal"`: ingreso bloqueado temporalmente; mostrar `mensaje`.
- `401`: celular o clave incorrectos (mismo mensaje exista o no el celular).
- `429`: demasiados intentos; respetar la cabecera `Retry-After` (segundos).

### `POST /v1/auth/otp`
`{ "id_verificacion": "3f6c…", "codigo": "123456" }` → `200` con tokens (`resultado: "permitir"`).
`401` código incorrecto (quedan intentos) · `403` intentos agotados (3) · `410` vencida → volver al login · `404` no existe.

### `POST /v1/auth/biometria`
`{ "id_verificacion": "3f6c…", "resultado_simulado": "supera" }` (`supera` | `no_supera`).
La biometría es **simulada** (no hay proveedor real en el alcance): la pantalla de demo debe ofrecer las dos opciones. `200` con tokens · `403` no superada (volver al login).

### `POST /v1/auth/refresh`
`{ "refresh_token": "…" }` → `200` `{ token, expira_utc, refresh_token, refresh_expira_utc }`. El refresh **es de un solo uso**: guarda siempre el nuevo. `401` → volver al login. La sesión dura como máximo 8 h.

---

## 2. Cuenta

### `GET /v1/cuentas/me`
```json
{ "id_cuenta": "C00001", "llave": "3000000001", "tipo_cliente": "microcomerciante", "departamento": "Antioquia",
  "estado": "activa", "saldo_cop": 3897902, "reservado_cop": 0, "disponible_cop": 3897902, "moneda": "COP" }
```
- `reservado_cop`: dinero retenido por operaciones en evaluación, verificación o revisión (no ha salido). Muestra **disponible** como saldo principal.
- `estado`: `activa` | `congelada` | `bloqueada` (bloqueo decidido por un analista: deshabilitar envíos).
- Formato de montos en pantalla: `$ 3.897.902` (punto de miles, sin decimales).

### `GET /v1/cuentas/me/movimientos?limite=20&cursor=…`
```json
{ "movimientos": [ { "id": 12045, "fecha_utc": "2026-11-27T20:41:10Z", "fecha_colombia": "2026-11-27 15:41:10", "tipo": "debito",
    "concepto": "transferencia_inmediata", "monto_cop": 25000, "saldo_despues_cop": 3872902, "id_transaccion": "TY20261127154110A1B2C3D4E5",
    "contraparte": "C00002" } ],
  "siguiente_cursor": 12040 }
```
`limite` 1-100. Para la página siguiente envía `cursor = siguiente_cursor`; `null` = no hay más. `concepto`: `apertura`, `transferencia_inmediata`, `pago_qr`, `pago_servicios`, `recarga_celular`. `contraparte`: otro cliente (`C…`) o `PUENTE-OTRA_ENTIDAD` / `PUENTE-COMERCIO` / `PUENTE-SERVICIO` / `PUENTE-OPERADOR`.

---

## 3. Destinatarios

### `POST /v1/destinatarios`
- Cliente Yuma: `{ "alias": "Mamá", "tipo_destino": "cuenta_yuma", "llave": "3000000002" }`
- Otra entidad: `{ "alias": "Arriendo", "tipo_destino": "otra_entidad", "entidad": "Banco X", "numero_producto": "0012345678" }` (6-20 dígitos)

`201` → `{ "id", "alias", "tipo_destino", "id_destino", "entidad", "producto": "****5678", "inscrito_utc" }` · `404` llave no registrada · `409` ya inscrito · `422` tu propia cuenta.

### `GET /v1/destinatarios` → `{ "destinatarios": [ … ] }`

---

## 4. Mover dinero (todas con `Idempotency-Key`)

| Operación | Ruta | Cuerpo |
|---|---|---|
| Transferir a destinatario | `POST /v1/transferencias` | `{ "id_destinatario": "…", "monto_cop": 120000, "descripcion": "Arriendo" }` |
| Transferir a una llave Yuma | `POST /v1/transferencias` | `{ "llave": "3000000002", "monto_cop": 25000 }` |
| Pago con QR | `POST /v1/pagos/qr` | `{ "codigo_comercio": "COM0412", "monto_cop": 18000 }` (COM + 4 dígitos) |
| Pago de servicios | `POST /v1/pagos/servicios` | `{ "id_servicio": "SRV07", "referencia_pago": "778899001", "monto_cop": 64000 }` |
| Recarga de celular | `POST /v1/recargas` | `{ "id_operador": "OPE03", "celular": "3105551234", "monto_cop": 10000 }` |

Respuesta común (`201` nueva · `200` si es un reintento con la misma `Idempotency-Key`):
```json
{ "id_transaccion": "TY20261127154110A1B2C3D4E5", "tipo_operacion": "transferencia_inmediata", "estado": "completada",
  "monto_cop": 25000, "tipo_destino": "cuenta_yuma", "id_destino": "C00002", "descripcion": null,
  "mensaje": "Operación exitosa.", "verificacion": null, "creada_utc": "…", "resuelta_utc": "…" }
```

### Estados y qué mostrar

| `estado` | Significado | Pantalla |
|---|---|---|
| `completada` | El Escudo aprobó y el dinero salió. | Comprobante. |
| `requiere_verificacion` | Riesgo medio: falta la **verificación facial**. Viene `verificacion.id_verificacion`. | Mostrar `mensaje` (puede ser una **advertencia anti-estafa**: "Yuma Pay nunca te pedirá trasladar tu dinero a una 'cuenta segura'…") y pedir la biometría. |
| `en_revision` | Retenida para un analista (o el Escudo no respondió: la billetera falla cerrado). El dinero **no salió** y sigue reservado. | "En revisión", consultar luego con `GET /v1/transferencias/{id}`. |
| `rechazada` | No se procesó por seguridad. El dinero sigue en la cuenta. | Mostrar `mensaje`. |

### `POST /v1/transferencias/{id}/biometria`
`{ "id_verificacion": "…", "resultado_simulado": "supera" }` → `200` con la operación: `completada` si supera; `en_revision` si no (un analista confirma con el cliente). `409` si la operación ya no está pendiente.

### `GET /v1/transferencias/{id}`
Seguimiento de una operación propia (`404` si no existe o es de otro cliente). Sirve para refrescar las que están `en_revision`: cuando el analista resuelve pasan a `completada` o `rechazada`.

### Errores de negocio (`ProblemDetails` con `codigo`)
`422 saldo_insuficiente` · `422 cuenta_no_activa` · `422 misma_cuenta` · `422 destino_bloqueado` · `422 limite_diario` · `404 destino_inexistente` · `409 idempotencia_conflicto` (misma clave con otros datos) · `401 sesion_invalida` (sesión cerrada, p. ej. tras cambio de clave) · `400` validación (`errors` por campo) o falta `Idempotency-Key`.

---

## 5. Seguridad del cliente

- `GET /v1/dispositivos` → `{ "dispositivos": [ { "id_dispositivo", "sistema_operativo", "registrado_utc" } ] }`
- `POST /v1/dispositivos` `{ "id_dispositivo": "D-NUEVO", "sistema_operativo": "Android" | "iOS" }` → `201` nuevo · `200` ya existía · `422` máximo 20.
- `POST /v1/seguridad/clave` `{ "clave_actual", "clave_nueva" }` (8-128) → `204`. Cierra las demás sesiones del cliente.

Un dispositivo registrado hace menos de 24 h, un cambio de clave (24 h) o de SIM (48 h) hacen más probable que la siguiente operación pida verificación: es esperado.

---

## 6. Herramientas de demostración (solo `Development`)

- `GET /v1/dev/otp/{id_verificacion}` → `{ "codigo": "123456" }`: el "SMS" del OTP.
- `POST /v1/dev/cambio-sim` `{ "id_cliente": "C00003" }`: simula que el operador reportó un cambio de SIM (modalidad *sim_swapping*).
- Credenciales de prueba: `seed/billetera_usuarios_prueba.json` (`llave`, `clave`, `id_dispositivo` habitual, `departamento`, `saldo_inicial_cop`). Se generan al arrancar; **no se versionan**.

### Recorridos para la demo
1. **Cliente normal**: ingresar con su dispositivo y departamento habituales → transferir un monto pequeño → `completada`.
2. **Duplicación de SIM**: `POST /v1/dev/cambio-sim` → ingresar desde un dispositivo nuevo y otro departamento → biometría → registrar el dispositivo → enviar casi todo el saldo a un destino nuevo de otra entidad → `en_revision`. En la consola del analista (Escudo) el caso aparece con su expediente y narrativa; al confirmar fraude con bloqueo, la billetera rechaza la operación y bloquea la cuenta.

---

## 7. Consola del analista (Escudo, `http://localhost:8080`, rol `analista`)

Además de lo que ya usa `frontend/` (cola, resolver, evaluar, reputación, replay):

| Método | Ruta | Uso |
|---|---|---|
| GET | `/v1/alertas/{id}/expediente` | Hechos ordenados (operación, decisión con reglas y normas, eventos, ingresos, desafíos, resolución) con su eslabón de auditoría. |
| GET | `/v1/alertas/{id}/narrativa` | Explicación en lenguaje natural (`contenido`: `resumen`, `linea_de_tiempo`, `factores`, `normas_aplicadas`, `recomendacion_al_analista`, `confianza`). `202` mientras se genera. `origen`: `ollama` o `plantilla` (respaldo). `desactualizada: true` → ofrecer "Regenerar". |
| POST | `/v1/alertas/{id}/narrativa/regenerar` | Vuelve a generarla (`202`). |
| GET | `/v1/clientes/{id}/resumen?dias=30` | Resumen narrado de la actividad del cliente (1-90 días). Puede tardar varios segundos con el LLM en CPU. |
