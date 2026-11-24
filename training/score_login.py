"""Score de ingreso (inicio de sesion) determinista y explicable, 0-1.

score = min(1, suma de pesos de las senales activas).  Cada senal aporta una razon.

  Senal                                              Peso
  dispositivo no visto antes para el cliente          0.30  (solo si el cliente ya tiene dispositivos conocidos)
  intentos fallidos en la ultima hora                 0.12 por intento, tope 0.36
  intentos fallidos extra en 24 h (24 h - 1 h)        0.03 por intento, tope 0.09
  cambio de SIM detectado en las ultimas 48 h         0.25
  cambio de clave en las ultimas 24 h                 0.20
  registro de dispositivo en las ultimas 24 h         0.10
  departamento de la IP distinto al habitual          0.20  (habitual = moda de inicios de sesion exitosos
                                                            y tx previas del cliente, aprendida del historial;
                                                            requiere >= 3 observaciones; NO usa clientes.csv)
  hora de madrugada (00:00-04:59)                     0.08

Solo usa el estado previo del cliente (EstadoEnLinea) y los campos del propio evento.
Uso: puntuar_login(estado, ev) -> (score, [razones]); luego estado.procesar_evento(ev).
"""
from __future__ import annotations

from typing import List, Tuple

from features import DIA, HORA, EstadoEnLinea

PESOS = {
    "dispositivo_no_visto": 0.30,
    "fallido_1h_por_intento": 0.12, "fallido_1h_tope": 0.36,
    "fallido_24h_extra_por_intento": 0.03, "fallido_24h_extra_tope": 0.09,
    "cambio_sim_48h": 0.25,
    "cambio_clave_24h": 0.20,
    "registro_dispositivo_24h": 0.10,
    "ip_distinta_habitual": 0.20,
    "madrugada": 0.08,
}
MIN_OBS_IP = 3


def puntuar_login(est: EstadoEnLinea, ev: dict) -> Tuple[float, List[str]]:
    cid, ts = ev["id_cliente"], ev["ts"]
    score = 0.0
    razones: List[str] = []

    conocidos = est.dispositivos.get(cid)
    if conocidos and ev["id_dispositivo"] not in conocidos:
        score += PESOS["dispositivo_no_visto"]
        razones.append("Dispositivo no visto antes para este cliente")

    f1 = est.fallidos_en(cid, ts, HORA)
    f24 = est.fallidos_en(cid, ts, DIA)
    if f1:
        score += min(PESOS["fallido_1h_tope"], PESOS["fallido_1h_por_intento"] * f1)
        razones.append(f"{f1} intento(s) fallido(s) en la ultima hora")
    extra = f24 - f1
    if extra > 0:
        score += min(PESOS["fallido_24h_extra_tope"], PESOS["fallido_24h_extra_por_intento"] * extra)
        razones.append(f"{extra} intento(s) fallido(s) adicional(es) en 24 horas")

    t = est.ultimo_cambio_sim.get(cid)
    if t is not None and ts - t <= 2 * DIA:
        score += PESOS["cambio_sim_48h"]
        razones.append("Cambio de SIM en las ultimas 48 horas")
    t = est.ultimo_cambio_clave.get(cid)
    if t is not None and ts - t <= DIA:
        score += PESOS["cambio_clave_24h"]
        razones.append("Cambio de clave en las ultimas 24 horas")
    t = est.ultimo_registro.get(cid)
    if t is not None and ts - t <= DIA:
        score += PESOS["registro_dispositivo_24h"]
        razones.append("Dispositivo registrado en las ultimas 24 horas")

    dep = ev.get("departamento_ip")
    obs = sum(est.ip_conteo.get(cid, {}).values())
    habitual = est.ip_habitual(cid)
    if dep and habitual and obs >= MIN_OBS_IP and dep != habitual:
        score += PESOS["ip_distinta_habitual"]
        razones.append(f"Ubicacion de la IP ({dep}) distinta a la habitual ({habitual})")

    if (ts % DIA) // HORA <= 4:
        score += PESOS["madrugada"]
        razones.append("Ingreso de madrugada")

    return min(1.0, score), razones


def _evaluar_historico():
    """Sanidad: AUC del score frente a 'el cliente tiene una tx fraudulenta en los 120 min siguientes'."""
    import bisect
    import numpy as np
    from sklearn.metrics import roc_auc_score
    import datos
    import features as F

    tx, ev, cl = datos.cargar()
    ft = {}
    for r in tx[tx.es_fraude == 1].itertuples():
        ft.setdefault(r.id_cliente, []).append(r.ts)
    est = EstadoEnLinea(datos.vinculacion(cl))
    ys, ss = [], []
    flujo = F.construir_flujo(datos.filas_tx(tx), datos.filas_ev(ev))
    for ts, tipo, _, fila in flujo:
        if tipo == 0:
            est.procesar_tx(fila)
        else:
            if fila["tipo_evento"] == "inicio_sesion":
                s, _ = puntuar_login(est, fila)
                lst = ft.get(fila["id_cliente"], [])
                i = bisect.bisect_left(lst, ts)
                ys.append(int(i < len(lst) and lst[i] - ts <= 7200))
                ss.append(s)
            est.procesar_evento(fila)
    ys, ss = np.array(ys), np.array(ss)
    print(f"Inicios de sesion: {len(ys)}; seguidos de fraude <=120 min: {ys.sum()}; AUC={roc_auc_score(ys, ss):.3f}")
    for u in (0.3, 0.5, 0.7):
        m = ss >= u
        print(f"  score>={u}: {m.sum()} logins ({100*m.mean():.2f} %), precision {100*ys[m].mean() if m.any() else 0:.1f} %, "
              f"cobertura {100*ys[m].sum()/max(ys.sum(),1):.1f} %")


if __name__ == "__main__":
    _evaluar_historico()
