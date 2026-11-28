# Escudo Yuma · Consola de analista y billetera

Frontend web sin proceso de compilación ni dependencias de npm. El servidor incluido entrega la consola de analista y la billetera de cliente. Reenvía `/v1/*` y `/health/*` a Escudo Yuma, y `/wallet-api/v1/*` a la API de la billetera, manteniendo un solo origen en el navegador.

## Requisitos

- Node.js 18 o posterior.
- API Escudo Yuma disponible, para usar datos y operaciones reales.
- API Yuma Billetera disponible, para iniciar sesión y hacer operaciones reales desde la billetera.

No se necesita API para recorrer el modo de demostración.

## Iniciar

Desde la raíz del repositorio:

```powershell
node .\frontend\server.mjs
```

Abre [http://localhost:5173](http://localhost:5173). Para entrar a la consola con información ficticia, selecciona **Explorar con datos de ejemplo**. No pide credenciales y sus cambios se quedan en esa sesión del navegador.

La billetera de cliente está en [http://localhost:5173/billetera](http://localhost:5173/billetera). Esa vista no usa datos de demostración: necesita la API Yuma Billetera activa en `http://localhost:8082` y credenciales creadas por el backend.

Para recorrerla sin backend ni credenciales, usa **Entrar sin credenciales · demo local** en la pantalla de acceso. Desde **Enviar dinero**, selecciona **Preparar escenario de revisión** y confirma la transferencia: se simula un caso de riesgo y aparece en la consola demo si la abres en otra pestaña del mismo navegador. Actualiza la cola si hace falta. Esta simulación se guarda en el navegador y no llama a Escudo ni a la billetera real; **Reiniciar demo** devuelve el saldo y limpia sus casos.

Para trabajar con el backend real, inicia primero la API según el README de la raíz del repositorio. Después entra con una cuenta de rol `analista` de ese entorno. Las credenciales de prueba se generan al iniciar el entorno y se guardan en `seed/usuarios_prueba.json`; no hay credenciales predefinidas en esta carpeta.

Si la API está en otra dirección, configúrala antes de iniciar el servidor:

```powershell
$env:API_BASE_URL = "https://servidor-de-la-api"
$env:FRONTEND_PORT = "5173"
node .\frontend\server.mjs
```

`API_BASE_URL` acepta direcciones `http` y `https`. El valor predeterminado es `http://localhost:8080`.

Si la API de billetera está en otra dirección, configura `BILLETERA_API_BASE_URL`; su valor predeterminado es `http://localhost:8082`.

```powershell
$env:BILLETERA_API_BASE_URL = "http://localhost:8082"
node .\frontend\server.mjs
```

Las credenciales de cliente se generan al iniciar la API y se guardan en `seed/billetera_usuarios_prueba.json`, que está excluido del control de versiones. El acceso puede pedir OTP o una biometría simulada según las señales de la cuenta y del dispositivo. El botón para consultar un OTP de prueba solo funciona si la API publica el endpoint de desarrollo. En la configuración actual de Docker Compose la API se ejecuta en `Production` y no tiene proveedor SMS; si el ingreso exige OTP, el backend responderá que la verificación no está disponible. El equipo que configura la API debe habilitar el buzón OTP de desarrollo o configurar el proveedor de SMS para completar ese flujo. La biometría en este proyecto es una simulación, no una lectura del sensor del dispositivo.

## Integración con el proyecto

1. Copia la carpeta `frontend/` al repositorio del equipo.
2. Mantén Escudo y la billetera disponibles en las direcciones configuradas como `API_BASE_URL` y `BILLETERA_API_BASE_URL`.
3. Ejecuta `node frontend/server.mjs` como proceso del frontend y publica el puerto `5173` para acceder desde el navegador.

El frontend es un servicio aparte de las API; copiar esta carpeta no inicia ni publica los backends. Si el equipo ya usa un proxy inverso, configura el dominio para servir los archivos y reenviar `/v1/*` y `/health/*` a Escudo, y `/wallet-api/v1/*` a Yuma Billetera, o mantén este servidor como servicio web. No hace falta cambiar los endpoints del backend.

## Funciones y servicios usados

- Cola de alertas pendientes y resueltas, búsqueda, filtros y resolución: `GET /v1/alertas` y `POST /v1/alertas/{id}/resolver`.
- Evaluación de transacciones: `POST /v1/score/transaccion`.
- Consulta de reputación: `GET /v1/cuentas/{idCuenta}/reputacion`.
- Registro de dispositivo: `POST /v1/auth/dispositivos`.
- Estado y replay de sandbox: `GET /health/ready` y `GET/POST /v1/sandbox/replay`.

El modo demo usa datos sintéticos y no llama a la API. Su evaluación de riesgo es ilustrativa: no ejecuta el modelo ONNX. En modo real, una evaluación puede actualizar el estado en línea y crear una alerta; el replay puede reiniciar el estado en línea y requiere que esté habilitado en la API. La API puede responder que el login requiere OTP, pero no ofrece una ruta para validar ese OTP; en ese caso la consola informa que el acceso no puede completarse.
