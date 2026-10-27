'use strict';

// ================= Dashboards personalizables =================
// Un dashboard es { id, name, cols, cell, rowH, widgets: [{ id, type, w, h, cfg }] } y se guarda en la PC.
// cols es un número fijo de columnas, o 'auto': la celda mide ~cell px y entran tantas columnas como
// quepan en la pantalla (un 1×1 es siempre un cuadradito, en el celu y en la PC).
// rowH es un alto fijo en px, o 'auto': cada fila mide lo mismo que una columna (celdas cuadradas).
// Los widgets se ubican en orden en la grilla; w/h son celdas que ocupan (w se recorta a las columnas disponibles).
// Tipos: metric (tarjetas de métricas), sensor (cualquier sensor), apps (accesos a aplicaciones), control.

let dashboards = [];
let dashId = null;         // dashboard elegido; se recuerda por PC (cada PC tiene sus propios dashboards)
let editing = false;
let draft = null;          // copia del dashboard mientras se edita
let dashSig = '';          // firma del layout dibujado (para reconstruir solo si cambia)
const busyUntil = {};      // id de widget -> momento hasta el que no se redibuja (el usuario lo está tocando)
const sensorHist = new Map();
const iconUrls = new Map();
let appsCache = null;

const MAX_ROWS = 8;
const uid = () => Math.random().toString(36).slice(2, 10);
const clone = o => JSON.parse(JSON.stringify(o));
const dash = () => editing ? draft : dashboards.find(x => x.id === dashId) || dashboards[0];

// ---------- Catálogo ----------
const CONTROLS = {
  volume: 'Volumen',
  brightness: 'Brillo',
  media: 'Multimedia',
  mic: 'Micrófono',
  powerMode: 'Modo de energía de Windows',
  asusMode: 'Modo ASUS',
  tdp: 'Presets de TDP',
  batteryLimit: 'Límite de carga',
  displayHz: 'Frecuencia de pantalla',
  power: 'Bloquear / suspender',
};

const SENSOR_STYLES = { number: 'Número', gauge: 'Medidor', chart: 'Gráfico' };

function defaultDashboard() {
  // Columnas automáticas: en el celu entran 4 (cada tarjeta ocupa el ancho completo); en la PC, más
  const m = (metric, w, h) => ({ id: uid(), type: 'metric', w, h, cfg: { metric } });
  return {
    id: uid(), name: 'Principal', cols: 'auto', cell: CELL_DEFAULT, rowH: 'auto',
    widgets: [
      m('cpu', 4, 4), m('gpu', 4, 3), m('mem', 4, 2), m('battery', 4, 3), m('fans', 4, 2),
      m('power', 4, 2), m('net', 4, 2), m('disks', 4, 3), m('system', 4, 4),
    ],
  };
}

// ---------- Carga y guardado ----------
// Al cambiar de PC: todo lo de esta sección pertenecía a la PC anterior
function resetDashboardState() {
  if (editing) exitEdit();
  dashboards = [];
  dashId = null;
  dashSig = '';
  appsCache = null;
  sensorHist.clear();
  iconUrls.forEach(p => p.then(u => u && URL.revokeObjectURL(u)));
  iconUrls.clear();
  $('#dash').innerHTML = '';
}

async function loadDashboards() {
  dashId = store.get('ip_dash:' + pcId);
  dashboards = await api('/dashboards');
  if (!dashboards.length) {
    const d = defaultDashboard();
    await api('/dashboards/save', d);
    dashboards = [d];
  }
  if (!dashboards.some(x => x.id === dashId)) dashId = dashboards[0].id;
  fillSelect();
  // Widgets de apps sin nombres guardados: se completan con la lista de la PC en segundo plano
  if (dashboards.some(d => d.widgets.some(w => w.type === 'apps' && !w.cfg.names))) loadApps().catch(() => { });
}

async function saveDashboard(d) {
  await api('/dashboards/save', d);
  const i = dashboards.findIndex(x => x.id === d.id);
  i >= 0 ? dashboards[i] = clone(d) : dashboards.push(clone(d));
}

function fillSelect() {
  $('#dash-select').innerHTML = dashboards.map(d => `<option value="${esc(d.id)}" ${d.id === dashId ? 'selected' : ''}>${esc(d.name)}</option>`).join('')
    + '<option value="__new">+ Nuevo dashboard…</option>';
}

function selectDashboard(id) {
  dashId = id;
  store.set('ip_dash:' + pcId, id);
  fillSelect();
  dashSig = '';
  if (last) renderDashboard(last);
}

// ---------- Dibujo ----------
function renderDashboard(d) {
  const db = dash();
  if (!db || drag) return; // mientras se arrastra no se redibuja, o se perdería el elemento arrastrado
  const host = $('#dash');
  const cols = colsOf(db);
  const sig = JSON.stringify([db.id, cols, db.rowH, db.widgets, editing]);
  if (sig !== dashSig) {
    dashSig = sig;
    host._cols = cols;
    host.style.setProperty('--cols', cols);
    applyRowHeight(db);
    host.classList.toggle('editing', editing);
    host.innerHTML = db.widgets.map(w => `
      <div class="widget card w-${w.type}" data-id="${w.id}" style="grid-column:span ${Math.min(w.w, cols)};grid-row:span ${w.h}">
        <div class="wbody"></div>
        ${editing ? `<div class="wedit">
          <button class="w-move" data-move="${w.id}" aria-label="Mover ${esc(widgetTitle(w))}">
            <span class="w-grip">⠿</span><span class="w-name">${esc(widgetTitle(w))}</span><span class="w-size">${w.w}×${w.h}</span>
          </button>
          <button class="wbtn w-cfg" data-cfg="${w.id}" aria-label="Configurar">⚙</button>
          <button class="wbtn w-del" data-del="${w.id}" aria-label="Quitar">✕</button>
          <button class="w-resize" data-resize="${w.id}" aria-label="Cambiar tamaño"></button>
        </div>` : ''}
      </div>`).join('') || `<div class="card empty">Este dashboard está vacío. Tocá <b>Editar</b> y después <b>+ Widget</b>.</div>`;
  }

  trackSensors(db, d);
  const now = Date.now();
  for (const w of db.widgets) {
    const el = host.querySelector(`.widget[data-id="${w.id}"]`);
    if (!el || (busyUntil[w.id] || 0) > now) continue;
    const html = renderWidget(w, d) ?? `<h3>${esc(widgetTitle(w))}</h3><p class="note">Sin datos en este equipo.</p>`;
    if (el._html === html) continue; // solo se toca el DOM si cambió algo
    el._html = html;
    el.firstElementChild.innerHTML = html;
    hydrateIcons(el);
  }
}

// Alto de fila en px. En 'auto' sigue al ancho de columna: mínimo 84 (lo que necesita un 1×1 para
// título, valor y barra) y máximo 160 (para que en pantallas grandes las celdas no sean enormes).
const ROW_MIN = 84, ROW_MAX = 160;
const GAP = 12;
const CELL_DEFAULT = 76; // con 76 px entran 4 columnas en un celu de 375 px
const CELL_SIZES = { 64: 'Chica', 76: 'Mediana', 96: 'Grande', 120: 'Muy grande' };

// Columnas reales: las fijas del dashboard, o en 'auto' las celdas de ~cell px que entran en el ancho
function colsOf(db) {
  if (db.cols !== 'auto') return +db.cols || 4;
  const width = $('#dash').clientWidth;
  if (!width) return 4; // pestaña oculta: se recalcula al mostrarse
  return Math.max(2, Math.floor((width + GAP) / ((db.cell || CELL_DEFAULT) + GAP)));
}

function rowPx(db) {
  if (db.rowH !== 'auto') return +db.rowH || 90;
  const cols = colsOf(db);
  const cellW = ($('#dash').clientWidth - GAP * (cols - 1)) / cols;
  return Math.round(Math.min(ROW_MAX, Math.max(ROW_MIN, cellW)));
}
function applyRowHeight(db) {
  $('#dash').style.setProperty('--rowh', rowPx(db) + 'px');
}
// Al cambiar el ancho de la ventana: si cambia la cantidad de columnas se redibuja; si no, solo el alto
new ResizeObserver(() => {
  const db = dash();
  if (!db || drag) return;
  if (colsOf(db) !== $('#dash')._cols) { dashSig = ''; if (last) renderDashboard(last); }
  else applyRowHeight(db);
}).observe($('#dash'));

function widgetTitle(w) {
  if (w.cfg?.title) return w.cfg.title;
  switch (w.type) {
    case 'metric': return METRICS[w.cfg.metric]?.name || 'Métrica';
    case 'sensor': return findSensor(last, w.cfg.sensorId)?.s.name || 'Sensor';
    case 'apps': return 'Aplicaciones';
    case 'control': return CONTROLS[w.cfg.control] || 'Control';
  }
  return 'Widget';
}

function renderWidget(w, d) {
  switch (w.type) {
    case 'metric': return METRICS[w.cfg.metric]?.render(d, summary, w.cfg) ?? null;
    case 'sensor': return renderSensorWidget(w, d);
    case 'apps': return renderAppsWidget(w);
    case 'control': return renderControlWidget(w, d);
  }
  return null;
}

// ---------- Widget: sensor ----------
function findSensor(d, id) {
  for (const h of d?.hardware || []) {
    const s = h.sensors.find(x => x.id === id);
    if (s) return { h, s };
  }
  return null;
}

function trackSensors(db, d) {
  for (const w of db.widgets) {
    if (w.type !== 'sensor') continue;
    const f = findSensor(d, w.cfg.sensorId);
    if (!f) continue;
    if (!sensorHist.has(w.cfg.sensorId)) sensorHist.set(w.cfg.sensorId, []);
    const arr = sensorHist.get(w.cfg.sensorId);
    if (arr._t !== d.time) { arr._t = d.time; push(arr, f.s.value); }
  }
}

function sensorMax(s, arr) {
  if (['Temperature'].includes(s.type)) return 100;
  if (['Load', 'Level', 'Control'].includes(s.type)) return 100;
  const peak = Math.max(...arr.filter(v => v != null), s.max || 0, s.value || 0);
  return peak > 0 ? peak * 1.1 : 1;
}

function renderSensorWidget(w, d) {
  const f = findSensor(d, w.cfg.sensorId);
  if (!f) return null;
  const { h, s } = f;
  const arr = sensorHist.get(s.id) || [];
  const title = w.cfg.title || s.name;
  const value = fmtSensor(s.type, s.value);
  const cls = s.type === 'Temperature' ? tempClass(s.value) : '';
  const max = sensorMax(s, arr);
  const style = w.cfg.style || 'number';

  if (style === 'gauge') {
    const pct = Math.max(0, Math.min(1, (s.value || 0) / max));
    // Arco de 240°: de 150° a 390°
    const a0 = 150 * Math.PI / 180, a = a0 + pct * 240 * Math.PI / 180;
    const pt = ang => `${50 + 40 * Math.cos(ang)} ${50 + 40 * Math.sin(ang)}`;
    const large = pct * 240 > 180 ? 1 : 0;
    return `<h3>${esc(title)} <small>${esc(h.name)}</small></h3>
      <svg class="gauge" viewBox="0 0 100 86">
        <path d="M ${pt(a0)} A 40 40 0 1 1 ${pt(a0 + 240 * Math.PI / 180)}" stroke="var(--card-2)" stroke-width="9" fill="none" stroke-linecap="round"/>
        ${pct > 0 ? `<path d="M ${pt(a0)} A 40 40 0 ${large} 1 ${pt(a)}" stroke="var(--accent)" stroke-width="9" fill="none" stroke-linecap="round"/>` : ''}
        <text x="50" y="56" text-anchor="middle" class="gv ${cls}">${esc(value)}</text>
      </svg>`;
  }

  if (style === 'chart') {
    const vals = arr.filter(v => v != null);
    return `<h3>${esc(title)} <small>${esc(h.name)}</small></h3>
      <div class="big ${cls}">${esc(value)}</div>
      <div class="chart-wrap">${spark(arr, max)}</div>
      <div class="chart-mm"><span>mín ${fmtSensor(s.type, vals.length ? Math.min(...vals) : null)}</span><span>máx ${fmtSensor(s.type, vals.length ? Math.max(...vals) : null)}</span></div>`;
  }

  return `<h3>${esc(title)} <small>${esc(h.name)}</small></h3>
    <div class="big ${cls}">${esc(value)}</div>
    <div class="extra">${spark(arr, max)}</div>`;
}

// ---------- Widget: aplicaciones ----------
function renderAppsWidget(w) {
  const apps = (w.cfg.apps || []);
  // Los nombres se guardan con el widget; la lista de la PC solo se usa si falta alguno
  const names = { ...Object.fromEntries((appsCache || []).map(a => [a.id, a.name])), ...(w.cfg.names || {}) };
  return `${w.cfg.title ? `<h3>${esc(w.cfg.title)}</h3>` : ''}
    <div class="apps ${w.cfg.labels === false ? 'nolabels' : ''}">
      ${apps.map(id => `<button class="app" data-app="${esc(id)}" title="${esc(names[id] || id)}">
        <img data-icon="${esc(id)}" alt=""><span>${esc(names[id] || id.split(/[\\!]/).pop())}</span></button>`).join('')
      || '<p class="note">Sin aplicaciones. Editá el widget para elegirlas.</p>'}
    </div>`;
}

async function loadApps(refresh = false) {
  if (!appsCache || refresh) appsCache = await api('/apps' + (refresh ? '?refresh=true' : ''));
  return appsCache;
}

// Los íconos requieren el token, así que se descargan con fetch y se usan como blob
function iconUrl(id) {
  if (!iconUrls.has(id)) {
    iconUrls.set(id, fetch(base + '/api/apps/icon?id=' + encodeURIComponent(id), { headers: { Authorization: 'Bearer ' + token } })
      .then(r => r.ok ? r.blob() : null)
      .then(b => b ? URL.createObjectURL(b) : null)
      .catch(() => null));
  }
  return iconUrls.get(id);
}

function hydrateIcons(root) {
  root.querySelectorAll('img[data-icon]:not([src])').forEach(async img => {
    const url = await iconUrl(img.dataset.icon);
    if (url) img.src = url; else img.classList.add('noicon');
  });
}

// ---------- Widget: controles rápidos ----------
function renderControlWidget(w, d) {
  const c = d.controls;
  if (!c) return null;
  const t = `<h3>${esc(widgetTitle(w))}</h3>`;
  const seg = (items, cur, act) => `<div class="seg">${items.map(([v, l]) =>
    `<button class="${String(v) === String(cur) ? 'on' : ''}" data-act="${act}" data-v="${v}">${l}</button>`).join('')}</div>`;

  switch (w.cfg.control) {
    case 'volume':
      return `${t}<div class="wslider icon">
        <button class="btn sm" data-act="mute" aria-label="${c.system.muted ? 'Activar sonido' : 'Silenciar'}">${c.system.muted ? '🔇' : '🔊'}</button>
        <input type="range" min="0" max="100" value="${c.system.volume ?? 0}" data-act="volume"><span>${c.system.volume ?? '—'}</span></div>`;
    case 'brightness':
      if (c.system.brightness == null) return null;
      return `${t}<div class="wslider icon"><b aria-hidden="true">☀</b><input type="range" min="0" max="100" value="${c.system.brightness}" data-act="brightness"><span>${c.system.brightness}</span></div>`;
    case 'media':
      return `${t}<div class="media">
        <button class="btn" data-act="media" data-v="prev" aria-label="Anterior"><b>⏮</b></button>
        <button class="btn" data-act="media" data-v="playpause" aria-label="Play o pausa"><b>⏯</b></button>
        <button class="btn" data-act="media" data-v="next" aria-label="Siguiente"><b>⏭</b></button></div>`;
    case 'mic':
      if (c.system.micMuted == null) return null;
      return `${t}<button class="btn big-btn ${c.system.micMuted ? 'danger' : ''}" data-act="mic">${c.system.micMuted ? '🎙 Silenciado' : '🎙 Activo'}</button>`;
    case 'powerMode':
      return t + seg([['efficiency', 'Eficiencia'], ['balanced', 'Equilibrado'], ['performance', 'Rendim.']], c.system.powerMode, 'powerMode');
    case 'asusMode':
      if (!d.asus?.available) return null;
      return t + seg([[2, 'Silent'], [0, 'Balanced'], [1, 'Turbo']], d.asus.mode, 'asusMode');
    case 'batteryLimit':
      if (!d.asus?.available) return null;
      return t + seg([[60, '60 %'], [80, '80 %'], [100, '100 %']], c.batteryLimit, 'batteryLimit');
    case 'tdp': {
      if (c.tdp.backend === 'none') return null;
      const presets = (w.cfg.presets || [8, 15, 25, 30]).filter(x => x >= c.tdp.min && x <= c.tdp.max);
      return t + `<div class="btns">${presets.map(x => `<button class="btn sm ${c.tdp.spl === x ? 'primary' : ''}" data-act="tdp" data-v="${x}">${x} W</button>`).join('')}</div>`;
    }
    case 'displayHz': {
      const disp = c.displays.find(x => x.primary) || c.displays[0];
      const mode = disp && (c.displayModes?.[disp.device] || []).find(m => m.width === disp.width && m.height === disp.height);
      if (!mode) return null;
      return t + seg(mode.hz.map(h => [h, h + ' Hz']), Math.round(disp.refreshHz), 'displayHz');
    }
    case 'power':
      return `${t}<div class="btns"><button class="btn sm" data-act="power" data-v="lock">Bloquear</button><button class="btn sm" data-act="power" data-v="sleep">Suspender</button></div>`;
  }
  return null;
}

async function runAction(el) {
  const a = el.dataset.act, v = el.dataset.v, c = last.controls;
  switch (a) {
    case 'mute': return act('/audio', { mute: !c.system.muted });
    case 'media': return act('/media', { action: v });
    case 'mic': return act('/mic', { mute: !c.system.micMuted });
    case 'powerMode': return act('/powermode', { action: v }, 'Modo cambiado');
    case 'asusMode': return act('/asus/mode', { value: +v }, 'Modo ASUS cambiado');
    case 'batteryLimit': return act('/asus/battery-limit', { value: +v }, `Carga limitada a ${v} %`);
    case 'tdp': {
      const w = +v, max = c.tdp.max;
      return act('/tdp', { spl: w, sppt: Math.min(w + 2, max), fppt: Math.min(w + 5, max) }, `TDP ${w} W`);
    }
    case 'displayHz': {
      const disp = c.displays.find(x => x.primary) || c.displays[0];
      return act('/display/mode', { device: disp.device, width: disp.width, height: disp.height, hz: +v }, `${v} Hz`);
    }
    case 'power':
      if (v === 'sleep' && !confirm('¿Suspender la PC?')) return;
      return act('/power', { action: v }, 'Hecho');
  }
}

// ---------- Interacción en modo normal ----------
const host = $('#dash');

host.addEventListener('pointerdown', e => {
  const w = e.target.closest('.widget');
  if (w && !editing) busyUntil[w.dataset.id] = Infinity;
});
const release = e => {
  const w = e.target.closest?.('.widget');
  if (w) busyUntil[w.dataset.id] = Date.now() + 1500;
};
host.addEventListener('pointerup', release);
host.addEventListener('pointercancel', release);

host.addEventListener('click', e => {
  if (editing) return onEditClick(e);
  const app = e.target.closest('[data-app]');
  if (app) return act('/apps/launch', { action: app.dataset.app }, 'Abriendo ' + (app.title || 'app'));
  const b = e.target.closest('button[data-act]');
  if (b) runAction(b);
});

host.addEventListener('input', e => {
  const r = e.target.closest('input[type=range][data-act]');
  if (r) r.nextElementSibling.textContent = r.value;
});

host.addEventListener('change', e => {
  const r = e.target.closest('input[type=range][data-act]');
  if (!r) return;
  if (r.dataset.act === 'volume') act('/audio', { volume: +r.value });
  if (r.dataset.act === 'brightness') act('/brightness', { value: +r.value });
});

// ---------- Modo edición ----------
$('#dash-edit').onclick = () => {
  editing = true;
  draft = clone(dashboards.find(x => x.id === dashId) || dashboards[0]);
  // Mientras se edita, la barra de herramientas reemplaza al selector de dashboard
  $('#dash-tools').classList.remove('hidden');
  $('.dash-bar').classList.add('hidden');
  dashSig = '';
  renderDashboard(last);
};

function exitEdit() {
  editing = false;
  draft = null;
  $('#dash-tools').classList.add('hidden');
  $('.dash-bar').classList.remove('hidden');
  dashSig = '';
  if (last) renderDashboard(last);
}

$('#dash-cancel').onclick = exitEdit;
$('#dash-save').onclick = async () => {
  try {
    await saveDashboard(draft);
    toast('Dashboard guardado');
    fillSelect();
    exitEdit();
  } catch (e) { toast(e.message); }
};

function onEditClick(e) {
  const cfg = e.target.closest('[data-cfg]');
  const del = e.target.closest('[data-del]');
  if (cfg) openWidgetConfig(draft.widgets.find(w => w.id === cfg.dataset.cfg));
  if (del && confirm('¿Quitar este widget?')) {
    draft.widgets = draft.widgets.filter(w => w.id !== del.dataset.del);
    renderDashboard(last);
  }
}

// Mover (arrastrando ⠿) y redimensionar (arrastrando la esquina). La captura va en el contenedor
// porque los widgets se vuelven a dibujar mientras se arrastra.
let drag = null;

host.addEventListener('pointerdown', e => {
  if (!editing) return;
  const mv = e.target.closest('[data-move]');
  const rs = e.target.closest('[data-resize]');
  if (!mv && !rs) return;
  e.preventDefault();
  const w = draft.widgets.find(x => x.id === (mv || rs).dataset[mv ? 'move' : 'resize']);
  const el = host.querySelector(`.widget[data-id="${w.id}"]`);
  const gap = parseFloat(getComputedStyle(host).columnGap) || 12;
  drag = {
    mode: mv ? 'move' : 'resize', w, el, x: e.clientX, y: e.clientY, w0: w.w, h0: w.h,
    cols: colsOf(draft),
    cellW: (host.clientWidth - gap * (colsOf(draft) - 1)) / colsOf(draft) + gap,
    cellH: rowPx(draft) + gap,
  };
  el.classList.add('dragging');
  try { host.setPointerCapture(e.pointerId); } catch { /* puntero ya liberado */ }
});

host.addEventListener('pointermove', e => {
  if (!drag) return;
  if (drag.mode === 'resize') {
    const w = Math.max(1, Math.min(drag.cols, Math.min(drag.w0, drag.cols) + Math.round((e.clientX - drag.x) / drag.cellW)));
    const h = Math.max(1, Math.min(MAX_ROWS, drag.h0 + Math.round((e.clientY - drag.y) / drag.cellH)));
    if (w !== drag.w.w || h !== drag.w.h) {
      drag.w.w = w;
      drag.w.h = h;
      drag.el.style.gridColumn = `span ${w}`;
      drag.el.style.gridRow = `span ${h}`;
      drag.el.querySelector('.w-size').textContent = `${w}×${h}`;
    }
    return;
  }
  // Mover: se reubica en la posición del widget que está debajo del puntero
  drag.el.style.pointerEvents = 'none';
  const over = document.elementFromPoint(e.clientX, e.clientY)?.closest('.widget');
  drag.el.style.pointerEvents = '';
  if (!over || over === drag.el) return;
  const from = draft.widgets.indexOf(drag.w);
  const to = draft.widgets.findIndex(x => x.id === over.dataset.id);
  draft.widgets.splice(from, 1);
  draft.widgets.splice(to, 0, drag.w);
  // Se mueve el nodo en vez de redibujar todo, así no se pierde el arrastre
  to > from ? over.after(drag.el) : over.before(drag.el);
});

const endDrag = () => {
  if (!drag) return;
  drag.el.classList.remove('dragging');
  drag = null;
  dashSig = '';
  renderDashboard(last);
};
host.addEventListener('pointerup', endDrag);
host.addEventListener('pointercancel', endDrag);

// ---------- Ventana modal ----------
function openModal(title, html, onMount) {
  $('#modal-title').textContent = title;
  $('#modal-body').innerHTML = html;
  $('#modal').classList.remove('hidden');
  onMount?.($('#modal-body'));
}

// Botones segmentados de los formularios: actualizan su input oculto y avisan con "segchange"
$('#modal-body').addEventListener('click', e => {
  const b = e.target.closest('[data-seg] button');
  if (!b) return;
  const seg = b.parentElement;
  seg.querySelectorAll('button').forEach(x => x.classList.toggle('on', x === b));
  $(`input[name="${seg.dataset.seg}"]`, seg.parentElement).value = b.dataset.v;
  seg.dispatchEvent(new CustomEvent('segchange', { bubbles: true }));
});
const closeModal = () => $('#modal').classList.add('hidden');
$('#modal-close').onclick = closeModal;
$('#modal').addEventListener('click', e => { if (e.target.id === 'modal') closeModal(); });

// ---------- Agregar y configurar widgets ----------
$('#dash-add').onclick = () => openModal('Agregar widget', `
  <div class="pick">
    <button class="btn" data-type="metric"><b>📊 Métrica</b><span>CPU, GPU, RAM, batería, red, discos…</span></button>
    <button class="btn" data-type="sensor"><b>🌡 Sensor</b><span>Cualquier sensor como número, medidor o gráfico</span></button>
    <button class="btn" data-type="apps"><b>🚀 Aplicaciones</b><span>Accesos para abrir apps en la PC</span></button>
    <button class="btn" data-type="control"><b>🎛 Control</b><span>Volumen, multimedia, modos, TDP, Hz…</span></button>
  </div>`, body => {
  body.onclick = e => {
    const b = e.target.closest('[data-type]');
    if (!b) return;
    const type = b.dataset.type;
    const defaults = {
      metric: { w: Math.min(4, colsOf(draft)), h: 3, cfg: { metric: 'cpu' } },
      sensor: { w: Math.min(2, colsOf(draft)), h: 2, cfg: { style: 'number' } },
      apps: { w: Math.min(4, colsOf(draft)), h: 2, cfg: { apps: [], labels: true } },
      control: { w: Math.min(4, colsOf(draft)), h: 1, cfg: { control: 'volume' } },
    }[type];
    openWidgetConfig({ id: uid(), type, ...defaults }, true);
  };
});

function sizeFields(w) {
  const opts = (n, cur) => Array.from({ length: n }, (_, i) => `<option ${i + 1 === cur ? 'selected' : ''}>${i + 1}</option>`).join('');
  return `<label class="field">Título (opcional)<input name="title" maxlength="40" value="${esc(w.cfg.title || '')}"></label>
  <div class="form-row two">
    <label class="field">Ancho<select name="w">${opts(Math.max(colsOf(draft), w.w), w.w)}</select></label>
    <label class="field">Alto<select name="h">${opts(MAX_ROWS, w.h)}</select></label>
  </div>`;
}

async function openWidgetConfig(w, isNew = false) {
  const tmp = clone(w);
  let fields = '';
  if (tmp.type === 'metric') {
    fields = `<label class="field">Métrica<select name="metric">${Object.entries(METRICS).map(([k, m]) =>
      `<option value="${k}" ${tmp.cfg.metric === k ? 'selected' : ''}>${m.name}</option>`).join('')}</select></label>
      ${summary?.gpus.length > 1 ? `<label class="field">GPU<select name="index">${summary.gpus.map((g, i) => `<option value="${i}" ${tmp.cfg.index == i ? 'selected' : ''}>${esc(g.name)}</option>`).join('')}</select></label>` : ''}`;
  } else if (tmp.type === 'control') {
    fields = `<label class="field">Control<select name="control">${Object.entries(CONTROLS).map(([k, n]) =>
      `<option value="${k}" ${tmp.cfg.control === k ? 'selected' : ''}>${n}</option>`).join('')}</select></label>
      <label class="field" data-presets>Presets de TDP (W, separados por coma)<input name="presets" inputmode="numeric" value="${(tmp.cfg.presets || [8, 15, 25, 30]).join(', ')}"></label>`;
  } else if (tmp.type === 'sensor') {
    const opts = (last?.hardware || []).flatMap(h => h.sensors.map(s =>
      `<label class="pick-item" data-q="${esc((h.name + ' ' + s.name + ' ' + s.type).toLowerCase())}">
        <input type="radio" name="sensorId" value="${esc(s.id)}" ${tmp.cfg.sensorId === s.id ? 'checked' : ''}>
        <span>${esc(s.name)} <small>${esc(h.name)} · ${esc(s.type)}</small></span><b>${esc(fmtSensor(s.type, s.value))}</b></label>`)).join('');
    fields = `${segField('style', 'Estilo', Object.entries(SENSOR_STYLES), tmp.cfg.style || 'number')}
      <input class="filter" placeholder="Buscar sensor…" data-filter>
      <div class="pick-list">${opts}</div>`;
  } else if (tmp.type === 'apps') {
    fields = `<label class="toggle"><input type="checkbox" name="labels" ${tmp.cfg.labels !== false ? 'checked' : ''}> Mostrar nombres</label>
      <input class="filter" placeholder="Buscar aplicación…" data-filter>
      <p class="note" data-sel></p>
      <div class="pick-list" data-apps><p class="note">Cargando aplicaciones…</p></div>`;
  }

  openModal(isNew ? 'Nuevo widget' : 'Configurar widget', `
    <form id="wform">
      ${fields}
      ${sizeFields(tmp)}
      <div class="modal-foot">
        <span class="spacer"></span>
        <button type="button" class="btn ghost" data-close>Cancelar</button>
        <button type="submit" class="btn primary">${isNew ? 'Agregar' : 'Aplicar'}</button>
      </div>
    </form>`, async body => {
    $('[data-close]', body).onclick = closeModal;
    // Los presets de TDP solo se muestran para ese control
    const ctl = $('[name=control]', body);
    if (ctl) {
      const sync = () => $('[data-presets]', body).classList.toggle('hidden', ctl.value !== 'tdp');
      sync();
      ctl.onchange = sync;
    }
    const form = $('#wform', body);
    const filter = $('[data-filter]', body);
    if (filter) filter.oninput = () => {
      const q = filter.value.trim().toLowerCase();
      body.querySelectorAll('.pick-item').forEach(x => x.classList.toggle('hidden', !!q && !x.dataset.q.includes(q)));
    };

    // Apps: lista con íconos; el orden de selección es el orden en el widget
    let selected = [...(tmp.cfg.apps || [])];
    if (tmp.type === 'apps') {
      const list = await loadApps().catch(() => []);
      const box = $('[data-apps]', body);
      const showSel = () => $('[data-sel]', body).textContent = selected.length ? `${selected.length} seleccionadas` : 'Elegí una o más aplicaciones';
      box.innerHTML = list.map(a => `<label class="pick-item" data-q="${esc(a.name.toLowerCase())}">
        <input type="checkbox" value="${esc(a.id)}" ${selected.includes(a.id) ? 'checked' : ''}>
        <img data-icon="${esc(a.id)}" alt=""><span>${esc(a.name)}</span></label>`).join('');
      showSel();
      // Los íconos se cargan a medida que aparecen en la lista
      const io = new IntersectionObserver(es => es.forEach(x => {
        if (x.isIntersecting) { hydrateIcons(x.target); io.unobserve(x.target); }
      }), { root: box });
      box.querySelectorAll('.pick-item').forEach(x => io.observe(x));
      box.onchange = e => {
        const id = e.target.value;
        selected = e.target.checked ? [...selected, id] : selected.filter(x => x !== id);
        showSel();
      };
    }

    form.onsubmit = e => {
      e.preventDefault();
      const f = new FormData(form);
      tmp.w = +f.get('w');
      tmp.h = +f.get('h');
      tmp.cfg.title = f.get('title').trim() || undefined;
      if (tmp.type === 'metric') { tmp.cfg.metric = f.get('metric'); if (f.has('index')) tmp.cfg.index = +f.get('index'); }
      if (tmp.type === 'control') {
        tmp.cfg.control = f.get('control');
        const p = String(f.get('presets')).split(/[,\s]+/).map(Number).filter(x => x > 0);
        if (p.length) tmp.cfg.presets = p;
      }
      if (tmp.type === 'sensor') {
        if (!f.get('sensorId')) return toast('Elegí un sensor');
        tmp.cfg.sensorId = f.get('sensorId');
        tmp.cfg.style = f.get('style');
      }
      if (tmp.type === 'apps') {
        if (!selected.length) return toast('Elegí al menos una aplicación');
        tmp.cfg.apps = selected;
        tmp.cfg.names = Object.fromEntries(selected.map(id => [id, appsCache?.find(a => a.id === id)?.name || id]));
        tmp.cfg.labels = f.has('labels');
      }
      const i = draft.widgets.findIndex(x => x.id === tmp.id);
      i >= 0 ? draft.widgets[i] = tmp : draft.widgets.push(tmp);
      closeModal();
      renderDashboard(last);
    };
  });
}

// ---------- Gestión de dashboards ----------
// Campo de botones segmentados: un input oculto guarda el valor elegido (ver segFields en openModal)
function segField(name, label, options, current, hint = '') {
  const cur = String(current);
  if (!options.some(([v]) => String(v) === cur)) options = [...options, [current, String(current)]];
  return `<div class="field" data-field="${name}">
    <span class="flabel">${label}</span>
    <div class="seg" data-seg="${name}">${options.map(([v, l]) =>
      `<button type="button" data-v="${v}" class="${String(v) === cur ? 'on' : ''}">${l}</button>`).join('')}</div>
    <input type="hidden" name="${name}" value="${esc(cur)}">
    ${hint ? `<p class="note">${hint}</p>` : ''}
  </div>`;
}

$('#dash-settings').onclick = () => openModal('Ajustes del dashboard', `
  <form id="dform">
    <label class="field">Nombre<input name="name" maxlength="40" required value="${esc(draft.name)}"></label>
    ${segField('cols', 'Columnas', [['auto', 'Auto'], [4, '4'], [6, '6'], [8, '8'], [12, '12']], draft.cols,
      'Auto: un 1×1 mide lo mismo en cualquier pantalla y entran más columnas cuanto más ancha sea.')}
    ${segField('cell', 'Tamaño de celda', Object.entries(CELL_SIZES).map(([px, n]) => [+px, n]), draft.cell || CELL_DEFAULT)}
    ${segField('rowH', 'Alto de fila', [['auto', 'Cuadrado'], [75, '75'], [90, '90'], [110, '110'], [130, '130']], draft.rowH)}
    <div class="modal-foot">
      <button type="button" class="btn ghost" id="d-dup">Duplicar</button>
      <button type="button" class="btn ghost danger" id="d-del">Eliminar</button>
      <span class="spacer"></span>
      <button type="submit" class="btn primary">Aplicar</button>
    </div>
  </form>`, body => {
  // El tamaño de celda solo importa con columnas automáticas
  const syncCell = () => $('[data-field=cell]', body).classList.toggle('hidden', $('[name=cols]', body).value !== 'auto');
  syncCell();
  $('#dform', body).addEventListener('segchange', syncCell);
  $('#dform', body).onsubmit = e => {
    e.preventDefault();
    const f = new FormData(e.target);
    draft.name = f.get('name').trim();
    draft.cols = f.get('cols') === 'auto' ? 'auto' : +f.get('cols');
    draft.cell = +f.get('cell');
    draft.rowH = f.get('rowH') === 'auto' ? 'auto' : +f.get('rowH');
    if (draft.cols !== 'auto') draft.widgets.forEach(w => w.w = Math.min(w.w, draft.cols));
    closeModal();
    renderDashboard(last);
  };
  $('#d-dup', body).onclick = async () => {
    const copy = { ...clone(draft), id: uid(), name: (draft.name + ' (copia)').slice(0, 40) };
    await saveDashboard(copy);
    closeModal();
    exitEdit();
    selectDashboard(copy.id);
    toast('Dashboard duplicado');
  };
  $('#d-del', body).onclick = async () => {
    if (dashboards.length < 2) return toast('Tiene que quedar al menos un dashboard');
    if (!confirm(`¿Eliminar "${draft.name}"?`)) return;
    await act('/dashboards/delete', { action: draft.id }, 'Dashboard eliminado');
    dashboards = dashboards.filter(x => x.id !== draft.id);
    closeModal();
    exitEdit();
    selectDashboard(dashboards[0].id);
  };
});

$('#dash-select').onchange = async e => {
  if (e.target.value !== '__new') return selectDashboard(e.target.value);
  const name = prompt('Nombre del nuevo dashboard', 'Nuevo');
  if (!name?.trim()) return fillSelect();
  const d = { id: uid(), name: name.trim().slice(0, 40), cols: 'auto', cell: CELL_DEFAULT, rowH: 'auto', widgets: [] };
  try {
    await saveDashboard(d);
    selectDashboard(d.id);
    $('#dash-edit').click();
  } catch (err) { toast(err.message); fillSelect(); }
};
