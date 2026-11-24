#!/usr/bin/env python3
"""Prepara los datos de la prueba de carga k6 (solo biblioteca estándar).

  load/transacciones_carga.json : muestra de transacciones.csv SIN es_fraude / modalidad_fraude (el cuerpo del POST /v1/score/transaccion).
  load/usuarios_carga.json      : llaves/claves de prueba (de seed/usuarios_prueba.json) + el departamento de clientes.csv,
                                  para el 10 % de logins. Contiene secretos de PRUEBA: ignorado por git y nunca se imprime.

Uso: python load/preparar_datos.py [--datos ../Caso_de_estudio_Yuma_Pay/03_Datos] [--n-tx 4000] [--n-usuarios 1000]
"""
import argparse
import csv
import json
import random
from pathlib import Path

OCULTAS = {"es_fraude", "modalidad_fraude"}
ENTEROS = {"monto_cop", "saldo_antes_cop", "destino_nuevo", "dispositivo_nuevo", "cambio_clave_24h",
           "cambio_sim_48h", "ip_departamento_distinto", "antiguedad_cuenta_dias"}
RAIZ = Path(__file__).resolve().parent.parent


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--datos", type=Path, default=RAIZ.parent / "Caso_de_estudio_Yuma_Pay" / "03_Datos")
    ap.add_argument("--n-tx", type=int, default=4000)
    ap.add_argument("--n-usuarios", type=int, default=1000)
    ap.add_argument("--seed", type=Path, default=RAIZ / "seed" / "usuarios_prueba.json", help="credenciales sembradas (perfil de carga: seed/usuarios_carga_seed.json)")
    args = ap.parse_args()
    rng = random.Random(42)  # determinista

    with open(args.datos / "transacciones.csv", encoding="utf-8-sig", newline="") as f:
        filas = list(csv.DictReader(f))
    # ~8 % de operaciones fraudulentas (la etiqueta solo se usa para muestrear; NO viaja en el JSON): ejercita retener/rechazar y alertas.
    fraude = [r for r in filas if r["es_fraude"] == "1"]
    legit = [r for r in filas if r["es_fraude"] != "1"]
    n_f = min(len(fraude), args.n_tx * 8 // 100)
    muestra = rng.sample(fraude, n_f) + rng.sample(legit, args.n_tx - n_f)
    rng.shuffle(muestra)
    txs = [{k: (int(v) if k in ENTEROS else v) for k, v in r.items() if k not in OCULTAS} for r in muestra]
    (RAIZ / "load" / "transacciones_carga.json").write_text(json.dumps(txs, ensure_ascii=False), encoding="utf-8")
    print(f"transacciones_carga.json: {len(txs)} transacciones (sin es_fraude/modalidad_fraude)")

    ruta_seed = args.seed
    if not ruta_seed.exists():
        print(f"{ruta_seed} no existe: levante la API (Seed__Habilitado=true) y repita para generar usuarios_carga.json")
        return
    creds = json.loads(ruta_seed.read_text(encoding="utf-8"))
    # clientes.csv: id_cliente,<...>,departamento(3.ª columna)
    with open(args.datos / "clientes.csv", encoding="utf-8-sig", newline="") as f:
        lector = csv.reader(f)
        next(lector)
        deptos = {fila[0]: fila[2] for fila in lector}
    clientes = [c for c in creds if c["rol"] == "cliente"]
    servicio = next(c for c in creds if c["rol"] == "servicio")
    usuarios = [{"llave": c["llave"], "clave": c["clave"], "id_dispositivo": c["id_dispositivo"],
                 "departamento_ip": deptos.get(c["id_cliente"], "Antioquia")} for c in rng.sample(clientes, min(args.n_usuarios, len(clientes)))]
    out = {"servicio": {"llave": servicio["llave"], "clave": servicio["clave"], "id_dispositivo": servicio["id_dispositivo"], "departamento_ip": "Antioquia"},
           "clientes": usuarios}
    (RAIZ / "load" / "usuarios_carga.json").write_text(json.dumps(out), encoding="utf-8")
    print(f"usuarios_carga.json: {len(usuarios)} clientes + 1 cuenta de servicio (secretos de prueba, no se imprimen)")


if __name__ == "__main__":
    main()
