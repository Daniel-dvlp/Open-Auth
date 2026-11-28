(() => {
  'use strict';

  const API_ROOT = '/wallet-api';
  const DEMO_STORAGE = 'yuma-wallet-local-demo-v1';
  const STORAGE = {
    accessToken: 'yuma-wallet-access-token',
    refreshToken: 'yuma-wallet-refresh-token',
  };
  const state = {
    accessToken: sessionStorage.getItem(STORAGE.accessToken) || '',
    refreshToken: sessionStorage.getItem(STORAGE.refreshToken) || '',
    account: null,
    demo: false,
    movements: [],
    nextCursor: null,
    pendingLoginVerification: null,
    pendingTransfer: null,
    deviceId: getDeviceId(),
    operatingSystem: 'Android',
  };
  let refreshPromise = null;
  let toastTimer = null;

  function accountStorageKey(name) {
    return `${name}:${state.account?.id_cuenta || 'sin-cuenta'}`;
  }

  class ApiError extends Error {
    constructor(message, status = 0, code = '', correlationId = '') {
      super(message);
      this.name = 'ApiError';
      this.status = status;
      this.code = code;
      this.correlationId = correlationId;
      this.retryAfter = '';
    }
  }

  function readSessionObject(key) {
    try {
      const value = sessionStorage.getItem(key);
      return value ? JSON.parse(value) : null;
    } catch {
      sessionStorage.removeItem(key);
      return null;
    }
  }

  function readWalletDemo() {
    try {
      const saved = JSON.parse(localStorage.getItem(DEMO_STORAGE) || '{}');
      return {
        disponible_cop: Number.isSafeInteger(saved.disponible_cop) ? saved.disponible_cop : 1580000,
        reservado_cop: Number.isSafeInteger(saved.reservado_cop) ? saved.reservado_cop : 0,
        movimientos: Array.isArray(saved.movimientos) ? saved.movimientos : [],
        alertas: Array.isArray(saved.alertas) ? saved.alertas : [],
      };
    } catch {
      return { disponible_cop: 1580000, reservado_cop: 0, movimientos: [], alertas: [] };
    }
  }

  function saveWalletDemo(data) {
    try { localStorage.setItem(DEMO_STORAGE, JSON.stringify(data)); }
    catch { showToast('El navegador no pudo guardar esta simulación local.', true); }
  }

  function getDeviceId() {
    try {
      let id = localStorage.getItem('yuma-wallet-device-id');
      if (!id) {
        const bytes = new Uint8Array(16);
        crypto.getRandomValues(bytes);
        id = `YW-${Array.from(bytes, byte => byte.toString(16).padStart(2, '0')).join('').toUpperCase()}`;
        localStorage.setItem('yuma-wallet-device-id', id);
      }
      return id;
    } catch {
      return `YW-${Date.now().toString(16).toUpperCase()}`;
    }
  }

  function normalizePhone(value) {
    let digits = String(value || '').replace(/\D/g, '');
    if (digits.startsWith('57') && digits.length === 12) digits = digits.slice(2);
    return digits;
  }

  function formatMoney(value) {
    const amount = Number(value);
    if (!Number.isFinite(amount)) return '—';
    return new Intl.NumberFormat('es-CO', { style: 'currency', currency: 'COP', maximumFractionDigits: 0 }).format(amount);
  }

  function formatDate(value) {
    if (!value) return 'Fecha no disponible';
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return 'Fecha no disponible';
    return new Intl.DateTimeFormat('es-CO', { timeZone: 'America/Bogota', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }).format(date);
  }

  function escapeHtml(value = '') {
    return String(value).replace(/[&<>"']/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[character]);
  }

  function conceptLabel(concept = '') {
    const labels = {
      apertura: 'Apertura de cuenta',
      transferencia_inmediata: 'Transferencia Yuma',
      pago_qr: 'Pago con QR',
      pago_servicios: 'Pago de servicios',
      recarga_celular: 'Recarga de celular',
    };
    return labels[concept] || concept.replaceAll('_', ' ') || 'Movimiento de cuenta';
  }

  async function parseResponse(response) {
    if (response.status === 204) return null;
    const text = await response.text();
    if (!text) return null;
    try { return JSON.parse(text); }
    catch { return { detail: text }; }
  }

  async function sendRequest(path, options = {}) {
    const headers = {
      accept: 'application/json',
      ...(options.body !== undefined ? { 'content-type': 'application/json' } : {}),
      ...(options.headers || {}),
      ...(options.auth !== false && state.accessToken ? { authorization: `Bearer ${state.accessToken}` } : {}),
    };
    let response;
    try {
      response = await fetch(`${API_ROOT}${path}`, {
        method: options.method || 'GET',
        headers,
        ...(options.body !== undefined ? { body: JSON.stringify(options.body) } : {}),
        cache: 'no-store',
      });
    } catch {
      throw new ApiError('No pudimos conectar con la billetera. Revisa que la API de Yuma esté activa.', 0);
    }

    const payload = await parseResponse(response);
    if (!response.ok) {
      const detail = payload?.detail || payload?.title || 'No se pudo completar la solicitud.';
      const error = new ApiError(detail, response.status, payload?.codigo || '', response.headers.get('X-Correlation-Id') || '');
      error.retryAfter = response.headers.get('Retry-After') || '';
      throw error;
    }
    return payload;
  }

  async function refreshAccessToken() {
    if (!state.refreshToken) return false;
    if (!refreshPromise) {
      refreshPromise = sendRequest('/v1/auth/refresh', {
        method: 'POST',
        body: { refresh_token: state.refreshToken },
        auth: false,
      }).then(tokens => {
        saveTokens(tokens);
        return true;
      }).catch(() => {
        clearTokens();
        return false;
      }).finally(() => { refreshPromise = null; });
    }
    return refreshPromise;
  }

  async function api(path, options = {}, allowRefresh = true) {
    if (state.demo) return demoApi(path, options);
    try {
      return await sendRequest(path, options);
    } catch (error) {
      if (error instanceof ApiError && error.status === 401 && options.auth !== false && allowRefresh && state.refreshToken) {
        if (await refreshAccessToken()) return api(path, options, false);
        endSession('Tu sesión terminó. Ingresa de nuevo para continuar.');
      }
      throw error;
    }
  }

  async function demoApi(path, options = {}) {
    const url = new URL(path, window.location.origin);
    const demo = readWalletDemo();
    if (url.pathname === '/v1/cuentas/me') {
      return { id_cuenta: 'CLI-YM-0084', llave: '3000000084', tipo_cliente: 'persona', departamento: 'Antioquia', estado: 'activa', saldo_cop: demo.disponible_cop + demo.reservado_cop, disponible_cop: demo.disponible_cop, reservado_cop: demo.reservado_cop, moneda: 'COP' };
    }
    if (url.pathname === '/v1/cuentas/me/movimientos') {
      return { movimientos: demo.movimientos, siguiente_cursor: null };
    }
    if (url.pathname === '/v1/transferencias' && options.method === 'POST') {
      const payload = options.body || {};
      const amount = Number(payload.monto_cop);
      const requiresReview = amount >= demo.disponible_cop * 0.8;
      const id = `TX-DEMO-${Date.now()}`;
      const createdAt = new Date().toISOString();
      const reasons = requiresReview
        ? ['El monto representa al menos el 80 % del saldo disponible.', 'El destinatario es nuevo en esta simulación.']
        : ['El monto y el destinatario no muestran señales elevadas en esta simulación.'];
      const movement = {
        id: Date.now(), id_transaccion: id, fecha_utc: createdAt, tipo: 'debito', concepto: 'transferencia_inmediata',
        monto_cop: amount, contraparte: payload.llave, estado: requiresReview ? 'en_revision' : 'completada',
      };
      demo.disponible_cop -= amount;
      if (requiresReview) demo.reservado_cop += amount;
      demo.movimientos.unshift(movement);
      if (requiresReview) {
        demo.alertas.unshift({
          id: id.toLowerCase(), id_transaccion: id, id_cliente: 'CLI-YM-0084', monto_cop: amount,
          puntaje: 0.982, decision: 'retener', razones: reasons, estado: 'pendiente', creada_utc: createdAt,
          origen_demo_billetera: true,
        });
      }
      saveWalletDemo(demo);
      return {
        id_transaccion: id, estado: requiresReview ? 'en_revision' : 'completada', monto_cop: amount,
        llave_destino: payload.llave, mensaje: requiresReview
          ? 'La transferencia quedó en revisión. El dinero sigue reservado en tu cuenta.'
          : 'La transferencia de demostración se completó.', creada_utc: createdAt,
      };
    }
    throw new ApiError('Esta acción no está disponible en la demo local.');
  }

  function saveTokens(tokens) {
    state.accessToken = tokens?.token || '';
    state.refreshToken = tokens?.refresh_token || '';
    if (state.accessToken) sessionStorage.setItem(STORAGE.accessToken, state.accessToken);
    else sessionStorage.removeItem(STORAGE.accessToken);
    if (state.refreshToken) sessionStorage.setItem(STORAGE.refreshToken, state.refreshToken);
    else sessionStorage.removeItem(STORAGE.refreshToken);
  }

  function clearTokens() {
    state.accessToken = '';
    state.refreshToken = '';
    sessionStorage.removeItem(STORAGE.accessToken);
    sessionStorage.removeItem(STORAGE.refreshToken);
  }

  function setMessage(id, message = '', success = false) {
    const element = document.getElementById(id);
    if (!element) return;
    element.textContent = message;
    element.classList.toggle('is-success', success);
  }

  function clearTransferInputs() {
    document.getElementById('transfer-form').reset();
    setMessage('transfer-error');
  }

  function setBusy(button, busy, label = 'Procesando…') {
    if (!button) return;
    if (busy) {
      button.dataset.originalLabel = button.querySelector('span')?.textContent || button.textContent.trim();
      button.disabled = true;
      const text = button.querySelector('span');
      if (text) text.textContent = label;
    } else {
      button.disabled = false;
      const text = button.querySelector('span');
      if (text && button.dataset.originalLabel) text.textContent = button.dataset.originalLabel;
      delete button.dataset.originalLabel;
    }
  }

  function presentError(error) {
    if (!(error instanceof ApiError)) return 'No pudimos completar la solicitud. Inténtalo de nuevo.';
    const correlation = error.correlationId ? ` Referencia: ${error.correlationId}.` : '';
    const retry = error.status === 429 && Number(error.retryAfter) > 0 ? ` Inténtalo de nuevo en ${error.retryAfter} segundos.` : '';
    return `${error.message}${retry}${correlation}`;
  }

  function showToast(message, isError = false) {
    const toast = document.getElementById('wallet-toast');
    toast.textContent = message;
    toast.classList.toggle('is-error', isError);
    toast.classList.add('is-visible');
    window.clearTimeout(toastTimer);
    toastTimer = window.setTimeout(() => toast.classList.remove('is-visible'), 3800);
  }

  function openDialog(id) {
    const dialog = document.getElementById(id);
    if (dialog && !dialog.open) dialog.showModal();
  }

  function closeDialog(id) {
    const dialog = document.getElementById(id);
    if (dialog?.open) dialog.close();
  }

  function showAuth() {
    document.getElementById('auth-view').hidden = false;
    document.getElementById('wallet-view').hidden = true;
    document.getElementById('login-phone').focus();
  }

  function showWallet() {
    document.getElementById('auth-view').hidden = true;
    document.getElementById('wallet-view').hidden = false;
    document.getElementById('wallet-demo-banner').hidden = !state.demo;
    document.getElementById('wallet-session-label').innerHTML = `<i class="yb-status-dot"></i> ${state.demo ? 'DEMO LOCAL' : 'CUENTA YUMA'}`;
    document.getElementById('prepare-wallet-demo').hidden = !state.demo;
    document.getElementById('wallet-demo-tip').hidden = !state.demo;
  }

  function endSession(message = '') {
    clearTokens();
    state.demo = false;
    state.account = null;
    state.movements = [];
    state.nextCursor = null;
    showWallet();
    changePage('home');
    if (message) showToast(message, true);
    window.setTimeout(() => showAuth(), 900);
  }

  async function handleLogin(event) {
    event.preventDefault();
    setMessage('login-error');
    const form = new FormData(event.currentTarget);
    const phone = normalizePhone(form.get('llave'));
    if (!/^3\d{9}$/.test(phone)) {
      setMessage('login-error', 'Escribe un celular colombiano de 10 dígitos que empiece por 3.');
      return;
    }

    const button = document.getElementById('login-submit');
    setBusy(button, true, 'Verificando acceso…');
    state.operatingSystem = String(form.get('sistema_operativo') || 'Android');
    try {
      const result = await api('/v1/auth/login', {
        method: 'POST',
        auth: false,
        body: {
          llave: phone,
          clave: String(form.get('clave') || ''),
          id_dispositivo: state.deviceId,
          departamento_ip: String(form.get('departamento_ip') || 'Antioquia'),
          sistema_operativo: state.operatingSystem,
        },
      }, false);

      if (result?.resultado === 'permitir') {
        await completeLogin(result);
        return;
      }
      if (result?.resultado === 'verificacion_requerida' && result.verificacion?.id_verificacion) {
        state.pendingLoginVerification = { ...result.verificacion, phone };
        document.getElementById('auth-verification-copy').textContent = result.mensaje || 'Confirma el ingreso para proteger tu cuenta.';
        document.getElementById('otp-field').hidden = result.verificacion.metodo !== 'otp';
        document.getElementById('otp-submit').hidden = result.verificacion.metodo !== 'otp';
        document.getElementById('dev-otp-button').hidden = result.verificacion.metodo !== 'otp';
        document.getElementById('login-biometric-copy').textContent = result.mensaje || 'La verificación facial está simulada en este entorno.';
        setMessage('otp-error');
        setMessage('login-biometric-error');
        openDialog(result.verificacion.metodo === 'biometria' ? 'login-biometric' : 'auth-verification');
        return;
      }
      setMessage('login-error', result?.mensaje || 'No se pudo iniciar sesión. Revisa el celular y la clave.');
    } catch (error) {
      setMessage('login-error', presentError(error));
    } finally {
      setBusy(button, false);
    }
  }

  async function handleOtp(event) {
    event.preventDefault();
    const verification = state.pendingLoginVerification;
    const code = document.getElementById('otp-code').value.trim();
    if (!verification || !/^\d{6}$/.test(code)) {
      setMessage('otp-error', 'Ingresa el código de seis dígitos.');
      return;
    }
    const button = document.getElementById('otp-submit');
    setBusy(button, true, 'Confirmando…');
    setMessage('otp-error');
    try {
      const result = await api('/v1/auth/otp', {
        method: 'POST', auth: false,
        body: { id_verificacion: verification.id_verificacion, codigo: code },
      }, false);
      if (result?.resultado !== 'permitir' || !result.token) throw new ApiError(result?.mensaje || 'El código no fue aceptado.', 401);
      closeDialog('auth-verification');
      await completeLogin(result);
    } catch (error) {
      setMessage('otp-error', presentError(error));
    } finally {
      setBusy(button, false);
    }
  }

  async function handleLoginBiometric(resultSimulated) {
    const verification = state.pendingLoginVerification;
    if (!verification) return;
    const button = document.querySelector(`[data-login-biometric="${resultSimulated}"]`);
    setBusy(button, true, 'Validando…');
    document.querySelectorAll('[data-login-biometric]').forEach(item => { item.disabled = true; });
    setMessage('login-biometric-error');
    try {
      const result = await api('/v1/auth/biometria', {
        method: 'POST', auth: false,
        body: { id_verificacion: verification.id_verificacion, resultado_simulado: resultSimulated },
      }, false);
      if (result?.resultado !== 'permitir' || !result.token) throw new ApiError(result?.mensaje || 'No pudimos verificar tu identidad. Vuelve a ingresar.', 403);
      closeDialog('login-biometric');
      await completeLogin(result);
    } catch (error) {
      setMessage('login-biometric-error', presentError(error));
    } finally {
      document.querySelectorAll('[data-login-biometric]').forEach(item => { item.disabled = false; });
      setBusy(button, false);
    }
  }

  async function completeLogin(tokens) {
    saveTokens(tokens);
    state.pendingLoginVerification = null;
    document.getElementById('login-password').value = '';
    closeDialog('auth-verification');
    closeDialog('login-biometric');
    showWallet();
    await registerCurrentDevice();
    await loadAccountData();
    if (state.pendingTransfer?.id_transaccion && state.pendingTransfer?.id_cliente === state.account?.id_cuenta) {
      await restorePendingTransaction();
    }
    showToast('Ya estás en Yuma Pay.');
  }

  async function registerCurrentDevice() {
    if (state.demo) return;
    try {
      await api('/v1/dispositivos', {
        method: 'POST',
        body: { id_dispositivo: state.deviceId, sistema_operativo: state.operatingSystem },
      });
    } catch (error) {
      showToast(`No pudimos recordar este dispositivo. ${error.message}`, true);
    }
  }

  async function loadAccountData() {
    const [accountResult, movementsResult] = await Promise.allSettled([
      api('/v1/cuentas/me'),
      api('/v1/cuentas/me/movimientos?limite=10'),
    ]);
    if (accountResult.status === 'rejected') {
      showToast(presentError(accountResult.reason), true);
      if (accountResult.reason instanceof ApiError && accountResult.reason.status === 401) endSession();
      return;
    }

    state.account = accountResult.value;
    state.pendingTransfer = readSessionObject(accountStorageKey('yuma-wallet-pending-transaction'));
    renderAccount();
    if (movementsResult.status === 'fulfilled') {
      state.movements = movementsResult.value?.movimientos || [];
      state.nextCursor = movementsResult.value?.siguiente_cursor ?? null;
      renderMovements();
    } else {
      state.movements = [];
      renderMovements('No pudimos cargar tus movimientos. Puedes volver a intentar.');
    }
    updateTransferAvailability();
  }

  function renderAccount() {
    const account = state.account;
    if (!account) return;
    document.getElementById('greeting-name').textContent = 'de vuelta';
    document.getElementById('available-balance').textContent = formatMoney(account.disponible_cop);
    document.getElementById('reserved-balance').textContent = formatMoney(account.reservado_cop);
    document.getElementById('masked-account').textContent = `•••• ${String(account.llave || '').slice(-4)}`;
    const now = new Date();
    document.getElementById('today-label').textContent = new Intl.DateTimeFormat('es-CO', { timeZone: 'America/Bogota', weekday: 'long', day: 'numeric', month: 'long' }).format(now).toUpperCase();
    const accountActive = account.estado === 'activa';
    document.querySelectorAll('[data-go="transfer"], [data-page="transfer"]').forEach(button => {
      button.disabled = !accountActive;
      button.title = accountActive ? '' : 'Las transferencias están deshabilitadas para esta cuenta.';
    });
    if (!accountActive) showToast('Tu cuenta no está habilitada para enviar dinero. Consulta con el equipo de soporte.', true);
  }

  function updateTransferAvailability() {
    document.getElementById('transfer-available').textContent = formatMoney(state.account?.disponible_cop);
  }

  function renderMovements(message = '') {
    const homeList = document.getElementById('home-movements');
    const fullList = document.getElementById('all-movements');
    if (message) {
      homeList.innerHTML = `<div class="yb-empty-state">${escapeHtml(message)}</div>`;
      fullList.innerHTML = `<div class="yb-empty-state">${escapeHtml(message)}</div>`;
      return;
    }
    const homeRows = state.movements.slice(0, 5);
    const markup = state.movements.length ? state.movements.map(renderMovement).join('') : '<div class="yb-empty-state">Todavía no hay movimientos en esta cuenta.</div>';
    homeList.innerHTML = homeRows.length ? homeRows.map(renderMovement).join('') : '<div class="yb-empty-state">Todavía no hay movimientos en esta cuenta.</div>';
    fullList.innerHTML = markup;
    document.getElementById('load-more').hidden = !state.nextCursor;
  }

  function renderMovement(movement) {
    const credit = movement.tipo === 'credito';
    const amountClass = credit ? 'is-credit' : 'is-debit';
    const icon = credit ? '↓' : '↗';
    const status = movement.estado === 'en_revision' ? '<small class="yb-movement-status">En revisión</small>' : '';
    const counterpart = movement.contraparte ? ` · ${counterpartLabel(movement.contraparte)}` : '';
    return `<article class="yb-movement-row"><span class="yb-movement-icon ${credit ? 'is-credit' : ''}" aria-hidden="true">${icon}</span><span class="yb-movement-main"><strong>${escapeHtml(conceptLabel(movement.concepto))}</strong><small>${escapeHtml(formatDate(movement.fecha_utc))}${escapeHtml(counterpart)}</small></span><span class="yb-movement-amount ${amountClass}">${escapeHtml(formatMoney(movement.monto_cop))}${status}</span></article>`;
  }

  function counterpartLabel(value) {
    if (value.startsWith('PUENTE-COMERCIO')) return 'Comercio';
    if (value.startsWith('PUENTE-SERVICIO')) return 'Servicio';
    if (value.startsWith('PUENTE-OPERADOR')) return 'Operador';
    if (value.startsWith('PUENTE-OTRA_ENTIDAD')) return 'Otra entidad';
    if (value.startsWith('PUENTE-')) return 'Pago externo';
    return 'Cuenta Yuma';
  }

  async function loadMoreMovements() {
    if (!state.nextCursor) return;
    const button = document.getElementById('load-more');
    setBusy(button, true, 'Cargando…');
    try {
      const result = await api(`/v1/cuentas/me/movimientos?limite=20&cursor=${encodeURIComponent(state.nextCursor)}`);
      state.movements.push(...(result?.movimientos || []));
      state.nextCursor = result?.siguiente_cursor ?? null;
      renderMovements();
    } catch (error) {
      showToast(presentError(error), true);
    } finally {
      setBusy(button, false);
      button.hidden = !state.nextCursor;
    }
  }

  function changePage(page) {
    const pages = { home: 'home-page', transfer: 'transfer-page', activity: 'activity-page' };
    if (!pages[page]) return;
    Object.entries(pages).forEach(([key, id]) => { document.getElementById(id).hidden = key !== page; });
    document.querySelectorAll('.yb-nav-item').forEach(button => button.classList.toggle('is-active', button.dataset.page === page));
    document.getElementById('breadcrumb-label').textContent = ({ home: 'INICIO', transfer: 'ENVIAR DINERO', activity: 'MOVIMIENTOS' })[page];
    if (page === 'transfer') document.getElementById('transfer-phone').focus();
  }

  function createIdempotencyKey() {
    const random = crypto.randomUUID ? crypto.randomUUID().replaceAll('-', '') : `${Date.now().toString(16)}${Math.random().toString(16).slice(2)}`;
    return `yuma-${random}`;
  }

  function getPendingOperation(payload) {
    const fingerprint = JSON.stringify(payload);
    const operationKey = accountStorageKey('yuma-wallet-pending-transfer');
    let pending = readSessionObject(operationKey);
    if (pending && pending.fingerprint !== fingerprint) {
      throw new ApiError('Hay una transferencia cuyo resultado no se confirmó. Reintenta con los mismos datos antes de iniciar otra.', 409, 'transferencia_pendiente');
    }
    if (!pending) {
      pending = { fingerprint, key: createIdempotencyKey(), payload, owner: state.account?.id_cuenta || '' };
      sessionStorage.setItem(operationKey, JSON.stringify(pending));
    }
    return pending;
  }

  async function handleTransfer(event) {
    event.preventDefault();
    setMessage('transfer-error');
    const phone = normalizePhone(document.getElementById('transfer-phone').value);
    const amount = Number(document.getElementById('transfer-amount').value);
    const description = document.getElementById('transfer-description').value.trim();
    if (!/^3\d{9}$/.test(phone)) {
      setMessage('transfer-error', 'Escribe un celular Yuma de 10 dígitos que empiece por 3.');
      return;
    }
    if (phone === normalizePhone(state.account?.llave)) {
      setMessage('transfer-error', 'El celular de destino debe ser distinto al tuyo.');
      return;
    }
    if (!Number.isSafeInteger(amount) || amount < 1) {
      setMessage('transfer-error', 'Escribe un monto entero mayor que cero.');
      return;
    }
    if (amount > Number(state.account?.disponible_cop || 0)) {
      setMessage('transfer-error', 'El monto supera el dinero disponible en tu cuenta.');
      return;
    }

    const payload = { llave: phone, monto_cop: amount, ...(description ? { descripcion: description } : {}) };
    document.getElementById('transfer-receipt').hidden = true;
    if (state.demo) {
      await submitDemoTransfer(payload);
      return;
    }
    let pending;
    try { pending = getPendingOperation(payload); }
    catch (error) { setMessage('transfer-error', presentError(error)); return; }

    const button = document.getElementById('transfer-submit');
    setBusy(button, true, 'Revisando la operación…');
    try {
      const result = await api('/v1/transferencias', {
        method: 'POST',
        headers: { 'Idempotency-Key': pending.key },
        body: payload,
      });
      sessionStorage.removeItem(accountStorageKey('yuma-wallet-pending-transfer'));
      document.getElementById('transfer-receipt').hidden = false;
      renderReceipt(result);
      await loadAccountData();
      if (result?.estado === 'requiere_verificacion' && result.verificacion?.id_verificacion) {
        state.pendingTransfer = {
          id_transaccion: result.id_transaccion,
          id_verificacion: result.verificacion.id_verificacion,
          id_cliente: state.account?.id_cuenta || '',
          monto_cop: result.monto_cop,
          llave_destino: phone,
          descripcion: description,
        };
        sessionStorage.setItem(accountStorageKey('yuma-wallet-pending-transaction'), JSON.stringify(state.pendingTransfer));
        renderReceipt(result); // de nuevo, ya con la verificación pendiente: muestra «Confirmar que soy yo»
        document.getElementById('transfer-biometric-copy').textContent = result.mensaje || 'Confirma esta transferencia con la verificación facial simulada.';
        openDialog('transfer-biometric');
      } else {
        state.pendingTransfer = null;
        sessionStorage.removeItem(accountStorageKey('yuma-wallet-pending-transaction'));
        clearTransferInputs();
        showTransferConfirmation(result);
      }
    } catch (error) {
      if (error.status > 0 && error.status < 500 && error.status !== 429) sessionStorage.removeItem(accountStorageKey('yuma-wallet-pending-transfer'));
      const retryCopy = error.status === 0 || error.status >= 500
        ? `${presentError(error)} Si vuelves a intentar con los mismos datos, se reutiliza la clave segura de esta operación.`
        : presentError(error);
      setMessage('transfer-error', retryCopy);
    } finally {
      setBusy(button, false);
    }
  }

  async function submitDemoTransfer(payload) {
    const button = document.getElementById('transfer-submit');
    setBusy(button, true, 'Simulando transferencia…');
    try {
      const result = await demoApi('/v1/transferencias', { method: 'POST', body: payload });
      document.getElementById('transfer-receipt').hidden = false;
      renderReceipt(result);
      await loadAccountData();
      clearTransferInputs();
      showTransferConfirmation(result);
    } catch (error) {
      setMessage('transfer-error', presentError(error));
    } finally {
      setBusy(button, false);
    }
  }

  function showTransferConfirmation(result) {
    const message = result?.estado === 'completada'
      ? 'Envío realizado. Revisa el comprobante.'
      : result?.estado === 'en_revision'
        ? state.demo ? 'Envío registrado. El caso ya está en la consola demo.' : 'Envío registrado y en revisión; el dinero sigue reservado.'
        : result?.estado === 'rechazada'
          ? 'El envío no se realizó. El dinero permanece en tu cuenta.'
          : 'Recibimos tu solicitud de envío.';
    showToast(message, result?.estado === 'rechazada');
    document.getElementById('transfer-receipt').scrollIntoView({ behavior: 'smooth', block: 'nearest' });
  }

  async function enterWalletDemo() {
    state.demo = true;
    state.account = null;
    state.movements = [];
    state.nextCursor = null;
    showWallet();
    changePage('home');
    await loadAccountData();
    showToast('Demo local lista. No necesitas usuario ni contraseña.');
  }

  function prepareWalletDemoTransfer() {
    const available = Number(state.account?.disponible_cop || 0);
    document.getElementById('transfer-phone').value = '300 000 0002';
    document.getElementById('transfer-amount').value = String(Math.max(1, Math.floor(available * 0.85)));
    document.getElementById('transfer-description').value = 'Prueba de seguridad';
    document.getElementById('transfer-phone').focus();
    setMessage('transfer-error');
  }

  async function resetWalletDemo() {
    if (!state.demo) return;
    localStorage.removeItem(DEMO_STORAGE);
    state.pendingTransfer = null;
    document.getElementById('transfer-form').reset();
    document.getElementById('transfer-receipt').hidden = true;
    await loadAccountData();
    showToast('Datos de la demo reiniciados.');
  }

  function statusCopy(status) {
    const labels = {
      completada: { title: 'Envío realizado', detail: 'El dinero se envió correctamente.', type: 'success', mark: '✓' },
      requiere_verificacion: { title: 'Confirma que eres tú', detail: 'El dinero todavía no ha salido de tu cuenta.', type: 'review', mark: '◎' },
      en_revision: { title: 'Tu transferencia está en revisión', detail: 'El monto sigue en tu cuenta mientras el equipo revisa la operación.', type: 'review', mark: '◷' },
      rechazada: { title: 'No se pudo completar', detail: 'El dinero permanece en tu cuenta.', type: 'rejected', mark: '×' },
    };
    return labels[status] || { title: 'Estado de la transferencia', detail: 'Consulta el estado de la operación.', type: 'review', mark: '◷' };
  }

  function renderReceipt(result) {
    const card = document.getElementById('transfer-receipt');
    const copy = statusCopy(result?.estado);
    const destination = state.pendingTransfer?.llave_destino || result?.llave_destino || document.getElementById('transfer-phone').value || result?.id_destino;
    const canCheck = result?.estado === 'en_revision' || result?.estado === 'requiere_verificacion';
    // Verificación pendiente: el cliente puede volver a abrirla aunque haya cerrado el diálogo (vence a los pocos minutos y pasa a revisión).
    const canVerify = result?.estado === 'requiere_verificacion' && state.pendingTransfer?.id_transaccion === result?.id_transaccion;
    const actions = [
      canVerify ? `<button class="yb-button yb-button-primary" type="button" data-reopen-verification="${escapeHtml(result.id_transaccion)}">Confirmar que soy yo</button>` : '',
      canCheck ? `<button class="yb-button yb-button-secondary" type="button" data-check-transfer="${escapeHtml(result.id_transaccion)}">Consultar estado</button>` : '',
    ].join('');
    card.className = `yb-receipt-card ${copy.type === 'review' ? 'is-review' : ''} ${copy.type === 'rejected' ? 'is-rejected' : ''}`.trim();
    card.innerHTML = `<div class="yb-receipt-head"><span class="yb-receipt-mark" aria-hidden="true">${copy.mark}</span><div><h2>${copy.title}</h2><p>${escapeHtml(result?.mensaje || copy.detail)}</p></div></div><div class="yb-receipt-amount">${escapeHtml(formatMoney(result?.monto_cop))}</div><div class="yb-receipt-meta"><span>Destino<strong>${escapeHtml(destination || 'Cuenta Yuma')}</strong></span><span>Referencia<strong>${escapeHtml(result?.id_transaccion || '—')}</strong></span><span>Estado<strong>${escapeHtml(result?.estado || '—').replaceAll('_', ' ')}</strong></span><span>Fecha<strong>${escapeHtml(formatDate(result?.creada_utc))}</strong></span></div>${actions ? `<div class="yb-receipt-actions">${actions}</div>` : ''}`;
  }

  function reopenTransferVerification() {
    if (!state.pendingTransfer?.id_verificacion) return;
    setMessage('transfer-biometric-error');
    openDialog('transfer-biometric');
  }

  async function handleTransferBiometric(resultSimulated) {
    const transfer = state.pendingTransfer;
    if (!transfer?.id_transaccion || !transfer.id_verificacion) return;
    document.querySelectorAll('[data-transfer-biometric]').forEach(item => { item.disabled = true; });
    const button = document.querySelector(`[data-transfer-biometric="${resultSimulated}"]`);
    setBusy(button, true, 'Confirmando…');
    setMessage('transfer-biometric-error');
    try {
      const result = await api(`/v1/transferencias/${encodeURIComponent(transfer.id_transaccion)}/biometria`, {
        method: 'POST',
        body: { id_verificacion: transfer.id_verificacion, resultado_simulado: resultSimulated },
      });
      state.pendingTransfer = null;
      sessionStorage.removeItem(accountStorageKey('yuma-wallet-pending-transaction'));
      closeDialog('transfer-biometric');
      document.getElementById('transfer-receipt').hidden = false;
      renderReceipt(result);
      await loadAccountData();
      clearTransferInputs();
      showTransferConfirmation(result);
    } catch (error) {
      setMessage('transfer-biometric-error', presentError(error));
    } finally {
      document.querySelectorAll('[data-transfer-biometric]').forEach(item => { item.disabled = false; });
      setBusy(button, false);
    }
  }

  async function checkTransfer(id) {
    const button = document.querySelector(`[data-check-transfer="${CSS.escape(id)}"]`);
    if (button) setBusy(button, true, 'Consultando…');
    try {
      const result = await api(`/v1/transferencias/${encodeURIComponent(id)}`);
      if (result?.estado !== 'requiere_verificacion' && state.pendingTransfer?.id_transaccion === id) {
        state.pendingTransfer = null; // la verificación ya se resolvió o venció (pasó a revisión)
        sessionStorage.removeItem(accountStorageKey('yuma-wallet-pending-transaction'));
      }
      renderReceipt(result);
      await loadAccountData();
      if (result?.estado === 'completada' || result?.estado === 'rechazada') showToast(result.estado === 'completada' ? 'La transferencia fue completada.' : 'La transferencia fue rechazada.');
    } catch (error) {
      showToast(presentError(error), true);
    } finally {
      if (button) setBusy(button, false);
    }
  }

  async function restorePendingTransaction() {
    const pending = state.pendingTransfer;
    try {
      const result = await api(`/v1/transferencias/${encodeURIComponent(pending.id_transaccion)}`);
      document.getElementById('transfer-receipt').hidden = false;
      renderReceipt(result);
      if (result?.estado === 'requiere_verificacion' && pending.id_verificacion) {
        changePage('transfer'); // el comprobante con «Confirmar que soy yo» vive en la página de envío
        openDialog('transfer-biometric');
      } else if (result?.estado !== 'en_revision') {
        state.pendingTransfer = null;
        sessionStorage.removeItem(accountStorageKey('yuma-wallet-pending-transaction'));
      }
    } catch (error) {
      if (error.status === 404) {
        state.pendingTransfer = null;
        sessionStorage.removeItem(accountStorageKey('yuma-wallet-pending-transaction'));
      }
    }
  }

  async function loadDevelopmentOtp() {
    const verification = state.pendingLoginVerification;
    if (!verification?.id_verificacion) return;
    const button = document.getElementById('dev-otp-button');
    setBusy(button, true, 'Buscando código…');
    setMessage('dev-otp-result');
    try {
      const result = await api(`/v1/dev/otp/${encodeURIComponent(verification.id_verificacion)}`, { auth: false }, false);
      document.getElementById('dev-otp-result').textContent = result?.codigo ? `Código de prueba: ${result.codigo}` : 'El código todavía no está disponible.';
    } catch (error) {
      document.getElementById('dev-otp-result').textContent = error.status === 404
        ? 'El código de prueba solo está disponible cuando la API se ejecuta en Development.'
        : presentError(error);
    } finally {
      setBusy(button, false);
    }
  }

  function setupEvents() {
    document.getElementById('login-form').addEventListener('submit', handleLogin);
    document.getElementById('wallet-demo-entry').addEventListener('click', enterWalletDemo);
    document.getElementById('prepare-wallet-demo').addEventListener('click', prepareWalletDemoTransfer);
    document.getElementById('reset-wallet-demo').addEventListener('click', resetWalletDemo);
    document.getElementById('otp-form').addEventListener('submit', handleOtp);
    document.getElementById('transfer-form').addEventListener('submit', handleTransfer);
    document.getElementById('dev-otp-button').addEventListener('click', loadDevelopmentOtp);
    document.getElementById('refresh-home').addEventListener('click', loadAccountData);
    document.getElementById('refresh-activity').addEventListener('click', loadAccountData);
    document.getElementById('load-more').addEventListener('click', loadMoreMovements);
    document.getElementById('logout-button').addEventListener('click', () => endSession('Sesión cerrada.'));
    document.getElementById('login-phone').addEventListener('input', event => {
      const value = normalizePhone(event.currentTarget.value).slice(0, 10);
      event.currentTarget.value = value.replace(/(\d{3})(?=\d)/g, '$1 ').trim();
    });
    document.getElementById('transfer-phone').addEventListener('input', event => {
      const value = normalizePhone(event.currentTarget.value).slice(0, 10);
      event.currentTarget.value = value.replace(/(\d{3})(?=\d)/g, '$1 ').trim();
    });

    document.addEventListener('click', event => {
      const nav = event.target.closest('[data-page]');
      if (nav && !nav.disabled) changePage(nav.dataset.page);
      const quickNav = event.target.closest('[data-go]');
      if (quickNav && !quickNav.disabled) changePage(quickNav.dataset.go);
      const close = event.target.closest('[data-close]');
      if (close) closeDialog(close.dataset.close);
      const loginBio = event.target.closest('[data-login-biometric]');
      if (loginBio) handleLoginBiometric(loginBio.dataset.loginBiometric);
      const transferBio = event.target.closest('[data-transfer-biometric]');
      if (transferBio) handleTransferBiometric(transferBio.dataset.transferBiometric);
      const check = event.target.closest('[data-check-transfer]');
      if (check) checkTransfer(check.dataset.checkTransfer);
      const reopen = event.target.closest('[data-reopen-verification]');
      if (reopen) reopenTransferVerification();
    });
    // Cerrar la verificación (×, Escape o clic fuera) no la cancela: queda pendiente y se puede retomar desde el comprobante.
    document.getElementById('transfer-biometric').addEventListener('close', () => {
      if (state.pendingTransfer?.id_verificacion) {
        showToast('Tu envío sigue pendiente de verificación. Usa «Confirmar que soy yo»; si no confirmas en unos minutos, pasa a revisión.');
      }
    });
  }

  async function init() {
    setupEvents();
    if (!state.accessToken && state.refreshToken) await refreshAccessToken();
    if (!state.accessToken) {
      showAuth();
      return;
    }
    showWallet();
    await loadAccountData();
    if (state.pendingTransfer?.id_transaccion && state.pendingTransfer?.id_cliente === state.account?.id_cuenta) {
      await restorePendingTransaction();
    }
  }

  init();
})();
