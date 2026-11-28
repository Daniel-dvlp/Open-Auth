const $ = selector => document.querySelector(selector);
const state = {
  token: sessionStorage.getItem('yuma-token') || '',
  analyst: sessionStorage.getItem('yuma-analyst') || '',
  demo: false,
  alerts: [],
  filtered: [],
  selectedId: null,
  selectedAlert: null,
  reputation: null,
  view: 'pendiente',
  activePage: 'pendiente',
  expiresUtc: null,
  visibleCount: 7,
  loading: false,
};
let demoReplay = { estado: 'inactivo', procesadas: 0, inicio_utc: null, fin_utc: null, mensaje: null };
let replayPollTimer = null;

const demoAlerts = [
  { id: 'a129c812-2b2c-4f44-91e0-1a8300000001', id_transaccion: 'TX-YU-2026-48219', id_cliente: 'CLI-YM-0084', monto_cop: 1480000, puntaje: .982, decision: 'retener', razones: ['Transferencia por el 94 % del saldo disponible.', 'El destino no aparece en el historial de la cuenta.', 'El dispositivo no coincide con los ingresos recientes.'], estado: 'pendiente', creada_utc: new Date(Date.now() - 4 * 60000).toISOString() },
  { id: 'a129c812-2b2c-4f44-91e0-1a8300000002', id_transaccion: 'TX-YU-2026-48203', id_cliente: 'CLI-YM-0157', monto_cop: 780000, puntaje: .713, decision: 'desafiar', razones: ['Monto por encima del patrón habitual de la cuenta.', 'Destino nuevo con actividad reciente elevada.'], estado: 'pendiente', creada_utc: new Date(Date.now() - 11 * 60000).toISOString() },
  { id: 'a129c812-2b2c-4f44-91e0-1a8300000003', id_transaccion: 'TX-YU-2026-48196', id_cliente: 'CLI-YM-0211', monto_cop: 2200000, puntaje: .991, decision: 'retener', razones: ['Cambio de SIM detectado hace menos de 48 horas.', 'Cambio de clave reciente y dispositivo no visto.', 'Transferencia de alto valor a un beneficiario nuevo.'], estado: 'pendiente', creada_utc: new Date(Date.now() - 19 * 60000).toISOString() },
  { id: 'a129c812-2b2c-4f44-91e0-1a8300000004', id_transaccion: 'TX-YU-2026-48172', id_cliente: 'CLI-YM-0059', monto_cop: 450000, puntaje: .997, decision: 'rechazar', razones: ['El destino tiene señales de recepción asociadas a varias víctimas.', 'La reputación del destino es baja.'], estado: 'pendiente', creada_utc: new Date(Date.now() - 31 * 60000).toISOString() },
  { id: 'a129c812-2b2c-4f44-91e0-1a8300000005', id_transaccion: 'TX-YU-2026-48151', id_cliente: 'CLI-YM-0130', monto_cop: 1100000, puntaje: .614, decision: 'desafiar', razones: ['La cuenta tiene poca antigüedad.', 'El monto supera el promedio de sus transferencias.'], estado: 'pendiente', creada_utc: new Date(Date.now() - 48 * 60000).toISOString() },
  { id: 'a129c812-2b2c-4f44-91e0-1a8300000006', id_transaccion: 'TX-YU-2026-48124', id_cliente: 'CLI-YM-0192', monto_cop: 985000, puntaje: .978, decision: 'retener', razones: ['Múltiples intentos fallidos de ingreso en la última hora.', 'Transferencia casi total a un destino nuevo.'], estado: 'pendiente', creada_utc: new Date(Date.now() - 73 * 60000).toISOString() },
];
const demoReputations = {
  'CLI-YM-0084': { puntaje: 18, reputacion: 82, senales: ['destino nuevo', 'monto inusual'], bloqueo_definitivo: false },
  'CLI-YM-0157': { puntaje: 9, reputacion: 91, senales: ['cuenta activa'], bloqueo_definitivo: false },
  'CLI-YM-0211': { puntaje: 34, reputacion: 66, senales: ['cambio de SIM reciente', 'dispositivo nuevo'], bloqueo_definitivo: false },
  'CLI-YM-0059': { puntaje: 78, reputacion: 22, senales: ['fan-in elevado', 'patrón de retiro rápido'], bloqueo_definitivo: false },
  'CLI-YM-0130': { puntaje: 12, reputacion: 88, senales: ['cuenta reciente'], bloqueo_definitivo: false },
  'CLI-YM-0192': { puntaje: 26, reputacion: 74, senales: ['intentos fallidos recientes'], bloqueo_definitivo: false },
};

const icon = (name, cls = '') => {
  const paths = {
    arrow: '<path d="M5 12h14m-6-6 6 6-6 6"/>',
    chevron: '<path d="m9 18 6-6-6-6"/>',
    clock: '<circle cx="12" cy="12" r="8.5"/><path d="M12 7v5l3 2"/>',
    user: '<circle cx="12" cy="8" r="3.2"/><path d="M5 20c.8-3.2 3.1-4.8 7-4.8s6.2 1.6 7 4.8"/>',
    lock: '<rect x="5" y="10" width="14" height="11" rx="2"/><path d="M8 10V7a4 4 0 0 1 8 0v3m-4 5v2"/>',
    card: '<rect x="3" y="5" width="18" height="14" rx="2"/><path d="M3 10h18m-14 5h3"/>',
    alert: '<path d="M12 3 2.8 19h18.4L12 3Z"/><path d="M12 9v4m0 3h.01"/>',
  };
  return `<svg class="${cls}" viewBox="0 0 24 24" aria-hidden="true">${paths[name] || paths.arrow}</svg>`;
};

function escapeHtml(value = '') {
  return String(value).replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[char]);
}

function money(value, compact = false) {
  const amount = Number(value) || 0;
  if (compact && amount >= 1_000_000) return `$${(amount / 1_000_000).toLocaleString('es-CO', { maximumFractionDigits: 1 })} M`;
  if (compact && amount >= 100_000) return `$${(amount / 1_000).toLocaleString('es-CO', { maximumFractionDigits: 0 })} mil`;
  return new Intl.NumberFormat('es-CO', { style: 'currency', currency: 'COP', maximumFractionDigits: 0 }).format(amount);
}

function riskPercent(score) {
  const number = Number(score);
  if (!Number.isFinite(number)) return 0;
  return Math.max(0, Math.min(100, Math.round(number <= 1 ? number * 100 : number)));
}

function formattedRisk(score) {
  const value = Number(score);
  if (!Number.isFinite(value)) return '0';
  const percent = value <= 1 ? value * 100 : value;
  return percent % 1 === 0 ? String(percent) : percent.toFixed(1);
}

function prettyDecision(decision = '') {
  const labels = { aprobar: 'Aprobada', desafiar: 'Desafiada', retener: 'Retenida', rechazar: 'Rechazada' };
  return labels[decision] || decision.replaceAll('_', ' ');
}

function timeAgo(date) {
  if (!date) return 'Hora no disponible';
  const seconds = Math.max(0, Math.floor((Date.now() - new Date(date).getTime()) / 1000));
  if (!Number.isFinite(seconds)) return 'Hora no disponible';
  if (seconds < 60) return 'Hace unos segundos';
  if (seconds < 3600) return `Hace ${Math.floor(seconds / 60)} min`;
  if (seconds < 86400) return `Hace ${Math.floor(seconds / 3600)} h`;
  return new Intl.DateTimeFormat('es-CO', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }).format(new Date(date));
}

function fullDate(date) {
  if (!date) return '—';
  const parsed = new Date(date);
  return Number.isNaN(parsed.getTime()) ? '—' : new Intl.DateTimeFormat('es-CO', { dateStyle: 'medium', timeStyle: 'short' }).format(parsed);
}

async function api(path, options = {}) {
  if (state.demo) return mockApi(path, options);
  const headers = { accept: 'application/json', ...(options.body ? { 'content-type': 'application/json' } : {}), ...(state.token ? { authorization: `Bearer ${state.token}` } : {}), ...options.headers };
  let response;
  try { response = await fetch(path, { ...options, headers }); }
  catch { throw new Error('No se pudo conectar con la API. Comprueba que el backend esté levantado y vuelve a intentar.'); }
  const text = await response.text();
  let payload = null;
  try { payload = text ? JSON.parse(text) : null; } catch { payload = text; }
  // Un 401 con sesión abierta es un token vencido; sin sesión (el propio ingreso) es una llave o clave incorrecta.
  if (response.status === 401 && state.token) {
    signOut();
    throw new Error('Tu sesión terminó. Inicia sesión para continuar.');
  }
  if (response.status === 401) throw new Error(payload?.detail || 'La llave o la clave no son correctas.');
  if (response.status === 429) throw new Error('Demasiados intentos con esta llave. Por seguridad se bloquea 5 minutos; espera y vuelve a intentar (o usa otro analista).');
  if (!response.ok) {
    const message = payload?.detail || payload?.title || (response.status === 403 ? 'Esta cuenta no tiene rol de analista.' : `La solicitud falló (${response.status}).`);
    throw new Error(message);
  }
  return payload;
}

async function mockApi(path, options = {}) {
  const url = new URL(path, location.origin);
  if (url.pathname === '/v1/alertas' && options.method !== 'POST') {
    const requestedState = url.searchParams.get('estado') || 'pendiente';
    let walletAlerts = [];
    try { walletAlerts = JSON.parse(localStorage.getItem('yuma-wallet-local-demo-v1') || '{}').alertas || []; }
    catch { walletAlerts = []; }
    const alerts = [...walletAlerts, ...demoAlerts].filter(alert => alert.estado === requestedState);
    return { total: alerts.length, alertas: alerts.map(({ ...alert }) => alert) };
  }
  if (url.pathname.startsWith('/v1/cuentas/') && url.pathname.endsWith('/reputacion')) {
    const idCuenta = decodeURIComponent(url.pathname.split('/')[3]);
    return { id_cuenta: idCuenta, ...(demoReputations[idCuenta] || { puntaje: 12, reputacion: 88, senales: ['operación de demostración'], bloqueo_definitivo: false }), actualizado_utc: new Date().toISOString() };
  }
  if (url.pathname === '/v1/score/transaccion' && options.method === 'POST') {
    const tx = JSON.parse(options.body || '{}');
    let risk = .1;
    const reasons = [];
    if (tx.destino_nuevo === 1) { risk += .16; reasons.push('El destino no aparece en el historial reciente.'); }
    if (tx.dispositivo_nuevo === 1) { risk += .18; reasons.push('La operación llega desde un dispositivo no visto.'); }
    if (tx.cambio_clave_24h === 1) { risk += .17; reasons.push('Se detectó un cambio de clave en las últimas 24 horas.'); }
    if (tx.cambio_sim_48h === 1) { risk += .22; reasons.push('Se detectó un cambio de SIM en las últimas 48 horas.'); }
    if (tx.ip_departamento_distinto === 1) { risk += .12; reasons.push('La IP corresponde a un departamento distinto al habitual.'); }
    if (tx.monto_cop >= tx.saldo_antes_cop * .8 && tx.destino_nuevo === 1) { risk += .24; reasons.push('El monto consume casi todo el saldo y va a un destino nuevo.'); }
    if (tx.antiguedad_cuenta_dias < 30) { risk += .07; reasons.push('La cuenta tiene menos de 30 días de antigüedad.'); }
    const knownDestination = demoReputations[tx.id_destino];
    if (knownDestination && knownDestination.reputacion <= 30) { risk = .997; reasons.unshift('Destino de ejemplo con reputación baja y señales de recepción asociadas a varias cuentas.'); }
    risk = Math.min(.997, risk);
    const decision = knownDestination?.reputacion <= 30 ? 'rechazar' : risk >= .977 ? 'retener' : risk >= .461 ? 'desafiar' : 'aprobar';
    if (!reasons.length) reasons.push('Sin señales elevadas en este ejemplo de demostración.');
    const authSignal = tx.dispositivo_nuevo === 1 || tx.cambio_clave_24h === 1 || tx.cambio_sim_48h === 1;
    const warningCase = decision === 'desafiar' && tx.destino_nuevo === 1 && tx.saldo_antes_cop > 0 && tx.monto_cop >= tx.saldo_antes_cop * .8 && !authSignal;
    const response = { decision, decision_interna: decision === 'aprobar' ? 'aprobar' : decision === 'retener' || decision === 'rechazar' ? decision : warningCase ? 'advertir' : 'verificar', puntaje: Number(risk.toFixed(6)), razones: reasons, latencia_ms: 2.4, latencia_total_ms: 3.1, version_modelo: 'simulación-local' };
    if (decision === 'retener' || decision === 'rechazar') {
      const id = globalThis.crypto?.randomUUID?.() || `demo-${Date.now()}`;
      demoAlerts.unshift({ id, id_transaccion: tx.id_transaccion, id_cliente: tx.id_cliente, monto_cop: Number(tx.monto_cop), puntaje: response.puntaje, decision, razones, estado: 'pendiente', creada_utc: new Date().toISOString() });
      demoReputations[tx.id_cliente] ||= { puntaje: 0, reputacion: 100, senales: [], bloqueo_definitivo: false };
    }
    return response;
  }
  if (url.pathname === '/v1/auth/dispositivos' && options.method === 'POST') {
    const body = JSON.parse(options.body || '{}');
    return { id_dispositivo: body.id_dispositivo, nuevo_registro: true, registrado_utc: new Date().toISOString() };
  }
  if (url.pathname === '/v1/sandbox/replay' && options.method === 'POST') {
    if (demoReplay.estado === 'ejecutando') throw new Error('Ya hay un replay de ejemplo en curso.');
    demoReplay = { estado: 'ejecutando', procesadas: 0, inicio_utc: new Date().toISOString(), fin_utc: null, mensaje: 'Simulación local de demostración.' };
    window.setTimeout(() => { demoReplay = { ...demoReplay, estado: 'terminado', procesadas: 1200, fin_utc: new Date().toISOString(), mensaje: 'Replay de ejemplo terminado. No se consultó el sandbox.' }; }, 5000);
    return { ...demoReplay };
  }
  if (url.pathname === '/v1/sandbox/replay' && options.method !== 'POST') return { ...demoReplay };
  const resolver = url.pathname.match(/^\/v1\/alertas\/([^/]+)\/resolver$/);
  if (resolver && options.method === 'POST') {
    let walletDemo = null;
    let alert = demoAlerts.find(item => item.id === resolver[1]);
    if (!alert) {
      try {
        walletDemo = JSON.parse(localStorage.getItem('yuma-wallet-local-demo-v1') || '{}');
        alert = walletDemo.alertas?.find(item => item.id === resolver[1]);
      } catch { walletDemo = null; }
    }
    if (!alert || alert.estado !== 'pendiente') throw new Error('La alerta de ejemplo ya fue resuelta.');
    const request = JSON.parse(options.body || '{}');
    alert.estado = 'resuelta';
    alert.resolucion = request.resolucion;
    alert.accion = request.accion;
    alert.bloqueo_definitivo = request.accion === 'bloquear_cuenta';
    if (walletDemo) localStorage.setItem('yuma-wallet-local-demo-v1', JSON.stringify(walletDemo));
    if (alert.bloqueo_definitivo && demoReputations[alert.id_cliente]) demoReputations[alert.id_cliente].bloqueo_definitivo = true;
    return { id_alerta: alert.id, estado: 'resuelta', resolucion: request.resolucion, accion: request.accion, puntaje_reputacion: demoReputations[alert.id_cliente]?.puntaje || 0, bloqueo_definitivo: alert.bloqueo_definitivo };
  }
  throw new Error('Esta acción no está disponible en el modo de demostración.');
}

function setApiHealth(online) {
  $('#health-dot').classList.toggle('offline', !online);
  $('#health-label').textContent = online ? 'API operativa' : 'API sin conexión';
  $('#health-meta').textContent = online ? 'Escudo Yuma · v1' : 'Revisa que el backend esté activo';
}

function setModeCopy() {
  const demo = state.demo;
  $('#score-effect-title').textContent = demo ? 'Esta evaluación se simula en el navegador.' : 'Esta evaluación sí llega al motor.';
  $('#score-effect-copy').textContent = demo ? 'No ejecuta el modelo ONNX, no cambia el estado real y no crea alertas en el backend.' : 'Actualiza su estado en línea y auditoría; si la decisión es retener o rechazar, puede crear una alerta real.';
  $('#account-hint').textContent = demo ? 'Usa cuentas de ejemplo como CLI-YM-0084; la consulta permanece en este navegador.' : 'Como analista puedes consultar cualquier cuenta existente en el entorno.';
  $('#replay-warning-title').textContent = demo ? 'La simulación no reinicia el motor real.' : 'El replay reinicia el estado en línea.';
  $('#replay-warning-copy').textContent = demo ? 'Solo muestra cómo se vería el flujo; no se conecta al sandbox.' : 'Hazlo solo en un entorno de demostración; no es una acción para producción.';
  $('#replay-confirm-copy').textContent = demo ? 'Entiendo que se mostrará una simulación local del replay.' : 'Entiendo que esta ejecución restablece el estado en línea del motor.';
  $('#replay-config-copy').textContent = demo ? 'La ejecución de ejemplo es local y ficticia. Para el replay real, el backend debe habilitarlo y tener los datos del sandbox disponibles.' : 'Esta herramienta depende de que Sandbox:ReplayHabilitado esté activo y de que el sandbox y los datos estén disponibles.';
}

async function checkHealth() {
  if (state.demo) {
    $('#health-dot').classList.add('offline');
    $('#health-label').textContent = 'Modo demo';
    $('#health-meta').textContent = 'Datos sintéticos · locales';
    return;
  }
  try {
    const response = await fetch('/health/ready', { cache: 'no-store' });
    if (!response.ok) throw new Error('Health check failed');
    setApiHealth(true);
  }
  catch { setApiHealth(false); }
}

function showLogin(message = '') {
  $('#login-feedback').textContent = message;
  const dialog = $('#login-dialog');
  if (!dialog.open) dialog.showModal();
  document.body.classList.add('has-modal');
}

function signOut() {
  state.token = '';
  state.analyst = '';
  state.demo = false;
  state.expiresUtc = null;
  sessionStorage.removeItem('yuma-token');
  sessionStorage.removeItem('yuma-analyst');
  $('#profile-name').textContent = 'Analista';
  $('#profile-avatar').textContent = 'A';
  $('#analyst-chip').innerHTML = '<span class="live-dot"></span> ANALISTA';
  $('#analyst-chip').classList.remove('demo-chip');
  $('#footer-context').textContent = 'Decisiones sujetas a revisión humana';
  setModeCopy();
  if ($('#login-dialog').open) return;
  showLogin('La sesión se cerró. Vuelve a ingresar para revisar la cola.');
}

function enterDemoMode() {
  state.token = 'demo-local';
  state.analyst = 'Vista demo';
  state.demo = true;
  state.view = 'pendiente';
  state.activePage = 'pendiente';
  $('#profile-name').textContent = 'Vista demo';
  $('#profile-avatar').textContent = 'D';
  $('#analyst-chip').innerHTML = '<span class="live-dot"></span> MODO DEMO';
  $('#analyst-chip').classList.add('demo-chip');
  $('#footer-context').textContent = 'Datos sintéticos · decisiones locales, sin conexión a la API';
  setModeCopy();
  $('#login-dialog').close();
  document.body.classList.remove('has-modal');
  checkHealth();
  loadAlerts();
  renderSession();
}

function tokenClaims(token) {
  try {
    const encoded = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
    return JSON.parse(decodeURIComponent(escape(atob(encoded))));
  } catch { return {}; }
}

async function handleLogin(event) {
  event.preventDefault();
  const form = new FormData(event.currentTarget);
  const key = String(form.get('llave') || '').trim();
  const button = $('#login-submit');
  const feedback = $('#login-feedback');
  button.disabled = true;
  button.innerHTML = '<span>Verificando acceso…</span><span class="button-spinner"></span>';
  feedback.textContent = '';
  state.token = ''; // un token vencido de otra sesión no debe confundirse con una clave incorrecta
  try {
    const result = await api('/v1/auth/login', {
      method: 'POST',
      body: JSON.stringify({ llave: key, clave: form.get('clave'), id_dispositivo: `DISP-${key.toLowerCase()}`, departamento_ip: 'Bogotá D.C.' }),
    });
    if (result.resultado === 'otp') {
      const reasons = (result.razones || []).join(' · ');
      throw new Error(`El ingreso requiere verificación adicional, que todavía no está disponible en esta API.${reasons ? ` Señales: ${reasons}.` : ''}`);
    }
    if (result.resultado !== 'permitir' || !result.token) {
      throw new Error(`El acceso no fue permitido.${(result.razones || []).length ? ` ${(result.razones || []).join(' · ')}` : ''}`);
    }
    const claims = tokenClaims(result.token);
    if (claims.rol !== 'analista') throw new Error('La consola está habilitada para el rol analista. Esta cuenta tiene otro rol.');
    state.token = result.token;
    state.analyst = key;
    state.expiresUtc = result.expira_utc || null;
    sessionStorage.setItem('yuma-token', result.token);
    sessionStorage.setItem('yuma-analyst', key);
    $('#profile-name').textContent = key;
    $('#profile-avatar').textContent = key.slice(0, 1).toUpperCase();
    $('#analyst-chip').innerHTML = '<span class="live-dot"></span> ANALISTA';
    $('#analyst-chip').classList.remove('demo-chip');
    $('#footer-context').textContent = 'Decisiones sujetas a revisión humana';
    setModeCopy();
    $('#login-dialog').close();
    document.body.classList.remove('has-modal');
    renderSession();
    await loadAlerts();
  } catch (error) {
    feedback.textContent = error.message;
  } finally {
    button.disabled = false;
    button.innerHTML = '<span>Entrar a la consola</span><span>→</span>';
  }
}

function updateMetrics() {
  const total = state.view === 'pendiente' ? state.alerts.length : state.alerts.length;
  const held = state.alerts.filter(item => item.decision === 'retener').length;
  const amount = state.alerts.reduce((sum, item) => sum + (Number(item.monto_cop) || 0), 0);
  const highest = state.alerts.reduce((max, item) => Math.max(max, riskPercent(item.puntaje)), 0);
  $('#metric-total').textContent = Number(state.total ?? total).toLocaleString('es-CO');
  $('#metric-hold').textContent = held.toLocaleString('es-CO');
  $('#metric-amount').textContent = money(amount, true);
  const rawHighest = state.alerts.reduce((max, item) => Math.max(max, Number(item.puntaje) || 0), 0);
  $('#metric-risk').textContent = `${formattedRisk(rawHighest)}%`;
  $('#risk-meter-fill').style.width = `${highest}%`;
  $('#nav-pending').textContent = state.view === 'pendiente' ? String(state.total ?? total) : '—';
  const metricLive = document.querySelector('.metric-live');
  if (metricLive) metricLive.innerHTML = `<i></i> ${state.demo ? 'Muestras locales' : 'Cola sincronizada'}`;
  $('#queue-count').textContent = state.filtered.length.toLocaleString('es-CO');
}

function renderQueue() {
  const container = $('#alert-list');
  const search = $('#search-input').value.trim().toLowerCase();
  const decision = $('#decision-filter').value;
  state.filtered = state.alerts.filter(item => {
    const matchesDecision = decision === 'todos' || item.decision === decision;
    const matchesSearch = !search || [item.id_transaccion, item.id_cliente, item.id].some(value => String(value || '').toLowerCase().includes(search));
    return matchesDecision && matchesSearch;
  });
  // Por defecto, del más nuevo al más antiguo. "Prioridad" conserva el orden del Escudo (riesgo × monto).
  if (($('#order-filter')?.value || 'recientes') === 'recientes') {
    state.filtered.sort((a, b) => new Date(b.creada_utc || 0) - new Date(a.creada_utc || 0));
  }
  updateMetrics();
  if (!state.filtered.length) {
    const emptyTitle = state.alerts.length ? 'No hay coincidencias' : state.view === 'pendiente' ? 'La cola está despejada' : 'Aún no hay casos resueltos';
    const emptyCopy = state.alerts.length ? 'Prueba con otro cliente, transacción o filtro.' : state.view === 'pendiente' ? 'Cuando una operación necesite revisión humana, aparecerá aquí.' : 'Las decisiones de los analistas aparecerán aquí.';
    container.innerHTML = `<div class="queue-empty"><div class="queue-empty-icon">${state.alerts.length ? '⌕' : '✓'}</div><strong>${emptyTitle}</strong><p>${emptyCopy}</p></div>`;
    $('#queue-footer-label').textContent = '0 casos visibles';
    $('#show-more-button').hidden = true;
    renderDetail(null);
    return;
  }
  const visible = state.filtered.slice(0, state.visibleCount);
  container.innerHTML = visible.map((item, index) => {
    const selected = item.id === state.selectedId;
    const risk = riskPercent(item.puntaje);
    // La cola se ordena por riesgo × monto; un caso de los últimos 15 min se marca "Nuevo" para encontrarlo aunque no quede arriba.
    const isNew = state.view === 'pendiente' && Date.now() - new Date(item.creada_utc).getTime() < 15 * 60_000;
    return `<button type="button" class="alert-row ${selected ? 'selected' : ''} ${isNew ? 'is-new' : ''}" data-alert-id="${escapeHtml(item.id)}" style="--row-index:${Math.min(index, 8)}">
      <span class="alert-identity"><span class="row-mark ${risk >= 75 ? 'mark-high' : risk >= 45 ? 'mark-mid' : 'mark-low'}">${risk >= 75 ? '!' : '·'}</span><span class="row-main"><strong>${escapeHtml(item.id_transaccion || 'Transacción sin ID')}</strong><small>${escapeHtml(item.id_cliente || 'Cliente sin ID')} <i>·</i> ${timeAgo(item.creada_utc)}${isNew ? ' <b class="new-badge">Nuevo</b>' : ''}</small></span></span>
      <span><span class="decision-pill decision-${escapeHtml(item.decision)}"><i></i>${escapeHtml(prettyDecision(item.decision))}</span></span>
      <span class="row-amount">${money(item.monto_cop)}</span>
      <span class="row-risk"><strong class="risk-number ${risk >= 75 ? 'risk-danger' : risk >= 45 ? 'risk-warn' : ''}">${formattedRisk(item.puntaje)}%</strong><span class="risk-track"><i style="width:${risk}%"></i></span></span>
      <span class="row-chevron">${icon('chevron')}</span>
    </button>`;
  }).join('');
  $('#queue-footer-label').textContent = `Mostrando ${visible.length} de ${state.filtered.length} casos`;
  $('#show-more-button').hidden = state.filtered.length <= state.visibleCount;
  container.querySelectorAll('.alert-row').forEach(row => row.addEventListener('click', () => selectAlert(row.dataset.alertId)));
  const active = state.filtered.find(item => item.id === state.selectedId);
  if (active) renderDetail(active);
  else if (state.filtered[0]) selectAlert(state.filtered[0].id, false);
}

async function loadAlerts({ quiet = false } = {}) {
  if (!state.token || state.loading) return;
  state.loading = true;
  if (!quiet) $('#alert-list').innerHTML = '<div class="loading-state"><span class="loader"></span><span>Actualizando cola…</span></div>';
  try {
    const payload = await api(`/v1/alertas?estado=${state.view}&limite=200`);
    const previous = new Set((state.alerts || []).map(item => item.id));
    const fresh = quiet && state.view === 'pendiente' && previous.size ? (payload.alertas || []).filter(item => !previous.has(item.id)) : [];
    const signature = (payload.alertas || []).map(item => `${item.id}:${item.estado}`).join('|');
    if (quiet && signature === state.alertsSignature) {
      $('#last-updated').textContent = `Actualizado ${new Intl.DateTimeFormat('es-CO', { hour: '2-digit', minute: '2-digit', second: '2-digit' }).format(new Date())}`;
      return; // sin cambios: no se repinta (conserva la narrativa abierta y la posición)
    }
    state.alertsSignature = signature;
    state.alerts = payload.alertas || [];
    if (fresh.length) toast(`${fresh.length === 1 ? 'Nuevo caso' : `${fresh.length} casos nuevos`} en la cola: ${fresh.slice(0, 2).map(item => item.id_cliente).join(', ')}`);
    state.total = payload.total ?? state.alerts.length;
    state.visibleCount = 7;
    if (!quiet) toast(state.demo ? 'Ejemplos ficticios cargados · solo en esta sesión' : state.alerts.length ? `${state.alerts.length} ${state.view === 'pendiente' ? 'alertas pendientes' : 'casos resueltos'} cargados` : 'Cola actualizada');
    renderQueue();
    const now = new Date();
    $('#last-updated').textContent = `Actualizado ${new Intl.DateTimeFormat('es-CO', { hour: '2-digit', minute: '2-digit', second: '2-digit' }).format(now)}`;
    checkHealth();
  } catch (error) {
    $('#alert-list').innerHTML = `<div class="error-state"><span class="error-state-mark">!</span><strong>No se pudo cargar la cola</strong><p>${escapeHtml(error.message)}</p><button type="button" class="text-button" id="retry-button">Intentar de nuevo →</button></div>`;
    $('#retry-button')?.addEventListener('click', () => loadAlerts());
    setApiHealth(false);
  } finally { state.loading = false; }
}

function selectAlert(id, rerender = true) {
  state.selectedId = id;
  state.selectedAlert = state.alerts.find(item => item.id === id) || null;
  state.reputation = null;
  state.narrative = null;
  clearTimeout(state.narrativeTimer);
  if (rerender) renderQueue();
  else renderDetail(state.selectedAlert);
  if (state.selectedAlert) {
    loadReputation(state.selectedAlert.id_cliente);
    loadNarrative(state.selectedAlert.id);
  }
}

// Narrativa del caso redactada por la IA local (Ollama) y validada contra el expediente. Si todavía está en cola, se reintenta.
async function loadNarrative(idAlerta) {
  if (state.demo || !idAlerta) return;
  try {
    const result = await api(`/v1/alertas/${encodeURIComponent(idAlerta)}/narrativa`);
    if (state.selectedAlert?.id !== idAlerta) return;
    state.narrative = result?.estado === 'en_cola' ? { pending: true } : result;
    renderDetail(state.selectedAlert);
    if (state.narrative.pending) state.narrativeTimer = setTimeout(() => loadNarrative(idAlerta), 4000);
  } catch (error) {
    if (state.selectedAlert?.id !== idAlerta) return;
    state.narrative = { error: error.message };
    renderDetail(state.selectedAlert);
  }
}

function narrativeSection() {
  const n = state.narrative;
  if (state.demo) return '';
  const heading = '<div class="section-heading"><h3>Resumen del caso · IA local</h3><span>Ollama · validado contra el expediente</span></div>';
  if (!n || n.pending) return `<div class="detail-section narrative-section">${heading}<p class="muted-note"><span class="loader narrative-loader"></span> La IA local está redactando el resumen del expediente…</p></div>`;
  if (n.error) return `<div class="detail-section narrative-section">${heading}<p class="muted-note">No fue posible obtener la narrativa: ${escapeHtml(n.error)}</p></div>`;
  const c = n.contenido || {};
  const origin = n.origen === 'ollama'
    ? `Redactó ${escapeHtml(n.modelo || 'Ollama')} en ${(Number(n.latencia_ms || 0) / 1000).toFixed(1)} s`
    : `Plantilla del sistema${n.motivo_respaldo ? ` (la IA no pasó la validación: ${escapeHtml(n.motivo_respaldo)})` : ''}`;
  const list = items => `<ul class="narrative-list">${items.join('')}</ul>`;
  return `<div class="detail-section narrative-section">${heading}
    <p class="narrative-summary">${escapeHtml(c.resumen || '')}</p>
    ${(c.factores || []).length ? `<div class="narrative-block"><strong>Factores</strong>${list(c.factores.map(f => `<li>${escapeHtml(f)}</li>`))}</div>` : ''}
    ${(c.normas_aplicadas || []).length ? `<div class="narrative-block"><strong>Normas aplicadas</strong>${list(c.normas_aplicadas.map(x => `<li><b>${escapeHtml(x.codigo)}</b> · ${escapeHtml(x.como_aplica)}</li>`))}</div>` : ''}
    ${c.recomendacion_al_analista ? `<div class="narrative-block"><strong>Recomendación</strong><p>${escapeHtml(c.recomendacion_al_analista)}</p></div>` : ''}
    ${(c.linea_de_tiempo || []).length ? `<details class="narrative-block"><summary>Línea de tiempo del expediente (${c.linea_de_tiempo.length} hechos)</summary>${list(c.linea_de_tiempo.map(h => `<li><span class="mono-value">${escapeHtml(h.fecha)}</span> ${escapeHtml(h.hecho)}</li>`))}</details>` : ''}
    <p class="narrative-origin">${origin}${n.desactualizada ? ' · el expediente cambió después de redactarla' : ''}. La IA no decide: describe lo que el Escudo ya decidió.</p>
  </div>`;
}

async function loadReputation(idCuenta) {
  if (!idCuenta) return;
  try {
    const reputation = await api(`/v1/cuentas/${encodeURIComponent(idCuenta)}/reputacion`);
    if (state.selectedAlert?.id_cliente === idCuenta) {
      state.reputation = reputation;
      renderDetail(state.selectedAlert);
    }
  } catch (error) {
    if (error.message.includes('Sesión terminó')) return;
    if (state.selectedAlert?.id_cliente === idCuenta) {
      state.reputation = { error: error.message };
      renderDetail(state.selectedAlert);
    }
  }
}

function dateTimeBogota() {
  const parts = new Intl.DateTimeFormat('en-CA', { timeZone: 'America/Bogota', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' }).formatToParts(new Date());
  const value = Object.fromEntries(parts.map(part => [part.type, part.value]));
  return `${value.year}-${value.month}-${value.day}T${value.hour}:${value.minute}:${value.second}`;
}

function setScoreExample() {
  const form = $('#score-form');
  const fields = form.elements;
  fields.namedItem('id_transaccion').value = `TX-WEB-${Date.now().toString().slice(-10)}`;
  fields.namedItem('fecha_hora').value = dateTimeBogota();
  fields.namedItem('tipo_operacion').value = 'transferencia_inmediata';
  fields.namedItem('canal').value = 'app';
  fields.namedItem('monto_cop').value = '850000';
  fields.namedItem('saldo_antes_cop').value = '900000';
  fields.namedItem('id_cliente').value = 'CLI-DEMO-001';
  fields.namedItem('antiguedad_cuenta_dias').value = '420';
  fields.namedItem('id_destino').value = 'DESTINO-77';
  fields.namedItem('tipo_destino').value = 'cuenta_yuma';
  fields.namedItem('id_dispositivo').value = 'DISP-WEB-01';
  fields.namedItem('departamento_ip').value = 'Antioquia';
  for (const name of ['destino_nuevo', 'dispositivo_nuevo', 'cambio_clave_24h', 'cambio_sim_48h', 'ip_departamento_distinto']) fields.namedItem(name).value = '0';
  fields.namedItem('destino_nuevo').value = '1';
  $('#score-error').textContent = '';
  $('#score-output').innerHTML = '<div class="output-empty"><div class="empty-orbit"><span>◈</span></div><div class="panel-kicker">RESULTADO DEL MOTOR</div><h2>Esperando una operación</h2><p>La decisión, el puntaje y las razones aparecerán aquí después de evaluar.</p></div>';
}

function collectScorePayload(form) {
  const data = new FormData(form);
  const rawDate = String(data.get('fecha_hora') || '');
  const fecha = rawDate.replace('T', ' ');
  return {
    id_transaccion: String(data.get('id_transaccion')).trim(),
    fecha_hora: fecha.length === 16 ? `${fecha}:00` : fecha,
    id_cliente: String(data.get('id_cliente')).trim(),
    tipo_operacion: data.get('tipo_operacion'),
    canal: data.get('canal'),
    monto_cop: Number(data.get('monto_cop')),
    saldo_antes_cop: Number(data.get('saldo_antes_cop')),
    id_destino: String(data.get('id_destino')).trim(),
    tipo_destino: data.get('tipo_destino'),
    destino_nuevo: Number(data.get('destino_nuevo')),
    id_dispositivo: String(data.get('id_dispositivo')).trim(),
    dispositivo_nuevo: Number(data.get('dispositivo_nuevo')),
    cambio_clave_24h: Number(data.get('cambio_clave_24h')),
    cambio_sim_48h: Number(data.get('cambio_sim_48h')),
    departamento_ip: String(data.get('departamento_ip') || '').trim(),
    ip_departamento_distinto: Number(data.get('ip_departamento_distinto')),
    antiguedad_cuenta_dias: Number(data.get('antiguedad_cuenta_dias')),
  };
}

function renderScoreResult(result) {
  const decision = result.decision || 'desafiar';
  const risk = riskPercent(result.puntaje);
  const demoNote = state.demo ? 'Simulación ilustrativa: este modo no ejecuta el modelo ONNX ni modifica el backend.' : 'Decisión devuelta por el motor real. Una transacción retenida o rechazada puede aparecer en la cola de revisión.';
  const labels = { aprobar: 'Aprobar', desafiar: result.decision_interna === 'advertir' ? 'Mostrar advertencia' : 'Solicitar verificación', retener: 'Retener', rechazar: 'Rechazar' };
  const marks = { aprobar: '✓', desafiar: '?', retener: 'Ⅱ', rechazar: '!' };
  $('#score-output').innerHTML = `<div class="score-result-head"><div><div class="panel-kicker">RESULTADO DEL MOTOR ${state.demo ? '· MODO DEMO' : ''}</div><h2>Evaluación completada</h2></div><span class="detail-id">${escapeHtml(result.version_modelo || '')}</span></div>
    <div class="decision-hero ${escapeHtml(decision)}"><span class="decision-hero-mark">${marks[decision] || '·'}</span><div><span>DECISIÓN</span><strong>${escapeHtml(labels[decision] || prettyDecision(decision))}</strong></div><div class="result-score"><strong>${formattedRisk(result.puntaje)}</strong><small> / 100</small></div></div>
    <div class="result-meta"><div><span>NIVEL INTERNO</span><strong>${escapeHtml(result.decision_interna || decision)}</strong></div><div><span>LATENCIA DEL MOTOR</span><strong>${Number(result.latencia_ms || 0).toFixed(2)} ms</strong></div><div><span>LATENCIA TOTAL</span><strong>${Number(result.latencia_total_ms || 0).toFixed(2)} ms</strong></div><div><span>ID EVALUADO</span><strong>${escapeHtml($('#score-form').elements.namedItem('id_transaccion').value)}</strong></div></div>
    <div class="result-reasons"><h3>Razones de la decisión</h3>${Array.isArray(result.razones) && result.razones.length ? `<ul class="reason-list">${result.razones.map((reason, index) => `<li><span class="reason-index">${String(index + 1).padStart(2,'0')}</span><span>${escapeHtml(reason)}</span></li>`).join('')}</ul>` : '<p class="muted-note">El motor no devolvió razones.</p>'}</div><div class="result-disclaimer">${escapeHtml(demoNote)}</div>`;
}

async function submitScore(event) {
  event.preventDefault();
  $('#score-error').textContent = '';
  if (!state.token) { showLogin('Inicia sesión como analista para evaluar una operación.'); return; }
  const form = event.currentTarget;
  const button = $('#score-submit');
  const payload = collectScorePayload(form);
  const now = new Date();
  const txDate = new Date(payload.fecha_hora.replace(' ', 'T') + '-05:00');
  if (Number.isNaN(txDate.getTime()) || txDate < new Date('2020-01-01T00:00:00-05:00') || txDate > new Date(Date.now() + 86400000)) {
    $('#score-error').textContent = 'La fecha debe estar entre 2020 y mañana. Revisa también que sea la hora de Colombia.';
    return;
  }
  if (payload.monto_cop < 1 || payload.monto_cop > 1_000_000_000_000 || payload.saldo_antes_cop < 0 || payload.saldo_antes_cop > 1_000_000_000_000) {
    $('#score-error').textContent = 'El monto debe ser mayor que cero y el monto/saldo no pueden superar 1 billón de COP.';
    return;
  }
  button.disabled = true;
  button.innerHTML = '<span>Evaluando…</span><span class="button-spinner"></span>';
  $('#score-output').innerHTML = '<div class="loading-state"><span class="loader"></span><span>Consultando el motor…</span></div>';
  try {
    const result = await api('/v1/score/transaccion', { method: 'POST', body: JSON.stringify(payload) });
    renderScoreResult(result);
    toast(state.demo ? 'Evaluación ficticia · el modelo real no se ejecutó' : `Decisión: ${prettyDecision(result.decision)}`);
  } catch (error) {
    $('#score-error').textContent = error.message;
    $('#score-output').innerHTML = `<div class="error-state"><span class="error-state-mark">!</span><strong>No se pudo evaluar la transacción</strong><p>${escapeHtml(error.message)}</p></div>`;
  } finally {
    button.disabled = false;
    button.innerHTML = 'Evaluar operación <span>→</span>';
  }
}

function renderAccountResult(account) {
  const result = $('#account-result');
  const score = Number(account.puntaje) || 0;
  const reputation = Math.max(0, Math.min(100, Number(account.reputacion) || 0));
  result.innerHTML = `<div class="lookup-result"><div class="lookup-identity"><div class="panel-kicker">CUENTA CONSULTADA</div><h2>${escapeHtml(account.id_cuenta || '')}</h2><div class="lookup-score"><strong>${reputation}</strong><small>/100 reputación</small></div><div class="lookup-meter"><i style="width:${reputation}%"></i></div><div class="lookup-score-label">Riesgo registrado: ${score}/100</div></div><div class="lookup-signals"><h3>Señales registradas</h3>${account.bloqueo_definitivo ? '<div class="blocked-banner">Cuenta con bloqueo definitivo decidido por analista.</div>' : ''}${account.senales?.length ? `<div class="signal-chip-list">${account.senales.map(signal => `<span>${escapeHtml(signal.replaceAll('_',' '))}</span>`).join('')}</div>` : '<p class="muted-note">No hay señales registradas para esta cuenta.</p>'}<p class="form-hint">Actualizada: ${escapeHtml(fullDate(account.actualizado_utc))}</p></div></div>`;
}

async function submitAccountLookup(event) {
  event.preventDefault();
  const id = String(new FormData(event.currentTarget).get('id_cuenta') || '').trim();
  const result = $('#account-result');
  result.innerHTML = '<div class="loading-state"><span class="loader"></span><span>Consultando cuenta…</span></div>';
  try {
    const account = await api(`/v1/cuentas/${encodeURIComponent(id)}/reputacion`);
    renderAccountResult(account);
  } catch (error) {
    result.innerHTML = `<div class="error-state"><span class="error-state-mark">!</span><strong>No se encontró la cuenta</strong><p>${escapeHtml(error.message)}</p></div>`;
  }
}

function setReplayStatus(status) {
  const wrap = $('#replay-status');
  const running = status.estado === 'ejecutando';
  const finished = status.estado === 'terminado';
  const failed = status.estado === 'error';
  wrap.classList.toggle('running', running);
  wrap.classList.toggle('finished', finished);
  wrap.classList.toggle('failed', failed);
  const label = running ? 'Replay en curso' : finished ? 'Replay terminado' : failed ? 'Replay con error' : 'Sin replay activo';
  const demoCopy = state.demo && (running || finished) ? 'Estado de ejemplo; el sandbox real no se ejecutó.' : status.mensaje || (running ? 'El backend está procesando el conjunto de datos.' : 'Puedes consultar el historial de ejecución en el backend.');
  wrap.querySelector('strong').textContent = label;
  wrap.querySelector('small').textContent = demoCopy;
  const progress = $('#replay-progress');
  progress.hidden = !running && !finished && !failed;
  const percent = finished ? 100 : 0;
  $('#replay-progress-fill').style.width = `${percent}%`;
  $('#replay-progress-label').textContent = status.procesadas ? `${Number(status.procesadas).toLocaleString('es-CO')} procesadas` : `${percent}%`;
  $('#replay-start').disabled = !$('#replay-confirm').checked || running;
  $('#replay-start').innerHTML = running ? 'Replay en curso…' : 'Iniciar replay <span>→</span>';
}

async function loadReplayStatus() {
  if (!state.token) return;
  try {
    const status = await api('/v1/sandbox/replay');
    setReplayStatus(status);
    if (status.estado === 'ejecutando') startReplayPolling();
  } catch (error) {
    $('#replay-error').textContent = error.message;
  }
}

function startReplayPolling() {
  clearInterval(replayPollTimer);
  replayPollTimer = setInterval(async () => {
    try {
      const status = await api('/v1/sandbox/replay');
      setReplayStatus(status);
      if (status.estado !== 'ejecutando') clearInterval(replayPollTimer);
    } catch (error) {
      clearInterval(replayPollTimer);
      $('#replay-error').textContent = error.message;
    }
  }, 2500);
}

async function startReplay() {
  $('#replay-error').textContent = '';
  if (!$('#replay-confirm').checked) return;
  $('#replay-start').disabled = true;
  try {
    const status = await api('/v1/sandbox/replay', { method: 'POST' });
    setReplayStatus(status);
    if (state.demo) startReplayPolling();
    else startReplayPolling();
  } catch (error) {
    $('#replay-error').textContent = error.message;
    $('#replay-start').disabled = !$('#replay-confirm').checked;
  }
}

function renderSession() {
  const user = state.analyst || 'Analista';
  $('#session-name').textContent = state.demo ? 'Vista demo' : user;
  $('#session-user').textContent = state.demo ? 'Sesión local, sin identidad real' : user;
  $('#session-avatar').textContent = state.demo ? 'D' : user.slice(0,1).toUpperCase();
  $('#session-expiry').textContent = state.demo ? 'No aplica · datos locales' : state.expiresUtc ? fullDate(state.expiresUtc) : 'No disponible';
  $('#session-connection').textContent = state.demo ? 'Modo demo, no conectado' : 'API Escudo Yuma';
}

async function registerDevice(event) {
  event.preventDefault();
  const data = new FormData(event.currentTarget);
  const message = $('#device-result');
  $('#device-error').textContent = '';
  if (state.demo) {
    message.textContent = 'Simulación local: este dispositivo no se agregó al backend.';
    return;
  }
  try {
    const result = await api('/v1/auth/dispositivos', { method: 'POST', body: JSON.stringify({ id_dispositivo: String(data.get('id_dispositivo')).trim(), sistema_operativo: data.get('sistema_operativo') || null }) });
    message.textContent = result.nuevo_registro ? `Dispositivo ${result.id_dispositivo} registrado.` : `El dispositivo ${result.id_dispositivo} ya estaba registrado.`;
    event.currentTarget.reset();
  } catch (error) { $('#device-error').textContent = error.message; }
}

function renderDetail(item) {
  const panel = $('#detail-panel');
  if (!item) {
    panel.innerHTML = '<div class="detail-empty"><div class="empty-orbit"><span>⌁</span></div><div class="panel-kicker">DETALLE DEL CASO</div><h2>Selecciona una alerta</h2><p>Revisa las señales y el contexto de la cuenta antes de registrar una decisión.</p></div>';
    return;
  }
  const risk = riskPercent(item.puntaje);
  const reasons = Array.isArray(item.razones) ? item.razones : [];
  const rep = state.reputation;
  const repScore = rep && !rep.error ? Number(rep.reputacion) : null;
  const isResolved = state.view === 'resuelta';
  panel.innerHTML = `<div class="detail-content">
    <div class="detail-topline"><div class="panel-kicker">${isResolved ? 'CASO RESUELTO' : 'DETALLE DEL CASO'}</div><span class="detail-id">#${escapeHtml(String(item.id_transaccion || '').replace(/^TX-/, '').slice(-8) || '—')}</span></div>
    <div class="detail-title-row"><div><h2>${escapeHtml(item.id_transaccion || 'Transacción')}</h2><p class="detail-created">${icon('clock')} Registrada ${timeAgo(item.creada_utc)}</p></div><span class="decision-pill decision-${escapeHtml(item.decision)}"><i></i>${escapeHtml(prettyDecision(item.decision))}</span></div>
    <div class="risk-hero"><div class="risk-hero-copy"><span>PUNTAJE DE RIESGO</span><strong class="${risk >= 75 ? 'risk-danger' : risk >= 45 ? 'risk-warn' : ''}">${formattedRisk(item.puntaje)}<small>/100</small></strong><p>${risk >= 75 ? 'Señales de riesgo elevadas' : risk >= 45 ? 'Revisión recomendada' : 'Riesgo moderado'}</p></div><div class="risk-gauge" style="--risk-angle:${risk * 3.6}deg"><div><strong>${formattedRisk(item.puntaje)}<small>%</small></strong><span>RIESGO</span></div></div></div>
    <div class="detail-section"><div class="section-heading"><h3>Señales que explican la decisión</h3><span>${reasons.length} ${reasons.length === 1 ? 'señal' : 'señales'}</span></div>${reasons.length ? `<ul class="reason-list">${reasons.map((reason, index) => `<li><span class="reason-index">${String(index + 1).padStart(2, '0')}</span><span>${escapeHtml(reason)}</span></li>`).join('')}</ul>` : '<p class="muted-note">La API no reportó razones para esta alerta.</p>'}</div>
    ${narrativeSection()}
    <div class="detail-section account-section"><div class="section-heading"><h3>Cuenta de origen</h3><span class="account-live"><i></i> ${state.demo ? 'Ejemplo local' : 'Verificada'}</span></div><div class="account-card"><div class="account-avatar">${icon('user')}</div><div class="account-info"><strong>${escapeHtml(item.id_cliente || 'Cliente sin ID')}</strong><small>Identificador de cuenta</small></div><div class="account-rep"><span>REPUTACIÓN</span><strong class="${repScore !== null && repScore < 40 ? 'risk-danger' : ''}">${repScore === null ? (rep?.error ? '—' : '···') : `${repScore}`}<small>${repScore === null ? '' : '/100'}</small></strong></div></div>${rep?.bloqueo_definitivo ? '<div class="blocked-banner">Esta cuenta tiene bloqueo definitivo por decisión de analista.</div>' : ''}${rep?.error ? `<p class="muted-note">No fue posible consultar reputación: ${escapeHtml(rep.error)}</p>` : ''}${rep?.senales?.length ? `<div class="account-signals">${rep.senales.slice(0, 3).map(s => `<span>${escapeHtml(s.replaceAll('_', ' '))}</span>`).join('')}</div>` : ''}</div>
    <div class="detail-section transaction-section"><div class="section-heading"><h3>Resumen de operación</h3><span>${escapeHtml(fullDate(item.creada_utc))}</span></div><div class="transaction-summary"><div><span>MONTO</span><strong>${money(item.monto_cop)}</strong></div><div><span>CLIENTE</span><strong>${escapeHtml(item.id_cliente || '—')}</strong></div><div><span>ID DE ALERTA</span><strong class="mono-value">${escapeHtml(item.id || '—')}</strong></div></div></div>
    ${!isResolved ? `<div class="review-note"><span class="review-note-icon">✳</span><span><strong>La decisión final es tuya</strong><small>${state.demo ? 'En el entorno real, toda acción queda auditada.' : 'El modelo orienta la revisión. Toda acción queda auditada.'}</small></span></div><button class="button button-resolve" id="resolve-alert-button" type="button"><span>Registrar resolución</span><span>→</span></button>` : `<div class="resolved-note"><span>✓</span> ${state.demo ? 'Resolución de ejemplo · solo local' : 'Resolución registrada en auditoría'}</div>`}
  </div>`;
  $('#resolve-alert-button')?.addEventListener('click', openResolution);
}

function openResolution() {
  const item = state.selectedAlert;
  if (!item) return;
  $('#resolution-form').reset();
  $('#resolution-feedback').textContent = '';
  $('#resolution-title').textContent = `Resolver ${item.id_transaccion || 'alerta'}`;
  $('#resolution-copy').textContent = `Esta decisión sobre ${item.id_cliente || 'la cuenta'} quedará registrada en la auditoría del caso.`;
  $('#resolution-dialog').showModal();
  document.body.classList.add('has-modal');
  updateResolutionOptions();
}

function updateResolutionOptions() {
  const resolution = $('#resolution-form input[name="resolucion"]:checked')?.value;
  const action = $('#resolution-action');
  const block = action.querySelector('option[value="bloquear_cuenta"]');
  const hold = action.querySelector('option[value="mantener_retencion"]');
  block.disabled = resolution !== 'fraude_confirmado';
  hold.disabled = resolution === 'falso_positivo';
  if (action.selectedOptions[0]?.disabled) action.value = 'monitorear';
  $('#resolution-mark').classList.toggle('mark-false-positive', resolution === 'falso_positivo');
  $('#resolution-mark').textContent = resolution === 'falso_positivo' ? '↗' : '✓';
}

async function submitResolution(event) {
  event.preventDefault();
  const item = state.selectedAlert;
  if (!item) return;
  const form = new FormData(event.currentTarget);
  const button = $('#resolution-submit');
  button.disabled = true;
  button.innerHTML = '<span>Guardando decisión…</span><span class="button-spinner"></span>';
  $('#resolution-feedback').textContent = '';
  try {
    const result = await api(`/v1/alertas/${encodeURIComponent(item.id)}/resolver`, {
      method: 'POST',
      body: JSON.stringify({ resolucion: form.get('resolucion'), accion: form.get('accion'), comentario: String(form.get('comentario') || '').trim() || null }),
    });
    $('#resolution-dialog').close();
    document.body.classList.remove('has-modal');
    toast(state.demo ? 'Resolución de ejemplo guardada localmente' : result.bloqueo_definitivo ? 'Decisión guardada · cuenta bloqueada' : 'Decisión guardada y auditada');
    await loadAlerts({ quiet: true });
  } catch (error) {
    $('#resolution-feedback').textContent = error.message;
  } finally {
    button.disabled = false;
    button.innerHTML = 'Confirmar decisión <span>→</span>';
  }
}

function setView(view) {
  state.activePage = view;
  const queueView = view === 'pendiente' || view === 'resuelta';
  if (queueView) state.view = view;
  state.selectedId = null;
  const pages = { scoring: 'view-scoring', cuenta: 'view-cuenta', sandbox: 'view-sandbox', acceso: 'view-acceso' };
  $('#view-alertas').hidden = !queueView;
  for (const [key, id] of Object.entries(pages)) $(`#${id}`).hidden = view !== key;
  const title = { pendiente: 'Cola de revisión', resuelta: 'Casos resueltos', scoring: 'Evaluar transacción', cuenta: 'Consultar cuenta', sandbox: 'Replay sandbox', acceso: 'Mi acceso' }[view] || 'Escudo Yuma';
  $('#breadcrumb-current').textContent = title;
  if (queueView) {
    $('#page-title').textContent = view === 'pendiente' ? 'Cola de revisión' : 'Casos resueltos';
    $('#page-subtitle').textContent = view === 'pendiente' ? 'Casos que requieren criterio humano antes de continuar.' : 'Decisiones registradas por el equipo de análisis.';
    $('#queue-title').innerHTML = `${view === 'pendiente' ? 'Alertas pendientes' : 'Casos resueltos'} <span class="heading-count" id="queue-count">—</span>`;
  }
  document.querySelectorAll('.nav-item').forEach(link => link.classList.toggle('active', link.dataset.view === view));
  if (queueView) {
    $('#decision-filter').value = 'todos';
    $('#search-input').value = '';
    renderDetail(null);
    loadAlerts();
  } else if (view === 'sandbox') {
    $('#replay-error').textContent = '';
    loadReplayStatus();
  } else if (view === 'acceso') renderSession();
}

let toastTimer;
function toast(message) {
  const region = $('#toast-region');
  region.innerHTML = `<div class="toast"><span class="toast-check">✓</span>${escapeHtml(message)}</div>`;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { region.innerHTML = ''; }, 3200);
}

function updateClock() {
  const now = new Date();
  $('#local-clock').textContent = new Intl.DateTimeFormat('es-CO', { hour: '2-digit', minute: '2-digit', timeZone: 'America/Bogota' }).format(now);
  $('#today-label').textContent = new Intl.DateTimeFormat('es-CO', { weekday: 'long', day: 'numeric', month: 'long', timeZone: 'America/Bogota' }).format(now).toUpperCase();
}

$('#login-form').addEventListener('submit', handleLogin);
$('#demo-entry').addEventListener('click', enterDemoMode);
$('#refresh-button').addEventListener('click', () => loadAlerts());
$('#reload-button').addEventListener('click', () => loadAlerts());
$('#search-input').addEventListener('input', renderQueue);
$('#decision-filter').addEventListener('change', renderQueue);
$('#order-filter').addEventListener('change', () => { state.selectedId = null; renderQueue(); });
$('#show-more-button').addEventListener('click', () => { state.visibleCount += 10; renderQueue(); });
$('#profile-button').addEventListener('click', signOut);
$('#resolution-form').addEventListener('submit', submitResolution);
$('#resolution-form').querySelectorAll('input[name="resolucion"]').forEach(input => input.addEventListener('change', updateResolutionOptions));
$('#resolution-cancel').addEventListener('click', () => $('#resolution-dialog').close());
$('#resolution-close').addEventListener('click', () => $('#resolution-dialog').close());
$('#resolution-dialog').addEventListener('close', () => document.body.classList.remove('has-modal'));
$('#login-dialog').addEventListener('close', () => document.body.classList.remove('has-modal'));
document.querySelectorAll('.nav-item').forEach(link => link.addEventListener('click', event => { event.preventDefault(); setView(link.dataset.view); }));
window.addEventListener('storage', event => {
  if (event.key === 'yuma-wallet-local-demo-v1' && state.demo && state.token) loadAlerts({ quiet: true });
});
$('#score-form').addEventListener('submit', submitScore);
$('#score-reset').addEventListener('click', setScoreExample);
$('#account-form').addEventListener('submit', submitAccountLookup);
$('#replay-confirm').addEventListener('change', () => { $('#replay-start').disabled = !$('#replay-confirm').checked || $('#replay-status').classList.contains('running'); });
$('#replay-start').addEventListener('click', startReplay);
$('#device-form').addEventListener('submit', registerDevice);
$('#sign-out-button').addEventListener('click', signOut);

updateClock();
setInterval(updateClock, 30_000);
setScoreExample();
checkHealth();
if (state.token) {
  $('#profile-name').textContent = state.analyst || 'Analista';
  $('#profile-avatar').textContent = (state.analyst || 'A').slice(0, 1).toUpperCase();
  state.expiresUtc = tokenClaims(state.token).exp ? new Date(tokenClaims(state.token).exp * 1000).toISOString() : null;
  renderSession();
  loadAlerts();
} else showLogin();
setInterval(() => { if (state.token && (state.activePage === 'pendiente' || state.activePage === 'resuelta')) loadAlerts({ quiet: true }); }, 8_000); // casi en tiempo real: los casos nuevos aparecen sin recargar
