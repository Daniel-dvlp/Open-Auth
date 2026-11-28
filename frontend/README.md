# Escudo Yuma · Consola de analista

Frontend web sin proceso de compilación ni dependencias de npm. La carpeta se puede incorporar al repositorio del equipo como `frontend/` y ejecutarse junto a la API. El servidor incluido entrega la interfaz y reenvía las rutas `/v1/*` y `/health/*` al backend, manteniendo un solo origen en el navegador.

## Requisitos

- Node.js 18 o posterior.
- API Escudo Yuma disponible, para usar datos y operaciones reales.

No se necesita API para recorrer el modo de demostración.

## Iniciar

Desde la raíz del repositorio:

```powershell
node .\frontend\server.mjs
```

Abre [http://localhost:5173](http://localhost:5173). Para entrar a la consola con información ficticia, selecciona **Explorar con datos de ejemplo**. No pide credenciales y sus cambios se quedan en esa sesión del navegador.

Para trabajar con el backend real, inicia primero la API según el README de la raíz del repositorio. Después entra con una cuenta de rol `analista` de ese entorno. Las credenciales de prueba se generan al iniciar el entorno y se guardan en `seed/usuarios_prueba.json`; no hay credenciales predefinidas en esta carpeta.

Si la API está en otra dirección, configúrala antes de iniciar el servidor:

```powershell
$env:API_BASE_URL = "https://servidor-de-la-api"
$env:FRONTEND_PORT = "5173"
node .\frontend\server.mjs
```

`API_BASE_URL` acepta direcciones `http` y `https`. El valor predeterminado es `http://localhost:8080`.

## Integración con el proyecto

1. Copia la carpeta `frontend/` al repositorio del equipo.
2. Mantén la API disponible en la dirección configurada como `API_BASE_URL`.
3. Ejecuta `node frontend/server.mjs` como proceso del frontend y publica el puerto `5173` para acceder desde el navegador.

El frontend es un servicio aparte de la API; copiar esta carpeta no inicia ni publica el backend. Si el equipo ya usa un proxy inverso, configura el dominio para servir los archivos del frontend y reenviar `/v1/*` y `/health/*` a la API, o mantén este servidor como servicio web. No hace falta cambiar los endpoints del backend.

## Funciones y servicios usados

- Cola de alertas pendientes y resueltas, búsqueda, filtros y resolución: `GET /v1/alertas` y `POST /v1/alertas/{id}/resolver`.
- Evaluación de transacciones: `POST /v1/score/transaccion`.
- Consulta de reputación: `GET /v1/cuentas/{idCuenta}/reputacion`.
- Registro de dispositivo: `POST /v1/auth/dispositivos`.
- Estado y replay de sandbox: `GET /health/ready` y `GET/POST /v1/sandbox/replay`.

El modo demo usa datos sintéticos y no llama a la API. Su evaluación de riesgo es ilustrativa: no ejecuta el modelo ONNX. En modo real, una evaluación puede actualizar el estado en línea y crear una alerta; el replay puede reiniciar el estado en línea y requiere que esté habilitado en la API. La API puede responder que el login requiere OTP, pero no ofrece una ruta para validar ese OTP; en ese caso la consola informa que el acceso no puede completarse.
