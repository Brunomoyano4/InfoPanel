'use strict';

// ================= Estado =================
const $ = (s, el = document) => el.querySelector(s);
const store = {
  get(k) { try { return localStorage.getItem(k); } catch { return null; } },
  set(k, v) { try { v == null ? localStorage.removeItem(k) : localStorage.setItem(k, v); } catch { } },
};

// ================= Temas =================
const THEMES = [
  { id: 'dark', name: 'Oscuro', swatch: '#3ddc97', bg: '#0e1116' },
  { id: 'amoled', name: 'AMOLED', swatch: '#3ddc97', bg: '#000000' },
  { id: 'light', name: 'Claro', swatch: '#0f9d63', bg: '#f4f6f9' },
  { id: 'rog', name: 'ROG', swatch: '#ff2a3c', bg: '#0d0b0c' },
  { id: 'cyber', name: 'Cyber', swatch: '#00e5ff', bg: '#0b0a14' },
  { id: 'ocean', name: 'Océano', swatch: '#4fc3f7', bg: '#0a1420' },
];

// Tema personalizado: un tema base más colores propios (por dispositivo)
const CUSTOM_DEFAULT = { base: 'dark', accent: '#3ddc97', info: '#5aa9ff' };
const ACCENT_SWATCHES = ['#3ddc97', '#00e5ff', '#4fc3f7', '#7c6cff', '#c86bfa', '#ff4fa3', '#ff2a3c', '#ff8a3d', '#f5c542', '#a3e635'];
const HEX = /^#?([0-9a-f]{3}|[0-9a-f]{6})$/i;

function getCustom() {
  try { return { ...CUSTOM_DEFAULT, ...JSON.parse(store.get('ip_custom') || '{}') }; }
  catch { return { ...CUSTOM_DEFAULT }; }
}

// Normaliza "#abc" / "abc" a "#aabbcc"
function normHex(v) {
  const m = String(v).trim().match(HEX);
  if (!m) return null;
  const h = m[1].length === 3 ? [...m[1]].map(c => c + c).join('') : m[1];
  return '#' + h.toLowerCase();
}

// Texto negro o blanco sobre el acento según su luminancia
function onColor(hex) {
  const [r, g, b] = [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16) / 255)
    .map(c => c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);
  return 0.2126 * r + 0.7152 * g + 0.0722 * b > 0.4 ? '#0b0f14' : '#ffffff';
}

function setTheme(id) {
  const root = document.documentElement;
  const custom = id === 'custom' ? getCustom() : null;
  const t = THEMES.find(x => x.id === (custom ? custom.base : id)) || THEMES[0];
  root.dataset.theme = t.id;
  root.dataset.custom = custom ? '1' : '';
  for (const p of ['--accent', '--info', '--on-accent']) root.style.removeProperty(p);
  if (custom) {
    root.style.setProperty('--accent', custom.accent);
    root.style.setProperty('--info', custom.info);
    root.style.setProperty('--on-accent', onColor(custom.accent));
  }
  document.querySelector('meta[name=theme-color]').content = t.bg;
  store.set('ip_theme', custom ? 'custom' : t.id);
  markTheme();
}

function markTheme() {
  const current = store.get('ip_theme') || 'dark';
  document.querySelectorAll('#themes [data-theme]').forEach(b => b.classList.toggle('on', b.dataset.theme === current));
  const swatch = document.querySelector('#themes [data-theme=custom] i');
  if (swatch) swatch.style.background = getCustom().accent;
  $('#theme-editor')?.classList.toggle('hidden', current !== 'custom');
}

function themeEditorHtml() {
  const c = getCustom();
  const colorRow = (key, label) => `<div class="color-row" data-key="${key}">
      <span>${label}</span>
      <input type="color" value="${c[key]}" aria-label="${label}">
      <input class="hex" value="${c[key]}" maxlength="7" spellcheck="false" aria-label="${label} en hexadecimal">
    </div>`;
  return `<div id="theme-editor" class="theme-editor hidden">
    <p class="note">Base</p>
    <div class="seg" id="te-base">${THEMES.map(t => `<button data-v="${t.id}" class="${t.id === c.base ? 'on' : ''}">${t.name}</button>`).join('')}</div>
    ${colorRow('accent', 'Acento')}
    <div class="swatches">${ACCENT_SWATCHES.map(s => `<button data-swatch="${s}" style="background:${s}" aria-label="${s}"></button>`).join('')}</div>
    ${colorRow('info', 'Secundario')}
    <p class="note">El acento colorea botones, barras y selección; el secundario, gráficos y detalles.</p>
    <div class="btns"><button class="btn sm" id="te-reset">Restablecer</button></div>
  </div>`;
}

function bindThemeEditor() {
  const ed = $('#theme-editor');
  const save = patch => {
    const c = { ...getCustom(), ...patch };
    store.set('ip_custom', JSON.stringify(c));
    setTheme('custom');
    // Sincroniza los campos sin pisar el que se está escribiendo
    ed.querySelectorAll('.color-row').forEach(r => {
      const v = c[r.dataset.key];
      r.querySelector('input[type=color]').value = v;
      const hex = r.querySelector('.hex');
      if (document.activeElement !== hex) hex.value = v;
      hex.classList.remove('bad');
    });
    ed.querySelectorAll('#te-base button').forEach(b => b.classList.toggle('on', b.dataset.v === c.base));
  };
  ed.addEventListener('input', e => {
    const row = e.target.closest('.color-row');
    if (!row) return;
    const v = normHex(e.target.value);
    if (!v) return e.target.classList.add('bad');
    save({ [row.dataset.key]: v });
  });
  ed.addEventListener('click', e => {
    const base = e.target.closest('#te-base [data-v]');
    const sw = e.target.closest('[data-swatch]');
    if (base) save({ base: base.dataset.v });
    if (sw) save({ accent: sw.dataset.swatch });
    if (e.target.id === 'te-reset') save({ ...CUSTOM_DEFAULT });
  });
}

setTheme(store.get('ip_theme') || 'dark');

let pcs = (() => { try { return JSON.parse(store.get('ip_pcs') || '[]'); } catch { return []; } })();
let pcId = store.get('ip_pc');
let token = null;   // token de la PC actual
let base = '';      // dirección de la PC actual ('' = la que sirvió la página)
let ws = null;
let last = null;
let info = null;
const HIST = 90;
const hist = { cpuTemp: [], cpuLoad: [], gpuLoad: [], cpuPower: [], memLoad: [], batLevel: [] };
const touched = {}; // id de control -> último momento en que el usuario lo tocó

// ================= Utilidades =================
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const num = (v, d = 0) => v == null ? '—' : Number(v).toFixed(d);
const tempClass = t => t == null ? '' : t >= 85 ? 't-hot' : t >= 70 ? 't-warn' : 't-ok';

function fmtBytes(b, perSec = false) {
  if (b == null) return '—';
  const u = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0;
  while (b >= 1024 && i < u.length - 1) { b /= 1024; i++; }
  return `${b.toFixed(b >= 100 || i === 0 ? 0 : 1)} ${u[i]}${perSec ? '/s' : ''}`;
}

function fmtDuration(sec) {
  if (sec == null) return '—';
  const d = Math.floor(sec / 86400), h = Math.floor(sec % 86400 / 3600), m = Math.floor(sec % 3600 / 60);
  return d ? `${d}d ${h}h` : h ? `${h}h ${m}m` : `${m}m`;
}

const UNITS = {
  Voltage: [3, 'V'], Current: [2, 'A'], Power: [1, 'W'], Clock: [0, 'MHz'], Temperature: [1, '°C'],
  Load: [1, '%'], Frequency: [0, 'Hz'], Fan: [0, 'RPM'], Flow: [1, 'L/h'], Control: [0, '%'],
  Level: [1, '%'], Factor: [2, ''], Data: [2, 'GB'], SmallData: [0, 'MB'], Energy: [0, 'mWh'], Noise: [0, 'dBA'],
};
function fmtSensor(type, v) {
  if (v == null) return '—';
  if (type === 'Throughput') return fmtBytes(v, true);
  if (type === 'TimeSpan') return fmtDuration(v);
  const [d, u] = UNITS[type] || [2, ''];
  return `${num(v, d)} ${u}`.trim();
}

// Íconos de línea (trazo con currentColor) para títulos de tarjetas y controles
const ICONS = {
  cpu: '<rect x="5" y="5" width="14" height="14" rx="2"/><rect x="9" y="9" width="6" height="6"/><path d="M9 2v3M15 2v3M9 19v3M15 19v3M2 9h3M2 15h3M19 9h3M19 15h3"/>',
  gpu: '<rect x="2" y="6" width="20" height="12" rx="2"/><path d="M6 10h4v4H6zM14 10h4M14 14h4"/>',
  mem: '<rect x="3" y="7" width="18" height="10" rx="2"/><path d="M7 7v10M11 7v10M15 7v10M3 20h18"/>',
  battery: '<rect x="2" y="7" width="18" height="10" rx="2"/><path d="M22 11v2M6 11v2"/>',
  fan: '<circle cx="12" cy="12" r="2"/><path d="M12 10c0-4 1-7 4-7 2 0 3 2 2 4l-4 4M14 12c4 0 7 1 7 4 0 2-2 3-4 2l-4-4M12 14c0 4-1 7-4 7-2 0-3-2-2-4l4-4M10 12c-4 0-7-1-7-4 0-2 2-3 4-2l4 4"/>',
  bolt: '<path d="M13 2 4 14h7l-1 8 9-12h-7z"/>',
  wifi: '<path d="M5 12.5a10 10 0 0 1 14 0M8.5 16a5 5 0 0 1 7 0M2 9a15 15 0 0 1 20 0"/><circle cx="12" cy="19.5" r="1"/>',
  disk: '<ellipse cx="12" cy="6" rx="8" ry="3"/><path d="M4 6v12c0 1.7 3.6 3 8 3s8-1.3 8-3V6M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3"/>',
  info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v6M12 7.5v.5"/>',
  temp: '<path d="M14 4v10.5a4 4 0 1 1-4 0V4a2 2 0 0 1 4 0Z"/>',
  volume: '<path d="M11 5 6 9H2v6h4l5 4V5z"/><path d="M15.5 8.5a5 5 0 0 1 0 7M19 5a10 10 0 0 1 0 14"/>',
  mute: '<path d="M11 5 6 9H2v6h4l5 4V5z"/><path d="m22 9-6 6M16 9l6 6"/>',
  sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/>',
  apps: '<rect x="3" y="3" width="7" height="7" rx="1.5"/><rect x="14" y="3" width="7" height="7" rx="1.5"/><rect x="3" y="14" width="7" height="7" rx="1.5"/><rect x="14" y="14" width="7" height="7" rx="1.5"/>',
  sliders: '<path d="M4 21v-7M4 10V3M12 21v-9M12 8V3M20 21v-5M20 12V3M1 14h6M9 8h6M17 16h6"/>',
};
const icon = (name, size = 18) =>
  `<svg class="ico" width="${size}" height="${size}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${ICONS[name] || ''}</svg>`;

function toast(msg) {
  const t = $('#toast');
  t.textContent = msg;
  t.classList.add('show');
  clearTimeout(toast.t);
  toast.t = setTimeout(() => t.classList.remove('show'), 2200);
}

function push(arr, v) {
  arr.push(v ?? null);
  if (arr.length > HIST) arr.shift();
}

function spark(values, max, color = 'var(--accent)') {
  const pts = values.map((v, i) => v == null ? null : `${(i / (HIST - 1) * 100).toFixed(1)},${(40 - Math.min(v / max, 1) * 38).toFixed(1)}`).filter(Boolean);
  // Área suave bajo la línea: le da cuerpo al gráfico cuando el valor es estable
  const area = pts.length > 1 ? `<polygon points="${pts[0].split(',')[0]},40 ${pts.join(' ')} ${pts[pts.length - 1].split(',')[0]},40" fill="${color}" fill-opacity=".1"/>` : '';
  return `<svg class="spark" viewBox="0 0 100 40" preserveAspectRatio="none" aria-hidden="true">${area}
    <polyline points="${pts.join(' ')}" fill="none" stroke="${color}" stroke-width="1.5" vector-effect="non-scaling-stroke"/></svg>`;
}

// ================= PCs y conexión =================
// La app puede manejar varias PCs: cada una se guarda como { id, name, url, token } y todas las llamadas
// van a la PC elegida (url vacía = la misma PC que sirvió la página). Cada PC exige su propio token.

function savePcs() {
  store.set('ip_pcs', JSON.stringify(pcs));
  store.set('ip_pc', pcId);
}

function useCurrentPc() {
  const p = pcs.find(x => x.id === pcId) || pcs[0];
  pcId = p?.id || null;
  token = p?.token || null;
  base = p?.url || '';
}

// Versión anterior (una sola PC, token suelto): se convierte en la primera PC guardada
async function migrateLegacyToken() {
  const legacy = store.get('ip_token');
  if (!legacy || pcs.length) return;
  try {
    const hello = await (await fetch('/api/hello')).json();
    pcs = [{ id: hello.id, name: hello.name, url: location.origin, token: legacy }];
    pcId = hello.id;
    savePcs();
  } catch { }
  store.set('ip_token', null);
}

async function api(path, body) {
  const r = await fetch(base + '/api' + path, {
    method: body === undefined ? 'GET' : 'POST',
    headers: { Authorization: 'Bearer ' + token, 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (r.status === 401) { unpairCurrent('La PC desvinculó este dispositivo'); throw new Error('Sesión vencida'); }
  let data = null;
  try { data = await r.json(); } catch { }
  if (!r.ok) throw new Error(data?.error || 'La PC rechazó la acción');
  return data;
}

async function act(path, body, okMsg) {
  try {
    await api(path, body);
    if (okMsg) toast(okMsg);
    return true;
  } catch (e) {
    toast(e.message);
    return false;
  }
}

function connect() {
  const target = base || location.origin;
  const socket = new WebSocket(target.replace(/^http/, 'ws') + '/ws');
  ws = socket;
  socket.onopen = () => socket.send(token);
  socket.onmessage = e => {
    if (ws !== socket) return; // conexión de una PC anterior
    setStatus(true);
    onData(JSON.parse(e.data));
  };
  socket.onclose = e => {
    if (ws !== socket) return;
    setStatus(false);
    if (e.code === 1008) return unpairCurrent('La PC desvinculó este dispositivo');
    setTimeout(() => { if (ws === socket) connect(); }, 2000);
  };
}

function disconnect() {
  const s = ws;
  ws = null;
  if (s) { s.onclose = null; s.close(); }
}

function setStatus(on) {
  const s = $('#status');
  s.className = 'status ' + (on ? 'on' : 'off');
  $('span', s).textContent = on ? 'En vivo' : 'Reconectando…';
}

// Quita la PC actual (token inválido o desvinculada) y pasa a otra, o al emparejamiento si no queda ninguna
function unpairCurrent(msg) {
  pcs = pcs.filter(p => p.id !== pcId);
  pcId = pcs[0]?.id || null;
  savePcs();
  if (msg) toast(msg);
  switchPc(pcId);
}

function showPair() {
  disconnect();
  $('#app').classList.add('hidden');
  $('#pair').classList.remove('hidden');
}

// Cambia de PC: se corta la conexión, se limpia todo lo que dependía de la anterior y se arranca de nuevo
function switchPc(id) {
  disconnect();
  pcId = id;
  useCurrentPc();
  savePcs();
  if (!token) return showPair();
  last = null;
  info = null;
  summary = null;
  Object.values(hist).forEach(a => a.length = 0);
  controlBuilt = false;
  $('#control').innerHTML = '';
  sensorSig = '';
  $('#sensors').innerHTML = '';
  $('#processes').innerHTML = '';
  resetDashboardState();
  start();
}

async function start() {
  $('#pair').classList.add('hidden');
  $('#app').classList.remove('hidden');
  setStatus(false);
  $('#machine-name').textContent = pcs.find(p => p.id === pcId)?.name || '—';
  $('#cpu-name').textContent = pcs.length > 1 ? `${pcs.length} PCs vinculadas` : '';
  try {
    info = await api('/info');
    $('#machine-name').textContent = info.machine;
    $('#cpu-name').textContent = info.cpu;
    await loadDashboards();
  } catch (e) {
    toast(`No se pudo conectar con ${pcs.find(p => p.id === pcId)?.name || 'la PC'}: ${e.message === 'Failed to fetch' ? 'no responde' : e.message}`);
    // Se reintenta solo, por si la PC estaba apagada o cambió de red
    setTimeout(() => { if (!info && token) start(); }, 5000);
    return;
  }
  connect();
}

// Vincula una PC (la que sirvió la página o una de la red) con su PIN
async function pairPc(url, pin) {
  const target = url || '';
  const hello = await (await fetch(target + '/api/hello')).json();
  const r = await fetch(target + '/api/pair', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ pin }) });
  if (!r.ok) throw new Error('PIN incorrecto');
  const { token: t } = await r.json();
  const pc = { id: hello.id, name: hello.name, url: url || location.origin, token: t };
  pcs = [...pcs.filter(p => p.id !== pc.id), pc];
  return pc;
}

$('#pair-form').addEventListener('submit', async e => {
  e.preventDefault();
  $('#pair-error').textContent = '';
  try {
    const pc = await pairPc('', $('#pin').value.trim());
    switchPc(pc.id);
  } catch (err) {
    $('#pair-error').textContent = err.message;
  }
});

// ---------- Selector de PCs ----------
$('#machine').onclick = () => openPcs();

async function openPcs() {
  const secure = location.protocol === 'https:';
  const urlFor = (ip, port, httpsPort) => secure ? `https://${ip}:${httpsPort}` : `http://${ip}:${port}`;
  let peers = [];
  try { peers = (await api('/peers')).filter(p => !pcs.some(x => x.id === p.id)); } catch { }

  openModal('PCs', `
    <div class="pc-list">${pcs.map(p => `
      <div class="pc ${p.id === pcId ? 'on' : ''}">
        <div><b>${esc(p.name)}</b><small>${esc(p.url.replace(/^https?:\/\//, ''))}</small></div>
        <div class="btns">${p.id === pcId ? '<span class="badge">Actual</span>' : `<button class="btn sm primary" data-use="${esc(p.id)}">Usar</button>`}
          <button class="btn sm danger" data-forget="${esc(p.id)}" aria-label="Quitar">✕</button></div>
      </div>`).join('')}</div>
    <h4 class="modal-sub">En esta red</h4>
    <div class="pc-list">${peers.map(p => `
      <div class="pc">
        <div><b>${esc(p.name)}</b><small>${esc(p.ip)}</small></div>
        <button class="btn sm" data-link="${esc(urlFor(p.ip, p.port, p.httpsPort))}" data-setup="http://${esc(p.ip)}:${p.port}/setup.html">Vincular</button>
      </div>`).join('') || '<p class="note">No se encontraron otras PCs con InfoPanel. Tienen que estar encendidas, en la misma red y con InfoPanel abierto.</p>'}</div>
    <h4 class="modal-sub">Agregar por dirección</h4>
    <form class="inline" id="pc-add">
      <input name="addr" placeholder="192.168.1.20" required>
      <button class="btn" type="submit">Vincular</button>
    </form>
    <p class="note" id="pc-msg"></p>`, body => {
    const msg = html => $('#pc-msg', body).innerHTML = html;
    const link = async (url, setup) => {
      const pin = prompt('PIN de esa PC (figura en el menú de su ícono en la bandeja)');
      if (!pin) return;
      msg('Conectando…');
      try {
        const pc = await pairPc(url, pin.trim());
        closeModal();
        toast(`${pc.name} vinculada`);
        switchPc(pc.id);
      } catch (e) {
        // Un error de red en HTTPS suele ser el certificado de esa PC todavía no instalado
        msg(e.message === 'PIN incorrecto' ? 'PIN incorrecto.'
          : `No se pudo conectar. ${secure ? `Si es la primera vez, instalá el certificado de esa PC desde <a href="${esc(setup)}" target="_blank">${esc(setup)}</a> y probá de nuevo.` : 'Verificá la dirección y que InfoPanel esté abierto.'}`);
      }
    };
    body.onclick = e => {
      const use = e.target.closest('[data-use]');
      const forget = e.target.closest('[data-forget]');
      const lnk = e.target.closest('[data-link]');
      if (use) { closeModal(); switchPc(use.dataset.use); }
      if (forget && confirm('¿Quitar esta PC de la lista? Habrá que volver a vincularla con su PIN.')) {
        const wasCurrent = forget.dataset.forget === pcId;
        pcs = pcs.filter(p => p.id !== forget.dataset.forget);
        savePcs();
        closeModal();
        if (wasCurrent) switchPc(pcs[0]?.id || null); else openPcs();
      }
      if (lnk) link(lnk.dataset.link, lnk.dataset.setup);
    };
    $('#pc-add', body).onsubmit = e => {
      e.preventDefault();
      const addr = new FormData(e.target).get('addr').trim().replace(/^https?:\/\//, '').replace(/\/.*$/, '');
      const [host, port] = addr.split(':');
      const url = secure ? `https://${host}:${port || 5056}` : `http://${host}:${port || 5055}`;
      link(url, `http://${host}:5055/setup.html`);
    };
  });
}

// En HTTP se ofrece instalar la app (requiere HTTPS)
if (location.protocol !== 'https:' && !store.get('ip_install_hidden')) $('#install-banner').classList.remove('hidden');
$('#install-dismiss').onclick = () => { store.set('ip_install_hidden', '1'); $('#install-banner').classList.add('hidden'); };
// La app instalada funciona aunque la PC no responda al abrirla (la interfaz queda guardada)
if ('serviceWorker' in navigator && isSecureContext) navigator.serviceWorker.register('/sw.js').catch(() => { });

// Pestañas
document.querySelectorAll('.tabs button').forEach(b => b.addEventListener('click', () => {
  document.querySelectorAll('.tabs button').forEach(x => x.classList.toggle('active', x === b));
  document.querySelectorAll('.view').forEach(v => v.classList.toggle('active', v.dataset.view === b.dataset.tab));
  store.set('ip_tab', b.dataset.tab);
}));
const savedTab = store.get('ip_tab');
if (savedTab) document.querySelector(`.tabs button[data-tab="${savedTab}"]`)?.click();

// ================= Extracción de datos =================
const hwByType = (...types) => (last?.hardware || []).filter(h => types.includes(h.type));
const find = (hw, type, re) => hw?.sensors.find(s => s.type === type && re.test(s.name));
const val = (hw, type, re) => find(hw, type, re)?.value;
// Primer sensor con valor real (> 0) según el orden de preferencia: sin PawnIO, LHM da 0 y se usan los alternativos
const pick = (hw, type, res) => {
  for (const re of res) {
    const s = hw?.sensors.find(s => s.type === type && re.test(s.name) && s.value > 0);
    if (s) return s.value;
  }
  return null;
};

function summarize(d) {
  const cpu = hwByType('Cpu')[0];
  const cpuClocks = cpu?.sensors.filter(s => s.type === 'Clock' && /^Core #/.test(s.name) && s.value > 0).map(s => s.value) || [];
  const mem = (d.hardware || []).find(h => h.type === 'Memory' && !/virtual/i.test(h.name) && find(h, 'Data', /^Memory Used$/));

  return {
    cpu: cpu && {
      load: val(cpu, 'Load', /^CPU Total$/),
      temp: pick(cpu, 'Temperature', [/Tctl|Tdie|Package/i, /ACPI/i, /./]),
      power: pick(cpu, 'Power', [/^Package$/i, /RAPL/i]),
      clock: cpuClocks.length ? Math.max(...cpuClocks) : pick(cpu, 'Clock', [/Efectivo/i]),
      cores: cpu.sensors.filter(s => s.type === 'Load' && /^CPU Core #\d+$/.test(s.name)).map(s => s.value || 0),
    },
    gpus: hwByType('GpuNvidia', 'GpuAmd', 'GpuIntel').map(g => ({
      name: g.name,
      load: val(g, 'Load', /^(GPU Core|D3D 3D)$/),
      temp: val(g, 'Temperature', /GPU Core|GPU Hot Spot|GPU/i),
      power: val(g, 'Power', /GPU (Package|Power|Core)/i) ?? g.sensors.find(s => s.type === 'Power')?.value,
      clock: val(g, 'Clock', /GPU Core/i),
      vramUsed: val(g, 'SmallData', /GPU Memory Used|Dedicated Memory Used/i),
      vramTotal: val(g, 'SmallData', /GPU Memory Total/i),
    })),
    mem: mem && {
      load: val(mem, 'Load', /^Memory$/),
      used: val(mem, 'Data', /^Memory Used$/),
      avail: val(mem, 'Data', /^Memory Available$/),
    },
    battery: hwByType('Battery').map(b => ({
      level: val(b, 'Level', /Charge Level/i),
      rate: val(b, 'Power', /Charge Rate|Discharge Rate/i),
      charging: !!find(b, 'Power', /^Charge Rate/i),
      remaining: val(b, 'TimeSpan', /Remaining/i),
      wear: val(b, 'Level', /Degradation/i),
    }))[0],
    net: hwByType('Network').map(n => ({
      name: n.name,
      up: val(n, 'Throughput', /Upload Speed/i) || 0,
      down: val(n, 'Throughput', /Download Speed/i) || 0,
    })),
    storage: hwByType('Storage').map(s => ({
      name: s.name,
      temp: val(s, 'Temperature', /./),
      read: val(s, 'Throughput', /Read Rate/i),
      write: val(s, 'Throughput', /Write Rate/i),
    })),
    fans: [
      ...(d.asus?.fans || []).map(f => ({ name: 'Ventilador ' + f.fan.toUpperCase(), rpm: f.rpm })),
      ...(d.hardware || []).flatMap(h => h.sensors.filter(s => s.type === 'Fan').map(s => ({ name: s.name, rpm: s.value }))),
    ],
  };
}

// Agrupa módulos de RAM idénticos (ej. memoria soldada: "4 × 4 GB")
function ramGroups(ram) {
  const groups = new Map();
  for (const m of ram) {
    const k = `${m.sizeGb}|${m.speedMts}|${m.manufacturer}|${m.part}`;
    const g = groups.get(k);
    g ? g.count++ : groups.set(k, { ...m, count: 1 });
  }
  return [...groups.values()];
}

// ================= Métricas (contenido de los widgets de tipo "metric") =================
// Cada métrica devuelve el HTML interno del widget (título + cuerpo) o null si no hay datos.
let summary = null;

function tick(d) {
  summary = summarize(d);
  push(hist.cpuTemp, summary.cpu?.temp);
  push(hist.cpuLoad, summary.cpu?.load);
  push(hist.cpuPower, summary.cpu?.power);
  push(hist.gpuLoad, summary.gpus[0]?.load);
  push(hist.memLoad, summary.mem?.load);
  push(hist.batLevel, summary.battery?.level);
}

// Encabezado de tarjeta: ícono + título + dato secundario a la derecha
const head = (ico, title, meta = '') => `<h3>${icon(ico)}<span>${title}</span>${meta ? `<small>${meta}</small>` : ''}</h3>`;
const stat = (k, v, cls = '') => `<div class="stat"><div class="k">${k}</div><div class="v ${cls}">${v}</div></div>`;

const METRICS = {
  cpu: {
    name: 'CPU',
    render(d, s) {
      if (!s.cpu) return null;
      return `${head('cpu', 'CPU', `${num(s.cpu.clock)} MHz`)}
        <div class="big">${num(s.cpu.load)}<small>%</small></div>
        <div class="bar"><i style="width:${s.cpu.load || 0}%"></i></div>
        <div class="stats">
          ${stat('Temperatura', `${num(s.cpu.temp, 1)} °C`, tempClass(s.cpu.temp))}
          ${stat('Consumo', `${num(s.cpu.power, 1)} W`)}
          ${stat('Uptime', fmtDuration(d.system?.uptimeSec))}
        </div>
        ${s.cpu.cores.length ? `<div class="cores" title="Carga por núcleo">${s.cpu.cores.map(v => `<i style="--h:${Math.min(v, 100)}%"></i>`).join('')}</div>` : ''}
        <div class="charts">${spark(hist.cpuLoad, 100)}${spark(hist.cpuTemp, 100, 'var(--warn)')}</div>`;
    },
  },
  gpu: {
    name: 'GPU',
    render(d, s, cfg) {
      const i = cfg?.index || 0;
      const g = s.gpus[i];
      if (!g) return null;
      return `${head('gpu', 'GPU', esc(g.name))}
        <div class="big">${num(g.load)}<small>%</small></div>
        <div class="bar info"><i style="width:${g.load || 0}%"></i></div>
        <div class="stats">
          ${stat('Temperatura', `${num(g.temp, 1)} °C`, tempClass(g.temp))}
          ${stat('Consumo', `${num(g.power, 1)} W`)}
          ${stat('Clock', `${num(g.clock)} MHz`)}
        </div>
        ${g.vramUsed != null ? `<div class="row"><span>VRAM</span><span class="v">${num(g.vramUsed)}${g.vramTotal ? ' / ' + num(g.vramTotal) : ''} MB</span></div>` : ''}
        ${i === 0 ? `<div class="charts">${spark(hist.gpuLoad, 100, 'var(--info)')}</div>` : ''}`;
    },
  },
  mem: {
    name: 'Memoria',
    render(d, s) {
      if (!s.mem) return null;
      const total = (s.mem.used || 0) + (s.mem.avail || 0);
      return `${head('mem', 'Memoria', `${num(s.mem.load)} % en uso`)}
        <div class="big">${num(s.mem.used, 1)}<small>/ ${num(total, 1)} GB</small></div>
        <div class="bar ${s.mem.load >= 90 ? 'hot' : s.mem.load >= 80 ? 'warn' : ''}"><i style="width:${s.mem.load || 0}%"></i></div>
        <div class="charts">${spark(hist.memLoad, 100)}</div>
        <div class="stats end">
          ${stat('Usada', `${num(s.mem.used, 1)} GB`)}
          ${stat('Libre', `${num(s.mem.avail, 1)} GB`)}
          ${stat('Uso', `${num(s.mem.load)} %`)}
        </div>`;
    },
  },
  battery: {
    name: 'Batería',
    render(d, s) {
      const b = s.battery;
      if (!b) return null;
      return `${head('battery', 'Batería', b.charging ? 'Cargando' : 'Descargando')}
        <div class="big">${num(b.level)}<small>%</small></div>
        <div class="bar ${b.level <= 15 ? 'hot' : ''}"><i style="width:${b.level || 0}%"></i></div>
        <div class="charts">${spark(hist.batLevel, 100)}</div>
        <div class="stats end">
          ${stat(b.charging ? 'Carga' : 'Descarga', `${num(b.rate, 1)} W`)}
          ${stat('Restante', b.remaining ? fmtDuration(b.remaining) : '—')}
          ${stat('Desgaste', b.wear != null ? `${num(b.wear, 1)} %` : '—')}
        </div>`;
    },
  },
  fans: {
    name: 'Ventiladores',
    render(d, s) {
      if (!s.fans.length) return null;
      return `${head('fan', 'Ventiladores', d.asus?.available ? `Modo ${esc(d.asus.modeName)}` : '')}
        <div class="fan-list">${s.fans.map(f => `<div class="row"><span>${esc(f.name)}</span><span class="v">${num(f.rpm)} RPM</span></div>`).join('')}</div>`;
    },
  },
  power: {
    name: 'Límites de potencia',
    render(d) {
      const tdp = d.controls?.tdp;
      if (!tdp || tdp.backend === 'none') return null;
      const v = x => x == null ? '—' : x + ' W';
      return `${head('bolt', 'Límites de potencia', summary?.cpu?.power != null ? `ahora ${num(summary.cpu.power, 1)} W` : '')}
        <div class="stats">
          ${stat('SPL', v(tdp.spl))}
          ${stat('sPPT', v(tdp.sppt))}
          ${stat('fPPT', v(tdp.fppt))}
        </div>
        <div class="charts">${spark(hist.cpuPower, Math.max(tdp.fppt || 30, 10), 'var(--info)')}</div>`;
    },
  },
  net: {
    name: 'Red',
    render(d, s) {
      if (!s.net.length) return null;
      // Columnas fijas para que los valores no salten de renglón al cambiar
      const active = s.net.filter(n => n.up + n.down > 0);
      return `${head('wifi', 'Red')}
        ${(active.length ? active : s.net.slice(0, 2)).map(n => `
          <div class="sub-title">${esc(n.name)}</div>
          <div class="stats two">
            ${stat('↓ Bajada', fmtBytes(n.down, true))}
            ${stat('↑ Subida', fmtBytes(n.up, true))}
          </div>`).join('')}`;
    },
  },
  disks: {
    name: 'Discos',
    render(d, s) {
      if (!d.system?.disks?.length && !s.storage.length) return null;
      return `${head('disk', 'Discos')}
        ${(d.system?.disks || []).map(k => `
          <div class="row"><span>${esc(k.name)} ${esc(k.label)}</span><span class="v">${k.totalGb - k.freeGb} / ${k.totalGb} GB</span></div>
          <div class="bar"><i style="width:${k.totalGb ? (k.totalGb - k.freeGb) / k.totalGb * 100 : 0}%"></i></div>`).join('')}
        ${s.storage.map(st => `
          <div class="sub-title">${esc(st.name)}</div>
          <div class="stats">
            ${stat('Temperatura', `${num(st.temp)} °C`, tempClass(st.temp))}
            ${stat('Lectura', fmtBytes(st.read, true))}
            ${stat('Escritura', fmtBytes(st.write, true))}
          </div>`).join('')}`;
    },
  },
  system: {
    name: 'Sistema',
    render(d) {
      const si = info?.system;
      if (!si) return null;
      return `${head('info', 'Sistema')}
        <div class="row"><span>Windows</span><span class="v r">${esc(si.windows)}</span></div>
        <div class="row"><span>Placa</span><span class="v r">${esc(si.board)}</span></div>
        <div class="row"><span>BIOS</span><span class="v r">${esc(si.bios)}</span></div>
        ${si.gpus.map(g => `<div class="row"><span>${esc(g.name)}</span><span class="v r">Driver ${esc(g.driver)}</span></div>`).join('')}
        ${ramGroups(si.ram).map(g => `<div class="row"><span>RAM</span><span class="v r">${g.count > 1 ? g.count + ' × ' : ''}${g.sizeGb} GB · ${g.speedMts} MT/s · ${esc(g.manufacturer)}</span></div>`).join('')}
        ${(info.urls || []).map(u => `<div class="row"><span>Dirección</span><span class="v r">${esc(u.replace('http://', ''))}</span></div>`).join('')}
        <div class="row"><span>Encendido hace</span><span class="v r">${fmtDuration(d.system?.uptimeSec)}</span></div>`;
    },
  },
};

// ================= Sensores =================
let sensorSig = '';
let sensorType = store.get('ip_stype') || 'all';
const sensorCells = new Map();

// Nombres en español de los tipos de sensor de LibreHardwareMonitor
const TYPE_LABELS = {
  Temperature: 'Temperatura', Load: 'Carga', Power: 'Consumo', Clock: 'Clock', Fan: 'Ventiladores',
  Voltage: 'Voltaje', Current: 'Corriente', Data: 'Datos', SmallData: 'Memoria', Throughput: 'Transferencia',
  Level: 'Nivel', Control: 'Control', Factor: 'Factor', Energy: 'Energía', TimeSpan: 'Tiempo', Frequency: 'Frecuencia',
};
const typeLabel = t => TYPE_LABELS[t] || t;
const HW_ICONS = { Cpu: 'cpu', GpuAmd: 'gpu', GpuNvidia: 'gpu', GpuIntel: 'gpu', Memory: 'mem', Battery: 'battery', Storage: 'disk', Network: 'wifi' };

function renderSensors(d) {
  const sig = d.hardware.map(h => h.id + ':' + h.sensors.length).join('|');
  if (sig !== sensorSig) {
    sensorSig = sig;
    sensorCells.clear();
    const open = new Set([...document.querySelectorAll('details.hw[open]')].map(x => x.dataset.id));
    // Chips por tipo: solo los más útiles primero, el resto después
    const types = [...new Set(d.hardware.flatMap(h => h.sensors.map(s => s.type)))];
    const order = ['Temperature', 'Load', 'Power', 'Clock', 'Fan'];
    types.sort((a, b) => (order.indexOf(a) + 1 || 99) - (order.indexOf(b) + 1 || 99));
    if (sensorType !== 'all' && !types.includes(sensorType)) sensorType = 'all';
    $('#sensor-types').innerHTML = [['all', 'Todos'], ...types.map(t => [t, typeLabel(t)])]
      .map(([t, l]) => `<button type="button" data-type="${esc(t)}" class="${t === sensorType ? 'on' : ''}">${esc(l)}</button>`).join('');

    $('#sensors').innerHTML = d.hardware.map((h, i) => {
      const groups = {};
      h.sensors.forEach(s => (groups[s.type] ||= []).push(s));
      return `<details class="hw" data-id="${esc(h.id)}" ${open.has(h.id) || (!open.size && i === 0) ? 'open' : ''}>
        <summary>${icon(HW_ICONS[h.type] || 'sliders')}<span class="hw-name">${esc(h.name)}</span><small>${h.sensors.length}</small></summary>
        ${Object.entries(groups).map(([type, list]) => `
          <div class="sgroup" data-type="${esc(type)}"><h4>${esc(typeLabel(type))}</h4>
          ${list.map(s => `<div class="srow" data-name="${esc((h.name + ' ' + s.name).toLowerCase())}">
            <span class="sn">${esc(s.name)}</span><span class="mm" data-mm="${esc(s.id)}"></span><span class="v" data-v="${esc(s.id)}"></span></div>`).join('')}
          </div>`).join('')}
      </details>`;
    }).join('');
    document.querySelectorAll('[data-v]').forEach(el => sensorCells.set(el.dataset.v, { v: el, mm: el.previousElementSibling }));
    applySensorFilter();
  }
  for (const h of d.hardware) for (const s of h.sensors) {
    const c = sensorCells.get(s.id);
    if (!c) continue;
    c.v.textContent = fmtSensor(s.type, s.value);
    c.v.className = 'v ' + (s.type === 'Temperature' ? tempClass(s.value) : '');
    c.mm.textContent = s.min != null ? `${fmtSensor(s.type, s.min)} – ${fmtSensor(s.type, s.max)}` : '';
  }
}

function applySensorFilter() {
  const q = $('#sensor-filter').value.trim().toLowerCase();
  document.querySelectorAll('.sgroup').forEach(g => {
    const typeOk = sensorType === 'all' || g.dataset.type === sensorType;
    let any = false;
    g.querySelectorAll('.srow').forEach(r => {
      const ok = typeOk && (!q || r.dataset.name.includes(q));
      r.classList.toggle('hidden', !ok);
      any ||= ok;
    });
    g.classList.toggle('hidden', !any);
  });
  // Los componentes sin resultados se ocultan; con filtro activo se abren los que tienen
  const filtering = !!q || sensorType !== 'all';
  document.querySelectorAll('details.hw').forEach(x => {
    const any = !!x.querySelector('.sgroup:not(.hidden)');
    x.classList.toggle('hidden', !any);
    if (filtering && any) x.open = true;
  });
}
$('#sensor-filter').addEventListener('input', applySensorFilter);
$('#sensor-types').addEventListener('click', e => {
  const b = e.target.closest('[data-type]');
  if (!b) return;
  sensorType = b.dataset.type;
  store.set('ip_stype', sensorType);
  $('#sensor-types').querySelectorAll('button').forEach(x => x.classList.toggle('on', x === b));
  applySensorFilter();
});

// ================= Procesos =================
const PROC_LIMIT = 50;
let procSort = { key: store.get('ip_psort') || 'cpu', desc: store.get('ip_pdesc') !== '0' };

function renderProcesses(d) {
  const { key, desc } = procSort;
  const list = [...(d.system?.processes || [])].sort((a, b) => {
    const r = key === 'name' ? a.name.localeCompare(b.name) : a[key] - b[key];
    return desc ? -r : r;
  }).slice(0, PROC_LIMIT);
  const head = (k, label, cls = '') =>
    `<button class="sort ${cls} ${key === k ? 'on' : ''}" data-sort="${k}">${label}${key === k ? (desc ? ' ▼' : ' ▲') : ''}</button>`;

  $('#processes').innerHTML = `<div class="card">
    <div class="proc head">${head('name', 'Proceso')}${head('cpu', 'CPU', 'r')}${head('memoryMb', 'RAM', 'r')}<span></span></div>
    ${list.map(p => `<div class="proc">
      <span class="n"><span>${esc(p.name)}</span><small>PID ${p.pid}</small></span>
      <span class="r cpu"><span>${num(p.cpu, 1)} %</span><i style="--w:${Math.min(100, p.cpu * 6)}%"></i></span>
      <span class="r">${fmtBytes(p.memoryMb * 1024 * 1024)}</span>
      <button class="kill" data-kill="${p.pid}" data-name="${esc(p.name)}" aria-label="Cerrar ${esc(p.name)}"><svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" aria-hidden="true"><path d="M6 6l12 12M18 6 6 18"/></svg></button>
    </div>`).join('')}
  </div>`;
}

$('#processes').addEventListener('click', e => {
  const s = e.target.closest('[data-sort]');
  if (s) {
    const k = s.dataset.sort;
    // Repetir la columna invierte el orden; una nueva arranca descendente (salvo nombre)
    procSort = procSort.key === k ? { key: k, desc: !procSort.desc } : { key: k, desc: k !== 'name' };
    store.set('ip_psort', procSort.key);
    store.set('ip_pdesc', procSort.desc ? '1' : '0');
    if (last) renderProcesses(last);
    return;
  }
  const b = e.target.closest('[data-kill]');
  if (!b) return;
  if (confirm(`¿Cerrar ${b.dataset.name} (PID ${b.dataset.kill})?`))
    act('/process/kill', { value: +b.dataset.kill }, 'Proceso cerrado');
});

// ================= Control =================
let controlBuilt = false;
let curves = [];
let curveFan = 'cpu';

function slider(id, label, min, max, step = 1) {
  return `<div class="slider"><span>${label}</span><input type="range" id="${id}" min="${min}" max="${max}" step="${step}"><span class="v" id="${id}-v"></span></div>`;
}

function setSlider(id, v) {
  const el = document.getElementById(id);
  if (!el || v == null || Date.now() - (touched[id] || 0) < 3000) return;
  el.value = v;
  document.getElementById(id + '-v').textContent = v;
}

function bindSlider(id, onChange, suffix = '') {
  const el = document.getElementById(id);
  const out = document.getElementById(id + '-v');
  el.addEventListener('input', () => { touched[id] = Date.now(); out.textContent = el.value + suffix; });
  if (onChange) el.addEventListener('change', () => { touched[id] = Date.now(); onChange(+el.value); });
}

function buildControl(d) {
  const c = d.controls;
  const html = [];

  // Slider con un ícono (o botón) adelante en lugar de una etiqueta de texto
  const iconSlider = (id, lead) => `<div class="slider icon-row">${lead}<input type="range" id="${id}" min="0" max="100" aria-label="${id === 'vol' ? 'Volumen' : 'Brillo'}"><span class="v" id="${id}-v"></span></div>`;
  html.push(`<div class="card" data-cat="sound"><h3>${icon('volume')}<span>Sonido y pantalla</span></h3>
    ${iconSlider('vol', `<button class="btn icon" id="mute" aria-label="Silenciar">${icon('volume', 20)}</button>`)}
    ${c.system.brightness != null ? iconSlider('bright', `<span class="lead-ico">${icon('sun', 20)}</span>`) : ''}
    ${c.system.micMuted != null ? `<div class="switch-row"><span>Micrófono</span><button class="switch" id="mic" role="switch" aria-checked="true" aria-label="Micrófono activo"></button></div>` : ''}
    ${c.system.outputs.length > 1 ? '<label class="field-row"><span>Salida</span><select id="output"></select></label>' : ''}
  </div>`);

  html.push(`<div class="card" data-cat="sound"><h3>Multimedia</h3>
    <div class="media">
      <button class="btn" data-media="prev" aria-label="Anterior"><b>⏮</b><span>Anterior</span></button>
      <button class="btn" data-media="playpause" aria-label="Play o pausa"><b>⏯</b><span>Play/Pausa</span></button>
      <button class="btn" data-media="next" aria-label="Siguiente"><b>⏭</b><span>Siguiente</span></button>
    </div></div>`);

  html.push(`<div class="card" data-cat="power"><h3>Energía de Windows</h3>
    <div class="seg" id="pmode">
      <button data-v="efficiency">Eficiencia</button><button data-v="balanced">Equilibrado</button><button data-v="performance">Rendimiento</button>
    </div>
    <p class="note">Plan de energía</p>
    <select id="plan"></select>
  </div>`);

  if (d.asus?.available) html.push(`<div class="card" data-cat="fans"><h3>ASUS · Modo y ventiladores</h3>
    <div class="seg" id="amode"><button data-v="2">Silent</button><button data-v="0">Balanced</button><button data-v="1">Turbo</button></div>
    <p class="note">Curva del ventilador: arrastrá los puntos (temperatura → velocidad). Se aplica al modo actual; cambiar de modo vuelve a la curva de fábrica.</p>
    <div class="seg" id="cfan"></div>
    <svg class="curve" id="curve" viewBox="0 0 320 200"></svg>
    <button class="btn primary block" id="curve-apply">Aplicar curva</button>
    <div class="pair-btns">
      <button class="btn" id="curve-reload">Recargar</button>
      <button class="btn" id="curve-reset">De fábrica</button>
    </div>
  </div>`);

  if (c.tdp.backend !== 'none') html.push(`<div class="card" data-cat="power"><h3>TDP <small>${c.tdp.backend === 'asus' ? 'ACPI ASUS' : 'ryzenadj'}</small></h3>
    <div class="seg" id="tdp-presets">${[8, 12, 15, 20, 25, 30].filter(w => w >= c.tdp.min && w <= c.tdp.max).map(w => `<button data-w="${w}">${w}</button>`).join('')}</div>
    ${slider('spl', 'SPL', c.tdp.min, c.tdp.max)}
    ${slider('sppt', 'sPPT', c.tdp.min, c.tdp.max)}
    ${slider('fppt', 'fPPT', c.tdp.min, c.tdp.max)}
    <p class="note t-warn" id="tdp-unknown">Los valores actuales no se pueden leer: lo que ves es una propuesta.</p>
    <button class="btn primary block" id="tdp-apply">Aplicar TDP</button>
    <p class="note">SPL sostenido · sPPT boost largo · fPPT boost corto (en W). Armoury Crate puede pisarlos al cambiar de modo.</p>
  </div>`);

  if (c.lhmFans.length) html.push(`<div class="card" data-cat="fans"><h3>Ventiladores de la placa</h3>
    ${c.lhmFans.map((f, i) => `${slider('lf' + i, esc(f.name), f.min, f.max)}
      <div class="btns"><button class="btn sm" data-lfauto="${i}">Automático</button></div>`).join('')}
  </div>`);

  html.push(`<div class="card" data-cat="display"><h3>Pantallas</h3>
    <div id="displays"></div>
    <div class="seg" id="topo">
      <button data-v="internal" title="Solo la pantalla interna">Interna</button><button data-v="clone">Duplicar</button><button data-v="extend">Extender</button><button data-v="external" title="Solo la pantalla externa">Externa</button>
    </div>
    <p class="note">Presets: guardan orden, posición, resolución, Hz, pantalla principal y cuáles están activas.</p>
    <div id="dpresets"></div>
    <form class="inline" id="dp-form">
      <input id="dp-name" placeholder="Nombre del preset" maxlength="40" required>
      <button class="btn" type="submit">Guardar actual</button>
    </form>
  </div>`);

  // Límite de carga y Equipo: dos tarjetas compactas apiladas en un solo lugar de la grilla
  html.push(`<div class="stack" data-cat="power">
    ${d.asus?.available ? `<div class="card compact"><h3>Límite de carga <small title="Cargar hasta 60–80 % alarga la vida útil de la batería si el equipo pasa mucho tiempo enchufado.">se reaplica al iniciar</small></h3>
      <div class="seg" id="blimit"><button data-v="60">60 %</button><button data-v="80">80 %</button><button data-v="100">100 %</button></div>
    </div>` : ''}
    <div class="card compact"><h3>Equipo</h3>
      <div class="power-btns">
        <button class="btn sm" data-power="lock">Bloquear</button>
        <button class="btn sm" data-power="sleep">Suspender</button>
        <button class="btn sm" data-power="hibernate">Hibernar</button>
        <button class="btn sm danger" data-power="restart">Reiniciar</button>
        <button class="btn sm danger" data-power="shutdown">Apagar</button>
      </div>
    </div>
  </div>`);

  html.push(`<div class="card" data-cat="app"><h3>App</h3>
    <p class="note">Tema</p>
    <div class="themes" id="themes">${THEMES.map(t => `<button data-theme="${t.id}" title="${t.name}"><i style="background:${t.swatch}"></i>${t.name}</button>`).join('')}<button data-theme="custom" title="Personalizado"><i></i>Personalizado</button></div>
    ${themeEditorHtml()}
    <label class="toggle"><input type="checkbox" id="autostart"> Iniciar InfoPanel con Windows</label>
    <div class="pair-btns">
      <button class="btn" id="logout">Desvincular este dispositivo</button>
      <button class="btn danger" id="revoke">Desvincular todos</button>
    </div></div>`);

  $('#control').innerHTML = `<div class="grid">${html.join('')}</div>`;
  buildControlCats();

  // --- Eventos ---
  bindSlider('vol', v => act('/audio', { volume: v }));
  $('#mute').onclick = () => act('/audio', { mute: !last.controls.system.muted });
  if ($('#mic')) $('#mic').onclick = () => act('/mic', { mute: !last.controls.system.micMuted });
  if ($('#output')) $('#output').onchange = e => act('/audio/output', { action: e.target.value }, 'Salida cambiada');
  if ($('#bright')) bindSlider('bright', v => act('/brightness', { value: v }));
  if ($('#blimit')) $('#blimit').onclick = e => {
    const b = e.target.closest('button');
    if (b) act('/asus/battery-limit', { value: +b.dataset.v }, `Carga limitada a ${b.dataset.v} %`);
  };
  $('#themes').onclick = e => { const b = e.target.closest('[data-theme]'); if (b) setTheme(b.dataset.theme); };
  bindThemeEditor();
  markTheme();

  document.querySelectorAll('[data-media]').forEach(b => b.onclick = () => act('/media', { action: b.dataset.media }));
  $('#pmode').onclick = e => { const b = e.target.closest('button'); if (b) act('/powermode', { action: b.dataset.v }, 'Modo cambiado'); };
  $('#plan').onchange = e => act('/powerplan', { action: e.target.value }, 'Plan cambiado');

  document.querySelectorAll('[data-power]').forEach(b => b.onclick = () => {
    const a = b.dataset.power;
    const ask = { sleep: '¿Suspender la PC?', hibernate: '¿Hibernar la PC?', restart: '¿Reiniciar la PC?', shutdown: '¿Apagar la PC?' }[a];
    if (!ask || confirm(ask)) act('/power', { action: a }, 'Hecho');
  });

  if ($('#amode')) {
    $('#amode').onclick = async e => {
      const b = e.target.closest('button');
      if (b && await act('/asus/mode', { value: +b.dataset.v }, 'Modo ' + b.textContent)) setTimeout(loadCurves, 500);
    };
    $('#cfan').onclick = e => { const b = e.target.closest('button'); if (b) { curveFan = b.dataset.v; drawCurve(); } };
    $('#curve-apply').onclick = () => {
      const c = curves.find(x => x.fan === curveFan);
      if (c) act('/asus/curve', c, 'Curva aplicada');
    };
    $('#curve-reload').onclick = loadCurves;
    $('#curve-reset').onclick = async () => {
      if (await act('/asus/mode', { value: last.asus.mode }, 'Curvas de fábrica restauradas')) setTimeout(loadCurves, 500);
    };
    initCurveDrag();
    loadCurves();
  }

  if ($('#spl')) {
    ['spl', 'sppt', 'fppt'].forEach(id => bindSlider(id, null, ''));
    $('#tdp-presets').onclick = e => {
      const b = e.target.closest('[data-w]');
      if (!b) return;
      const w = +b.dataset.w;
      $('#tdp-presets').querySelectorAll('button').forEach(x => x.classList.toggle('on', x === b));
      ['spl', 'sppt', 'fppt'].forEach((id, i) => { touched[id] = Date.now(); const v = Math.min(w + i * 2, c.tdp.max); $('#' + id).value = v; $('#' + id + '-v').textContent = v; });
    };
    $('#tdp-apply').onclick = () => act('/tdp', { spl: +$('#spl').value, sppt: +$('#sppt').value, fppt: +$('#fppt').value }, 'TDP aplicado');
  }

  c.lhmFans.forEach((f, i) => bindSlider('lf' + i, v => act('/fans/lhm', { id: f.id, percent: v })));
  document.querySelectorAll('[data-lfauto]').forEach(b => b.onclick = () =>
    act('/fans/lhm', { id: c.lhmFans[+b.dataset.lfauto].id, percent: null }, 'Ventilador en automático'));

  $('#displays').onchange = async e => {
    const box = e.target.closest('.dmode');
    if (!box) return;
    const modes = last.controls.displayModes[box.dataset.device] || [];
    const resSel = $('[data-k=res]', box), hzSel = $('[data-k=hz]', box);
    const [w, h] = resSel.value.split('x').map(Number);
    const mode = modes.find(m => m.width === w && m.height === h);
    if (e.target === resSel) {
      // Al cambiar la resolución se elige la mayor frecuencia disponible para ella
      hzSel.innerHTML = mode.hz.map(x => `<option value="${x}">${x} Hz</option>`).join('');
      if (!confirm(`¿Cambiar la resolución a ${w}×${h}?`)) { $('#displays').dataset.sig = ''; return updateControl(last); }
    }
    await act('/display/mode', { device: box.dataset.device, width: w, height: h, hz: +hzSel.value }, `${w}×${h} @ ${hzSel.value} Hz`);
  };

  $('#topo').onclick = e => {
    const b = e.target.closest('button');
    if (b) act('/display/topology', { action: b.dataset.v }, 'Pantallas: ' + b.textContent);
  };
  $('#dp-form').onsubmit = async e => {
    e.preventDefault();
    const name = $('#dp-name').value.trim();
    if (await act('/display/preset/save', { action: name }, `Preset "${name}" guardado`)) $('#dp-name').value = '';
  };
  $('#dpresets').onclick = e => {
    const apply = e.target.closest('[data-apply]');
    const del = e.target.closest('[data-del]');
    if (apply) act('/display/preset/apply', { action: apply.dataset.apply }, `Preset "${apply.dataset.apply}" aplicado`);
    if (del && confirm(`¿Borrar el preset "${del.dataset.del}"?`)) act('/display/preset/delete', { action: del.dataset.del }, 'Preset borrado');
  };
  $('#autostart').onchange = async e => {
    touched.autostart = Date.now();
    const on = e.target.checked;
    if (!await act('/autostart', { enabled: on }, on ? 'Se iniciará con Windows' : 'Ya no se inicia con Windows')) e.target.checked = !on;
  };

  $('#logout').onclick = () => { if (confirm('¿Desvincular este dispositivo de esta PC?')) unpairCurrent(); };
  $('#revoke').onclick = async () => { if (confirm('¿Desvincular todos los dispositivos? Habrá que volver a ingresar el PIN.')) { await act('/unpair-all', {}); unpairCurrent(); } };

  controlBuilt = true;
}

function updateControl(d) {
  const c = d.controls;
  if (!c) return;
  if (!controlBuilt) buildControl(d);

  setSlider('vol', c.system.volume);
  if ($('#mute').dataset.muted !== String(c.system.muted)) {
    $('#mute').dataset.muted = String(c.system.muted);
    $('#mute').innerHTML = icon(c.system.muted ? 'mute' : 'volume', 20);
    $('#mute').setAttribute('aria-label', c.system.muted ? 'Activar sonido' : 'Silenciar');
    $('#mute').classList.toggle('danger', !!c.system.muted);
  }
  setSlider('bright', c.system.brightness);

  document.querySelectorAll('#pmode button').forEach(b => b.classList.toggle('on', b.dataset.v === c.system.powerMode));
  const plan = $('#plan');
  const sig = c.system.powerPlans.map(p => p.guid + p.active).join();
  if (plan.dataset.sig !== sig) {
    plan.dataset.sig = sig;
    plan.innerHTML = c.system.powerPlans.map(p => `<option value="${p.guid}" ${p.active ? 'selected' : ''}>${esc(p.name)}</option>`).join('');
  }

  if (d.asus?.available)
    document.querySelectorAll('#amode button').forEach(b => b.classList.toggle('on', +b.dataset.v === d.asus.mode));

  if ($('#spl')) {
    // Si el equipo no permite leer los límites, se proponen valores intermedios en vez del mínimo
    const mid = w => Math.min(Math.max(w, c.tdp.min), c.tdp.max);
    setSlider('spl', c.tdp.spl ?? mid(15));
    setSlider('sppt', c.tdp.sppt ?? mid(17));
    setSlider('fppt', c.tdp.fppt ?? mid(20));
    $('#tdp-unknown').classList.toggle('hidden', c.tdp.spl != null);
  }

  c.lhmFans.forEach((f, i) => setSlider('lf' + i, f.value != null ? Math.round(f.value) : null));

  if ($('#mic')) {
    $('#mic').setAttribute('aria-checked', String(!c.system.micMuted));
    $('#mic').setAttribute('aria-label', c.system.micMuted ? 'Micrófono silenciado' : 'Micrófono activo');
  }
  const out = $('#output');
  const osig = JSON.stringify(c.system.outputs);
  if (out && out.dataset.sig !== osig) {
    out.dataset.sig = osig;
    out.innerHTML = c.system.outputs.map(o => `<option value="${esc(o.id)}" ${o.default ? 'selected' : ''}>${esc(o.name)}</option>`).join('');
  }
  document.querySelectorAll('#blimit button').forEach(b => b.classList.toggle('on', +b.dataset.v === c.batteryLimit));

  const dsig = JSON.stringify([c.displays, Object.keys(c.displayModes || {})]);
  if ($('#displays').dataset.sig !== dsig) {
    $('#displays').dataset.sig = dsig;
    $('#displays').innerHTML = c.displays.map(d => {
      const modes = c.displayModes?.[d.device] || [];
      const res = `${d.width}x${d.height}`;
      const cur = modes.find(m => `${m.width}x${m.height}` === res);
      return `<div class="disp">
        <div class="row"><span>${d.internal ? 'Pantalla interna' : esc(d.name)}${d.primary ? ' <span class="badge">Principal</span>' : ''}</span>
          <span class="v">${d.width}×${d.height} @ ${num(d.refreshHz)} Hz${d.rotation ? ` · ${d.rotation}°` : ''}</span></div>
        ${modes.length ? `<div class="dmode" data-device="${esc(d.device)}">
          <select data-k="res" aria-label="Resolución">${modes.map(m => `<option value="${m.width}x${m.height}" ${`${m.width}x${m.height}` === res ? 'selected' : ''}>${m.width}×${m.height}</option>`).join('')}</select>
          <select data-k="hz" aria-label="Frecuencia">${(cur?.hz || []).map(h => `<option value="${h}" ${h === Math.round(d.refreshHz) ? 'selected' : ''}>${h} Hz</option>`).join('')}</select>
        </div>` : ''}
      </div>`;
    }).join('');
  }

  const psig = JSON.stringify(c.displayPresets);
  if ($('#dpresets').dataset.sig !== psig) {
    $('#dpresets').dataset.sig = psig;
    $('#dpresets').innerHTML = c.displayPresets.map(p => `<div class="preset">
      <div><div>${esc(p.name)}</div><div class="sub">${p.displays.map(d => `${d.width}×${d.height}@${num(d.refreshHz)}`).join(' · ')}</div></div>
      <div class="btns"><button class="btn sm primary" data-apply="${esc(p.name)}">Aplicar</button><button class="btn sm danger" data-del="${esc(p.name)}" aria-label="Borrar">✕</button></div>
    </div>`).join('') || '<p class="note">Todavía no hay presets.</p>';
  }

  if (Date.now() - (touched.autostart || 0) > 3000) $('#autostart').checked = c.autoStart;
}

// ---------- Categorías y búsqueda de Control ----------
const CONTROL_CATS = [['sound', 'Sonido'], ['power', 'Energía'], ['fans', 'Ventiladores'], ['display', 'Pantallas'], ['app', 'App']];
let controlCat = store.get('ip_ccat') || 'all';

// Chips solo de las categorías que tiene esta PC (sin ASUS, por ejemplo, no hay "Ventiladores")
function buildControlCats() {
  const present = new Set([...document.querySelectorAll('#control [data-cat]')].map(x => x.dataset.cat));
  const cats = CONTROL_CATS.filter(([k]) => present.has(k));
  if (controlCat !== 'all' && !present.has(controlCat)) controlCat = 'all';
  $('#control-cats').innerHTML = [['all', 'Todos'], ...cats]
    .map(([k, l]) => `<button type="button" data-cat="${k}" class="${k === controlCat ? 'on' : ''}">${l}</button>`).join('');
  applyControlFilter();
}

// Una tarjeta se muestra si es de la categoría elegida y su texto (títulos, botones, etiquetas) contiene la búsqueda
function applyControlFilter() {
  const q = $('#control-filter').value.trim().toLowerCase();
  let any = false;
  document.querySelectorAll('#control .grid > [data-cat]').forEach(el => {
    const catOk = controlCat === 'all' || el.dataset.cat === controlCat;
    // La pila (Límite de carga + Equipo) se filtra por tarjeta
    const parts = el.classList.contains('stack') ? [...el.children] : [el];
    let shown = false;
    parts.forEach(p => {
      const ok = catOk && (!q || p.textContent.toLowerCase().includes(q));
      if (p !== el) p.classList.toggle('hidden', !ok);
      shown ||= ok;
    });
    el.classList.toggle('hidden', !shown);
    any ||= shown;
  });
  $('#control-empty').classList.toggle('hidden', any);
}

$('#control-filter').addEventListener('input', applyControlFilter);
$('#control-cats').addEventListener('click', e => {
  const b = e.target.closest('[data-cat]');
  if (!b) return;
  controlCat = b.dataset.cat;
  store.set('ip_ccat', controlCat);
  $('#control-cats').querySelectorAll('button').forEach(x => x.classList.toggle('on', x === b));
  applyControlFilter();
});

// ---------- Editor de curva ----------
const CX = t => 30 + (t - 20) / 90 * 280;     // 20–110 °C
const CY = s => 180 - s / 100 * 170;          // 0–100 %

async function loadCurves() {
  try {
    curves = await api('/asus/curves');
    if (!curves.some(c => c.fan === curveFan) && curves.length) curveFan = curves[0].fan;
    $('#cfan').innerHTML = curves.map(c => `<button data-v="${c.fan}">${c.fan.toUpperCase()}</button>`).join('');
    drawCurve();
  } catch (e) { toast(e.message); }
}

function drawCurve() {
  const svg = $('#curve');
  if (!svg) return;
  document.querySelectorAll('#cfan button').forEach(b => b.classList.toggle('on', b.dataset.v === curveFan));
  const c = curves.find(x => x.fan === curveFan);
  if (!c) { svg.innerHTML = `<text x="160" y="100" text-anchor="middle">Sin curvas disponibles</text>`; return; }

  let grid = '';
  for (let s = 0; s <= 100; s += 25) grid += `<line x1="30" x2="310" y1="${CY(s)}" y2="${CY(s)}" stroke="var(--line)"/><text x="4" y="${CY(s) + 3}">${s}%</text>`;
  for (let t = 30; t <= 110; t += 20) grid += `<text x="${CX(t)}" y="196" text-anchor="middle">${t}°</text>`;
  const pts = c.temps.map((t, i) => `${CX(t)},${CY(c.speeds[i])}`).join(' ');
  // Solo el punto activo (el que se toca o arrastra) muestra su valor: con puntos juntos las etiquetas se encimaban
  const a = curveActive != null && curveActive < c.temps.length ? curveActive : null;
  const label = a == null ? '' : (() => {
    const x = Math.min(250, Math.max(32, CX(c.temps[a]) - 34)), y = Math.max(4, CY(c.speeds[a]) - 34);
    return `<g class="curve-tip"><rect x="${x}" y="${y}" width="68" height="22" rx="6"/><text x="${x + 34}" y="${y + 15}" text-anchor="middle">${c.temps[a]} °C · ${c.speeds[a]} %</text></g>`;
  })();
  svg.innerHTML = `${grid}<polygon points="${CX(c.temps[0])},180 ${pts} ${CX(c.temps[c.temps.length - 1])},180" fill="var(--accent)" fill-opacity=".08"/>
    <polyline points="${pts}" fill="none" stroke="var(--accent)" stroke-width="2.5" stroke-linejoin="round"/>
    ${c.temps.map((t, i) => `<circle data-i="${i}" cx="${CX(t)}" cy="${CY(c.speeds[i])}" r="${i === a ? 10 : 7}" fill="${i === a ? 'var(--accent)' : 'var(--card)'}" stroke="var(--accent)" stroke-width="2.5"/>`).join('')}
    ${label}`;
}
let curveActive = null;

function initCurveDrag() {
  const svg = $('#curve');
  let idx = null;
  const clamp = (v, a, b) => Math.round(Math.min(b, Math.max(a, v)));
  const toPoint = e => {
    const r = svg.getBoundingClientRect();
    const x = (e.clientX - r.left) / r.width * 320;
    const y = (e.clientY - r.top) / r.height * 200;
    return { t: clamp(20 + (x - 30) / 280 * 90, 20, 110), s: clamp((180 - y) / 170 * 100, 0, 100) };
  };
  svg.addEventListener('pointerdown', e => {
    const p = e.target.closest('circle');
    if (!p) return;
    idx = +p.dataset.i;
    curveActive = idx;
    svg.setPointerCapture(e.pointerId);
    drawCurve();
  });
  svg.addEventListener('pointermove', e => {
    if (idx == null) return;
    const c = curves.find(x => x.fan === curveFan);
    const { t, s } = toPoint(e);
    c.temps[idx] = t;
    c.speeds[idx] = s;
    // Mantiene la curva creciente empujando los puntos vecinos
    for (let j = idx + 1; j < 8; j++) { c.temps[j] = Math.max(c.temps[j], t); c.speeds[j] = Math.max(c.speeds[j], s); }
    for (let j = idx - 1; j >= 0; j--) { c.temps[j] = Math.min(c.temps[j], t); c.speeds[j] = Math.min(c.speeds[j], s); }
    drawCurve();
  });
  const end = () => { idx = null; };
  svg.addEventListener('pointerup', end);
  svg.addEventListener('pointercancel', end);
}

// ================= Ciclo =================
function onData(d) {
  last = d;
  tick(d);
  renderDashboard(d);
  renderSensors(d);
  renderProcesses(d);
  updateControl(d);
}

// Se espera a que carguen todos los scripts (dashboard.js define loadDashboards/renderDashboard)
window.addEventListener('DOMContentLoaded', async () => {
  await migrateLegacyToken();
  useCurrentPc();
  token ? start() : showPair();
});
