"""Carga de datos sinteticos y construccion de atributos para todo el periodo."""
from __future__ import annotations

import os
from pathlib import Path

import numpy as np
import pandas as pd

import features as F

DIR_DATOS = Path(os.environ.get(
    "YUMA_DATOS", r"C:\Users\Daniel\Documents\Yuma_Pay\Caso_de_estudio_Yuma_Pay\03_Datos"))
DIR_ART = Path(__file__).with_name("artefactos")
FIN_ENTRENAMIENTO = "2026-09-01"  # train: mayo-agosto ; validacion: sep-oct
INICIO_ENTRENAMIENTO = "2026-05-15"  # rodaje: las primeras 2 semanas solo construyen estado (arranque en frio)


def cargar():
    tx = pd.read_csv(DIR_DATOS / "transacciones.csv", encoding="utf-8-sig")
    ev = pd.read_csv(DIR_DATOS / "eventos_app.csv", encoding="utf-8-sig")
    cl = pd.read_csv(DIR_DATOS / "clientes.csv", encoding="utf-8-sig")
    tx = tx.sort_values(["fecha_hora", "id_transaccion"]).reset_index(drop=True)
    ev = ev.sort_values(["fecha_hora", "id_evento"]).reset_index(drop=True)
    for d in (tx, ev):
        d["ts"] = pd.to_datetime(d["fecha_hora"]).astype("int64") // 10**9 if False else (
            (pd.to_datetime(d["fecha_hora"]) - pd.Timestamp("1970-01-01")).dt.total_seconds().astype("int64"))
    return tx, ev, cl


def vinculacion(cl: pd.DataFrame) -> dict:
    return {r.id_cliente: F.fecha_a_epoch(r.fecha_vinculacion) for r in cl.itertuples()}


_COLS_TX = ["id_transaccion", "ts", "id_cliente", "tipo_operacion", "canal", "monto_cop",
            "saldo_antes_cop", "id_destino", "tipo_destino", "destino_nuevo", "id_dispositivo",
            "dispositivo_nuevo", "cambio_clave_24h", "cambio_sim_48h", "departamento_ip",
            "ip_departamento_distinto", "antiguedad_cuenta_dias"]
_COLS_EV = ["id_evento", "ts", "id_cliente", "tipo_evento", "id_dispositivo", "departamento_ip", "resultado"]


def filas_tx(tx: pd.DataFrame):
    return tx[_COLS_TX].to_dict("records")


def filas_ev(ev: pd.DataFrame):
    return ev[_COLS_EV].to_dict("records")


def matriz_atributos(tx, ev, cl, imputacion=F._DEFECTO) -> np.ndarray:
    """Streaming sobre todo el periodo -> matriz float32 [N, n_atributos] alineada con tx.
    ev puede ser None/vacio (feed de eventos caido). imputacion: ver features.calcular_atributos."""
    filas_e = [] if ev is None else filas_ev(ev)
    X = F.calcular_atributos(filas_tx(tx), filas_e, vinculacion(cl), imputacion)
    return np.asarray(X, dtype=np.float32)
