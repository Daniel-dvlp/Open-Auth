"""Explicabilidad del modelo de transacciones.

Decision: en Python se usan contribuciones SHAP exactas (xgboost pred_contribs). Para .NET
(donde el ONNX solo devuelve el puntaje) se exporta una APROXIMACION ADITIVA por tablas:
para cada atributo i se parte su rango en cubetas (cortes por cuantiles del entrenamiento) y
se guarda la contribucion SHAP MEDIA (en log-odds) de esa cubeta en entrenamiento.

    cubeta_i   = numero de cortes_i que son <= x_i          (busqueda binaria, upper bound)
    contrib_i  = tabla_i[cubeta_i]
    razones    = los 3 atributos con mayor contrib_i > umbral_minimo (suben el riesgo)

Es un lookup O(n_atributos) sin dependencias, 1:1 portable a C#. Su fidelidad frente a SHAP
real se mide en validacion y se guarda en explicacion.json (campo "fidelidad").
"""
from __future__ import annotations

from typing import Dict, List

import numpy as np
import xgboost as xgb

PLANTILLAS: Dict[str, str] = {
    "destino_nuevo": "Destino nuevo para el cliente",
    "dispositivo_nuevo": "Dispositivo registrado en las ultimas 24 horas",
    "cambio_clave_24h": "Cambio de clave en las ultimas 24 horas",
    "cambio_sim_48h": "Cambio de SIM en las ultimas 48 horas",
    "ip_departamento_distinto": "Ubicacion distinta a la habitual",
    "monto_cop": "Monto alto ({v:.0f} COP)",
    "log_monto": "Monto alto para este tipo de operacion",
    "monto_sobre_saldo": "Consume gran parte del saldo ({v:.0%})",
    "antiguedad_cuenta_dias": "Cuenta muy reciente ({v:.0f} dias)",
    "n_tx_previas": "Poco historial del cliente ({v:.0f} operaciones previas)",
    "monto_vs_media": "Monto {v:.1f} veces superior a su promedio",
    "monto_vs_p95": "Monto {v:.1f} veces superior a su percentil 95 historico",
    "min_desde_ultima_tx": "Actividad tras un periodo inusual de inactividad ({v:.0f} min)",
    "n_tx_60min": "Varias operaciones en la ultima hora",
    "n_salidas_10min": "Rafaga de salidas de dinero ({v:.0f} en 10 min)",
    "n_salidas_60min": "Muchas salidas de dinero en 60 min ({v:.0f})",
    "acumulado_dia_cop": "Acumulado de salidas del dia elevado ({v:.0f} COP)",
    "n_destinos_nuevos_24h": "Varios destinos nuevos en 24 horas ({v:.0f})",
    "login_fallidos_1h": "Inicios de sesion fallidos en la ultima hora ({v:.0f})",
    "login_fallidos_24h": "Inicios de sesion fallidos en 24 horas ({v:.0f})",
    "min_desde_registro_dispositivo": "Dispositivo registrado hace solo {v:.0f} min",
    "min_desde_cambio_clave": "Cambio de clave hace solo {v:.0f} min",
    "min_desde_cambio_sim": "Cambio de SIM hace solo {v:.0f} min",
    "dispositivo_visto_antes": "Dispositivo nunca visto para este cliente",
    "fanin_destino_7d": "El destino recibio de {v:.0f} remitentes distintos en 7 dias",
    "n_tx_destino_7d": "Destino con muchas operaciones recibidas en 7 dias ({v:.0f})",
    "entradas_n_24h": "La cuenta origen recibio fondos hace poco ({v:.0f} entradas en 24 h)",
    "entradas_monto_24h": "La cuenta origen recibio {v:.0f} COP en 24 h",
    "entradas_n_7d": "La cuenta origen recibio fondos de terceros en 7 dias ({v:.0f})",
    "ratio_salida_entradas": "Lo que sale supera lo que entro recientemente (razon {v:.1f})",
    "reputacion_origen": "Reputacion baja de la cuenta origen ({v:.0f}/100)",
    "reputacion_destino": "Reputacion baja de la cuenta destino ({v:.0f}/100)",
    "hora": "Hora inusual ({v:.0f}h)",
    "es_madrugada": "Operacion de madrugada",
    "dest_cuenta_yuma": "Transferencia a otra cuenta Yuma",
    "dest_otra_entidad": "Transferencia a otra entidad",
}
for _i in range(1, 13):
    PLANTILLAS[f"R{_i:03d}"] = f"Activa la regla vigente R{_i:03d}"
PLANTILLA_POR_DEFECTO = "Patron inusual en {nombre}"
UMBRAL_MIN_CONTRIB = 0.15  # log-odds minimos para mencionar una razon


def _cortes(col: np.ndarray, max_cortes: int = 24) -> np.ndarray:
    u = np.unique(col)
    if len(u) <= max_cortes + 1:
        return u[1:].astype(float)  # un corte por cada valor distinto (menos el minimo)
    q = np.unique(np.quantile(col, np.linspace(0, 1, max_cortes + 2)[1:-1]))
    return q.astype(float)


def contribuciones_shap(modelo: xgb.XGBClassifier, X: np.ndarray) -> np.ndarray:
    return modelo.get_booster().predict(xgb.DMatrix(X), pred_contribs=True)[:, :-1]


def aproximar(X: np.ndarray, cortes: List[np.ndarray], tablas: List[np.ndarray]) -> np.ndarray:
    out = np.zeros(X.shape, dtype=float)
    for i in range(X.shape[1]):
        b = np.searchsorted(cortes[i], X[:, i], side="right")
        out[:, i] = tablas[i][b]
    return out


def construir_explicacion(modelo, Xtr, Xva, nombres: List[str]) -> dict:
    sh_tr = contribuciones_shap(modelo, Xtr)
    cortes, tablas = [], []
    for i in range(Xtr.shape[1]):
        c = _cortes(Xtr[:, i])
        b = np.searchsorted(c, Xtr[:, i], side="right")
        t = np.zeros(len(c) + 1)
        for k in range(len(c) + 1):
            m = b == k
            t[k] = sh_tr[m, i].mean() if m.any() else 0.0
        cortes.append(c)
        tablas.append(t)

    # fidelidad en validacion sobre las 1000 filas de mayor puntaje (donde se piden razones)
    p = modelo.predict_proba(Xva)[:, 1]
    top = np.argsort(-p)[:1000]
    sh = contribuciones_shap(modelo, Xva[top])
    ap = aproximar(Xva[top], cortes, tablas)
    top3_ap = np.argsort(-ap, axis=1)[:, :3]
    top3_sh = np.argsort(-sh, axis=1)[:, :3]
    top1_en_top3 = float(np.mean([top3_sh[r, 0] in top3_ap[r] for r in range(len(top))]))
    solape = float(np.mean([len(set(top3_sh[r]) & set(top3_ap[r])) / 3 for r in range(len(top))]))
    corr = float(np.corrcoef(sh.ravel(), ap.ravel())[0, 1])

    imp = modelo.get_booster().get_score(importance_type="gain")
    imp_n = {nombres[int(k[1:])] if k.startswith("f") and k[1:].isdigit() else k: float(v) for k, v in imp.items()}
    tot = sum(imp_n.values())
    importancia = {k: v / tot for k, v in sorted(imp_n.items(), key=lambda kv: -kv[1])}
    shap_abs = np.abs(sh_tr).mean(axis=0)
    shap_medio = {nombres[i]: float(shap_abs[i]) for i in np.argsort(-shap_abs)}

    return {
        "metodo_python": "SHAP exacto: xgboost Booster.predict(DMatrix, pred_contribs=True) (log-odds)",
        "metodo_dotnet": "Aproximacion aditiva por tablas: contrib_i = tabla_i[#cortes_i <= x_i]; "
                         "razones = top 3 con contrib > umbral_minimo_contribucion",
        "umbral_minimo_contribucion": UMBRAL_MIN_CONTRIB,
        "max_razones": 3,
        "atributos": nombres,
        "cortes": [c.tolist() for c in cortes],
        "tablas": [t.tolist() for t in tablas],
        "plantillas": {n: PLANTILLAS.get(n, PLANTILLA_POR_DEFECTO.replace("{nombre}", n)) for n in nombres},
        "importancia_gain": importancia,
        "shap_abs_medio_train": shap_medio,
        "fidelidad": {
            "filas": int(len(top)),
            "top3_contiene_top1_shap": top1_en_top3,
            "solape_top3": solape,
            "correlacion_contribuciones": corr,
        },
    }


def razones(x: np.ndarray, expl: dict) -> List[str]:
    """Razones (texto) para un vector de atributos usando la aproximacion exportada."""
    nombres = expl["atributos"]
    contrib = []
    for i, n in enumerate(nombres):
        cortes = expl["cortes"][i]
        b = int(np.searchsorted(cortes, x[i], side="right"))
        contrib.append(expl["tablas"][i][b])
    orden = np.argsort(contrib)[::-1][: expl["max_razones"]]
    out = []
    for i in orden:
        if contrib[i] > expl["umbral_minimo_contribucion"]:
            out.append(expl["plantillas"][nombres[i]].format(v=float(x[i])))
    return out
