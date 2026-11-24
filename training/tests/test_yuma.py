"""Pruebas del motor Escudo Yuma: no fuga, atributos prohibidos, paridad ONNX y score de login."""
import copy
import json
from pathlib import Path

import numpy as np
import pandas as pd
import pytest

import features as F
from features import EstadoEnLinea, NOMBRES_ATRIBUTOS, PROHIBIDOS, a_epoch
from score_login import puntuar_login

RAIZ = Path(__file__).resolve().parents[1]
ART = RAIZ / "artefactos"


def _tx(i, ts, cid="C1", op="transferencia_inmediata", monto=100000, dest="EXT1", tdest="otra_entidad", **kw):
    d = dict(id_transaccion=f"T{i:05d}", ts=ts, id_cliente=cid, tipo_operacion=op, canal="app",
             monto_cop=monto, saldo_antes_cop=500000, id_destino=dest, tipo_destino=tdest,
             destino_nuevo=0, id_dispositivo="D1", dispositivo_nuevo=0, cambio_clave_24h=0,
             cambio_sim_48h=0, departamento_ip="Antioquia", ip_departamento_distinto=0,
             antiguedad_cuenta_dias=400)
    d.update(kw)
    return d


def _ev(i, ts, cid="C1", tipo="inicio_sesion", disp="D1", res="exitoso", dep="Antioquia"):
    return dict(id_evento=f"E{i:05d}", ts=ts, id_cliente=cid, tipo_evento=tipo, id_dispositivo=disp,
                departamento_ip=dep, resultado=res)


def _escenario():
    t0 = a_epoch("2026-07-01 08:00:00")
    txs, evs = [], []
    for k in range(60):
        ts = t0 + k * 1800
        txs.append(_tx(k, ts, cid=f"C{k % 5}", dest=f"EXT{k % 7}", monto=20000 + 1000 * k,
                       destino_nuevo=int(k % 4 == 0)))
        if k % 3 == 0:
            evs.append(_ev(k, ts - 120, cid=f"C{k % 5}", res="fallido" if k % 6 == 0 else "exitoso"))
        if k % 11 == 0:
            evs.append(_ev(1000 + k, ts - 60, cid=f"C{k % 5}", tipo="registro_dispositivo", disp="DX"))
    return txs, evs


def test_no_fuga_sintetico():
    """Los atributos de una tx no cambian si se agregan tx/eventos posteriores."""
    txs, evs = _escenario()
    corte = 30
    t_corte = txs[corte - 1]["ts"]
    parcial = F.calcular_atributos(txs[:corte], [e for e in evs if e["ts"] <= t_corte])
    completo = F.calcular_atributos(txs, evs)
    np.testing.assert_array_equal(np.array(parcial), np.array(completo[:corte]))


def test_no_fuga_datos_reales():
    import datos
    if not (datos.DIR_DATOS / "transacciones.csv").exists():
        pytest.skip("datos no disponibles")
    tx, ev, cl = datos.cargar()
    n = 15000
    t_corte = int(tx["ts"].iloc[n - 1])
    vinc = datos.vinculacion(cl)
    tx_p, ev_p = tx.iloc[:n], ev[ev["ts"] <= t_corte]
    parcial = F.calcular_atributos(datos.filas_tx(tx_p), datos.filas_ev(ev_p), vinc)
    # el completo se corta a n+8000 para acelerar; basta con que haya tx y eventos posteriores
    tx_c, ev_c = tx.iloc[: n + 8000], ev[ev["ts"] <= int(tx["ts"].iloc[n + 7999])]
    completo = F.calcular_atributos(datos.filas_tx(tx_c), datos.filas_ev(ev_c), vinc)
    np.testing.assert_array_equal(np.array(parcial), np.array(completo[:n]))


def test_atributos_prohibidos_fuera():
    assert not (set(NOMBRES_ATRIBUTOS) & PROHIBIDOS)
    for n in NOMBRES_ATRIBUTOS:
        if n == "ip_departamento_distinto":  # bandera de la propia tx (no es el departamento del cliente)
            continue
        for p in ("departamento", "rango_edad", "tipo_cliente", "fraude", "mula_confirmada", "modalidad"):
            assert p not in n, f"{n} contiene {p}"
    assert len(set(NOMBRES_ATRIBUTOS)) == len(NOMBRES_ATRIBUTOS)
    # el orden exportado coincide con el de features.py
    exp = json.loads((ART / "atributos.json").read_text(encoding="utf-8"))
    assert exp["atributos"] == NOMBRES_ATRIBUTOS


def test_vector_tiene_n_atributos():
    est = EstadoEnLinea()
    v = est.procesar_tx(_tx(1, a_epoch("2026-07-01 03:00:00")))
    assert len(v) == F.N_ATRIBUTOS
    assert v[NOMBRES_ATRIBUTOS.index("es_madrugada")] == 1.0


def test_paridad_onnx_xgboost():
    import onnxruntime as ort
    import xgboost as xgb
    par = pd.read_csv(ART / "paridad.csv")
    X = par[NOMBRES_ATRIBUTOS].values.astype(np.float32)
    assert len(par) == 200
    sesion = ort.InferenceSession(str(ART / "modelo_tx.onnx"), providers=["CPUExecutionProvider"])
    nombre_in = sesion.get_inputs()[0].name
    salidas = sesion.run(None, {nombre_in: X})
    prob = next(s for s in salidas if getattr(s, "ndim", 0) == 2 and s.shape[1] == 2)
    m = xgb.XGBClassifier()
    m.load_model(str(ART / "modelo_tx.json"))
    p_xgb = m.predict_proba(X)[:, 1]
    assert np.max(np.abs(prob[:, 1] - p_xgb)) < 1e-4
    assert np.max(np.abs(p_xgb - par["score_python"].values)) < 1e-4


def test_score_login_sube_con_dispositivo_nuevo_y_fallidos():
    t = a_epoch("2026-07-01 14:00:00")

    def estado():
        est = EstadoEnLinea()
        for k in range(5):
            est.procesar_evento(_ev(k, t - 86400 * 3 + k * 600))  # historial normal, Antioquia, D1
        return est

    base = puntuar_login(estado(), _ev(100, t))
    est = estado()
    for k in range(3):
        est.procesar_evento(_ev(50 + k, t - 600 + k * 60, res="fallido", disp="D9"))
    sospechoso = puntuar_login(est, _ev(101, t, disp="D9"))
    assert 0.0 <= base[0] <= 1.0 and 0.0 <= sospechoso[0] <= 1.0
    assert sospechoso[0] > base[0] + 0.4
    assert any("Dispositivo no visto" in r for r in sospechoso[1])
    assert any("fallido" in r for r in sospechoso[1])


def test_score_login_cambio_sim_e_ip():
    t = a_epoch("2026-07-01 14:00:00")
    est = EstadoEnLinea()
    for k in range(5):
        est.procesar_evento(_ev(k, t - 86400 * 3 + k * 600))
    a = puntuar_login(est, _ev(10, t))[0]
    est.procesar_evento(_ev(11, t - 3600, tipo="cambio_sim_detectado"))
    b, razones = puntuar_login(est, _ev(12, t, dep="Chocó"))
    assert b > a
    assert any("SIM" in r for r in razones) and any("IP" in r for r in razones)


def test_politica_rechazo_requiere_fanin_y_reputacion():
    import politica
    d = politica.decidir([0.99, 0.99, 0.99, 0.3, 0.01], [5, 5, 1, 9, 9], [10, 80, 10, 10, 10], 0.2, 0.9)
    assert list(d) == [3, 2, 2, 1, 0]


def test_reputacion_baja_con_fanin_y_cuenta_nueva():
    t = a_epoch("2026-07-10 10:00:00")
    est = EstadoEnLinea({"C77": t - 5 * 86400})
    for k in range(4):
        est.procesar_tx(_tx(k, t - 3600 + k * 60, cid=f"V{k}", dest="C77", tdest="cuenta_yuma", destino_nuevo=1))
    assert est.reputacion("C77", "cuenta_yuma", t) <= 30.0
    assert est.reputacion("COM1", "comercio", t) == 100.0


# ------------------------------------------------------------------------------------------
# Robustez ante feed de eventos caido: imputacion desde banderas + piso de reglas de seguridad
# ------------------------------------------------------------------------------------------
def _modelo_y_umbrales():
    import xgboost as xgb
    m = xgb.XGBClassifier()
    m.load_model(str(ART / "modelo_tx.json"))
    return m, json.loads((ART / "umbrales.json").read_text(encoding="utf-8"))


def _tx_caso_descrito(t):
    """Dispositivo nuevo + cambio SIM + cambio clave + 95 % del saldo a un destino nuevo (el caso fail-open)."""
    return _tx(1, t, monto=475000, destino_nuevo=1, dispositivo_nuevo=1, cambio_clave_24h=1, cambio_sim_48h=1,
               ip_departamento_distinto=1, antiguedad_cuenta_dias=200, id_dispositivo="DX")


def _decision_caso(eventos):
    import politica
    m, u = _modelo_y_umbrales()
    t = a_epoch("2026-07-15 14:05:00")
    est = EstadoEnLinea(None, F.cargar_imputacion())
    for e in eventos(t):
        est.procesar_evento(e)
    v = est.procesar_tx(_tx_caso_descrito(t))
    X = np.array([v], dtype=np.float32)
    p = float(m.predict_proba(X)[0, 1])
    dec, piso = politica.decidir_con_piso([p], X, u["umbral_desafiar"], u["umbral_retener"])
    return p, int(dec[0]), int(piso[0]), u


def test_imputacion_desde_banderas_sin_eventos_y_con_eventos_validos():
    imp = F.cargar_imputacion()
    assert imp is not None and set(imp) == set(F.CLAVES_IMPUTACION)
    col = {n: i for i, n in enumerate(NOMBRES_ATRIBUTOS)}
    t = a_epoch("2026-07-15 14:05:00")
    tx = _tx_caso_descrito(t)
    # sin eventos: las banderas fijan los atributos derivados (y el dispositivo no cuenta como visto)
    v = EstadoEnLinea(None, imp).atributos_tx(tx)
    assert v[col["min_desde_cambio_sim"]] == imp["min_desde_cambio_sim"]
    assert v[col["min_desde_cambio_clave"]] == imp["min_desde_cambio_clave"]
    assert v[col["min_desde_registro_dispositivo"]] == imp["min_desde_registro_dispositivo"]
    assert v[col["dispositivo_visto_antes"]] == 0.0
    # sin imputacion el atributo vale "sin evento" (comportamiento anterior)
    assert EstadoEnLinea(None, None).atributos_tx(tx)[col["min_desde_cambio_sim"]] == F.TOPE_MIN_SIN_EVENTO
    # con un evento consistente dentro de la ventana, manda el evento real
    est = EstadoEnLinea(None, imp)
    est.procesar_evento(_ev(1, t - 600, tipo="cambio_sim_detectado"))
    assert est.atributos_tx(tx)[col["min_desde_cambio_sim"]] == 10.0
    # un evento mas viejo que la ventana (3 dias) con la bandera en 1 tambien se imputa
    est = EstadoEnLinea(None, imp)
    est.procesar_evento(_ev(1, t - 3 * 86400, tipo="cambio_sim_detectado"))
    assert est.atributos_tx(tx)[col["min_desde_cambio_sim"]] == imp["min_desde_cambio_sim"]
    # sin bandera no se imputa nada
    sin = _tx(2, t, id_dispositivo="D1")
    assert EstadoEnLinea(None, imp).atributos_tx(sin)[col["min_desde_cambio_sim"]] == F.TOPE_MIN_SIN_EVENTO


def test_caso_fail_open_con_estado_vacio_ya_no_aprueba():
    p, dec, piso, u = _decision_caso(lambda t: [])
    assert dec >= 1, f"fail-open: p={p:.3f}"
    assert dec == 2, "el piso de reglas lo manda a retener (analista)"
    assert p >= u["umbral_desafiar"], "el modelo, reentrenado con dropout de eventos, ya lo desafia por si solo"


def test_con_eventos_coherentes_sigue_reteniendo():
    p, dec, piso, _ = _decision_caso(lambda t: [
        _ev(1, t - 600, tipo="cambio_sim_detectado"), _ev(2, t - 400, tipo="cambio_clave"),
        _ev(3, t - 300, tipo="registro_dispositivo", disp="DX")])
    assert dec == 2


def test_piso_reglas_cada_regla_y_no_baja_decisiones():
    import politica as P
    col = {n: i for i, n in enumerate(NOMBRES_ATRIBUTOS)}

    def fila(**kw):
        x = np.zeros(F.N_ATRIBUTOS)
        x[col["reputacion_destino"]] = 100
        x[col["monto_cop"]] = 500000
        for k, v in kw.items():
            x[col[k]] = v
        return x

    X = np.array([
        fila(),                                                                                            # nada
        fila(op_retiro_cajero=1, dispositivo_nuevo=1, cambio_sim_48h=1, monto_sobre_saldo=0.3),            # A -> desafiar
        fila(op_transferencia_inmediata=1, dispositivo_nuevo=1, cambio_clave_24h=1),                       # A (sin vaciado) -> desafiar
        fila(op_transferencia_inmediata=1, dispositivo_nuevo=1, cambio_clave_24h=1,
             destino_nuevo=1, monto_sobre_saldo=0.95),                                                     # A + vaciado -> retener
        fila(op_pago_qr=1, dispositivo_nuevo=1, cambio_sim_48h=1),                                         # A exige salida -> nada
        fila(op_transferencia_inmediata=1, destino_nuevo=1, monto_sobre_saldo=0.92),                       # B -> desafiar
        fila(op_transferencia_inmediata=1, destino_nuevo=1, monto_sobre_saldo=0.92, monto_cop=50000),      # B exige >= 100.000 -> nada
        fila(op_pago_qr=1, destino_nuevo=1, monto_sobre_saldo=0.99),                                       # B exige transferencia -> nada
        fila(reputacion_destino=30, fanin_destino_7d=3),                                                   # C -> desafiar
        fila(reputacion_destino=31, fanin_destino_7d=3),                                                   # C no se cumple
    ])
    nivel, cod = P.piso_reglas(X)
    assert list(nivel) == [0, 1, 1, 2, 0, 1, 0, 0, 1, 0]
    assert list(cod) == [0, P.PISO_A, P.PISO_A, P.PISO_A_RETENER, 0, P.PISO_B, 0, 0, P.PISO_C, 0]
    # el piso solo SUBE: un puntaje que ya retiene/rechaza no baja; un aprobar se eleva al piso
    p = np.array([0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0])
    dec, motivo = P.decidir_con_piso(p, X, 0.5, 0.9)
    assert list(dec) == [0, 1, 1, 2, 0, 1, 0, 0, 1, 0]
    assert all(P.MOTIVO_PISO[c].startswith("Regla de seguridad") for c in set(motivo[motivo > 0]))
    dec2, motivo2 = P.decidir_con_piso(np.full(10, 0.99), X, 0.5, 0.9)
    assert (dec2 >= 2).all() and (motivo2 == 0).all()   # ya retenian: el piso no cambia nada
