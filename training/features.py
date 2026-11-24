"""
Atributos en STREAMING para el motor Escudo Yuma.

Un objeto `EstadoEnLinea` procesa eventos de app y transacciones en orden
cronologico. Para cada transaccion devuelve el vector de atributos usando
SOLO el estado previo (mas los campos de la propia transaccion) y despues
actualiza el estado. No hay pandas dentro de la logica por fila: solo
diccionarios, listas y deques con ventanas de tiempo, para portar 1:1 a C#
(Dictionary<string, ...>, Queue/LinkedList, List con busqueda binaria).

Tiempo: todo en segundos enteros ("ts") sobre la hora local de Colombia
(naive, tal como viene en los CSV). hora = (ts % 86400) // 3600 ;
dia = ts // 86400.  En C#: segundos desde 1970-01-01 de la fecha local.

Orden de procesamiento: se ordena por (ts, tipo, id) con la transaccion ANTES
que los eventos del mismo segundo; asi una tx nunca ve un evento de su mismo
instante. Las tx del mismo segundo se ven entre si segun el orden de id
(es el orden de llegada al motor).

REPUTACION DE CUENTA (0-100, 100 = confiable; formula determinista):
    penal = 0
    + min(50, 15 * max(0, fanin_7d - 1))        # victimas distintas que le enviaron en 7 dias
         (solo para cuentas Yuma / otras entidades; en comercios, servicios,
          corresponsales, cajeros y operadores el fan-in es normal y no penaliza)
    + 25 si antiguedad < 15 dias ; 10 si antiguedad < 30 dias   (cuentas Yuma)
    + 20 si entradas_7d_monto > 0 y salidas_24h_monto >= 0.7 * entradas_7d_monto
         (patron mula: lo que entra sale rapido)
    reputacion = max(0, 100 - penal)
La misma funcion se aplica a la cuenta ORIGEN (id_cliente) y al DESTINO.
Para el origen se calcula sobre el estado previo (sin la tx actual).
"""
from __future__ import annotations

import bisect
import json
import math
from collections import deque
from datetime import datetime
from pathlib import Path
from typing import Dict, Iterable, List, Optional

SALIDAS = {"transferencia_inmediata", "retiro_corresponsal", "retiro_cajero"}
RETIROS = {"retiro_corresponsal", "retiro_cajero"}
TIPOS_OP = [
    "transferencia_inmediata", "pago_qr", "retiro_corresponsal", "retiro_cajero",
    "pago_servicios", "recarga_celular", "compra_tarjeta_virtual",
]
TIPOS_CON_FANIN_SOSPECHOSO = {"cuenta_yuma", "otra_entidad"}

MIN = 60
HORA = 3600
DIA = 86400
TOPE_MIN_SIN_EVENTO = 10080  # 7 dias en minutos (valor cuando no hay evento)

# ---------------------------------------------------------------------------------------------
# IMPUTACION DESDE BANDERAS (robustez ante un feed de eventos caido o tardio)
# Las banderas de la tx (dispositivo_nuevo, cambio_clave_24h, cambio_sim_48h) siempre coinciden con
# eventos de la app en los datos, asi que el modelo aprendio a decidir por los atributos derivados
# de los eventos y a ignorar las banderas. Si el feed falta, esos atributos valen "sin evento"
# (10080 min) y una tx con las tres banderas parecia inocua (fail-open).
# Regla: si la bandera dice que el evento ocurrio dentro de su ventana pero el estado no tiene un
# evento consistente (ausente o mas viejo que la ventana), el atributo derivado se IMPUTA como si el
# evento hubiera ocurrido en la ventana, con un valor fijo calculado en entrenamiento
# (artefactos/imputacion.json): la MEDIANA de los minutos observados en fraudes de entrenamiento con
# la bandera y el evento presentes. Se prefiere la mediana al "peor caso" (0 min) porque 0 queda fuera
# del soporte de los datos (los arboles extrapolan mal) y la mediana de fraude ya es una zona
# de alto riesgo; ademas el modelo se reentrena con "dropout de eventos" usando exactamente este valor.
# ---------------------------------------------------------------------------------------------
VENTANA_SIM_MIN = 48 * 60       # cambio_sim_48h
VENTANA_CLAVE_MIN = 24 * 60     # cambio_clave_24h
VENTANA_REGISTRO_MIN = 24 * 60  # dispositivo_nuevo: el registro del dispositivo ocurre en las ultimas 24 h
CLAVES_IMPUTACION = ("min_desde_cambio_sim", "min_desde_cambio_clave", "min_desde_registro_dispositivo")
RUTA_IMPUTACION = Path(__file__).with_name("artefactos") / "imputacion.json"


def cargar_imputacion(ruta: Optional[Path] = None) -> Optional[Dict[str, float]]:
    """Lee artefactos/imputacion.json -> {clave: minutos}. None si el archivo no existe (sin imputacion)."""
    ruta = Path(ruta) if ruta else RUTA_IMPUTACION
    if not ruta.exists():
        return None
    d = json.loads(ruta.read_text(encoding="utf-8"))
    return {k: float(d[k]) for k in CLAVES_IMPUTACION}

NOMBRES_ATRIBUTOS: List[str] = [
    # campos binarios de la propia tx
    "destino_nuevo", "dispositivo_nuevo", "cambio_clave_24h", "cambio_sim_48h",
    "ip_departamento_distinto",
    # monto
    "monto_cop", "log_monto", "monto_sobre_saldo",
    # tipo de operacion (one-hot)
    "op_transferencia_inmediata", "op_pago_qr", "op_retiro_corresponsal", "op_retiro_cajero",
    "op_pago_servicios", "op_recarga_celular", "op_compra_tarjeta_virtual",
    "dest_cuenta_yuma", "dest_otra_entidad",
    # tiempo / cuenta
    "hora", "es_madrugada", "antiguedad_cuenta_dias",
    # historico del cliente
    "n_tx_previas", "monto_vs_media", "monto_vs_p95", "min_desde_ultima_tx",
    # velocidad
    "n_tx_60min", "n_salidas_10min", "n_salidas_60min", "acumulado_dia_cop",
    "n_destinos_nuevos_24h",
    # eventos de la app
    "login_fallidos_1h", "login_fallidos_24h",
    "min_desde_registro_dispositivo", "min_desde_cambio_clave", "min_desde_cambio_sim",
    "dispositivo_visto_antes",
    # red de cuentas / patron mula
    "fanin_destino_7d", "n_tx_destino_7d", "destino_n_previas", "destino_frac_nuevos",
    "entradas_n_24h", "entradas_monto_24h", "entradas_n_7d", "ratio_salida_entradas",
    # reputacion
    "reputacion_origen", "reputacion_destino",
    # reglas vigentes como senales 0/1
    "R001", "R002", "R003", "R004", "R005", "R006", "R007", "R008", "R009", "R010", "R011", "R012",
]
N_ATRIBUTOS = len(NOMBRES_ATRIBUTOS)

# Atributos que NUNCA pueden entrar al modelo (auditoria de sesgo / etiquetas).
PROHIBIDOS = {
    "departamento", "rango_edad", "tipo_cliente", "es_fraude", "modalidad_fraude",
    "cuenta_mula_confirmada", "departamento_ip", "id_cliente", "id_destino",
}


def a_epoch(fecha_hora: str) -> int:
    """'2026-07-15 14:32:10' -> segundos (hora local tratada como UTC naive)."""
    d = datetime.strptime(fecha_hora, "%Y-%m-%d %H:%M:%S")
    return int((d - datetime(1970, 1, 1)).total_seconds())


def fecha_a_epoch(fecha: str) -> int:
    d = datetime.strptime(fecha, "%Y-%m-%d")
    return int((d - datetime(1970, 1, 1)).total_seconds())


class EstadoEnLinea:
    """Estado incremental. Metodos publicos: procesar_evento, atributos_tx, actualizar_tx, procesar_tx."""

    def __init__(self, vinculacion_ts: Optional[Dict[str, int]] = None,
                 imputacion: Optional[Dict[str, float]] = None):
        # fecha de vinculacion (epoch) por cuenta Yuma; dato estatico de clientes.csv
        self.vinculacion_ts: Dict[str, int] = vinculacion_ts or {}
        # valores de imputacion desde banderas (None = sin imputacion); ver CLAVES_IMPUTACION
        self.imputacion: Optional[Dict[str, float]] = imputacion
        # historico de montos por cliente
        self.n_hist: Dict[str, int] = {}
        self.suma_hist: Dict[str, float] = {}
        self.montos_ord: Dict[str, List[int]] = {}
        self.ultima_tx: Dict[str, int] = {}
        # ventanas
        self.tx_60: Dict[str, deque] = {}          # ts de todas las tx (60 min)
        self.salidas_24h: Dict[str, deque] = {}    # (ts, monto) de salidas (24 h)
        self.acum_dia: Dict[str, List[int]] = {}   # [dia, acumulado] de salidas
        self.destinos_nuevos_24h: Dict[str, deque] = {}
        self.entradas_7d: Dict[str, deque] = {}    # por destino: (ts, remitente, monto)
        self.remitentes_7d: Dict[str, Dict[str, int]] = {}  # por destino: remitente -> conteo en ventana
        self.dest_n: Dict[str, int] = {}           # por destino: tx recibidas (historico)
        self.dest_nuevos: Dict[str, int] = {}      # por destino: de ellas, con destino_nuevo = 1 para el remitente
        # eventos de app
        self.fallidos_24h: Dict[str, deque] = {}
        self.ultimo_registro: Dict[str, int] = {}
        self.ultimo_cambio_clave: Dict[str, int] = {}
        self.ultimo_cambio_sim: Dict[str, int] = {}
        self.dispositivos: Dict[str, set] = {}
        # habitual de ubicacion (aprendido del historial; lo usa score_login)
        self.ip_conteo: Dict[str, Dict[str, int]] = {}

    # ------------------------------------------------------------------ eventos
    def procesar_evento(self, ev: dict) -> None:
        cid = ev["id_cliente"]
        ts = ev["ts"]
        tipo = ev["tipo_evento"]
        if tipo == "inicio_sesion":
            if ev["resultado"] == "fallido":
                self.fallidos_24h.setdefault(cid, deque()).append(ts)
            else:
                self.dispositivos.setdefault(cid, set()).add(ev["id_dispositivo"])
                self._sumar_ip(cid, ev.get("departamento_ip"))
        elif tipo == "registro_dispositivo":
            # el registro NO cuenta como "visto": visto = ya uso ese dispositivo en un login exitoso o una tx
            self.ultimo_registro[cid] = ts
        elif tipo == "cambio_clave":
            self.ultimo_cambio_clave[cid] = ts
        elif tipo == "cambio_sim_detectado":
            self.ultimo_cambio_sim[cid] = ts

    def _sumar_ip(self, cid: str, dep: Optional[str]) -> None:
        if dep:
            d = self.ip_conteo.setdefault(cid, {})
            d[dep] = d.get(dep, 0) + 1

    def ip_habitual(self, cid: str) -> Optional[str]:
        d = self.ip_conteo.get(cid)
        if not d:
            return None
        # desempate determinista: mayor conteo, luego nombre
        return sorted(d.items(), key=lambda kv: (-kv[1], kv[0]))[0][0]

    def fallidos_en(self, cid: str, ts: int, ventana: int) -> int:
        dq = self.fallidos_24h.get(cid)
        if not dq:
            return 0
        while dq and dq[0] < ts - DIA:
            dq.popleft()
        return sum(1 for t in dq if t >= ts - ventana)

    # ----------------------------------------------------------------- ventanas
    @staticmethod
    def _purgar(dq: deque, limite: int, idx: Optional[int] = None) -> None:
        if idx is None:
            while dq and dq[0] < limite:
                dq.popleft()
        else:
            while dq and dq[0][idx] < limite:
                dq.popleft()

    def _purgar_entradas(self, dest: str, ts: int) -> None:
        dq = self.entradas_7d.get(dest)
        if not dq:
            return
        rem = self.remitentes_7d[dest]
        while dq and dq[0][0] < ts - 7 * DIA:
            _, r, _ = dq.popleft()
            rem[r] -= 1
            if rem[r] <= 0:
                del rem[r]

    def fanin(self, dest: str, ts: int) -> int:
        self._purgar_entradas(dest, ts)
        return len(self.remitentes_7d.get(dest, ()))

    def _entradas(self, cuenta: str, ts: int):
        """(n_24h, monto_24h, n_7d, monto_7d) de entradas a la cuenta, solo estado previo."""
        self._purgar_entradas(cuenta, ts)
        dq = self.entradas_7d.get(cuenta)
        if not dq:
            return 0, 0, 0, 0
        n24 = m24 = 0
        m7 = 0
        for t, _, m in dq:
            m7 += m
            if t >= ts - DIA:
                n24 += 1
                m24 += m
        return n24, m24, len(dq), m7

    def _salidas_monto_24h(self, cuenta: str, ts: int) -> int:
        dq = self.salidas_24h.get(cuenta)
        if not dq:
            return 0
        self._purgar(dq, ts - DIA, 0)
        return sum(m for _, m in dq)

    # --------------------------------------------------------------- reputacion
    def reputacion(self, cuenta: str, tipo_destino: Optional[str], ts: int) -> float:
        """Ver formula en el docstring del modulo. tipo_destino None = cuenta origen (Yuma)."""
        penal = 0.0
        es_cuenta = tipo_destino is None or tipo_destino == "cuenta_yuma"
        if tipo_destino is None or tipo_destino in TIPOS_CON_FANIN_SOSPECHOSO:
            penal += min(50, 15 * max(0, self.fanin(cuenta, ts) - 1))
        if es_cuenta and cuenta in self.vinculacion_ts:
            antig = (ts - self.vinculacion_ts[cuenta]) / DIA
            if antig < 15:
                penal += 25
            elif antig < 30:
                penal += 10
        if es_cuenta:
            _, _, _, m7 = self._entradas(cuenta, ts)
            if m7 > 0 and self._salidas_monto_24h(cuenta, ts) >= 0.7 * m7:
                penal += 20
        return float(max(0.0, 100.0 - penal))

    # ---------------------------------------------------------------- atributos
    def atributos_tx(self, tx: dict) -> List[float]:
        """Vector de atributos (orden NOMBRES_ATRIBUTOS) con SOLO el estado previo."""
        cid = tx["id_cliente"]
        ts = tx["ts"]
        op = tx["tipo_operacion"]
        monto = tx["monto_cop"]
        saldo = tx["saldo_antes_cop"]
        dest = tx["id_destino"]
        tdest = tx["tipo_destino"]
        hora = (ts % DIA) // HORA
        es_salida = op in SALIDAS
        es_retiro = op in RETIROS
        es_transf = op == "transferencia_inmediata"

        # historico del cliente
        n = self.n_hist.get(cid, 0)
        if n >= 3:
            media = self.suma_hist[cid] / n
            lst = self.montos_ord[cid]
            p95 = lst[int(0.95 * (n - 1))]
            vs_media = monto / max(media, 1.0)
            vs_p95 = monto / max(p95, 1)
        else:
            vs_media = 1.0
            vs_p95 = 1.0
        ult = self.ultima_tx.get(cid)
        min_ult = min((ts - ult) / MIN, TOPE_MIN_SIN_EVENTO) if ult is not None else TOPE_MIN_SIN_EVENTO

        # velocidad (estado previo + la tx actual cuando la regla vigente la incluye)
        dq60 = self.tx_60.get(cid)
        n_tx_60 = 0
        if dq60:
            self._purgar(dq60, ts - HORA)
            n_tx_60 = len(dq60)
        dqs = self.salidas_24h.get(cid)
        s10 = s60 = 0
        if dqs:
            self._purgar(dqs, ts - DIA, 0)
            for t, _ in dqs:
                if t >= ts - HORA:
                    s60 += 1
                    if t >= ts - 10 * MIN:
                        s10 += 1
        ad = self.acum_dia.get(cid)
        acum_prev = ad[1] if (ad and ad[0] == ts // DIA) else 0
        if es_salida:
            s10 += 1
            s60 += 1
        acum = acum_prev + (monto if es_salida else 0)
        dqn = self.destinos_nuevos_24h.get(cid)
        n_dn = 0
        if dqn:
            self._purgar(dqn, ts - DIA)
            n_dn = len(dqn)

        # eventos app
        f1 = self.fallidos_en(cid, ts, HORA)
        f24 = self.fallidos_en(cid, ts, DIA)

        def minutos(d):
            t = d.get(cid)
            return min((ts - t) / MIN, TOPE_MIN_SIN_EVENTO) if t is not None else TOPE_MIN_SIN_EVENTO

        vis = 1.0 if tx["id_dispositivo"] in self.dispositivos.get(cid, ()) else 0.0
        m_reg = minutos(self.ultimo_registro)
        m_clave = minutos(self.ultimo_cambio_clave)
        m_sim = minutos(self.ultimo_cambio_sim)

        # red de cuentas
        fan = self.fanin(dest, ts)
        n_tx_dest = len(self.entradas_7d.get(dest, ()))
        dn_prev = self.dest_n.get(dest, 0)
        # proporcion suavizada (prior 0.25 con peso 4) de remitentes para los que el destino era nuevo
        frac_nuevos = (self.dest_nuevos.get(dest, 0) + 1.0) / (dn_prev + 4.0)
        e_n24, e_m24, e_n7, e_m7 = self._entradas(cid, ts)
        sal24 = self._salidas_monto_24h(cid, ts) + (monto if es_salida else 0)
        ratio = min(sal24 / e_m7, 10.0) if e_m7 > 0 else 10.0 if es_salida else 0.0

        rep_o = self.reputacion(cid, None, ts)
        rep_d = self.reputacion(dest, tdest, ts)

        dn = tx["destino_nuevo"]
        dv = tx["dispositivo_nuevo"]
        cc = tx["cambio_clave_24h"]
        cs = tx["cambio_sim_48h"]
        ipd = tx["ip_departamento_distinto"]
        antig = tx["antiguedad_cuenta_dias"]

        # imputacion desde banderas: la bandera de la tx manda cuando el estado no tiene un evento consistente
        imp = self.imputacion
        if imp:
            if cs == 1 and m_sim > VENTANA_SIM_MIN:
                m_sim = imp["min_desde_cambio_sim"]
            if cc == 1 and m_clave > VENTANA_CLAVE_MIN:
                m_clave = imp["min_desde_cambio_clave"]
            if dv == 1 and m_reg > VENTANA_REGISTRO_MIN:
                m_reg = imp["min_desde_registro_dispositivo"]
                vis = 0.0

        reglas = [
            1 if (es_transf and monto >= 1_000_000) else 0,
            1 if (es_salida and dv == 1 and monto >= 150_000) else 0,
            1 if (es_salida and hora <= 4 and monto >= 150_000) else 0,
            1 if (es_salida and s60 >= 4) else 0,
            1 if (es_transf and dn == 1 and monto >= 0.9 * saldo and monto >= 100_000) else 0,
            1 if (es_transf and cc == 1 and monto >= 200_000) else 0,
            1 if (es_salida and cs == 1 and monto >= 200_000) else 0,
            1 if (es_retiro and antig < 15 and monto >= 300_000) else 0,
            1 if (es_salida and ipd == 1 and monto >= 250_000) else 0,
            1 if (es_salida and acum >= 1_500_000) else 0,
            1 if (op == "pago_qr" and monto >= 200_000) else 0,
            1 if (es_retiro and monto >= 600_000) else 0,
        ]

        v = [
            dn, dv, cc, cs, ipd,
            monto, math.log1p(monto), min(monto / max(saldo, 1), 5.0),
        ]
        v += [1 if op == t else 0 for t in TIPOS_OP]
        v += [1 if tdest == "cuenta_yuma" else 0, 1 if tdest == "otra_entidad" else 0]
        v += [hora, 1 if hora <= 4 else 0, antig]
        v += [n, vs_media, vs_p95, min_ult]
        v += [n_tx_60, s10, s60, acum, n_dn]
        v += [f1, f24, m_reg, m_clave, m_sim, vis]
        v += [fan, n_tx_dest, dn_prev, frac_nuevos, e_n24, e_m24, e_n7, ratio]
        v += [rep_o, rep_d]
        v += reglas
        return [float(x) for x in v]

    # --------------------------------------------------------------- actualizar
    def actualizar_tx(self, tx: dict) -> None:
        cid = tx["id_cliente"]
        ts = tx["ts"]
        op = tx["tipo_operacion"]
        monto = tx["monto_cop"]
        dest = tx["id_destino"]
        self.n_hist[cid] = self.n_hist.get(cid, 0) + 1
        self.suma_hist[cid] = self.suma_hist.get(cid, 0.0) + monto
        bisect.insort(self.montos_ord.setdefault(cid, []), monto)
        self.ultima_tx[cid] = ts
        self.tx_60.setdefault(cid, deque()).append(ts)
        if op in SALIDAS:
            self.salidas_24h.setdefault(cid, deque()).append((ts, monto))
            ad = self.acum_dia.get(cid)
            dia = ts // DIA
            if ad is None or ad[0] != dia:
                self.acum_dia[cid] = [dia, monto]
            else:
                ad[1] += monto
        if tx["destino_nuevo"] == 1:
            self.destinos_nuevos_24h.setdefault(cid, deque()).append(ts)
        self.entradas_7d.setdefault(dest, deque()).append((ts, cid, monto))
        self.dest_n[dest] = self.dest_n.get(dest, 0) + 1
        self.dest_nuevos[dest] = self.dest_nuevos.get(dest, 0) + tx["destino_nuevo"]
        rem = self.remitentes_7d.setdefault(dest, {})
        rem[cid] = rem.get(cid, 0) + 1
        self.dispositivos.setdefault(cid, set()).add(tx["id_dispositivo"])
        self._sumar_ip(cid, tx.get("departamento_ip"))

    def procesar_tx(self, tx: dict) -> List[float]:
        v = self.atributos_tx(tx)
        self.actualizar_tx(tx)
        return v


def construir_flujo(tx_filas: Iterable[dict], ev_filas: Iterable[dict]) -> List[tuple]:
    """Mezcla cronologica: (ts, tipo, id, fila). tipo 0 = tx (antes), 1 = evento."""
    flujo = [(r["ts"], 0, r["id_transaccion"], r) for r in tx_filas]
    flujo += [(r["ts"], 1, r["id_evento"], r) for r in ev_filas]
    flujo.sort(key=lambda x: (x[0], x[1], x[2]))
    return flujo


_DEFECTO = object()  # "usar artefactos/imputacion.json si existe"


def calcular_atributos(tx_filas: List[dict], ev_filas: List[dict],
                       vinculacion_ts: Optional[Dict[str, int]] = None,
                       imputacion=_DEFECTO) -> List[List[float]]:
    """Corre el streaming completo; devuelve vectores en el orden de tx_filas (por id_transaccion).
    imputacion: dict de imputacion, None (sin imputar) o por defecto el de artefactos/imputacion.json."""
    if imputacion is _DEFECTO:
        imputacion = cargar_imputacion()
    est = EstadoEnLinea(vinculacion_ts, imputacion)
    salida: Dict[str, List[float]] = {}
    for _, tipo, _, fila in construir_flujo(tx_filas, ev_filas):
        if tipo == 1:
            est.procesar_evento(fila)
        else:
            salida[fila["id_transaccion"]] = est.procesar_tx(fila)
    return [salida[r["id_transaccion"]] for r in tx_filas]
