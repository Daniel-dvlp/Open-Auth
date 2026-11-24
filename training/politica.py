"""Politica de decision (aprobar / desafiar / retener / rechazar) y seleccion de umbrales.

Decision sobre el puntaje p del modelo (probabilidad de fraude):
    p <  umbral_desafiar                    -> aprobar   (0)
    umbral_desafiar <= p < umbral_retener   -> desafiar  (1)  (autenticacion adicional)
    p >= umbral_retener                     -> retener   (2)
    p >= umbral_retener  Y  fanin_destino_7d >= RECHAZO_FANIN_MIN
                         Y  reputacion_destino <= RECHAZO_REP_MAX -> rechazar (3)
Los parametros de rechazo son FIJOS a priori (no se ajustan con validacion).

PISO DE REGLAS DE SEGURIDAD (independiente del modelo; ver `piso_reglas`): decision minima segun
combinaciones de senales de la propia tx, para no depender de que el feed de eventos haya llegado.
La decision final es max(decision del modelo, piso). Parametros FIJOS a priori.
    A) dispositivo_nuevo Y (cambio_sim_48h O cambio_clave_24h) Y salida          -> >= desafiar
       ... y ademas monto_sobre_saldo >= 0,9 Y destino_nuevo                    -> >= retener (analista; sin bloqueo de cuenta)
    B) vaciado: transferencia, destino_nuevo, monto >= 90 % del saldo, >= 100.000 -> >= desafiar (advertir)
    C) reputacion_destino <= 30 Y fanin_destino_7d >= 3                          -> >= desafiar (verificar)

Seleccion de umbrales (en validacion, ver README):
  * umbral_desafiar = el MAS ALTO (menos friccion) tal que la deteccion (>= desafiar)
    sea >= 62,6 % + margen global y >= max(linea base completa, linea base validacion) + margen
    en CADA modalidad.
  * umbral_retener = el MAS ALTO (menos FP) tal que lo retenido/rechazado capture al
    menos 62,6 % + margen del fraude (mismo nivel de contencion que hoy bloquean las reglas).
    Sin esta guarda, la minimizacion pura del FP degenera en no retener casi nada.
"""
from __future__ import annotations

import numpy as np

import features as F

RECHAZO_FANIN_MIN = 3
RECHAZO_REP_MAX = 30.0
OBJ_DETECCION_GLOBAL = 0.626
MARGEN = 0.10  # holgura sobre la linea base: pocos casos por modalidad (engano_qr ~40 en validacion)
APROBAR, DESAFIAR, RETENER, RECHAZAR = 0, 1, 2, 3
NOMBRE_DECISION = {0: "aprobar", 1: "desafiar", 2: "retener", 3: "rechazar"}


def decidir(p, fanin_destino, rep_destino, umbral_desafiar, umbral_retener,
            fanin_min=RECHAZO_FANIN_MIN, rep_max=RECHAZO_REP_MAX):
    p = np.asarray(p, dtype=float)
    d = np.zeros(p.shape, dtype=np.int8)
    d[p >= umbral_desafiar] = DESAFIAR
    ret = p >= umbral_retener
    d[ret] = RETENER
    d[ret & (np.asarray(fanin_destino) >= fanin_min) & (np.asarray(rep_destino) <= rep_max)] = RECHAZAR
    return d


# ---------------------------------------------------------------- piso de reglas de seguridad
PISO_SOBRE_SALDO = 0.9          # "vaciado": monto >= 90 % del saldo
PISO_TOL = 1e-6                 # holgura: la matriz es float32 (0,9 -> 0,89999998) y C# usa double
PISO_MONTO_VACIADO_MIN = 100_000
PISO_C_REP_MAX = 30.0
PISO_C_FANIN_MIN = 3
PISO_NINGUNO, PISO_A, PISO_A_RETENER, PISO_B, PISO_C = 0, 1, 2, 3, 4
MOTIVO_PISO = {
    PISO_A: "Regla de seguridad: dispositivo nuevo junto con cambio de SIM o de clave recientes en una operacion de salida",
    PISO_A_RETENER: "Regla de seguridad: dispositivo nuevo con cambio de SIM o de clave y casi todo el saldo hacia un destino nuevo",
    PISO_B: "Regla de seguridad: transferencia que vacia el saldo hacia un destino nuevo (posible ingenieria social)",
    PISO_C: "Regla de seguridad: el destino recibe de muchos remitentes distintos y tiene reputacion baja",
}
_IDX = {n: i for i, n in enumerate(F.NOMBRES_ATRIBUTOS)}


def piso_reglas(X):
    """X: matriz [N, n_atributos]. Devuelve (nivel_minimo[int8: 0 aprobar, 1 desafiar, 2 retener], codigo[int8]).
    codigo: PISO_NINGUNO / PISO_A / PISO_A_RETENER / PISO_B / PISO_C (la regla que fija el piso; la mas fuerte)."""
    X = np.atleast_2d(np.asarray(X, dtype=float))
    c = lambda n: X[:, _IDX[n]]
    salida = (c("op_transferencia_inmediata") + c("op_retiro_corresponsal") + c("op_retiro_cajero")) > 0
    transf = c("op_transferencia_inmediata") == 1
    vaciado_a_nuevo = (c("destino_nuevo") == 1) & (c("monto_sobre_saldo") >= PISO_SOBRE_SALDO - PISO_TOL)
    a = (c("dispositivo_nuevo") == 1) & ((c("cambio_sim_48h") == 1) | (c("cambio_clave_24h") == 1)) & salida
    a_ret = a & vaciado_a_nuevo
    b = transf & vaciado_a_nuevo & (c("monto_cop") >= PISO_MONTO_VACIADO_MIN)
    cc = (c("reputacion_destino") <= PISO_C_REP_MAX) & (c("fanin_destino_7d") >= PISO_C_FANIN_MIN)
    nivel = np.zeros(len(X), dtype=np.int8)
    codigo = np.zeros(len(X), dtype=np.int8)
    for mask, niv, cod in [(cc, DESAFIAR, PISO_C), (b, DESAFIAR, PISO_B), (a, DESAFIAR, PISO_A), (a_ret, RETENER, PISO_A_RETENER)]:
        nivel[mask] = niv   # orden de aplicacion: de menor a mayor fuerza; el ultimo en escribir gana
        codigo[mask] = cod
    return nivel, codigo


def decidir_con_piso(p, X, umbral_desafiar, umbral_retener, **kw):
    """Decision del modelo (decidir) elevada por el piso de reglas. Devuelve (decision, codigo_piso_aplicado).
    codigo_piso_aplicado != 0 solo cuando el piso SUBE la decision del modelo."""
    X = np.atleast_2d(np.asarray(X, dtype=float))
    base = decidir(p, X[:, _IDX["fanin_destino_7d"]], X[:, _IDX["reputacion_destino"]],
                   umbral_desafiar, umbral_retener, **kw)
    nivel, codigo = piso_reglas(X)
    sube = nivel > base
    return np.where(sube, nivel, base).astype(np.int8), np.where(sube, codigo, 0).astype(np.int8)


def elegir_umbrales(p, y, modalidad, base_por_modalidad, margen=MARGEN):
    """p: puntajes; y: 0/1; modalidad: array de str ('' si legitima);
    base_por_modalidad: dict modalidad -> deteccion minima de linea base (0-1)."""
    p = np.asarray(p, float)
    y = np.asarray(y).astype(bool)
    mod = np.asarray(modalidad, dtype=object)
    cand = np.sort(np.unique(p[y]))  # solo tiene sentido cortar en puntajes de fraudes
    n_f = y.sum()

    def cumple_deteccion(t):
        if (p[y] >= t).sum() / n_f < OBJ_DETECCION_GLOBAL + margen:
            return False
        for m, base in base_por_modalidad.items():
            sel = y & (mod == m)
            if sel.sum() and (p[sel] >= t).mean() < min(1.0, base + margen):
                return False
        return True

    t_des = None
    for t in cand[::-1]:
        if cumple_deteccion(t):
            t_des = float(t)
            break
    if t_des is None:
        raise RuntimeError("No existe umbral que cumpla las restricciones de deteccion")

    t_ret = t_des
    for t in cand[::-1]:
        if t >= t_des and (p[y] >= t).sum() / n_f >= OBJ_DETECCION_GLOBAL + margen:
            t_ret = float(t)
            break
    return t_des, t_ret
