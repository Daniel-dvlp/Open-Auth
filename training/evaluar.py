"""Evaluacion en validacion (sep-oct 2026): linea base de reglas vs modelo + politica.

Definiciones (las del sandbox):
  * Linea base: bloquea toda tx con alguna de las 12 reglas.  FP% = fp / (vp + fp) entre bloqueadas.
  * Modelo: "detectado" = fraude con decision desafiar/retener/rechazar.
            "FP" = legitima con retener/rechazar; FP% = fp / (fraudes retenidos + fp).
            Desafiar a una legitima NO es FP pero es friccion: se reporta aparte.
  * Proyeccion 1/10.000: se re-ponderan las legitimas para que fraude/total = 1e-4
    (los fraudes y sus tasas de deteccion se mantienen). Supuesto fuerte: las legitimas de la
    muestra son representativas y el modelo se comporta igual con ese volumen de legitimas.

Uso: ./.venv/Scripts/python evaluar.py
"""
from __future__ import annotations

import json

import numpy as np
import xgboost as xgb

import datos
import features as F
import politica
from train import MODALIDADES, alerta_reglas, deteccion_base_por_modalidad

TASA_REAL = 1 / 10_000


def metricas_base(R, y, monto, mod, peso_leg=1.0):
    vp = int((R & y).sum())
    fp = float((R & ~y).sum()) * peso_leg
    out = {
        "fp_pct": 100 * fp / max(vp + fp, 1e-9),
        "deteccion_casos_pct": 100 * vp / y.sum(),
        "deteccion_monto_pct": 100 * monto[R & y].sum() / monto[y].sum(),
        "deteccion_por_modalidad_pct": {m: 100 * float(R[(mod == m) & y].mean()) for m in MODALIDADES},
        "legitimas_bloqueadas_pct": 100 * float(R[~y].mean()),
        "legitimas_desafiadas_pct": 0.0,
    }
    return out


def metricas_modelo(dec, y, monto, mod, peso_leg=1.0):
    det = dec >= politica.DESAFIAR
    blq = dec >= politica.RETENER
    vp_b = int((blq & y).sum())
    fp = float((blq & ~y).sum()) * peso_leg
    return {
        "fp_pct": 100 * fp / max(vp_b + fp, 1e-9),
        "deteccion_casos_pct": 100 * float(det[y].mean()),
        "deteccion_monto_pct": 100 * monto[det & y].sum() / monto[y].sum(),
        "deteccion_por_modalidad_pct": {m: 100 * float(det[(mod == m) & y].mean()) for m in MODALIDADES},
        "fraude_retenido_o_rechazado_pct": 100 * float(blq[y].mean()),
        "legitimas_desafiadas_pct": 100 * float((dec[~y] == politica.DESAFIAR).mean()),
        "legitimas_retenidas_pct": 100 * float((dec[~y] == politica.RETENER).mean()),
        "legitimas_rechazadas_pct": 100 * float((dec[~y] == politica.RECHAZAR).mean()),
        "legitimas_bloqueadas_pct": 100 * float(blq[~y].mean()),
        "n_retenidas": int((dec == politica.RETENER).sum()),
        "n_rechazadas": int((dec == politica.RECHAZAR).sum()),
        "n_desafiadas": int((dec == politica.DESAFIAR).sum()),
        "fp_en_rechazadas": int(((dec == politica.RECHAZAR) & ~y).sum()),
    }


def cargar_todo():
    tx, ev, cl = datos.cargar()
    X = datos.matriz_atributos(tx, ev, cl)
    modelo = xgb.XGBClassifier()
    modelo.load_model(str(datos.DIR_ART / "modelo_tx.json"))
    umb = json.loads((datos.DIR_ART / "umbrales.json").read_text(encoding="utf-8"))
    return tx, X, modelo, umb


def fmt(d):
    return json.dumps(d, indent=2, ensure_ascii=False)


def main():
    tx, X, modelo, umb = cargar_todo()
    va = (tx["fecha_hora"] >= datos.FIN_ENTRENAMIENTO).values
    y = tx["es_fraude"].values[va] == 1
    monto = tx["monto_cop"].values[va].astype(float)
    mod = tx["modalidad_fraude"].fillna("").values[va]
    Xv = X[va]
    p = modelo.predict_proba(Xv)[:, 1]
    t_des, t_ret = umb["umbral_desafiar"], umb["umbral_retener"]
    fan = Xv[:, F.NOMBRES_ATRIBUTOS.index("fanin_destino_7d")]
    rep = Xv[:, F.NOMBRES_ATRIBUTOS.index("reputacion_destino")]
    dec_modelo = politica.decidir(p, fan, rep, t_des, t_ret)
    dec, cod_piso = politica.decidir_con_piso(p, Xv, t_des, t_ret)  # modelo + piso de reglas de seguridad
    R = alerta_reglas(Xv)

    n_f, n_l = int(y.sum()), int((~y).sum())
    peso = (n_f * (1 - TASA_REAL) / TASA_REAL) / n_l  # legitimas re-ponderadas
    base = metricas_base(R, y, monto, mod)
    mdl = metricas_modelo(dec, y, monto, mod)
    base_p = metricas_base(R, y, monto, mod, peso)
    mdl_p = metricas_modelo(dec, y, monto, mod, peso)

    # Verificacion fuera de muestra: umbrales elegidos SOLO con septiembre, evaluados en octubre.
    sep = (tx["fecha_hora"].values[va] < "2026-10-01")
    base_full = deteccion_base_por_modalidad(alerta_reglas(X), tx["es_fraude"].values == 1,
                                             tx["modalidad_fraude"].fillna("").values)
    base_sep = deteccion_base_por_modalidad(R[sep], y[sep], mod[sep])
    bmin = {m: max(base_full[m], base_sep[m]) for m in MODALIDADES}
    ts_d, ts_r = politica.elegir_umbrales(p[sep], y[sep].astype(int), mod[sep], bmin)
    dec_oct, _ = politica.decidir_con_piso(p[~sep], Xv[~sep], ts_d, ts_r)
    oos = metricas_modelo(dec_oct, y[~sep], monto[~sep], mod[~sep])
    oos["umbrales_elegidos_con_septiembre"] = {"desafiar": ts_d, "retener": ts_r}
    oos["linea_base_octubre"] = metricas_base(R[~sep], y[~sep], monto[~sep], mod[~sep])

    # ---- feed de eventos caido: el MISMO periodo con TODOS los eventos de la app eliminados (estado sin eventos
    # desde el 1-may; imputacion desde banderas activa). El modelo y los umbrales NO se reajustan.
    tx_, ev_, cl_ = datos.cargar()
    X_sin = datos.matriz_atributos(tx_, None, cl_)[va]
    p_sin = modelo.predict_proba(X_sin)[:, 1]
    dec_sin_modelo = politica.decidir(p_sin, X_sin[:, F.NOMBRES_ATRIBUTOS.index("fanin_destino_7d")],
                                      X_sin[:, F.NOMBRES_ATRIBUTOS.index("reputacion_destino")], t_des, t_ret)
    dec_sin, cod_sin = politica.decidir_con_piso(p_sin, X_sin, t_des, t_ret)
    feed = metricas_modelo(dec_sin, y, monto, mod)
    feed["solo_modelo_sin_piso"] = metricas_modelo(dec_sin_modelo, y, monto, mod)

    # ---- friccion adicional por piso: filas donde el piso SUBE la decision del modelo
    def friccion(cod, dec_m, dec_f):
        out = {}
        for c, nombre in [(politica.PISO_A, "A_dispositivo_nuevo_sim_o_clave"), (politica.PISO_A_RETENER, "A_retener_vaciado_destino_nuevo"),
                          (politica.PISO_B, "B_vaciado_transferencia"), (politica.PISO_C, "C_destino_reputacion_baja_fanin")]:
            m = cod == c
            out[nombre] = {
                "sube_decision_en": int(m.sum()),
                "legitimas_desafiadas_extra": int((m & ~y & (dec_f == politica.DESAFIAR)).sum()),
                "legitimas_retenidas_extra": int((m & ~y & (dec_f >= politica.RETENER)).sum()),
                "fraudes_que_pasan_a_desafiar": int((m & y & (dec_m == politica.APROBAR) & (dec_f == politica.DESAFIAR)).sum()),
                "fraudes_que_pasan_a_retener": int((m & y & (dec_f >= politica.RETENER) & (dec_m < politica.RETENER)).sum()),
            }
        return out
    fric = {"con_eventos": friccion(cod_piso, dec_modelo, dec), "feed_caido": friccion(cod_sin, dec_sin_modelo, dec_sin)}

    reporte = {
        "periodo": "validacion 2026-09-01..2026-10-31",
        "n_tx": int(va.sum()), "n_fraudes": n_f, "n_legitimas": n_l,
        "umbrales": {"desafiar": t_des, "retener": t_ret},
        "linea_base": base, "modelo_politica": mdl,
        "proyeccion_1_en_10000": {"peso_legitimas": peso, "linea_base": base_p, "modelo_politica": mdl_p,
                                  "supuesto": "legitimas re-ponderadas; fraudes y tasas de deteccion constantes"},
        "verificacion_fuera_de_muestra_octubre": oos,
        "modelo_sin_piso_con_eventos": metricas_modelo(dec_modelo, y, monto, mod),
        "feed_caido_sin_eventos": feed,
        "friccion_por_piso": fric,
    }
    (datos.DIR_ART / "reporte_validacion.json").write_text(fmt(reporte), encoding="utf-8")

    def linea(nombre, b, m):
        print(f"{nombre:38s} {b:>12} {m:>12}")

    print(f"Validacion sep-oct: {int(va.sum())} tx, {n_f} fraudes, {n_l} legitimas")
    print(f"Umbrales: desafiar={t_des:.4f}  retener={t_ret:.4f}\n")
    linea("", "Linea base", "Modelo")
    linea("FP % (entre bloqueadas)", f"{base['fp_pct']:.1f}", f"{mdl['fp_pct']:.1f}")
    linea("Deteccion % casos", f"{base['deteccion_casos_pct']:.1f}", f"{mdl['deteccion_casos_pct']:.1f}")
    linea("Deteccion % monto", f"{base['deteccion_monto_pct']:.1f}", f"{mdl['deteccion_monto_pct']:.1f}")
    linea("Fraude retenido/rechazado %", f"{base['deteccion_casos_pct']:.1f}", f"{mdl['fraude_retenido_o_rechazado_pct']:.1f}")
    for m in MODALIDADES:
        linea(f"  deteccion {m} %", f"{base['deteccion_por_modalidad_pct'][m]:.1f}",
              f"{mdl['deteccion_por_modalidad_pct'][m]:.1f}")
    linea("Legitimas desafiadas %", "0.00", f"{mdl['legitimas_desafiadas_pct']:.2f}")
    linea("Legitimas retenidas %", f"{base['legitimas_bloqueadas_pct']:.2f}", f"{mdl['legitimas_retenidas_pct']:.2f}")
    linea("Legitimas rechazadas %", "0.00", f"{mdl['legitimas_rechazadas_pct']:.2f}")
    print("\nFeed de eventos CAIDO (sep-oct sin ningun evento): solo modelo -> modelo + piso")
    f0 = feed["solo_modelo_sin_piso"]
    linea("FP %", f"{f0['fp_pct']:.1f}", f"{feed['fp_pct']:.1f}")
    linea("Deteccion % casos", f"{f0['deteccion_casos_pct']:.1f}", f"{feed['deteccion_casos_pct']:.1f}")
    for m in MODALIDADES:
        linea(f"  deteccion {m} %", f"{f0['deteccion_por_modalidad_pct'][m]:.1f}", f"{feed['deteccion_por_modalidad_pct'][m]:.1f}")
    linea("Legitimas desafiadas %", f"{f0['legitimas_desafiadas_pct']:.2f}", f"{feed['legitimas_desafiadas_pct']:.2f}")
    linea("Legitimas retenidas %", f"{f0['legitimas_retenidas_pct']:.2f}", f"{feed['legitimas_retenidas_pct']:.2f}")
    print("\nFriccion adicional por piso (con eventos / feed caido):")
    for k in fric["con_eventos"]:
        a, b = fric["con_eventos"][k], fric["feed_caido"][k]
        print(f"  {k:36s} sube={a['sube_decision_en']:4d}/{b['sube_decision_en']:4d}  leg.desaf+={a['legitimas_desafiadas_extra']}/{b['legitimas_desafiadas_extra']}  "
              f"leg.ret+={a['legitimas_retenidas_extra']}/{b['legitimas_retenidas_extra']}  fraude->desaf={a['fraudes_que_pasan_a_desafiar']}/{b['fraudes_que_pasan_a_desafiar']}  fraude->ret={a['fraudes_que_pasan_a_retener']}/{b['fraudes_que_pasan_a_retener']}")
    print(f"\nProyeccion a tasa real 1/10.000 (peso legitimas x{peso:.1f}):")
    linea("FP % proyectado", f"{base_p['fp_pct']:.1f}", f"{mdl_p['fp_pct']:.1f}")
    print("\nFuera de muestra (umbrales de septiembre, evaluado octubre):")
    print(f"  umbrales {ts_d:.4f}/{ts_r:.4f}  FP {oos['fp_pct']:.1f} % (base {oos['linea_base_octubre']['fp_pct']:.1f} %), "
          f"deteccion {oos['deteccion_casos_pct']:.1f} % (base {oos['linea_base_octubre']['deteccion_casos_pct']:.1f} %), "
          f"legit. desafiadas {oos['legitimas_desafiadas_pct']:.2f} %")
    print("  por modalidad (modelo/base):", {m: (round(oos['deteccion_por_modalidad_pct'][m], 1),
          round(oos['linea_base_octubre']['deteccion_por_modalidad_pct'][m], 1)) for m in MODALIDADES})


if __name__ == "__main__":
    main()
