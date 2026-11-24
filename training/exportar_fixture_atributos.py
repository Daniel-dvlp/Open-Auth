"""Exporta un fixture de paridad de atributos para el port a C# (xUnit).

Corre EstadoEnLinea en streaming sobre TODO el periodo y guarda ~2.000 transacciones con sus
57 atributos (float64 con repr completo, para comparar con tolerancia 1e-6 en C#).
La muestra es determinista (semilla fija) y estratificada para cubrir los casos delicados:
destinos con fan-in, periodos con eventos de app (fallidos, cambio de clave/SIM, registro de
dispositivo), reputaciones degradadas, fraudes y legitimas al azar.
El test en C# repite el streaming sobre los mismos CSV y exige igualdad atributo por atributo.
(es_fraude solo se usa para estratificar la muestra; nunca entra a los atributos.)

Uso: ./.venv/Scripts/python exportar_fixture_atributos.py     -> artefactos/fixture_atributos.csv
"""
from __future__ import annotations

import csv

import numpy as np

import datos
import features as F

N_OBJETIVO = 2000
SEMILLA = 2026


def main() -> None:
    tx, ev, cl = datos.cargar()
    X = np.asarray(
        F.calcular_atributos(datos.filas_tx(tx), datos.filas_ev(ev), datos.vinculacion(cl)), dtype=np.float64)
    col = {n: i for i, n in enumerate(F.NOMBRES_ATRIBUTOS)}
    rng = np.random.default_rng(SEMILLA)
    n = len(tx)
    elegidos: set[int] = set()

    def tomar(mask: np.ndarray, cuantas: int) -> None:
        idx = np.where(mask)[0]
        idx = idx[~np.isin(idx, list(elegidos))] if elegidos else idx
        if len(idx):
            elegidos.update(rng.choice(idx, min(cuantas, len(idx)), replace=False).tolist())

    sin_evento = F.TOPE_MIN_SIN_EVENTO
    tomar(X[:, col["fanin_destino_7d"]] >= 2, 400)                        # destinos con fan-in
    tomar(X[:, col["reputacion_destino"]] < 100, 150)                      # reputacion degradada (destino)
    tomar(X[:, col["reputacion_origen"]] < 100, 100)                       # reputacion degradada (origen)
    tomar((X[:, col["login_fallidos_24h"]] > 0), 250)                      # fallidos de login
    tomar((X[:, col["min_desde_cambio_sim"]] < sin_evento) | (X[:, col["min_desde_cambio_clave"]] < sin_evento), 250)
    tomar(X[:, col["min_desde_registro_dispositivo"]] < sin_evento, 150)   # registro de dispositivo
    tomar(X[:, col["dispositivo_visto_antes"]] == 0, 100)                  # dispositivo nunca visto
    tomar(X[:, col["entradas_n_24h"]] > 0, 150)                            # entradas a la cuenta origen (patron mula)
    tomar(X[:, [col[f"R{i:03d}"] for i in range(1, 13)]].sum(axis=1) > 0, 150)  # reglas activas
    tomar(tx["es_fraude"].values == 1, 200)                                # fraudes (solo para estratificar)
    tomar(X[:, col["n_tx_previas"]] < 3, 80)                               # arranque en frio por cliente
    tomar(np.ones(n, dtype=bool), max(0, N_OBJETIVO - len(elegidos)))      # relleno aleatorio
    sel = np.array(sorted(elegidos))[:N_OBJETIVO] if len(elegidos) > N_OBJETIVO else np.array(sorted(elegidos))

    ruta = datos.DIR_ART / "fixture_atributos.csv"
    with open(ruta, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["id_transaccion"] + F.NOMBRES_ATRIBUTOS)
        ids = tx["id_transaccion"].values
        for i in sel:
            w.writerow([ids[i]] + [repr(float(v)) for v in X[i]])
    print(f"{len(sel)} transacciones -> {ruta}")
    for nombre, mask in [("fan-in>=2", X[sel, col["fanin_destino_7d"]] >= 2),
                         ("fallidos 24h>0", X[sel, col["login_fallidos_24h"]] > 0),
                         ("cambio SIM/clave", (X[sel, col["min_desde_cambio_sim"]] < sin_evento) | (X[sel, col["min_desde_cambio_clave"]] < sin_evento)),
                         ("rep destino<100", X[sel, col["reputacion_destino"]] < 100)]:
        print(f"  {nombre}: {int(mask.sum())}")


def exportar_sin_eventos(tx, cl) -> None:
    """Segundo fixture: el MISMO streaming pero SIN ningun evento de app (feed caido) -> fuerza la imputacion desde banderas.
    ~600 tx: las que tienen alguna de las banderas dispositivo_nuevo / cambio_sim_48h / cambio_clave_24h y relleno al azar."""
    X = np.asarray(F.calcular_atributos(datos.filas_tx(tx), [], datos.vinculacion(cl)), dtype=np.float64)
    col = {n: i for i, n in enumerate(F.NOMBRES_ATRIBUTOS)}
    rng = np.random.default_rng(SEMILLA + 1)
    banderas = (X[:, col["dispositivo_nuevo"]] == 1) | (X[:, col["cambio_sim_48h"]] == 1) | (X[:, col["cambio_clave_24h"]] == 1)
    elegidos = set(rng.choice(np.where(banderas)[0], 450, replace=False).tolist())
    resto = np.array([i for i in range(len(tx)) if i not in elegidos])
    elegidos.update(rng.choice(resto, 150, replace=False).tolist())
    sel = np.array(sorted(elegidos))
    ruta = datos.DIR_ART / "fixture_atributos_sin_eventos.csv"
    with open(ruta, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["id_transaccion"] + F.NOMBRES_ATRIBUTOS)
        ids = tx["id_transaccion"].values
        for i in sel:
            w.writerow([ids[i]] + [repr(float(v)) for v in X[i]])
    print(f"{len(sel)} transacciones (sin eventos, {int(banderas[sel].sum())} con banderas) -> {ruta}")


if __name__ == "__main__":
    main()
    _tx, _ev, _cl = datos.cargar()
    exportar_sin_eventos(_tx, _cl)
