# Escudo Yuma - entrenamiento (datos 100 % sinteticos)

## Como correr
```
./.venv/Scripts/python train.py      # atributos (streaming ~7 s) + XGBoost + ONNX + umbrales + paridad + explicacion
./.venv/Scripts/python evaluar.py    # linea base vs modelo (sep-oct) -> artefactos/reporte_validacion.json
./.venv/Scripts/python fairness.py   # auditoria de sesgo (fairlearn) -> artefactos/fairness.json
./.venv/Scripts/python score_login.py  # sanidad historica del score de ingreso
./.venv/Scripts/python -m pytest tests -q
```
Datos: `YUMA_DATOS` (variable de entorno) o la ruta por defecto en `datos.py`.

## Archivos
`features.py` (estado en streaming, 57 atributos, reputacion), `score_login.py`, `politica.py`
(decision y seleccion de umbrales), `explicacion.py`, `train.py`, `evaluar.py`, `fairness.py`, `tests/`.
Artefactos: `modelo_tx.onnx` (entrada `input` float32 [N,57]; probabilidad de fraude = `probabilities[:,1]`),
`modelo_tx.json`, `atributos.json`, `umbrales.json`, `paridad.csv` (200 filas: atributos + `score_python` + `decision_python`),
`explicacion.json`, `reporte_validacion.json`, `fairness.json`.

## Reglas de no-fuga
Cada tx se califica con el estado previo, en una sola pasada cronologica. A igual segundo, la tx se procesa antes que
los eventos de app. Atributos prohibidos (departamento, rango_edad, tipo_cliente, etiquetas, `cuenta_mula_confirmada`) no entran.
`ip_departamento_distinto` es un campo de la propia tx. El unico dato de `clientes.csv` usado es `fecha_vinculacion` (para la
reputacion de cuentas destino). Las primeras dos semanas (1-14 mayo) solo calientan el estado y no se entrenan (arranque en frio):
en produccion el estado debe precargarse con >= 14 dias de historia.

## Politica
`p < desafiar` aprobar; `desafiar <= p < retener` desafiar; `p >= retener` retener; rechazar si ademas
fan-in destino 7d >= 3 y reputacion destino <= 30 (fijos a priori). Umbrales (seleccionados en sep-oct):
desafiar 0,566; retener 0,971 (modelo reentrenado con imputacion desde banderas y piso de reglas de seguridad). Criterio: umbral de desafio = el mas alto con deteccion global >= 62,6 % + 10 pts y cada
modalidad >= su linea base + 10 pts; umbral de retencion = el mas alto con >= 72,6 % del fraude retenido (guarda anadida: sin ella
minimizar FP degenera en no retener nada). Hiperparametros fijos, sin busqueda sobre validacion.

## Resultados (validacion sep-oct: 41.575 tx, 494 fraudes)
| | Linea base | Modelo + politica |
|---|---|---|
| FP % (entre bloqueadas) | 91,7 | 0,0 (0 FP de 359 retenidas/rechazadas) |
| Deteccion casos / monto | 56,5 / 80,1 % | 90,7 / 93,7 % |
| Fraude retenido/rechazado | 56,5 % | 72,7 % |
| toma_de_cuenta | 58,6 | 98,7 |
| sim_swapping | 86,0 | 98,2 |
| ingenieria_social | 12,7 | 75,9 |
| engano_qr | 30,0 | 42,5 |
| cuenta_mula | 86,1 | 100,0 |
| Legitimas desafiadas | 0 | 0,37 % (153) |
| Legitimas retenidas/rechazadas | 7,48 % | 0,00 % |
| FP % proyectado a 1/10.000 | 99,9 | 0,0 |

(La linea base sobre todo el periodo es 89,0 % FP / 62,6 % deteccion; la ventana sep-oct es mas dificil para las reglas.)

## Advertencias honestas
* Los datos son sinteticos y muy separables (AUC 0,9998): el 0 % de FP en validacion no se debe extrapolar a produccion; con 0 FP el
  intervalo superior es amplio (regla de tres: < ~0,7 % de las legitimas retenidas con 95 % de confianza).
* Umbrales elegidos y evaluados en el mismo periodo. Verificacion fuera de muestra (umbrales solo con septiembre, evaluado en
  octubre): FP 0,0 %, deteccion 89,3 %; toma/sim/mula/ing. social por encima de la base, pero **engano_qr = 25 % vs 37,5 % de la
  base en octubre (8 casos)**: la modalidad mas debil, con pocos casos; la restriccion "ninguna modalidad empeora" no esta garantizada para ella.
* La proyeccion 1/10.000 re-pondera legitimas (x120) y supone comportamiento estable del modelo.
* Fairness: ver `fairness.json`. Con 0 retenciones de legitimas no hay disparidad en retencion; el desafio de legitimas (14 casos) y
  las tasas sobre todas las tx difieren por grupo (en parte por distinta prevalencia de fraude y muestras pequenas), varias superan 1,25: revisar antes de produccion.
* Explicacion para .NET: aproximacion por tablas de SHAP medio por cubeta (fidelidad: top-1 SHAP dentro del top-3 aprox. en 78 % de los casos, correlacion 0,84).
