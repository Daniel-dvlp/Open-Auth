"""Entrenamiento: atributos en streaming -> XGBoost -> ONNX + umbrales + paridad + explicacion.

Uso: ./.venv/Scripts/python train.py
Train: 15-mayo a agosto 2026 (1-14 mayo = rodaje del estado, no se entrena: arranque en frio). Validacion: septiembre-octubre 2026.
"""
from __future__ import annotations

import json
import time

import numpy as np
import pandas as pd
import xgboost as xgb
from onnxmltools import convert_xgboost
from onnxmltools.convert.common.data_types import FloatTensorType
from sklearn.metrics import average_precision_score, roc_auc_score

import datos
import features as F
import politica
from explicacion import construir_explicacion

ART = datos.DIR_ART
# Hiperparametros fijados a priori (sin busqueda sobre validacion).
PARAMS = dict(n_estimators=300, max_depth=5, learning_rate=0.05, subsample=0.8,
              colsample_bytree=0.8, min_child_weight=3, tree_method="hist",
              eval_metric="aucpr", random_state=7, n_jobs=8)
FRACCION_DROPOUT_EVENTOS = 0.30   # filas de entrenamiento cuyo contexto de eventos se borra (feed caido simulado)
SEMILLA_DROPOUT = 2027
MODALIDADES = ["toma_de_cuenta", "sim_swapping", "ingenieria_social", "engano_qr", "cuenta_mula"]
IDX_REGLAS = [F.NOMBRES_ATRIBUTOS.index(f"R{i:03d}") for i in range(1, 13)]


def alerta_reglas(X: np.ndarray) -> np.ndarray:
    """Alerta de la linea base = alguna de las 12 reglas (senales 0/1 en la matriz)."""
    return X[:, IDX_REGLAS].sum(axis=1) > 0


def deteccion_base_por_modalidad(R, y, mod):
    return {m: float(R[(mod == m) & y].mean()) for m in MODALIDADES}


def calcular_imputacion(X0, y, tr):
    """Valores de imputacion desde banderas: mediana de los minutos observados en FRAUDES de entrenamiento
    con la bandera = 1 y el evento presente dentro de su ventana (X0 = atributos SIN imputar)."""
    c = {n: i for i, n in enumerate(F.NOMBRES_ATRIBUTOS)}
    spec = [("min_desde_cambio_sim", "cambio_sim_48h", F.VENTANA_SIM_MIN),
            ("min_desde_cambio_clave", "cambio_clave_24h", F.VENTANA_CLAVE_MIN),
            ("min_desde_registro_dispositivo", "dispositivo_nuevo", F.VENTANA_REGISTRO_MIN)]
    imp, n = {}, {}
    for col, bandera, ventana in spec:
        sel = tr & (y == 1) & (X0[:, c[bandera]] == 1) & (X0[:, c[col]] <= ventana)
        imp[col] = float(np.median(X0[sel, c[col]]))
        n[col] = int(sel.sum())
    return imp, n


def main():
    t0 = time.time()
    ART.mkdir(exist_ok=True)
    tx, ev, cl = datos.cargar()
    y = tx["es_fraude"].values.astype(int)
    mod = tx["modalidad_fraude"].fillna("").values
    fh = tx["fecha_hora"]
    tr = ((fh >= datos.INICIO_ENTRENAMIENTO) & (fh < datos.FIN_ENTRENAMIENTO)).values
    va = (fh >= datos.FIN_ENTRENAMIENTO).values

    # 1) valores de imputacion (solo con filas de entrenamiento) -> artefactos/imputacion.json
    X0 = datos.matriz_atributos(tx, ev, cl, imputacion=None)
    imp, n_imp = calcular_imputacion(X0, y, tr)
    (ART / "imputacion.json").write_text(json.dumps(
        {**imp, "metodo": "mediana de minutos en fraudes de entrenamiento con la bandera=1 y el evento presente en su ventana",
         "n_fraudes_usados": n_imp, "periodo": f"{datos.INICIO_ENTRENAMIENTO}..{datos.FIN_ENTRENAMIENTO}",
         "regla": "bandera=1 y min_desde_* > ventana (sim 2880, clave 1440, registro 1440) -> min_desde_* = valor; "
                  "dispositivo_nuevo=1 ademas fija dispositivo_visto_antes=0"},
        indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"Imputacion desde banderas: {imp}  (n={n_imp})")

    # 2) atributos con imputacion (eventos completos) y con el feed de eventos caido (sin NINGUN evento)
    X = datos.matriz_atributos(tx, ev, cl, imputacion=imp)
    X_sin_ev = datos.matriz_atributos(tx, None, cl, imputacion=imp)
    print(f"Atributos en streaming: {X.shape} en {time.time() - t0:.1f}s")

    # 3) dropout de eventos: en ~30 % de las filas de entrenamiento se usa la version "feed caido"
    rng = np.random.default_rng(SEMILLA_DROPOUT)
    caida = rng.random(len(tx)) < FRACCION_DROPOUT_EVENTOS
    X_ent = np.where(caida[:, None], X_sin_ev, X)
    print(f"Dropout de eventos: {int((caida & tr).sum())} de {int(tr.sum())} filas de entrenamiento con feed caido")

    modelo = xgb.XGBClassifier(**PARAMS)
    modelo.fit(X_ent[tr], y[tr], eval_set=[(X[va], y[va])], verbose=False)
    p_va = modelo.predict_proba(X[va])[:, 1]
    print(f"Validacion: AUC={roc_auc_score(y[va], p_va):.4f}  AP={average_precision_score(y[va], p_va):.4f}")
    modelo.save_model(str(ART / "modelo_tx.json"))

    # ---- ONNX (float32 [N, n_atributos])
    onx = convert_xgboost(modelo, initial_types=[("input", FloatTensorType([None, F.N_ATRIBUTOS]))],
                          target_opset=15)
    (ART / "modelo_tx.onnx").write_bytes(onx.SerializeToString())
    (ART / "atributos.json").write_text(json.dumps(
        {"atributos": F.NOMBRES_ATRIBUTOS, "n_atributos": F.N_ATRIBUTOS,
         "entrada_onnx": "input: float32 [N, n_atributos]",
         "salida_probabilidad_fraude": "probabilities[:, 1]"},
        indent=2, ensure_ascii=False), encoding="utf-8")

    # ---- umbrales
    R = alerta_reglas(X)
    yb = y == 1
    base_full = deteccion_base_por_modalidad(R, yb, mod)
    base_val = deteccion_base_por_modalidad(R[va], yb[va], mod[va])
    base = {m: max(base_full[m], base_val[m]) for m in MODALIDADES}
    t_des, t_ret = politica.elegir_umbrales(p_va, y[va], mod[va], base)
    umbrales = {
        "umbral_desafiar": t_des, "umbral_retener": t_ret,
        "rechazo": {"fanin_destino_7d_min": politica.RECHAZO_FANIN_MIN,
                    "reputacion_destino_max": politica.RECHAZO_REP_MAX,
                    "nota": "rechazar = p >= umbral_retener Y fanin >= min Y reputacion <= max (fijos a priori)"},
        "decision": {"0": "aprobar", "1": "desafiar", "2": "retener", "3": "rechazar"},
        "piso_reglas_seguridad": "ver politica.piso_reglas: la decision final es max(modelo, piso); parametros fijos a priori",
        "criterio": {"deteccion_global_min": politica.OBJ_DETECCION_GLOBAL, "margen": politica.MARGEN,
                     "linea_base_min_por_modalidad": base,
                     "periodo_seleccion": "validacion 2026-09-01..2026-10-31"},
    }
    (ART / "umbrales.json").write_text(json.dumps(umbrales, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"Umbrales: desafiar={t_des:.4f} retener={t_ret:.4f}")

    # ---- paridad.csv: 200 filas de validacion (50 fraudes + 150 legitimas, semilla fija)
    rng = np.random.default_rng(11)
    iv = np.where(va)[0]
    fr = rng.choice(iv[y[iv] == 1], 50, replace=False)
    lg = rng.choice(iv[y[iv] == 0], 150, replace=False)
    sel = np.sort(np.concatenate([fr, lg]))
    p_sel = modelo.predict_proba(X[sel])[:, 1]
    dec, _ = politica.decidir_con_piso(p_sel, X[sel], t_des, t_ret)  # modelo + piso de reglas de seguridad
    par = pd.DataFrame(X[sel], columns=F.NOMBRES_ATRIBUTOS)
    par.insert(0, "id_transaccion", tx["id_transaccion"].values[sel])
    par["score_python"] = p_sel
    par["decision_python"] = dec
    par.to_csv(ART / "paridad.csv", index=False, float_format="%.9g")

    # ---- explicabilidad
    expl = construir_explicacion(modelo, X_ent[tr], X[va], F.NOMBRES_ATRIBUTOS)
    (ART / "explicacion.json").write_text(json.dumps(expl, indent=1, ensure_ascii=False), encoding="utf-8")
    fid = expl["fidelidad"]
    print(f"Explicacion vs SHAP (1000 mayores puntajes): top1 en top3 = {fid['top3_contiene_top1_shap']:.3f}, "
          f"solape top3 = {fid['solape_top3']:.3f}, corr = {fid['correlacion_contribuciones']:.3f}")
    print(f"Listo en {time.time() - t0:.1f}s")


if __name__ == "__main__":
    main()
