"""Auditoria de sesgo en validacion (sep-oct 2026) con fairlearn.

Atributos sensibles (SOLO para auditar, nunca como atributos del modelo): departamento,
rango_edad y tipo_cliente de clientes.csv. Para cada grupo se calcula la tasa de
  * retencion/rechazo  (decision >= retener)
  * desafio            (decision == desafiar)
sobre (a) todas las transacciones y (b) solo las legitimas (esta ultima es la que mide
la friccion/FP que sufre cada grupo sin mezclar con diferencias reales de fraude).
Ratio max/min entre grupos con al menos MIN_N transacciones; se marca si supera 1,25.
Si la tasa minima es 0 y la maxima > 0 el ratio es infinito (marcado).

Uso: ./.venv/Scripts/python fairness.py   (guarda artefactos/fairness.json)
"""
from __future__ import annotations

import json

import numpy as np
import pandas as pd
from fairlearn.metrics import MetricFrame, selection_rate

import datos
import features as F
import politica
from evaluar import cargar_todo

UMBRAL_RATIO = 1.25
MIN_N = 200


def razon(tasas: pd.Series, n: pd.Series):
    t = tasas[n >= MIN_N]
    if len(t) < 2:
        return None, False
    mx, mn = float(t.max()), float(t.min())
    if mx == 0:
        return 1.0, False
    r = float("inf") if mn == 0 else mx / mn
    return r, bool(r > UMBRAL_RATIO)


def main():
    tx, X, modelo, umb = cargar_todo()
    cl = pd.read_csv(datos.DIR_DATOS / "clientes.csv", encoding="utf-8-sig")
    va = (tx["fecha_hora"] >= datos.FIN_ENTRENAMIENTO).values
    d = tx[va].merge(cl, on="id_cliente", how="left")
    Xv = X[va]
    p = modelo.predict_proba(Xv)[:, 1]
    dec = politica.decidir(p, Xv[:, F.NOMBRES_ATRIBUTOS.index("fanin_destino_7d")],
                           Xv[:, F.NOMBRES_ATRIBUTOS.index("reputacion_destino")],
                           umb["umbral_desafiar"], umb["umbral_retener"])
    retiene = (dec >= politica.RETENER).astype(int)
    desafia = (dec == politica.DESAFIAR).astype(int)
    legit = (d["es_fraude"].values == 0)

    salida = {}
    print(f"{'atributo':14s} {'poblacion':10s} {'medida':18s} {'min':>8s} {'max':>8s} {'ratio':>8s}  marca")
    for attr in ["departamento", "rango_edad", "tipo_cliente"]:
        g = d[attr].values
        salida[attr] = {}
        for pob, mask in (("todas", np.ones(len(d), bool)), ("legitimas", legit)):
            for nombre, flag in (("retencion/rechazo", retiene), ("desafio", desafia)):
                mf = MetricFrame(metrics=selection_rate, y_true=flag[mask], y_pred=flag[mask],
                                 sensitive_features=g[mask])
                tasas = mf.by_group
                n = pd.Series(g[mask]).value_counts().reindex(tasas.index)
                r, marca = razon(tasas, n)
                tt = tasas[n >= MIN_N]
                salida[attr][f"{pob}:{nombre}"] = {
                    "ratio_max_min": r, "supera_1_25": marca,
                    "tasa_min": float(tt.min()), "tasa_max": float(tt.max()),
                    "grupos_evaluados": int((n >= MIN_N).sum()), "grupos_total": int(len(tasas)),
                    "tasas_por_grupo": {str(k): round(float(v), 5) for k, v in tasas.items()},
                    "n_por_grupo": {str(k): int(v) for k, v in n.items()},
                }
                rs = "n/a" if r is None else ("inf" if r == float("inf") else f"{r:.2f}")
                print(f"{attr:14s} {pob:10s} {nombre:18s} {100*tt.min():7.3f}% {100*tt.max():7.3f}% {rs:>8s}  "
                      f"{'SUPERA 1,25' if marca else 'ok'}")
    (datos.DIR_ART / "fairness.json").write_text(
        json.dumps(salida, indent=1, ensure_ascii=False, default=lambda o: None), encoding="utf-8")


if __name__ == "__main__":
    main()
