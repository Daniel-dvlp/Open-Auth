// Prueba de carga de Escudo Yuma (k6). 90 % POST /v1/score/transaccion + 10 % POST /v1/auth/login.
//
// Perfil: constant-arrival-rate 150 it/s durante 60 s ("sostenida") y un pico de 300 it/s durante 15 s ("pico", empieza a los 65 s).
// Datos (generados con load/preparar_datos.py, no versionados los de credenciales):
//   load/transacciones_carga.json  transacciones de transacciones.csv SIN es_fraude/modalidad_fraude
//   load/usuarios_carga.json       cuenta de servicio (token para scoring) + 1000 clientes de prueba para el login
// Ejecución (imagen fija, sin instalar k6):  ver docs/patrones.md (Prueba de carga)
//
// Rate limiting: el login tiene tope por llave (5 por 5 min) y por IP (30 por 5 min). La prueba NO lo desactiva: reparte los logins entre
// 1000 llaves (cada una se usa <= 2 veces) y, como todos salen de la misma IP, se levanta SOLO el tope por IP con el perfil de carga
// docker-compose.carga.yml (Seguridad__Login__MaxIntentosPorIp). El valor por defecto/producción no cambia.
import http from 'k6/http';
import exec from 'k6/execution';
import { check } from 'k6';
import { SharedArray } from 'k6/data';
import { Counter } from 'k6/metrics';

const BASE = __ENV.BASE_URL || 'http://api:8080';
const txs = new SharedArray('transacciones', () => JSON.parse(open('./transacciones_carga.json')));
const usuarios = JSON.parse(open('./usuarios_carga.json'));
const clientes = new SharedArray('clientes', () => usuarios.clientes);
const decisiones = {
  aprobar: new Counter('decision_aprobar'), desafiar: new Counter('decision_desafiar'),
  retener: new Counter('decision_retener'), rechazar: new Counter('decision_rechazar'),
};
const JSON_HDR = { 'Content-Type': 'application/json' };

export const options = {
  scenarios: {
    sostenida: { executor: 'constant-arrival-rate', rate: 150, timeUnit: '1s', duration: '60s', preAllocatedVUs: 100, maxVUs: 400, exec: 'iteracion' },
    pico: { executor: 'constant-arrival-rate', rate: 300, timeUnit: '1s', duration: '15s', startTime: '65s', preAllocatedVUs: 200, maxVUs: 600, exec: 'iteracion' },
  },
  thresholds: {
    http_req_duration: ['p(99)<300'],
    http_req_failed: ['rate<0.01'],
    // desglose informativo por tipo de petición (no aborta la prueba)
    'http_req_duration{tipo:score}': ['p(99)<300'],
    'http_req_duration{tipo:login}': ['p(99)<300'],
    'http_req_duration{scenario:sostenida}': ['p(99)<300'],
    'http_req_duration{scenario:pico}': ['p(99)<300'],
    'http_req_duration{scenario:sostenida,tipo:login}': ['p(99)<300'],
    'http_req_duration{scenario:pico,tipo:login}': ['p(99)<300'],
    'http_req_duration{scenario:sostenida,tipo:score}': ['p(99)<300'],
    'http_req_duration{scenario:pico,tipo:score}': ['p(99)<300'],
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

export function setup() {
  const s = usuarios.servicio;
  const r = http.post(`${BASE}/v1/auth/login`, JSON.stringify({ llave: s.llave, clave: s.clave, id_dispositivo: s.id_dispositivo, departamento_ip: s.departamento_ip }),
    { headers: JSON_HDR, tags: { tipo: 'setup' } });
  const token = r.status === 200 ? r.json('token') : null;
  if (!token) throw new Error(`no se obtuvo token de servicio (HTTP ${r.status})`);
  return { token }; // vigencia 15 min > duración de la prueba (~80 s)
}

export function iteracion(datos) {
  // __ITER es único por escenario; el desfase del pico evita repetir exactamente las mismas filas.
  const i = exec.scenario.iterationInTest + (exec.scenario.name === 'pico' ? 7919 : 0);
  if (i % 10 === 9) { // 10 % login
    const c = clientes[Math.floor(i / 10) % clientes.length];
    const r = http.post(`${BASE}/v1/auth/login`, JSON.stringify({ llave: c.llave, clave: c.clave, id_dispositivo: c.id_dispositivo, departamento_ip: c.departamento_ip }),
      { headers: JSON_HDR, tags: { tipo: 'login' } });
    check(r, { 'login 200': (x) => x.status === 200 });
  } else { // 90 % scoring
    const tx = txs[i % txs.length];
    const r = http.post(`${BASE}/v1/score/transaccion`, JSON.stringify(tx),
      { headers: { ...JSON_HDR, Authorization: `Bearer ${datos.token}` }, tags: { tipo: 'score' } });
    const ok = check(r, { 'score 200': (x) => x.status === 200 });
    if (ok) { const d = r.json('decision'); if (decisiones[d]) decisiones[d].add(1); }
  }
}



export function handleSummary(data) {
  const m = data.metrics;
  const t = (nombre) => {
    const v = m[nombre] && m[nombre].values;
    return v ? { p50_ms: v.med, p90_ms: v['p(90)'], p95_ms: v['p(95)'], p99_ms: v['p(99)'], max_ms: v.max, avg_ms: v.avg } : null;
  };
  const resumen = {
    generado_utc: new Date().toISOString(),
    perfil: { sostenida: '150 it/s x 60 s', pico: '300 it/s x 15 s (desde los 65 s)', mezcla: '90 % score / 10 % login' },
    duracion_s: data.state.testRunDurationMs / 1000,
    iteraciones: m.iterations.values.count,
    peticiones: m.http_reqs.values.count,
    rps_promedio: m.http_reqs.values.rate,
    latencia_http: t('http_req_duration'),
    latencia_score: t('http_req_duration{tipo:score}'),
    latencia_login: t('http_req_duration{tipo:login}'),
    latencia_sostenida: t('http_req_duration{scenario:sostenida}'),
    latencia_pico: t('http_req_duration{scenario:pico}'),
    latencia_login_sostenida: t('http_req_duration{scenario:sostenida,tipo:login}'),
    latencia_login_pico: t('http_req_duration{scenario:pico,tipo:login}'),
    latencia_score_sostenida: t('http_req_duration{scenario:sostenida,tipo:score}'),
    latencia_score_pico: t('http_req_duration{scenario:pico,tipo:score}'),
    http_req_failed_rate: m.http_req_failed.values.rate,
    checks_ok_rate: m.checks ? m.checks.values.rate : null,
    iteraciones_descartadas: m.dropped_iterations ? m.dropped_iterations.values.count : 0,
    decisiones: Object.fromEntries(Object.keys(decisiones).map((k) => [k, m[`decision_${k}`] ? m[`decision_${k}`].values.count : 0])),
    thresholds: Object.fromEntries(Object.entries(m).filter(([, v]) => v.thresholds).map(([k, v]) => [k, Object.fromEntries(Object.entries(v.thresholds).map(([e, r]) => [e, r.ok]))])),
  };
  return { '/reports/k6_resumen.json': JSON.stringify(resumen, null, 2), stdout: textoConsola(resumen) };
}

function textoConsola(r) {
  const f = (x) => (x === null || x === undefined ? 'n/d' : x.toFixed(1));
  const l = r.latencia_http;
  return `\nk6: ${r.iteraciones} iteraciones, ${r.rps_promedio.toFixed(1)} req/s, errores ${(r.http_req_failed_rate * 100).toFixed(2)} %, descartadas ${r.iteraciones_descartadas}\n`
    + `latencia ms  p50 ${f(l.p50_ms)}  p95 ${f(l.p95_ms)}  p99 ${f(l.p99_ms)}  max ${f(l.max_ms)}\n`
    + `thresholds: ${JSON.stringify(r.thresholds)}\n`;
}
