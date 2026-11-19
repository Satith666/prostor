'use strict';
/* Prostor — интерфейс. Без зависимостей, работает офлайн.
   Принцип скорости: страница строится один раз при переходе, а клики,
   прогресс и подсчёты меняют только свои элементы. */

// ============================================================ связь с программой
let TOKEN = new URLSearchParams(location.search).get('t') || '';
try {
  if (TOKEN) sessionStorage.setItem('prostor.t', TOKEN);
  else TOKEN = sessionStorage.getItem('prostor.t') || '';
} catch (e) { /* хранилище недоступно */ }

async function api(path, body) {
  const opt = { headers: { 'X-Token': TOKEN } };
  if (body !== undefined) {
    opt.method = 'POST';
    opt.headers['Content-Type'] = 'application/json';
    opt.body = JSON.stringify(body);
  }
  let r;
  try {
    r = await fetch(path, opt);
  } catch (e) {
    throw new Error('Нет связи с программой');
  }
  let data = null;
  try { data = await r.json(); } catch (e) { /* пустой ответ */ }
  if (!r.ok) throw new Error((data && data.error) || 'Ошибка ' + r.status);
  return data;
}

let pingFails = 0;
setInterval(() => {
  api('/api/ping').then(() => { pingFails = 0; }, () => { if (++pingFails >= 2) showOffline(); });
}, 15000);
api('/api/ping').catch(() => {});
addEventListener('pagehide', () => {
  try { navigator.sendBeacon('/api/bye?t=' + encodeURIComponent(TOKEN), ''); } catch (e) { /* ничего */ }
});

function showOffline(text) {
  if (document.querySelector('.offline')) return;
  const d = document.createElement('div');
  d.className = 'offline';
  d.innerHTML = `<div>${ic('power')}<h2>${esc(text || 'Программа закрыта')}</h2><p>Это окно можно закрыть.</p></div>`;
  document.body.appendChild(d);
}

// ============================================================ мелочи
const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => Array.from(r.querySelectorAll(s));
const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const sum = (a, f) => a.reduce((t, x) => t + (f ? f(x) : x), 0);
const ltr = (s) => '\u200e' + esc(s) + '\u200e'; // пути с обрезкой слева

function sizeParts(b) {
  b = Math.max(0, Number(b) || 0);
  if (b < 1024) return { n: String(b), u: 'Б' };
  const u = ['КБ', 'МБ', 'ГБ', 'ТБ'];
  let i = -1;
  do { b /= 1024; i++; } while (b >= 1024 && i < u.length - 1);
  let s = b >= 100 ? b.toFixed(0) : b >= 10 ? b.toFixed(1) : b.toFixed(2);
  if (s.includes('.')) s = s.replace(/\.?0+$/, '');
  return { n: s.replace('.', ','), u: u[i] };
}
function fmtSize(b) { const p = sizeParts(b); return p.n + '\u00a0' + p.u; }
const fmtNum = (n) => (Number(n) || 0).toLocaleString('ru-RU');
function plural(n, one, few, many) {
  const a = Math.abs(n) % 100, b = a % 10;
  if (a > 10 && a < 20) return many;
  if (b > 1 && b < 5) return few;
  if (b === 1) return one;
  return many;
}
const nFiles = (n) => fmtNum(n) + ' ' + plural(n, 'файл', 'файла', 'файлов');
const nItems = (n) => fmtNum(n) + ' ' + plural(n, 'пункт', 'пункта', 'пунктов');
function fmtDate(v, withTime) {
  if (!v) return '';
  const d = typeof v === 'number' ? new Date(v * 1000) : new Date(v);
  if (isNaN(d) || d.getFullYear() < 1990) return '';
  const o = { day: 'numeric', month: 'short', year: 'numeric' };
  if (withTime) Object.assign(o, { hour: '2-digit', minute: '2-digit' });
  return d.toLocaleString('ru-RU', o);
}
function fmtDur(sec) {
  if (!(sec >= 1)) return 'меньше секунды';
  sec = Math.round(sec);
  if (sec < 60) return sec + ' с';
  const m = Math.floor(sec / 60), s = sec % 60;
  if (m < 60) return m + ' мин' + (s ? ' ' + s + ' с' : '');
  const h = Math.floor(m / 60);
  if (h < 48) return h + ' ч ' + (m % 60) + ' мин';
  const d = Math.floor(h / 24);
  return d + ' ' + plural(d, 'день', 'дня', 'дней');
}
const pct = (a, b) => (b > 0 ? Math.max(0, Math.min(100, (a / b) * 100)) : 0);
const pctTxt = (a, b) => { const p = pct(a, b); return p > 0 && p < 0.1 ? '<0,1%' : p.toFixed(p < 10 ? 1 : 0).replace('.', ',') + '%'; };

const CAT_ICON = { system: 'windows', browsers: 'globe', apps: 'box', games: 'game', dev: 'terminal', privacy: 'eye' };
const LEVELS = { critical: [4, 'Важно'], high: [3, 'Желательно'], medium: [2, 'Полезно'], low: [1, 'По желанию'], info: [0, 'К сведению'] };

// ============================================================ состояние
const S = {
  info: null, settings: null, sep: '\\', page: '',
  cats: [], targets: null, scan: {}, scanAt: 0, scanning: false, cleaning: false, closed: new Set(['privacy', 'dev']), openGrp: new Set(), cleanQ: '', rules: null,
  heroDrive: null, freed: 0, animated: false,
  advice: null, advising: false,
  folders: null, an: null, du: null, duMin: 0, pr: null, st: null, tools: null, hist: null, schedDraft: null,
  q: null, qOpen: new Set(),
};

// ============================================================ фоновые задачи
// Один опрос на задачу, без наложения запросов. Подписки страницы
// сбрасываются при переходе, подписки диалогов живут отдельно.
let pageHooks = {};
const dialogHooks = {};
const watching = {};

function watch(kind) {
  if (watching[kind]) return watching[kind];
  let lines = [];
  watching[kind] = new Promise((resolve) => {
    const tick = async () => {
      let s;
      try {
        s = await api(`/api/job?kind=${kind}&log=${lines.length}`);
      } catch (e) {
        setTimeout(tick, 1500);
        return;
      }
      const got = s.log || [];
      if (s.logLen < lines.length) lines = [];
      lines = lines.slice(0, Math.max(0, s.logLen - got.length)).concat(got);
      s.lines = lines;
      for (const h of [pageHooks[kind], dialogHooks[kind]]) {
        if (h) { try { h(s); } catch (e) { console.error(e); } }
      }
      if (s.running) setTimeout(tick, 500);
      else { delete watching[kind]; resolve(s); }
    };
    tick();
  });
  return watching[kind];
}
async function startJob(kind, url, body) {
  try {
    await api(url, body || {});
  } catch (e) {
    if (/уже выполняется/.test(e.message)) return watch(kind);
    throw e;
  }
  return watch(kind);
}
const cancelJob = (kind) => api('/api/job/cancel', { kind }).catch(() => {});

// ============================================================ диалоги и уведомления
function toast(msg, err) {
  const t = document.createElement('div');
  t.className = 'toast' + (err ? ' err' : '');
  t.textContent = msg;
  $('#toasts').appendChild(t);
  setTimeout(() => t.remove(), err ? 6500 : 3500);
}

function openDialog(html, opts = {}) {
  const o = document.createElement('div');
  o.className = 'overlay';
  o.innerHTML = `<div class="dialog${opts.wide ? ' wide' : ''}" role="dialog" aria-modal="true">${html}</div>`;
  document.body.appendChild(o);
  const prevFocus = document.activeElement;
  const key = (e) => { if (e.key === 'Escape' && !opts.sticky) { e.stopPropagation(); close(null); } };
  function close(v) {
    if (!o.isConnected) return;
    o.remove();
    document.removeEventListener('keydown', key, true);
    if (prevFocus && prevFocus.focus) prevFocus.focus();
    if (opts.onClose) opts.onClose(v);
  }
  document.addEventListener('keydown', key, true);
  o.addEventListener('mousedown', (e) => { if (e.target === o && !opts.sticky) close(null); });
  const f = o.querySelector('.btns .primary') || o.querySelector('.btns .btn');
  if (f) setTimeout(() => f.focus(), 20);
  return { el: o.firstElementChild, close };
}

function ask(title, body, ok = 'Продолжить', opts = {}) {
  return new Promise((resolve) => {
    const d = openDialog(`<h2>${esc(title)}</h2>${body}<div class="btns">
      <button class="btn" data-v="0">${esc(opts.cancel || 'Отмена')}</button>
      <button class="btn primary" data-v="1">${esc(ok)}</button></div>`, { onClose: (v) => resolve(v === true) });
    $$('.btns button', d.el).forEach((b) => { b.onclick = () => d.close(b.dataset.v === '1'); });
  });
}
function info(title, body) {
  return new Promise((resolve) => {
    const d = openDialog(`<h2>${esc(title)}</h2>${body}<div class="btns"><button class="btn primary">Готово</button></div>`, { onClose: resolve });
    $('.btns button', d.el).onclick = () => d.close(true);
  });
}
const note = (icon, html, bad) => `<div class="note${bad ? ' bad' : ''}">${ic(icon)}<span>${html}</span></div>`;

// ============================================================ навигация
const NAV = [
  ['overview', 'Обзор'], ['advice', 'Рекомендации'], null,
  ['clean', 'Очистка'], ['analyze', 'Анализ диска'], ['dupes', 'Дубликаты'], null,
  ['programs', 'Программы'], ['startup', 'Автозагрузка'], ['tools', 'Инструменты'], null,
  ['history', 'История'], ['quarantine', 'Карантин'], ['settings', 'Настройки'],
];
const PAGES = {};
// Простой режим: пять разделов и только безопасные пункты. «Эксперт» — всё.
const SIMPLE_NAV = new Set(['overview', 'clean', 'analyze', 'programs', 'settings']);
const expertMode = () => !!(S.settings && S.settings.expert);

function buildNav() {
  $('#nav').innerHTML = NAV.map((n) => (n ? `<a href="#${n[0]}" data-p="${n[0]}">${ic(n[0])}<span>${n[1]}</span><em class="meta"></em></a>` : '<div class="gap"></div>')).join('');
}
function syncMode() {
  const ex = expertMode();
  for (const b of $$('#modeSw button')) {
    const on = (b.dataset.mode === 'expert') === ex;
    b.classList.toggle('on', on);
    b.setAttribute('aria-pressed', on ? 'true' : 'false');
  }
  const q = S.info && S.info.quarantine > 0;
  for (const a of $$('#nav a')) a.hidden = !ex && !SIMPLE_NAV.has(a.dataset.p) && !(a.dataset.p === 'quarantine' && q) && a.dataset.p !== S.page;
  for (const g of $$('#nav .gap')) g.hidden = !ex;
}
async function setMode(expert) {
  if (expertMode() === expert) return;
  if (await saveSettings({ expert })) {
    syncMode();
    render(false);
  }
}
function syncNav() {
  syncMode();
  for (const a of $$('#nav a')) a.classList.toggle('on', a.dataset.p === S.page);
  const meta = (p, text, hot) => {
    const e = $(`#nav a[data-p="${p}"] .meta`);
    if (e) { e.textContent = text || ''; e.classList.toggle('hot', !!hot); }
  };
  const junk = selectedJunk();
  meta('clean', S.scanning ? '…' : S.scanAt && junk > 0 ? fmtSize(junk) : '', junk > 0 && !S.scanning);
  const urgent = S.advice ? S.advice.recs.filter((r) => r.level === 'critical' || r.level === 'high').length : 0;
  meta('advice', S.advising ? '…' : urgent ? String(urgent) : '', urgent > 0 && !S.advising);
  meta('analyze', S.an && S.an.running ? '…' : '');
  meta('dupes', S.du && S.du.running ? '…' : '');
  const i = S.info;
  if (!i) return;
  meta('quarantine', i.quarantine > 0 ? fmtSize(i.quarantine) : '');
  $('#railFoot').innerHTML = `<div class="who ${i.admin ? '' : 'limited'}"><i></i>${i.admin ? 'Права администратора' : 'Без прав администратора'}</div>
    ${i.admin ? '' : '<button class="linkbtn" data-act="elevate">Получить права</button>'}
    <div>Версия ${esc(i.version)}</div>`;
}

function go(page) {
  if (location.hash !== '#' + page) location.hash = page;
  else render(true);
}
addEventListener('hashchange', () => render(true));

function render(navigated) {
  const id = (location.hash || '#overview').slice(1);
  const page = PAGES[id] ? id : 'overview';
  const m = $('#main');
  const top = navigated && page !== S.page ? 0 : m.scrollTop;
  if (page !== S.page) { pageHooks = {}; hideTip(); }
  S.page = page;
  m.dataset.page = page;
  delete m.dataset.ready;
  syncNav();
  const t0 = performance.now();
  try {
    PAGES[page](m);
  } catch (e) {
    console.error(e);
    m.innerHTML = `<div class="page"><div class="empty">${ic('warn')}<h3>Ошибка интерфейса</h3><p>${esc(e.message)}</p></div></div>`;
  }
  window.__lastRender = performance.now() - t0;
  m.scrollTop = top;
}
// перерисовать, только если пользователь сейчас на одной из этих страниц
const rerender = (...pages) => { if (!pages.length || pages.includes(S.page)) render(false); else syncNav(); };
const ready = (m) => { m.dataset.ready = S.page; };

function head(title, lead, actsHTML = '') {
  return `<div class="head"><div><h1>${esc(title)}</h1>${lead ? `<p>${lead}</p>` : ''}</div>${actsHTML ? `<div class="acts">${actsHTML}</div>` : ''}</div>`;
}
function adminBanner(what) {
  if (!S.info || S.info.admin) return '';
  return `<div class="banner">${ic('shield')}<span>Без прав администратора ${what}</span><span class="spacer"></span>
    <button class="btn sm" data-act="elevate">Получить права</button></div>`;
}
const loadingPage = (t = 'Загрузка…') => `<div class="page"><p class="skel">${esc(t)}</p></div>`;

// Один обработчик кликов на всё окно: data-act ищется у текущей страницы,
// затем среди общих действий.
let pageActs = {};
const COMMON = {
  elevate: () => elevate(),
  mode: (el) => setMode(el.dataset.mode === 'expert'),
  go: (el) => go(el.dataset.page),
  reveal: (el) => api('/api/fs/reveal', { path: el.dataset.path }).catch((e) => toast(e.message, true)),
  open: (el) => api('/api/fs/open', { path: el.dataset.path }).catch((e) => toast(e.message, true)),
};
document.addEventListener('click', (e) => {
  const el = e.target.closest('[data-act]');
  if (!el || el.closest('.dialog')) return;
  const fn = pageActs[el.dataset.act] || COMMON[el.dataset.act];
  if (!fn) return;
  if (el.tagName !== 'INPUT') e.preventDefault();
  fn(el, e);
});
const acts = (o) => { pageActs = o; };

// Права администратора без перезапуска: запускается помощник (запрос UAC),
// страница перезагружается и дальше работает через него.
async function elevate() {
  toast('Подтвердите запрос прав администратора');
  try {
    await api('/api/elevate', {});
    location.reload();
  } catch (e) {
    toast(e.message, true);
  }
}
async function refreshInfo() {
  try {
    S.info = await api('/api/info');
    S.settings = S.info.settings;
    S.sep = S.info.isWindows ? '\\' : '/';
  } catch (e) { /* оставим прежнее */ }
}

// ============================================================ очистка: данные
async function loadTargets() {
  const d = await api('/api/clean/targets');
  S.cats = d.categories || [];
  S.targets = d.targets || [];
  S.rules = d.rules || null;
}
const targetById = (id) => (S.targets || []).find((t) => t.id === id);
const usable = (t) => !(t.admin && S.info && !S.info.admin);
const scanOf = (t) => S.scan[t.id] || null;
const selectedTargets = () => (S.targets || []).filter((t) => t.checked && usable(t));
const selectedJunk = () => sum(selectedTargets(), (t) => (scanOf(t) ? scanOf(t).size : 0));

async function runScan() {
  if (!S.targets || S.scanning) return;
  S.scanning = true;
  rerender('overview', 'clean');
  try {
    const s = await startJob('scan', '/api/clean/scan', { ids: S.targets.map((t) => t.id) });
    if (s.err) toast(s.err, true);
    S.scan = {};
    for (const it of s.result || []) S.scan[it.id] = it;
    S.scanAt = Date.now();
  } catch (e) {
    toast(e.message, true);
  }
  S.scanning = false;
  rerender('overview', 'clean');
}

let selTimer = 0;
function saveSelection() {
  clearTimeout(selTimer);
  selTimer = setTimeout(() => {
    const sel = {};
    for (const t of S.targets) sel[t.id] = !!t.checked;
    api('/api/clean/select', { selected: sel }).catch(() => {});
  }, 400);
}

async function doClean(ids, title, again) {
  if (S.cleaning) return;
  const list = ids.map(targetById).filter((t) => t && usable(t));
  if (!list.length) { toast('Нечего очищать', true); return; }
  const total = sum(list, (t) => (scanOf(t) ? scanOf(t).size : 0));
  const running = [...new Set(list.flatMap((t) => (scanOf(t) && scanOf(t).running) || []))];
  const rows = list.slice().sort((a, b) => ((scanOf(b) || {}).size || 0) - ((scanOf(a) || {}).size || 0)).slice(0, 7)
    .map((t) => `<li><span>${esc(t.name)}</span><span>${scanOf(t) ? fmtSize(scanOf(t).size) : ''}</span></li>`).join('');
  const more = list.length > 7 ? `<p class="small faint">И ещё ${nItems(list.length - 7)}</p>` : '';
  let notes = '';
  if (list.some((t) => t.id === 'recycle')) notes += note('trash', 'Корзина будет очищена, файлы из неё не восстановить.');
  if (running.length) notes += note('info', `Сейчас запущены: ${esc(running.join(', '))}. Их занятые файлы будут пропущены.`);
  const caution = list.filter((t) => t.risk !== 'safe' && t.id !== 'recycle').map((t) => t.name);
  if (caution.length) notes += note('warn', `Выбраны пункты с пометкой «осторожно»: ${esc(caution.join(', '))}.`);
  if (!title) title = list.length === 1 ? `Очистить «${list[0].name}»?` : total ? `Очистить ${fmtSize(total)}?` : 'Начать очистку?';
  const ok = again || await ask(title, `<p>Файлы удаляются насовсем, минуя Корзину. Личные файлы, пароли и история браузеров не затрагиваются. Список удалённого сохранится в разделе «История».</p>
    ${notes}<ul class="reslist">${rows}</ul>${more}`, 'Очистить');
  if (!ok) return;
  S.cleaning = true;
  rerender('overview', 'clean');
  let s;
  try {
    s = await startJob('clean', '/api/clean/run', { ids: list.map((t) => t.id) });
  } catch (e) {
    S.cleaning = false;
    rerender('overview', 'clean');
    toast(e.message, true);
    return;
  }
  S.cleaning = false;
  if (s.err) toast(s.err, true);
  const res = s.result || { items: [], freed: 0, files: 0 };
  S.freed = res.freed;
  S.hist = null;
  await refreshInfo();
  rerender('overview', 'clean');
  showCleanResult(res, list.map((t) => t.id));
  runScan().then(() => { if (S.advice) runAdvisor(); });
}

// Программы, которые держат файлы: обычные окна и консоли можно закрыть,
// службы и системные процессы — нет.
const closableHolders = (hs) => [...new Set((hs || []).filter((h) => [1, 2, 5].includes(h.kind) && h.exe && !/^(explorer|svchost|csrss|winlogon|dwm|system)\.exe$/i.test(h.exe)).map((h) => h.exe))];

function showCleanResult(res, ids) {
  const items = (res.items || []).filter((i) => i.freed > 0 || i.note).slice(0, 8)
    .map((i) => `<li><span>${esc(i.name)}</span><span>${i.freed > 0 ? fmtSize(i.freed) : esc(i.note)}</span></li>`).join('');
  let extra = '';
  const hs = res.holders || [];
  const names = [...new Set(hs.map((h) => h.app || h.exe).filter(Boolean))];
  const closable = closableHolders(hs);
  if (res.locked > 0 && names.length) {
    extra = note('info', `${nFiles(res.locked)} не удалось удалить: их держат открытыми ${names.length > 1 ? 'программы' : 'программа'} <b>${esc(names.slice(0, 6).join(', '))}</b>${names.length > 6 ? ' и другие' : ''}.`
      + (res.scheduled > 0 ? ` ${fmtNum(res.scheduled)} удалятся при следующей перезагрузке.` : ''));
  } else if (res.failed > 0) {
    extra = `<p class="small">${nFiles(res.failed)} пропущено: их держат открытыми запущенные программы или Windows.`;
    if (res.scheduled > 0) extra += ` ${fmtNum(res.scheduled)} удалятся при следующей перезагрузке.`;
    extra += '</p>';
  }
  const lockBtns = res.locked > 0 && !res.scheduled
    ? (closable.length && ids ? `<button class="btn sm" data-v="close">${ic('stop')}Закрыть ${closable.length > 1 ? 'программы' : esc(closable[0].replace(/\.exe$/i, ''))} и повторить</button>` : '')
      + (S.info && S.info.admin ? `<button class="btn sm" data-v="reboot">${ic('refresh')}Удалить при перезагрузке</button>` : '')
    : '';
  const d = openDialog(`<h2>${res.freed > 0 ? 'Очистка завершена' : 'Готово'}</h2><div class="big">${fmtSize(res.freed)}</div>
    <p>Освобождено, удалено ${nFiles(res.files || 0)} ${res.seconds >= 1 ? 'за ' + fmtDur(res.seconds) : 'менее чем за секунду'}.</p>${extra}${lockBtns ? `<div class="lockacts">${lockBtns}</div>` : ''}${items ? `<ul class="reslist">${items}</ul>` : ''}
    <div class="btns">${res.journal ? `<button class="btn" data-v="j">${ic('journal')}Список удалённых файлов</button>` : ''}<button class="btn primary" data-v="ok">Готово</button></div>`);
  $$('button[data-v]', d.el).forEach((b) => {
    b.onclick = async () => {
      const v = b.dataset.v;
      if (v === 'reboot') {
        b.disabled = true;
        try {
          const r = await api('/api/clean/reboot', {});
          toast(r.scheduled ? `${nFiles(r.scheduled)} удалятся при следующей перезагрузке` : 'Не удалось поставить файлы в очередь');
        } catch (e) { toast(e.message, true); }
        d.close();
        return;
      }
      d.close();
      if (v === 'j') showJournal(res.journal, 'Удалённые файлы');
      if (v === 'close') {
        if (!(await ask('Закрыть программы?', `<p>Будут закрыты: ${esc(closable.join(', '))}. Несохранённые данные в них пропадут. Затем очистка повторится.</p>`, 'Закрыть и повторить'))) return;
        await api('/api/procs/close', { names: closable }).catch((e) => toast(e.message, true));
        doClean(ids, null, true);
      }
    };
  });
}

// ============================================================ ЖУРНАЛ УДАЛЕНИЙ
const J_ACTION = { d: 'удалён', q: 'в карантине', r: 'в Корзине', f: 'не удалён', s: 'при перезагрузке' };
function showJournal(id, title) {
  const st = { q: '', off: 0, recs: [], total: 0 };
  const d = openDialog(`<h2>${esc(title)}</h2><p class="small muted" id="jrSum">Загрузка…</p>
    <input class="input jr-q" id="jrQ" placeholder="Поиск по пути или пункту" aria-label="Поиск по журналу">
    <div class="jr-list" id="jrList"></div>
    <div class="btns"><button class="btn" id="jrMore" hidden>Показать ещё</button><button class="btn primary" id="jrOk">Готово</button></div>`, { wide: true });
  const el = (s) => $(s, d.el);
  const row = (r) => `<div class="jr-row"><span class="jr-a a-${esc(r.a)}">${J_ACTION[r.a] || esc(r.a)}</span><span class="path" title="${esc(r.p)}">${ltr(r.p)}</span><span class="num">${r.s ? fmtSize(r.s) : ''}</span></div>`;
  let seq = 0;
  const load = async (more) => {
    const my = ++seq;
    if (!more) { st.off = 0; st.recs = []; }
    let pg;
    try {
      pg = await api(`/api/journal/${encodeURIComponent(id)}?q=${encodeURIComponent(st.q)}&offset=${st.off}&limit=200`);
    } catch (e) {
      if (my === seq) el('#jrSum').textContent = e.message;
      return;
    }
    if (my !== seq || !d.el.isConnected) return;
    st.recs = st.recs.concat(pg.records || []);
    st.off = st.recs.length;
    st.total = pg.total;
    const c = pg.counts || {}, b = pg.bytes || {};
    const parts = [];
    if (c.d) parts.push(`удалено ${nFiles(c.d)} (${fmtSize(b.d)})`);
    if (c.q) parts.push(`в карантине ${fmtNum(c.q)} (${fmtSize(b.q)})`);
    if (c.r) parts.push(`в Корзине ${fmtNum(c.r)} (${fmtSize(b.r)})`);
    if (c.s) parts.push(`удалятся при перезагрузке ${fmtNum(c.s)}`);
    if (c.f) parts.push(`не удалось удалить ${fmtNum(c.f)}`);
    el('#jrSum').textContent = (parts.join(', ') || 'Записей нет') + (st.q ? `. Найдено: ${fmtNum(st.total)}` : '');
    el('#jrList').innerHTML = st.recs.length ? st.recs.map(row).join('') : '<p class="small faint">Ничего не найдено</p>';
    el('#jrMore').hidden = st.recs.length >= st.total;
  };
  let timer;
  el('#jrQ').addEventListener('input', (e) => { clearTimeout(timer); timer = setTimeout(() => { st.q = e.target.value.trim(); load(false); }, 250); });
  el('#jrMore').onclick = () => load(true);
  el('#jrOk').onclick = () => d.close();
  load(false);
}

// ============================================================ ОБЗОР
const fixedDrives = () => (S.info ? S.info.drives || [] : []).filter((d) => d.total > 0 && (d.type === 'fixed' || d.type === 'removable'));
function systemDrive() {
  const ds = fixedDrives();
  const sd = ((S.info && S.info.systemDrive) || 'C:').toUpperCase();
  return ds.find((d) => d.root.toUpperCase().startsWith(sd)) || ds[0];
}
const driveLetter = (d) => (/^[A-Z]:\\?$/i.test(d.root) ? d.root.slice(0, 2).toUpperCase() : 'C:');
const driveTitle = (d) => 'Диск ' + driveLetter(d);

PAGES.overview = (m) => {
  const i = S.info;
  if (!i) { m.innerHTML = loadingPage(); return; }
  const drives = fixedDrives();
  const sys = systemDrive();
  const d = drives.find((x) => x.root === S.heroDrive) || sys;
  if (!d) { m.innerHTML = `<div class="page">${head('Обзор', 'Диски не найдены.')}</div>`; ready(m); return; }
  const isSys = sys && d.root === sys.root;
  const used = d.total - d.free;
  const junk = isSys ? Math.min(selectedJunk(), used) : 0;
  const freed = isSys ? Math.min(S.freed, d.free) : 0;
  const usedSolid = Math.max(0, used - junk);
  const fp = sizeParts(d.free);

  let junkMark = '';
  if (isSys && S.scanAt && junk > 0) {
    const at = pct(usedSolid, d.total);
    junkMark = at > 62
      ? `<span class="m junk right" style="left:${pct(used, d.total)}%">можно очистить<b>${fmtSize(junk)}</b></span>`
      : `<span class="m junk" style="left:${at}%">можно очистить<b>${fmtSize(junk)}</b></span>`;
  } else if (isSys && S.scanning) {
    junkMark = `<span class="m right" style="left:${pct(used, d.total)}%">ищем мусор…</span>`;
  }
  const grow = S.animated ? '' : ' grow';
  S.animated = true;

  const adv = S.advice;
  const imp = adv ? adv.recs.filter((r) => r.level !== 'info').length : 0;
  const advSub = S.advising ? 'Проверяем систему…' : adv ? (imp ? `${imp} ${plural(imp, 'совет', 'совета', 'советов')} по освобождению места` : 'Серьёзных проблем нет') : 'Проверка ещё не запускалась';
  const anDone = S.an && S.an.summary && S.an.root && !S.an.running && /^[A-Z]:\\?$/i.test(S.an.root) && driveLetter({ root: S.an.root }) === driveLetter(d);
  const others = drives.filter((x) => x.root !== d.root);
  const nJunk = selectedTargets().filter((t) => scanOf(t) && scanOf(t).size > 0).length;
  const cleanLabel = S.cleaning ? 'Очистка…' : junk > 0 ? 'Очистить ' + fmtSize(junk) : 'Очистить';

  m.innerHTML = `<div class="page">
    <section class="hz">
      <div class="hz-head"><h1>${esc(driveTitle(d))}</h1>${d.label ? `<span class="faint">${esc(d.label)}</span>` : ''}
        ${drives.length > 1 ? `<div class="drv">${drives.map((x) => `<button class="${x.root === d.root ? 'on' : ''}" data-act="hero" data-root="${esc(x.root)}">${esc(driveLetter(x))}</button>`).join('')}</div>` : ''}</div>
      <p class="hz-sub">Занято ${fmtSize(used)} из ${fmtSize(d.total)}</p>
      <div class="hz-stage">
        <div class="hz-free"><b>${fp.n}</b><span>${fp.u} свободно</span></div>
        <div class="hz-bar${grow}" role="img" aria-label="Занято ${pctTxt(used, d.total)} диска">
          <i class="used" style="width:${pct(usedSolid, d.total)}%"></i><i class="junk" style="width:${pct(junk, d.total)}%"></i>${freed > 0 ? `<i class="freed" style="width:${pct(freed, d.total)}%"></i>` : ''}</div>
        <div class="hz-marks"><span class="m" style="left:0">занято<b>${pctTxt(used, d.total)}</b></span>${junkMark}</div>
      </div>
    </section>
    <div class="goto">
      ${isSys ? `<button class="go-row" data-act="go" data-page="clean">${ic('clean')}<span class="t"><b>Мусор и кэш</b><span>Временные файлы, кэш браузеров и программ, журналы</span></span>
        <span class="v ${junk > 0 ? 'hot' : ''}">${S.scanning ? '<span class="faint">…</span>' : S.scanAt ? fmtSize(junk) + `<small>${nItems(nJunk)}</small>` : ''}</span>${ic('chevron', 'chev')}</button>` : ''}
      <button class="go-row" data-act="go" data-page="advice">${ic('advice')}<span class="t"><b>Рекомендации</b><span>${esc(advSub)}</span></span>
        <span class="v">${adv && adv.potential > 0 ? `до ${fmtSize(adv.potential)}<small>можно освободить</small>` : ''}</span>${ic('chevron', 'chev')}</button>
      <button class="go-row" data-act="analyzeDrive" data-root="${esc(d.root)}">${ic('analyze')}<span class="t"><b>Что занимает место</b><span>Карта папок, самые крупные файлы и типы файлов</span></span>
        <span class="v">${anDone ? fmtSize(S.an.summary.size) + '<small>просканировано</small>' : ''}</span>${ic('chevron', 'chev')}</button>
      <button class="go-row" data-act="go" data-page="dupes">${ic('dupes')}<span class="t"><b>Дубликаты</b><span>Одинаковые фото, видео и установщики под разными именами</span></span>
        <span class="v">${S.du && S.du.result ? fmtSize(S.du.result.wasted) + '<small>лишних копий</small>' : ''}</span>${ic('chevron', 'chev')}</button>
    </div>
    ${isSys ? `<div class="go-cta"><button class="btn primary lg" data-act="cleanAll" ${S.scanning || S.cleaning || junk <= 0 ? 'disabled' : ''}>${ic('clean')}${cleanLabel}</button>
      <span class="note">${i.cleans ? `Prostor освободил ${fmtSize(i.totalFreed)} за ${fmtNum(i.cleans)} ${plural(i.cleans, 'очистку', 'очистки', 'очисток')}` : 'Очисток ещё не было'}</span></div>` : ''}
    ${others.length ? `<section class="drives"><h2 class="sec">Другие диски</h2><div class="panel">${others.map((x) => `<div class="drive">
        <div class="n">${esc(driveLetter(x))}<small>${esc(x.label || (x.type === 'removable' ? 'съёмный' : ''))}</small></div>
        <div class="bar"><i style="width:${pct(x.total - x.free, x.total)}%"></i></div>
        <div class="s">свободно ${fmtSize(x.free)} из ${fmtSize(x.total)}</div>
        <button class="btn sm" data-act="hero" data-root="${esc(x.root)}">Открыть</button></div>`).join('')}</div></section>` : ''}
  </div>`;
  acts({
    hero: (el) => { S.heroDrive = el.dataset.root; S.animated = false; render(false); },
    cleanAll: () => doClean(selectedTargets().filter((t) => scanOf(t) && scanOf(t).size > 0).map((t) => t.id)),
    analyzeDrive: (el) => {
      if (S.an && (S.an.running || (S.an.summary && sameP(S.an.root, el.dataset.root)))) go('analyze');
      else startAnalysis(el.dataset.root);
    },
  });
  if (S.freed > 0) setTimeout(() => { S.freed = 0; }, 8000);
  ready(m);
};

// ============================================================ РЕКОМЕНДАЦИИ
async function runAdvisor() {
  if (S.advising) return;
  S.advising = true;
  rerender('overview', 'advice');
  try {
    const s = await startJob('advisor', '/api/advisor/start');
    if (s.err) toast(s.err, true);
    if (s.result) S.advice = s.result;
  } catch (e) {
    toast(e.message, true);
  }
  S.advising = false;
  rerender('overview', 'advice');
}

async function recAction(kind, arg) {
  switch (kind) {
    case 'clean':
      if (!S.scanAt) await runScan();
      return doClean(arg.split(',').filter(Boolean));
    case 'tool': return runToolById(arg);
    case 'open': return api('/api/fs/open', { path: arg }).catch((e) => toast(e.message, true));
    case 'reveal': return api('/api/fs/reveal', { path: arg }).catch((e) => toast(e.message, true));
    case 'analyze': return startAnalysis(arg);
    case 'dupes': return startDupes(arg);
    case 'nav': return go(arg);
    case 'elevate': return elevate();
  }
  return null;
}

PAGES.advice = (m) => {
  const a = S.advice;
  let body;
  if (S.advising) {
    body = `<p class="muted" id="advPhase">Проверка…</p><div class="prog" style="margin-top:16px;max-width:520px"><i id="advBar"></i></div>
      <p class="small faint" style="margin-top:12px">Ищем мусор, большие папки, остатки обновлений и другие резервы места. Обычно до минуты.</p>`;
  } else if (!a) {
    body = `<div class="empty">${ic('advice')}<h3>Проверка ещё не запускалась</h3><p>Prostor посмотрит, что занимает место на компьютере, и подскажет, что можно безопасно удалить.</p>
      <button class="btn primary lg" data-act="advise">${ic('play')}Проверить систему</button></div>`;
  } else {
    body = `<div class="score"><b class="num">${a.potential > 0 ? fmtSize(a.potential) : '0'}</b><span>можно освободить<br>оценка состояния ${a.score} из 100</span></div>
      <div class="recs">${a.recs.map((r, k) => {
        const [n, label] = LEVELS[r.level] || [0, ''];
        return `<div class="rec">
          <div class="lvl ${r.level}" title="${esc(label)}">${[0, 1, 2, 3].map((x) => `<i class="${x < n ? 'on' : ''}"></i>`).join('')}</div>
          <div><span class="lv">${esc(label)}</span><h3>${esc(r.size > 0 ? r.title.replace(/:\s*[\d.,\s]+(Б|КБ|МБ|ГБ|ТБ)$/, '') : r.title)}</h3><p>${esc(r.desc)}</p>
            ${r.items && r.items.length ? `<ul>${r.items.map((it) => `<li><span title="${esc(it.path || it.label)}">${it.path ? `<a data-act="reveal" data-path="${esc(it.path)}">${esc(it.label)}</a>` : esc(it.label)}</span><em>${esc(it.note || '')}</em><b>${it.size ? fmtSize(it.size) : ''}</b></li>`).join('')}</ul>` : ''}
            ${r.actions && r.actions.length ? `<div class="acts">${r.actions.map((x, j) => `<button class="btn sm ${x.primary ? 'primary' : ''}" data-act="rec" data-r="${k}" data-k="${j}">${esc(x.label)}</button>`).join('')}</div>` : ''}
          </div>
          <div class="val">${r.size > 0 ? `<b>${fmtSize(r.size)}</b><span>можно освободить</span>` : ''}</div></div>`;
      }).join('') || '<div class="empty"><h3>Всё в порядке</h3><p>Советов нет.</p></div>'}</div>
      <p class="small faint" style="margin-top:16px">Проверка заняла ${fmtDur(a.took)}.</p>`;
  }
  m.innerHTML = `<div class="page">${head('Рекомендации', 'Что занимает место и что с этим сделать.', a && !S.advising ? `<button class="btn" data-act="advise">${ic('refresh')}Проверить заново</button>` : '')}${body}</div>`;
  acts({
    advise: () => runAdvisor(),
    rec: (el) => { const r = a.recs[+el.dataset.r]; const x = r.actions[+el.dataset.k]; recAction(x.kind, x.arg); },
  });
  pageHooks.advisor = (s) => {
    const p = $('#advPhase'); if (p) p.textContent = s.phase || 'Проверка…';
    const b = $('#advBar'); if (b && s.total) b.style.width = pct(s.done, s.total) + '%';
  };
  if (!S.advising) ready(m);
};

// ============================================================ ОЧИСТКА
const RISK_HELP = {
  safe: 'Удаляются кэш, журналы или временные файлы, которые программа создаст заново.',
  caution: 'Удаление безопасно, но что-то станет заметно: первые запуски медленнее, Корзину не восстановить.',
  expert: 'Действие необратимо: например, после удаления Windows.old нельзя откатить обновление.',
};
function sizeCell(sc) {
  if (S.scanning && !sc) return '<div class="sz zero">…</div>';
  if (sc && sc.size > 0) return `<div class="sz">${fmtSize(sc.size)}<small>${nFiles(sc.files)}</small></div>`;
  if (sc) return '<div class="sz zero">0</div>';
  return '<div class="sz zero">—</div>';
}
function cleanItemHTML(t) {
  const sc = scanOf(t);
  const can = usable(t);
  const tags = [];
  if (t.admin && !can) tags.push(`<span>${ic('shield')}нужны права</span>`);
  if (t.risk === 'expert') tags.push(`<span class="bad">${ic('warn')}необратимо</span>`);
  else if (t.risk === 'caution') tags.push(`<span>${ic('warn')}осторожно</span>`);
  if (t.source && expertMode()) tags.push('<span>Winapp2</span>');
  const run = sc && sc.running && sc.running.length
    ? `<div class="warnline">${ic('info')}<span>Запущено: ${esc(sc.running.join(', '))}. Занятые файлы будут пропущены.</span><button class="btn sm" data-act="closeProcs" data-names="${esc(sc.running.join(','))}">Закрыть программы</button></div>` : '';
  const where = expertMode() && (t.where || []).length
    ? `<details class="where"><summary>Папки и правило</summary><p class="small muted">${esc(RISK_HELP[t.risk] || '')}${t.source ? ' Правило из базы Winapp2, проверенное фильтром безопасности Prostor и тестом приманок.' : ''}</p>
      <ul>${t.where.map((w) => `<li class="path">${ltr(w)}</li>`).join('')}</ul></details>` : '';
  return `<div class="item ${can ? '' : 'off'}" data-id="${t.id}">
    <input type="checkbox" class="ck" data-act="tick" data-id="${t.id}" ${t.checked && can ? 'checked' : ''} ${can ? '' : 'disabled'} aria-label="${esc(t.name)}">
    <div class="t"><b>${esc(t.name)}</b>${tags.length ? `<span class="tags">${tags.join('')}</span>` : ''}<p>${esc(t.desc)}</p>${run}${where}</div>${sizeCell(sc)}</div>`;
}
// Программы из базы Winapp2 в простом режиме: одна строка на раздел.
function groupState(list) {
  const can = list.filter(usable);
  const on = can.filter((t) => t.checked);
  return on.length === 0 ? 'none' : on.length === can.length ? 'all' : 'some';
}
function groupHTML(c, imp) {
  const bySize = imp.slice().sort((a, b) => ((scanOf(b) || {}).size || 0) - ((scanOf(a) || {}).size || 0));
  const names = bySize.slice(0, 4).map((t) => t.name).join(', ') + (imp.length > 4 ? ` и ещё ${imp.length - 4}` : '');
  const open = S.openGrp.has(c.id);
  const sc = S.scanAt || S.scanning ? { size: sum(imp, (t) => (scanOf(t) ? scanOf(t).size : 0)), files: sum(imp, (t) => (scanOf(t) ? scanOf(t).files : 0)) } : null;
  const sub = open ? `<div class="sublist">${bySize.map((t) => `<label class="sub"><input type="checkbox" class="ck" data-act="tick" data-id="${t.id}" ${t.checked && usable(t) ? 'checked' : ''} ${usable(t) ? '' : 'disabled'}>
    <span>${esc(t.name)}</span><em>${scanOf(t) && scanOf(t).size > 0 ? fmtSize(scanOf(t).size) : ''}</em></label>`).join('')}</div>` : '';
  return `<div class="item grp" data-grp="${c.id}">
    <input type="checkbox" class="ck" data-act="tickGrp" data-cat="${c.id}" ${groupState(imp) === 'all' ? 'checked' : ''} aria-label="Другие программы">
    <div class="t"><b>Другие программы: ${imp.length}</b><p>Кэш и журналы установленных программ по базе Winapp2: ${esc(names)}.</p>
      <button class="linkbtn" data-act="toggleGrp" data-cat="${c.id}">${open ? 'Скрыть список' : 'Показать список'}</button>${sub}</div>${sc ? sizeCell(sc) : sizeCell(null)}</div>`;
}
function visibleInCat(c) {
  let list = S.targets.filter((t) => t.cat === c.id);
  if (!expertMode()) list = list.filter((t) => t.risk !== 'expert');
  if (S.scanAt && c.id !== 'system') list = list.filter((t) => !scanOf(t) || scanOf(t).found);
  // программы из базы показываем, только если у них нашлось что удалить
  if (S.scanAt) list = list.filter((t) => !t.source || !scanOf(t) || scanOf(t).size > 0);
  const q = expertMode() && S.cleanQ ? S.cleanQ.toLowerCase() : '';
  if (q) list = list.filter((t) => (t.name + ' ' + t.desc + ' ' + (t.where || []).join(' ')).toLowerCase().includes(q));
  return list;
}
function catState(c) {
  const list = visibleInCat(c).filter(usable);
  const on = list.filter((t) => t.checked);
  return { size: sum(on, (t) => (scanOf(t) ? scanOf(t).size : 0)), state: on.length === 0 ? 'none' : on.length === list.length ? 'all' : 'some' };
}
function cleanBar() {
  const sel = selectedTargets().filter((t) => !S.scanAt || (scanOf(t) && scanOf(t).size > 0));
  const size = sum(sel, (t) => (scanOf(t) ? scanOf(t).size : 0));
  const files = sum(sel, (t) => (scanOf(t) ? scanOf(t).files : 0));
  if (S.cleaning) {
    return `<div class="sum" style="min-width:220px"><b id="clFreed">0</b><span id="clPhase">Очистка…</span></div>
      <div class="grow"><div class="prog"><i id="clBar"></i></div></div><button class="btn" data-act="stopClean">${ic('stop')}Остановить</button>`;
  }
  return `<div class="sum"><b>${S.scanAt ? fmtSize(size) : S.scanning ? '…' : '—'}</b>
      <span id="scInfo">${S.scanning ? 'идёт поиск' : S.scanAt ? `выбрано ${nItems(sel.length)}, ${nFiles(files)}` : 'нажмите «Найти мусор»'}</span></div>
    <div class="grow">${S.scanning ? '<div class="prog"><i id="scBar"></i></div>' : ''}</div>
    <button class="btn" data-act="scan" ${S.scanning ? 'disabled' : ''}>${ic('refresh')}${S.scanAt ? 'Искать заново' : 'Найти мусор'}</button>
    <button class="btn primary lg" data-act="clean" ${!S.scanAt || S.scanning || size <= 0 ? 'disabled' : ''}>${ic('clean')}Очистить</button>`;
}
function syncClean() {
  // точечное обновление после клика по галочке
  for (const c of S.cats) {
    const box = $(`.cat[data-cat="${c.id}"]`);
    if (!box) continue;
    const st = catState(c);
    const ck = $('.cat-head .ck', box);
    ck.checked = st.state === 'all';
    ck.indeterminate = st.state === 'some';
    $('.cat-head .sz', box).textContent = S.scanAt ? fmtSize(st.size) : '';
    const g = $('.item.grp > .ck', box);
    if (g) {
      const gs = groupState(visibleInCat(c).filter((t) => t.source));
      g.checked = gs === 'all';
      g.indeterminate = gs === 'some';
    }
  }
  const bar = $('#cleanBar');
  if (bar) bar.innerHTML = cleanBar();
  syncNav();
}

PAGES.clean = (m) => {
  if (!S.targets) { m.innerHTML = loadingPage(); return; }
  const hidden = [];
  const ex = expertMode();
  const cats = S.cats.map((c) => {
    const list = visibleInCat(c);
    if (S.scanAt && c.id !== 'system') for (const t of S.targets) if (t.cat === c.id && !t.source && scanOf(t) && !scanOf(t).found) hidden.push(t.name);
    if (!list.length) return '';
    const st = catState(c);
    const own = list.filter((t) => !t.source), imp = list.filter((t) => t.source);
    const rows = own.map(cleanItemHTML).join('') + (imp.length ? (ex ? imp.map(cleanItemHTML).join('') : groupHTML(c, imp)) : '');
    return `<section class="cat ${S.closed.has(c.id) ? 'closed' : ''}" data-cat="${c.id}">
      <div class="cat-head" data-act="toggleCat" data-cat="${c.id}">
        <input type="checkbox" class="ck" data-act="tickCat" data-cat="${c.id}" ${st.state === 'all' ? 'checked' : ''} aria-label="${esc(c.name)}">
        ${ic(CAT_ICON[c.id] || 'folder')}<div class="t"><b>${esc(c.name)}</b><span>${esc(c.desc)}</span></div>
        <span class="sz">${S.scanAt ? fmtSize(st.size) : ''}</span>${ic('down', 'tg')}</div>
      <div class="list">${rows}</div></section>`;
  }).join('');
  const r = S.rules;
  const rulesLine = r ? `<p class="small faint">Правил очистки: ${fmtNum(r.builtin)} собственных и ${fmtNum(r.imported)} из базы <a href="https://github.com/MoscaDotTo/Winapp2" target="_blank" rel="noopener">Winapp2</a> (CC BY-SA 4.0); к программам этого компьютера подходят ${fmtNum(r.installed)}.${ex ? '' : ' Необратимые пункты видны в режиме «Эксперт».'}</p>` : '';
  m.innerHTML = `<div class="page">
    ${head('Очистка', 'Удаляются только временные файлы, кэш и журналы. Личные файлы, пароли и история браузеров не затрагиваются.',
      (ex ? `<input class="input sm" id="clQ" type="search" placeholder="Поиск по пунктам и папкам" aria-label="Поиск по пунктам" value="${esc(S.cleanQ || '')}">` : '')
      + '<button class="btn quiet" data-act="defaults">Рекомендуемые</button><button class="btn quiet" data-act="none">Снять все</button>')}
    ${adminBanner('пункты с пометкой «нужны права» будут пропущены.')}
    ${cats || (S.cleanQ ? `<div class="empty">${ic('search')}<h3>Ничего не найдено</h3><p>Нет пунктов, похожих на «${esc(S.cleanQ)}».</p></div>` : '')}
    ${hidden.length && !(ex && S.cleanQ) ? `<p class="small faint">Не установлено на этом компьютере: ${esc(hidden.join(', '))}.</p>` : ''}
    ${rulesLine}
    <div class="actionbar" id="cleanBar">${cleanBar()}</div></div>`;
  for (const c of S.cats) {
    const ck = $(`.cat[data-cat="${c.id}"] .cat-head .ck`);
    if (ck) ck.indeterminate = catState(c).state === 'some';
    const g = $(`.cat[data-cat="${c.id}"] .item.grp > .ck`);
    if (g) g.indeterminate = groupState(visibleInCat(c).filter((t) => t.source)) === 'some';
  }
  const qi = $('#clQ');
  if (qi) {
    let qt;
    qi.addEventListener('input', () => {
      clearTimeout(qt);
      qt = setTimeout(() => {
        S.cleanQ = qi.value.trim();
        render(false);
        const n = $('#clQ');
        if (n) { n.focus(); n.setSelectionRange(n.value.length, n.value.length); }
      }, 200);
    });
  }
  acts({
    tick: (el) => { targetById(el.dataset.id).checked = el.checked; saveSelection(); syncClean(); },
    tickCat: (el, e) => {
      e.stopPropagation();
      const c = S.cats.find((x) => x.id === el.dataset.cat);
      const want = catState(c).state !== 'all';
      for (const t of visibleInCat(c)) if (usable(t)) t.checked = want;
      for (const box of $$(`.cat[data-cat="${c.id}"] .item .ck`)) if (!box.disabled) box.checked = want;
      saveSelection();
      syncClean();
    },
    tickGrp: (el) => {
      const c = S.cats.find((x) => x.id === el.dataset.cat);
      const imp = visibleInCat(c).filter((t) => t.source);
      const want = groupState(imp) !== 'all';
      for (const t of imp) if (usable(t)) t.checked = want;
      for (const box of $$(`.item.grp[data-grp="${c.id}"] .ck`)) if (!box.disabled) box.checked = want;
      saveSelection();
      syncClean();
    },
    toggleGrp: (el) => {
      const c = el.dataset.cat;
      if (S.openGrp.has(c)) S.openGrp.delete(c); else S.openGrp.add(c);
      render(false);
    },
    toggleCat: (el, e) => {
      if (e.target.closest('.ck')) return;
      const c = el.dataset.cat;
      if (S.closed.has(c)) S.closed.delete(c); else S.closed.add(c);
      el.parentElement.classList.toggle('closed');
    },
    defaults: () => { for (const t of S.targets) t.checked = t.default; saveSelection(); render(false); },
    none: () => { for (const t of S.targets) t.checked = false; saveSelection(); render(false); },
    scan: () => runScan(),
    clean: () => doClean(selectedTargets().filter((t) => scanOf(t) && scanOf(t).size > 0).map((t) => t.id)),
    stopClean: () => cancelJob('clean'),
    closeProcs: async (el) => {
      const names = el.dataset.names.split(',');
      if (!(await ask('Закрыть программы?', `<p>Будут закрыты: ${esc(names.join(', '))}. Несохранённые данные в них пропадут.</p>`, 'Закрыть'))) return;
      el.disabled = true;
      await api('/api/procs/close', { names }).catch((e) => toast(e.message, true));
      runScan();
    },
  });
  pageHooks.scan = (s) => {
    const b = $('#scBar'); if (b && s.total) b.style.width = pct(s.done, s.total) + '%';
    const t = $('#scInfo'); if (t) t.textContent = `найдено ${fmtSize(s.bytes)}`;
  };
  pageHooks.clean = (s) => {
    const p = $('#clPhase'); if (p) p.textContent = s.phase || 'Очистка…';
    const f = $('#clFreed'); if (f) f.textContent = fmtSize(s.bytes);
    const b = $('#clBar'); if (b && s.total) b.style.width = pct(s.done, s.total) + '%';
  };
  if (S.scanAt || !S.scanning) ready(m);
};

// ============================================================ АНАЛИЗ ДИСКА
async function loadFolders() {
  if (!S.folders) { try { S.folders = (await api('/api/folders')) || []; } catch (e) { S.folders = []; } }
  return S.folders;
}
async function pickFolder(title) {
  try { const r = await api('/api/fs/pick', { title }); return (r && r.path) || ''; } catch (e) { toast(e.message, true); return ''; }
}
const sameP = (a, b) => String(a || '').replace(/[\\/]+$/, '').toLowerCase() === String(b || '').replace(/[\\/]+$/, '').toLowerCase();
function pickerHTML(cur, act) {
  const f = S.folders || [];
  return `<div class="chips picker">${f.map((x) => `<button class="chip ${cur && sameP(cur, x.path) ? 'on' : ''}" data-act="${act}" data-path="${esc(x.path)}">${ic(x.kind === 'drive' ? 'disk' : 'folder')}${esc(x.name)}</button>`).join('')}
    <button class="chip" data-act="${act}Pick">${ic('plus')}Другая папка…</button></div>`;
}

async function startAnalysis(path) {
  if (!path) return;
  const drv = fixedDrives().find((x) => sameP(x.root, path));
  S.an = { root: path, running: true, tab: (S.an && S.an.tab) || 'map', sel: new Set(), shown: 100, diskUsed: drv ? drv.total - drv.free : 0 };
  if (location.hash !== '#analyze') go('analyze'); else render(false);
  let s;
  try {
    s = await startJob('analyze', '/api/analyze/start', { path });
  } catch (e) {
    S.an.running = false;
    S.an.error = e.message;
    rerender('analyze');
    return;
  }
  S.an.running = false;
  if (s.err) { S.an.error = s.err; rerender('analyze'); return; }
  S.an.summary = s.result;
  S.an.root = s.result.root;
  await loadAnalysis(s.result.root);
}
async function loadAnalysis(path) {
  try {
    const [view, top, types] = await Promise.all([
      api('/api/analyze/view?path=' + encodeURIComponent(path)), api('/api/analyze/top'), api('/api/analyze/types')]);
    Object.assign(S.an, { view, top: top || [], types });
  } catch (e) {
    S.an.error = e.message;
  }
  rerender('analyze', 'overview');
}
async function openDir(path) {
  try {
    S.an.view = await api('/api/analyze/view?path=' + encodeURIComponent(path));
    S.an.shown = 100;
    TM.hover = null;
    hideTip();
    const tm = $('#tmPart');
    if (tm && S.an.tab === 'map') { tm.innerHTML = mapHTML(S.an); bindTreemap(); drawTreemap(); } else render(false);
  } catch (e) { toast(e.message, true); }
}

function isSystemPath(p) {
  const l = String(p).toLowerCase();
  return /^[a-z]:\\(windows|program files|program files \(x86\)|programdata)(\\|$)/.test(l) || /^[a-z]:\\?$/.test(l) || (/\\appdata(\\|$)/.test(l) && /\\(microsoft|packages)(\\|$)/.test(l));
}

// Удаление выбранного вручную: в карантин на N дней или в Корзину,
// если карантин выключен в настройках.
const qDays = () => (S.settings && S.settings.quarantineDays != null ? S.settings.quarantineDays : 7);
const qWhere = () => (qDays() > 0 ? 'карантин' : 'Корзину');
async function removePaths(paths, total, source) {
  if (!paths.length) return false;
  const days = qDays();
  const sys = paths.filter(isSystemPath);
  const list = paths.slice(0, 6).map((p) => `<li><span class="path">${ltr(p)}</span></li>`).join('');
  const many = `${fmtNum(paths.length)} ${plural(paths.length, 'объект', 'объекта', 'объектов')}`;
  const title = days > 0 ? (paths.length === 1 ? 'Удалить в карантин?' : `Удалить в карантин ${many}?`)
    : (paths.length === 1 ? 'Переместить в Корзину?' : `Переместить в Корзину ${many}?`);
  const text = days > 0
    ? `Файлы пролежат в карантине ${days} ${plural(days, 'день', 'дня', 'дней')}, затем удалятся сами. До этого их можно вернуть в разделе «Карантин».${total ? ` Место, примерно ${fmtSize(total)}, освободится после удаления из карантина.` : ''}`
    : `${total ? `Место освободится после очистки Корзины, примерно ${fmtSize(total)}. ` : ''}Пока файлы в Корзине, их можно восстановить.`;
  const ok = await ask(title, `<p>${text}</p>
     ${sys.length ? note('warn', 'Среди выбранного есть системные папки или данные программ. Их удаление может сломать Windows или программы.', true) : ''}
     <ul class="reslist">${list}</ul>${paths.length > 6 ? `<p class="small faint">И ещё ${paths.length - 6}</p>` : ''}`, days > 0 ? 'Удалить в карантин' : 'В Корзину');
  if (!ok) return false;
  try {
    const s = await startJob('delete', '/api/fs/remove', { paths, source });
    if (s.err) { toast(s.err, true); return false; }
    const r = s.result || {};
    const n = (r.removed || []).length;
    let msg;
    if (n < paths.length) msg = `Удалено ${n} из ${paths.length}. Остальное занято или защищено.`;
    else if (r.quarantined > 0) msg = `В карантине: ${fmtSize(r.held)}${r.freed > 0 ? `, в Корзине: ${fmtSize(r.freed)}` : ''}. Вернуть можно в разделе «Карантин».`;
    else msg = `В Корзине: ${fmtSize(r.freed)}`;
    toast(msg, n < paths.length);
    S.q = null; S.hist = null;
    refreshInfo();
    return r.removed || [];
  } catch (e) {
    toast(e.message, true);
    return false;
  }
}

// ---- карта папок (canvas)
const TM = { rects: [], hover: null, raf: 0, ro: null, dpr: 1 };

function squarify(values, x, y, w, h) {
  const out = [];
  const total = sum(values);
  if (total <= 0 || w <= 0 || h <= 0) return out;
  const areas = values.map((v) => (v * w * h) / total);
  const worst = (row, side) => {
    const s = sum(row), mx = Math.max(...row), mn = Math.min(...row);
    return Math.max((side * side * mx) / (s * s), (s * s) / (side * side * mn));
  };
  let i = 0;
  while (i < areas.length) {
    const side = Math.min(w, h);
    let row = [areas[i]], j = i + 1, wr = worst(row, side);
    while (j < areas.length) {
      const nr = row.concat(areas[j]), nw = worst(nr, side);
      if (nw > wr) break;
      row = nr; wr = nw; j++;
    }
    const s = sum(row);
    if (w >= h) {
      const cw = s / h; let cy = y;
      for (const a of row) { out.push({ x, y: cy, w: cw, h: a / cw }); cy += a / cw; }
      x += cw; w -= cw;
    } else {
      const rh = s / w; let cx = x;
      for (const a of row) { out.push({ x: cx, y, w: a / rh, h: rh }); cx += a / rh; }
      y += rh; h -= rh;
    }
    i = j;
  }
  return out;
}

function tmItems(v) {
  let items = (v.items || []).filter((it) => it.size > 0);
  if (v.other > 0) items.push({ name: 'Служебные и недоступные данные', size: v.other, kind: 'other' });
  items.sort((a, b) => b.size - a.size);
  const MAX = 60;
  if (items.length > MAX) {
    const rest = items.slice(MAX);
    items = items.slice(0, MAX).concat([{ name: `Ещё ${rest.length}`, size: sum(rest, (r) => r.size), group: true }]);
  }
  return items;
}

function drawTreemap() {
  const cv = $('#tm');
  if (!cv || !S.an || !S.an.view) return;
  const W = cv.clientWidth, H = cv.clientHeight;
  if (!W || !H) return;
  const dpr = window.devicePixelRatio || 1;
  if (cv.width !== Math.round(W * dpr) || cv.height !== Math.round(H * dpr)) {
    cv.width = Math.round(W * dpr);
    cv.height = Math.round(H * dpr);
  }
  const ctx = cv.getContext('2d');
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  const cs = getComputedStyle(document.documentElement);
  const col = (n) => cs.getPropertyValue(n).trim();
  const C = { bg: col('--tm-bg'), ink: col('--ink'), ink2: col('--ink-2'), t1: col('--tm-1'), t2: col('--tm-2'), files: col('--tm-files'), frame: col('--tm-line'), hatch: col('--tm-hatch'), klein: col('--klein') };
  // штриховка: служебные данные, «остальное» и группы
  const pc = document.createElement('canvas');
  pc.width = pc.height = Math.max(4, Math.round(6 * dpr));
  const px = pc.getContext('2d');
  px.fillStyle = C.files; px.fillRect(0, 0, pc.width, pc.height);
  px.strokeStyle = C.hatch; px.lineWidth = dpr;
  px.beginPath(); px.moveTo(0, pc.height); px.lineTo(pc.width, 0); px.stroke();
  const hatch = ctx.createPattern(pc, 'repeat');

  ctx.fillStyle = C.bg;
  ctx.fillRect(0, 0, cv.width, cv.height);
  // всё считается в физических пикселях, чтобы границы были ровными при любом масштабе Windows
  const PW = cv.width, PH = cv.height;
  const items = tmItems(S.an.view);
  const L1 = squarify(items.map((it) => it.size), 0, 0, PW, PH);
  const rects = [];
  const gap = Math.max(1, Math.round(2 * dpr));
  const font = (wgt, sz) => `${wgt} ${Math.round(sz * dpr)}px "Segoe UI Variable Text","Segoe UI",Inter,sans-serif`;
  const label = (text, x, y, maxW, f, color) => {
    if (maxW <= 4) return;
    ctx.font = f; ctx.fillStyle = color;
    let t = text;
    if (ctx.measureText(t).width > maxW) {
      while (t.length > 1 && ctx.measureText(t + '…').width > maxW) t = t.slice(0, -1);
      t += '…';
    }
    ctx.fillText(t, x, y);
  };
  const fillOf = (it, kid) => {
    if (kid) return kid.kind === 'rest' ? hatch : kid.kind === 'files' ? C.files : C.t2;
    if (it.kind === 'other' || it.group) return hatch;
    return it.isDir ? C.t1 : C.files;
  };
  const u = (v) => v * dpr; // CSS → физические пиксели
  L1.forEach((r, k) => {
    const it = items[k];
    const x0 = Math.round(r.x), y0 = Math.round(r.y);
    const x1 = Math.round(r.x + r.w) - gap, y1 = Math.round(r.y + r.h) - gap;
    if (x1 - x0 < 1 || y1 - y0 < 1) return;
    ctx.fillStyle = fillOf(it);
    ctx.fillRect(x0, y0, x1 - x0, y1 - y0);
    if (!it.isDir && !it.kind && !it.group) { // файл: тонкая рамка, чтобы отличать от папок
      ctx.strokeStyle = C.frame; ctx.lineWidth = Math.max(1, Math.round(dpr));
      ctx.strokeRect(x0 + 0.5 * dpr, y0 + 0.5 * dpr, x1 - x0 - dpr, y1 - y0 - dpr);
    }
    rects.push({ x0, y0, x1, y1, level: 1, item: it });
    const w = x1 - x0, h = y1 - y0;
    const big = w > u(70) && h > u(34);
    if (big) {
      label(it.name, x0 + u(8), y0 + u(17), w - u(16), font(600, 12), C.ink);
      if (h > u(50)) label(fmtSize(it.size), x0 + u(8), y0 + u(32), w - u(16), font(400, 11.5), C.ink2);
    } else if (w > u(34) && h > u(16)) {
      label(it.name, x0 + u(4), y0 + u(12), w - u(8), font(500, 10.5), C.ink2);
    }
    // второй уровень: что внутри папки
    if (it.kids && it.kids.length && w > u(90) && h > u(90)) {
      const top = u(40);
      const ix = x0 + u(6), iy = y0 + top, iw = w - u(12), ih = h - top - u(6);
      const L2 = squarify(it.kids.map((c) => c.size), ix, iy, iw, ih);
      const g2 = Math.max(1, Math.round(dpr));
      L2.forEach((q, j) => {
        const kd = it.kids[j];
        const a0 = Math.round(q.x), b0 = Math.round(q.y), a1 = Math.round(q.x + q.w) - g2, b1 = Math.round(q.y + q.h) - g2;
        if (a1 - a0 < 1 || b1 - b0 < 1) return;
        ctx.fillStyle = fillOf(it, kd);
        ctx.fillRect(a0, b0, a1 - a0, b1 - b0);
        rects.push({ x0: a0, y0: b0, x1: a1, y1: b1, level: 2, item: it, kid: kd });
        if (a1 - a0 > u(56) && b1 - b0 > u(32)) {
          label(kd.name, a0 + u(6), b0 + u(15), a1 - a0 - u(12), font(500, 11), C.ink);
          label(fmtSize(kd.size), a0 + u(6), b0 + u(28), a1 - a0 - u(12), font(400, 11), C.ink2);
        } else if (a1 - a0 > u(40) && b1 - b0 > u(16)) {
          label(kd.name, a0 + u(4), b0 + u(12), a1 - a0 - u(8), font(400, 10), C.ink2);
        }
      });
    }
  });
  TM.rects = rects;
  TM.dpr = dpr;
  const hv = TM.hover && rects.find((r) => r.item === TM.hover.item && r.kid === TM.hover.kid);
  if (hv) {
    const lw = Math.max(2, Math.round(2 * dpr));
    ctx.strokeStyle = C.klein;
    ctx.lineWidth = lw;
    ctx.strokeRect(hv.x0 + lw / 2, hv.y0 + lw / 2, hv.x1 - hv.x0 - lw, hv.y1 - hv.y0 - lw);
  }
  window.__tm = { w: PW, h: PH, dpr, total: sum(items, (it) => it.size), rects: rects.map((r) => ({ x0: r.x0, y0: r.y0, x1: r.x1, y1: r.y1, level: r.level, size: r.kid ? r.kid.size : r.item.size, name: r.kid ? r.kid.name : r.item.name })) };
}
function tmHit(e) {
  const cv = $('#tm');
  const b = cv.getBoundingClientRect();
  const x = (e.clientX - b.left) * TM.dpr, y = (e.clientY - b.top) * TM.dpr;
  let hit = null;
  for (const r of TM.rects) if (x >= r.x0 && x <= r.x1 && y >= r.y0 && y <= r.y1 && (!hit || r.level > hit.level)) hit = r;
  return hit;
}
function hideTip() { const t = $('#tip'); if (t) t.hidden = true; }
function tmTarget(r) {
  if (!r) return null;
  const it = r.item;
  if (r.kid) return r.kid.isDir ? it.path + S.sep + r.kid.name : it.isDir ? it.path : null;
  return it.isDir ? it.path : null;
}
function bindTreemap() {
  const cv = $('#tm');
  if (!cv) return;
  cv.onmousemove = (e) => {
    const r = tmHit(e);
    const tip = $('#tip');
    const same = r && TM.hover && r.item === TM.hover.item && r.kid === TM.hover.kid;
    if (!same) {
      TM.hover = r;
      cancelAnimationFrame(TM.raf);
      TM.raf = requestAnimationFrame(drawTreemap);
    }
    cv.classList.toggle('hand', !!tmTarget(r));
    if (!r) { tip.hidden = true; return; }
    const it = r.item, parent = S.an.view.size + (S.an.view.other || 0);
    const name = r.kid ? (r.kid.kind ? `${it.name}: ${r.kid.name.toLowerCase()}` : `${it.name} ${S.sep} ${r.kid.name}`) : it.name;
    const size = r.kid ? r.kid.size : it.size;
    let sub = `${fmtSize(size)}, ${pctTxt(size, parent)} от папки`;
    if (!r.kid && it.isDir) sub += `, ${nFiles(it.files)}`;
    if (it.kind === 'other') sub = `${fmtSize(size)}: служебные данные Windows и папки без доступа`;
    tip.innerHTML = `<b>${esc(name)}</b><span>${sub}</span>`;
    tip.hidden = false;
    const tw = tip.offsetWidth, th = tip.offsetHeight;
    let tx = e.clientX + 14, ty = e.clientY + 16;
    if (tx + tw > innerWidth - 8) tx = e.clientX - tw - 14;
    if (ty + th > innerHeight - 8) ty = e.clientY - th - 16;
    tip.style.left = tx + 'px';
    tip.style.top = ty + 'px';
  };
  cv.onmouseleave = () => { TM.hover = null; hideTip(); drawTreemap(); };
  cv.onclick = (e) => { const p = tmTarget(tmHit(e)); if (p) openDir(p); };
  if (TM.ro) TM.ro.disconnect();
  TM.ro = new ResizeObserver(() => { cancelAnimationFrame(TM.raf); TM.raf = requestAnimationFrame(drawTreemap); });
  TM.ro.observe(cv);
}

const catIcon = (c) => (['video', 'image', 'audio', 'archive', 'diskimage', 'program', 'document', 'code', 'model', 'data'].includes(c) ? c : 'other');

function mapHTML(a) {
  const v = a.view;
  const crumbs = (v.crumbs || []).map((c, k, arr) => `<button data-act="dir" data-path="${esc(c.path)}">${esc(k === 0 ? c.path.replace(/[\\/]$/, '') || c.path : c.name)}</button>${k < arr.length - 1 ? ic('chevron', 'sep') : ''}`).join('');
  const all = v.items || [];
  const total = v.size + (v.other || 0);
  const rows = all.slice(0, a.shown).map((it, k) => {
    const icon = it.isDir ? 'folder' : it.group ? 'layers' : catIcon(it.cat);
    const sub = it.isDir ? nFiles(it.files) + (it.denied ? ', нет доступа' : '') : it.group ? nFiles(it.files) : '';
    return `<tr class="${it.isDir ? 'go' : ''}" ${it.isDir ? `data-act="dir" data-path="${esc(it.path)}"` : ''}>
      <td><div class="nm">${ic(icon)}<div class="tx"><div>${esc(it.name)}</div>${sub ? `<small>${sub}</small>` : ''}</div></div></td>
      <td class="r w num">${fmtSize(it.size)}</td>
      <td class="w"><div class="share"><div class="bar"><i style="width:${pct(it.size, total)}%"></i></div><span>${pctTxt(it.size, total)}</span></div></td>
      <td class="w small faint">${fmtDate(it.mtime)}</td>
      <td class="w">${it.group ? '' : `<div class="ra"><button class="btn sm icon quiet" title="Показать в Проводнике" data-act="reveal" data-path="${esc(it.path)}">${ic('reveal')}</button>
        <button class="btn sm icon quiet" title="Удалить" data-act="del" data-i="${k}">${ic('trash')}</button></div>`}</td></tr>`;
  }).join('');
  return `<div class="crumbs">${v.parent ? `<button data-act="dir" data-path="${esc(v.parent)}" title="На уровень выше">${ic('back', 'sep')}</button>` : ''}${crumbs}</div>
    <div class="tm-wrap"><canvas id="tm" aria-label="Карта папок"></canvas></div>
    <div class="tm-legend"><span><i style="background:var(--tm-1)"></i>папки</span><span><i style="background:var(--tm-files);box-shadow:inset 0 0 0 1px var(--tm-line)"></i>файлы</span>
      <span><i style="background:repeating-linear-gradient(-45deg,var(--tm-hatch) 0 1px,var(--tm-files) 1px 4px)"></i>прочее и служебное</span><span class="spacer"></span><span>Нажмите на папку, чтобы открыть её</span></div>
    <table class="tbl" style="margin-top:28px"><thead><tr><th>Имя</th><th class="r">На диске</th><th>Доля</th><th>Изменено</th><th></th></tr></thead>
      <tbody>${rows || '<tr><td colspan="5" class="faint">Пусто</td></tr>'}</tbody></table>
    ${all.length > a.shown ? `<div class="more"><button class="btn" data-act="more">Показать все (${fmtNum(all.length)})</button></div>` : ''}`;
}

function topHTML(a) {
  const list = a.top || [];
  const selSize = sum(list.filter((f) => a.sel.has(f.path)), (f) => f.size);
  const all = list.length && a.sel.size === list.length;
  const rows = list.map((f) => {
    const name = f.path.split(/[\\/]/).pop();
    return `<tr><td class="w"><input type="checkbox" class="ck" data-act="topSel" data-path="${esc(f.path)}" ${a.sel.has(f.path) ? 'checked' : ''} aria-label="${esc(name)}"></td>
      <td><div class="nm">${ic(catIcon(f.cat))}<div class="tx"><div>${esc(name)}</div><small class="path" title="${esc(f.path)}">${ltr(f.path)}</small></div></div></td>
      <td class="r w num">${fmtSize(f.size)}</td><td class="w small faint">${fmtDate(f.mtime)}</td>
      <td class="w"><div class="ra"><button class="btn sm icon quiet" title="Показать в Проводнике" data-act="reveal" data-path="${esc(f.path)}">${ic('reveal')}</button></div></td></tr>`;
  }).join('');
  return `<p class="muted" style="margin-bottom:16px">${fmtNum(list.length)} самых крупных файлов. Отметьте ненужные и удалите: они попадут в ${qWhere()}.</p>
    <table class="tbl"><thead><tr><th><input type="checkbox" class="ck" data-act="topAll" ${all ? 'checked' : ''} aria-label="Выбрать все"></th><th>Файл</th><th class="r">На диске</th><th>Изменён</th><th></th></tr></thead>
    <tbody>${rows || '<tr><td colspan="5" class="faint">Нет файлов</td></tr>'}</tbody></table>
    <div class="actionbar" id="topBar"><div class="sum"><b>${fmtSize(selSize)}</b><span>выбрано ${nFiles(a.sel.size)}</span></div><span class="grow"></span>
      <button class="btn primary" data-act="topDel" ${a.sel.size ? '' : 'disabled'}>${ic('trash')}Удалить</button></div>`;
}

function typesHTML(a) {
  const t = a.types || { cats: [], exts: [], ages: [] };
  const total = t.total || 1;
  const maxC = Math.max(1, ...(t.cats || []).map((c) => c.size));
  const cats = (t.cats || []).filter((c) => c.size > 0).map((c) => `<div class="r">${ic(catIcon(c.id))}
    <div class="nm2"><span>${esc(c.name)}</span><small>${nFiles(c.count)}</small><div class="bar"><i style="width:${pct(c.size, maxC)}%"></i></div></div><b>${fmtSize(c.size)}</b></div>`).join('');
  const maxA = Math.max(1, ...(t.ages || []).map((x) => x.size));
  const ages = (t.ages || []).map((x) => `<div class="r">${ic('history')}
    <div class="nm2"><span>${esc(x.name)}</span><small>${nFiles(x.count)}</small><div class="bar"><i style="width:${pct(x.size, maxA)}%"></i></div></div><b>${fmtSize(x.size)}</b></div>`).join('');
  const exts = (t.exts || []).slice(0, 20).map((e) => `<tr><td><div class="nm">${ic(catIcon(e.cat))}<span>${esc(e.ext)}</span></div></td>
    <td class="r w faint num">${fmtNum(e.count)}</td><td class="r w num">${fmtSize(e.size)}</td><td class="w small faint">${pctTxt(e.size, total)}</td></tr>`).join('');
  return `<div class="grid2"><section><h2 class="sec">По типам</h2><div class="bars">${cats}</div></section>
    <section><h2 class="sec">По давности изменения</h2><div class="bars">${ages}</div>
      <p class="small faint" style="margin-top:10px">Файлы, которые не менялись годами, — кандидаты на архив или удаление.</p></section></div>
    <h2 class="sec">Расширения</h2><table class="tbl"><thead><tr><th>Тип</th><th class="r">Файлов</th><th class="r">На диске</th><th>Доля</th></tr></thead><tbody>${exts}</tbody></table>`;
}

PAGES.analyze = (m) => {
  if (!S.folders) loadFolders().then(() => rerender('analyze'));
  const a = S.an;
  let body = '';
  if (!a) {
    body = `<div class="empty">${ic('analyze')}<h3>Что занимает место?</h3><p>Выберите диск или папку. Prostor построит карту папок, найдёт самые крупные файлы и покажет, какие типы файлов занимают больше всего.</p></div>`;
  } else if (a.running) {
    body = `<div class="scanning"><div class="prog ${a.diskUsed ? '' : 'indet'}"><i id="anBar"></i></div>
      <div class="counters"><div><b id="anFiles">0</b><span>файлов</span></div><div><b id="anBytes">0</b><span>просмотрено</span></div><div><b id="anTime">0 с</b><span>прошло</span></div></div>
      <div class="row-flex"><button class="btn" data-act="stop">${ic('stop')}Остановить</button></div>
      <div class="curpath" id="anCur"></div></div>`;
  } else if (a.error) {
    body = `<div class="empty">${ic('warn')}<h3>Анализ не удался</h3><p>${esc(a.error)}</p><button class="btn" data-act="start" data-path="${esc(a.root)}">${ic('refresh')}Повторить</button></div>`;
  } else if (!a.view) {
    body = '<p class="skel">Загрузка результатов…</p>';
  } else {
    const sm = a.summary || {};
    const t = a.types || {};
    const tabs = [['map', 'Карта папок'], ['top', 'Крупные файлы'], ['types', 'Типы файлов']]
      .map(([id, n]) => `<button class="${a.tab === id ? 'on' : ''}" data-act="tab" data-tab="${id}">${n}</button>`).join('');
    const notes = [];
    if (sm.other > 0) notes.push(`<span title="Служебные данные файловой системы и папки без доступа">${ic('info')}служебные и недоступные данные: ${fmtSize(sm.other)}</span>`);
    if (t.hardlinks > 0) notes.push(`<span title="Системные файлы Windows, доступные по нескольким путям, посчитаны один раз">${ic('layers')}общие файлы Windows учтены один раз: ${fmtSize(t.hardlinks)}</span>`);
    if (t.cloud > 0) notes.push(`<span title="Файлы OneDrive и других облаков, которые хранятся только в облаке">${ic('download')}только в облаке: ${fmtSize(t.cloud)}</span>`);
    if (t.denied > 0) notes.push(`<span>${ic('shield')}нет доступа к ${fmtNum(t.denied)} ${plural(t.denied, 'папке', 'папкам', 'папкам')}</span>`);
    let part;
    if (a.tab === 'top') part = topHTML(a);
    else if (a.tab === 'types') part = typesHTML(a);
    else part = `<div id="tmPart">${mapHTML(a)}</div>`;
    body = `<div class="an-sum"><b>${fmtSize(sm.size)}</b><span class="muted">${nFiles(sm.files || 0)} в ${fmtNum(sm.dirs || 0)} ${plural(sm.dirs || 0, 'папке', 'папках', 'папках')}, анализ занял ${fmtDur(sm.took)}</span>
        <span class="spacer"></span><button class="btn sm" data-act="start" data-path="${esc(a.root)}">${ic('refresh')}Заново</button></div>
      ${notes.length ? `<div class="notes">${notes.join('')}</div>` : ''}
      <div class="tabs">${tabs}</div>${part}`;
  }
  m.innerHTML = `<div class="page wide">${head('Анализ диска', 'Куда ушло место: карта папок, самые крупные файлы и типы файлов. Размеры указаны так, как файлы занимают диск.')}
    ${pickerHTML(a && a.root, 'start')}${body}</div>`;
  acts({
    start: (el) => startAnalysis(el.dataset.path),
    startPick: async () => { const p = await pickFolder('Выберите папку для анализа'); if (p) startAnalysis(p); },
    stop: () => cancelJob('analyze'),
    tab: (el) => { a.tab = el.dataset.tab; render(false); },
    dir: (el) => openDir(el.dataset.path),
    more: () => { a.shown = 1e9; const tm = $('#tmPart'); if (tm) { tm.innerHTML = mapHTML(a); bindTreemap(); drawTreemap(); } },
    del: async (el) => {
      const it = a.view.items[+el.dataset.i];
      const done = await removePaths([it.path], it.size, 'Анализ диска');
      if (done && done.length) await loadAnalysis(a.view.path);
    },
    topSel: (el) => {
      if (el.checked) a.sel.add(el.dataset.path); else a.sel.delete(el.dataset.path);
      const bar = $('#topBar');
      const size = sum(a.top.filter((f) => a.sel.has(f.path)), (f) => f.size);
      if (bar) { $('.sum b', bar).textContent = fmtSize(size); $('.sum span', bar).textContent = 'выбрано ' + nFiles(a.sel.size); $('[data-act="topDel"]', bar).disabled = !a.sel.size; }
    },
    topAll: (el) => { a.sel = el.checked ? new Set(a.top.map((f) => f.path)) : new Set(); render(false); },
    topDel: async () => {
      const paths = [...a.sel];
      const done = await removePaths(paths, sum(a.top.filter((f) => a.sel.has(f.path)), (f) => f.size), 'Анализ диска');
      if (done) { a.sel = new Set(); await loadAnalysis(a.view.path); }
    },
  });
  pageHooks.analyze = (s) => {
    const f = $('#anFiles'); if (f) f.textContent = fmtNum(s.files);
    const b = $('#anBytes'); if (b) b.textContent = fmtSize(s.bytes);
    const t = $('#anTime'); if (t) t.textContent = s.elapsed >= 1 ? fmtDur(s.elapsed) : '0 с';
    const c = $('#anCur'); if (c) c.textContent = s.current ? '\u200e' + s.current + '\u200e' : '';
    const bar = $('#anBar'); if (bar && a && a.diskUsed) bar.style.width = Math.min(99, pct(s.bytes, a.diskUsed)) + '%';
  };
  if (a && a.view && a.tab === 'map' && !a.running && !a.error) { bindTreemap(); drawTreemap(); }
  if (!a || a.view || a.error) ready(m);
};

// ============================================================ ДУБЛИКАТЫ
async function startDupes(path, minMB) {
  if (!path) return;
  const mb = minMB || S.duMin || (S.settings && S.settings.minDupMB) || 1;
  S.du = { root: path, minMB: mb, running: true, sel: new Set(), shown: 50 };
  if (location.hash !== '#dupes') go('dupes'); else render(false);
  let s;
  try {
    s = await startJob('dupes', '/api/dupes/start', { path, minMB: mb });
  } catch (e) {
    S.du.running = false; S.du.error = e.message; rerender('dupes'); return;
  }
  S.du.running = false;
  if (s.err) { S.du.error = s.err; rerender('dupes'); return; }
  setDupes(s.result);
  rerender('dupes', 'overview');
}
function setDupes(res) {
  S.du.result = res;
  keepIn('old', true);
}
function keepIn(which, silent) {
  S.du.sel = new Set();
  for (const g of S.du.result.groups || []) {
    const sorted = g.files.slice().sort((a, b) => a.mtime - b.mtime);
    if (which === 'new') sorted.reverse();
    if (which !== 'none') sorted.slice(1).forEach((f) => S.du.sel.add(f.path));
  }
  if (!silent) render(false);
}
function dupesBar(d) {
  let size = 0, n = 0;
  for (const g of d.result.groups) for (const f of g.files) if (d.sel.has(f.path)) { n++; size += g.size; }
  return `<div class="sum"><b>${fmtSize(size)}</b><span>выбрано ${nFiles(n)}, они уйдут в ${qWhere()}</span></div><span class="grow"></span>
    <button class="btn primary lg" data-act="delete" ${n ? '' : 'disabled'}>${ic('trash')}Удалить выбранные</button>`;
}

PAGES.dupes = (m) => {
  if (!S.folders) loadFolders().then(() => rerender('dupes'));
  const d = S.du;
  const minMB = (d && d.minMB) || S.duMin || (S.settings && S.settings.minDupMB) || 1;
  const sizes = [[0.1, '100 КБ'], [1, '1 МБ'], [10, '10 МБ'], [100, '100 МБ']];
  let body = '';
  if (!d) {
    body = `<div class="empty">${ic('dupes')}<h3>Поиск одинаковых файлов</h3><p>Выберите папку выше. Файлы сравниваются по содержимому, поэтому одинаковые фото, видео и установщики найдутся даже под разными именами.</p></div>`;
  } else if (d.running) {
    body = `<div class="scanning"><div class="prog"><i id="duBar" style="width:2%"></i></div>
      <div class="counters"><div><b id="duFiles">0</b><span id="duWhat">файлов просмотрено</span></div><div><b id="duBytes">0</b><span>лишних копий найдено</span></div></div>
      <div class="row-flex"><button class="btn" data-act="stop">${ic('stop')}Остановить</button><span class="faint small" id="duPhase"></span></div><div class="curpath" id="duCur"></div></div>`;
  } else if (d.error) {
    body = `<div class="empty">${ic('warn')}<h3>Поиск не удался</h3><p>${esc(d.error)}</p></div>`;
  } else if (d.result) {
    const groups = d.result.groups || [];
    if (!groups.length) {
      body = `<div class="empty">${ic('check')}<h3>Дубликатов нет</h3><p>Проверено ${nFiles(d.result.scanned)} крупнее ${esc((sizes.find((x) => x[0] === minMB) || [0, minMB + ' МБ'])[1])}.</p></div>`;
    } else {
      body = `<div class="an-sum"><b>${fmtSize(d.result.wasted)}</b><span class="muted">занимают лишние копии: ${fmtNum(groups.length)} ${plural(groups.length, 'группа', 'группы', 'групп')} одинаковых файлов, проверено ${nFiles(d.result.scanned)}</span></div>
        <div class="row-flex" style="margin-bottom:14px"><span class="small muted">В каждой группе оставить</span>
          <div class="seg"><button data-act="keep" data-k="old">самый старый</button><button data-act="keep" data-k="new">самый новый</button><button data-act="keep" data-k="none">ничего не отмечать</button></div></div>
        <div class="list" id="dlist">${groups.slice(0, d.shown).map((g) => {
          const all = g.files.every((f) => d.sel.has(f.path));
          return `<div class="dgroup"><div class="gh"><b>${fmtSize(g.size)} × ${g.files.length}</b><span class="faint">лишнее ${fmtSize(g.wasted)}</span>${all ? '<span class="small" style="color:var(--red)">выбраны все копии</span>' : ''}</div>
            ${g.files.map((f) => `<div class="dfile ${d.sel.has(f.path) ? '' : 'keep'}"><input type="checkbox" class="ck" data-act="sel" data-path="${esc(f.path)}" ${d.sel.has(f.path) ? 'checked' : ''} aria-label="Удалить эту копию">
              <span class="p" title="${esc(f.path)}">${ltr(f.path)}</span><span class="k">${d.sel.has(f.path) ? 'удалить' : 'оставить'}, ${fmtDate(f.mtime)}</span>
              <button class="btn sm icon quiet" title="Показать в Проводнике" data-act="reveal" data-path="${esc(f.path)}">${ic('reveal')}</button></div>`).join('')}</div>`;
        }).join('')}</div>
        ${groups.length > d.shown ? `<div class="more"><button class="btn" data-act="more">Показать ещё ${Math.min(100, groups.length - d.shown)}</button></div>` : ''}
        <div class="actionbar" id="duBar2">${dupesBar(d)}</div>`;
    }
  }
  m.innerHTML = `<div class="page">${head('Дубликаты', `Одинаковые файлы, которые зря занимают место. Удалённое попадает в ${qWhere()}, его можно вернуть.`)}
    ${pickerHTML(d && d.root, 'start')}
    <div class="row-flex" style="margin:-14px 0 30px"><span class="small muted">Файлы не меньше</span>
      <div class="seg">${sizes.map(([v, n]) => `<button class="${minMB === v ? 'on' : ''}" data-act="min" data-v="${v}">${n}</button>`).join('')}</div></div>${body}</div>`;
  acts({
    start: (el) => startDupes(el.dataset.path, minMB),
    startPick: async () => { const p = await pickFolder('Где искать дубликаты?'); if (p) startDupes(p, minMB); },
    stop: () => cancelJob('dupes'),
    min: (el) => {
      const v = parseFloat(el.dataset.v);
      S.duMin = v;
      if (S.settings) { S.settings.minDupMB = Math.max(1, Math.round(v)); api('/api/settings', S.settings).catch(() => {}); }
      if (d && d.root && !d.running) startDupes(d.root, v); else render(false);
    },
    keep: (el) => keepIn(el.dataset.k),
    sel: (el) => {
      if (el.checked) d.sel.add(el.dataset.path); else d.sel.delete(el.dataset.path);
      const row = el.closest('.dfile');
      row.classList.toggle('keep', !el.checked);
      const k = $('.k', row);
      k.textContent = (el.checked ? 'удалить' : 'оставить') + k.textContent.replace(/^[^,]+/, '');
      $('#duBar2').innerHTML = dupesBar(d);
    },
    more: () => { d.shown += 100; render(false); },
    delete: async () => {
      const groups = d.result.groups;
      const full = groups.filter((g) => g.files.every((f) => d.sel.has(f.path)));
      if (full.length) {
        info('Выбраны все копии', `<p>В ${full.length} ${plural(full.length, 'группе', 'группах', 'группах')} отмечены все файлы, так удалится и оригинал. Оставьте хотя бы один файл в каждой группе.</p>`);
        return;
      }
      const paths = [];
      let total = 0;
      for (const g of groups) for (const f of g.files) if (d.sel.has(f.path)) { paths.push(f.path); total += g.size; }
      const done = await removePaths(paths, total, 'Дубликаты');
      if (!done || !done.length) return;
      const gone = new Set(done.map((p) => p.toLowerCase()));
      for (const g of groups) g.files = g.files.filter((f) => !gone.has(f.path.toLowerCase()));
      d.result.groups = groups.filter((g) => g.files.length > 1).map((g) => Object.assign(g, { wasted: g.size * (g.files.length - 1) }));
      d.result.wasted = sum(d.result.groups, (g) => g.wasted);
      for (const p of done) d.sel.delete(p);
      render(false);
    },
  });
  pageHooks.dupes = (s) => {
    const b = $('#duBar'); if (b) b.style.width = s.total ? Math.max(2, pct(s.done, s.total)) + '%' : '2%';
    const f = $('#duFiles'); if (f) f.textContent = fmtNum(s.total ? s.done : s.files);
    const w = $('#duWhat'); if (w) w.textContent = s.total ? `из ${fmtNum(s.total)} сравнено` : 'файлов просмотрено';
    const y = $('#duBytes'); if (y) y.textContent = fmtSize(s.bytes);
    const p = $('#duPhase'); if (p) p.textContent = s.phase || '';
    const c = $('#duCur'); if (c) c.textContent = s.current ? '\u200e' + s.current + '\u200e' : '';
  };
  if (!d || !d.running) ready(m);
};

// ============================================================ ПРОГРАММЫ
async function loadPrograms() {
  try { S.pr = Object.assign(S.pr || { q: '', sort: 'size', dir: -1 }, { list: (await api('/api/programs')) || [] }); } catch (e) { toast(e.message, true); S.pr = Object.assign(S.pr || {}, { list: [] }); }
  S.pr.loading = false;
  rerender('programs');
}
function filterPrograms() {
  const q = (S.pr.q || '').trim().toLowerCase();
  let shown = 0;
  for (const tr of $$('#prBody tr')) {
    const ok = !q || tr.dataset.s.includes(q);
    tr.hidden = !ok;
    if (ok) shown++;
  }
  const e = $('#prEmpty'); if (e) e.hidden = shown > 0;
}
PAGES.programs = (m) => {
  const p = S.pr;
  if (!p || !p.list) {
    m.innerHTML = loadingPage('Читаем список программ…');
    if (!p || !p.loading) { S.pr = Object.assign(p || { q: '', sort: 'size', dir: -1 }, { loading: true }); loadPrograms(); }
    return;
  }
  const key = { size: (x) => x.size, name: (x) => x.name.toLowerCase(), date: (x) => x.installDate || '' }[p.sort];
  const list = p.list.slice().sort((a, b) => (key(a) > key(b) ? 1 : key(a) < key(b) ? -1 : 0) * p.dir);
  const total = sum(p.list, (x) => x.size);
  const unknown = p.list.filter((x) => !x.size).length;
  const th = (id, name, cls = '') => `<th class="sort ${cls}" data-act="sort" data-s="${id}">${name}${p.sort === id ? (p.dir < 0 ? ' ↓' : ' ↑') : ''}</th>`;
  const rows = list.map((x) => `<tr data-s="${esc((x.name + ' ' + (x.publisher || '')).toLowerCase())}">
      <td><div class="nm">${ic('program')}<div class="tx"><div>${esc(x.name)}</div><small>${esc([x.publisher, x.version].filter(Boolean).join(', '))}</small></div></div></td>
      <td class="r w num">${x.size ? (x.sizeSource === 'folder' ? '≈ ' : '') + fmtSize(x.size) : '<span class="faint">—</span>'}</td>
      <td class="w small faint">${x.installDate ? fmtDate(x.installDate) : ''}</td>
      <td class="w small faint">${x.scope === 'user' ? 'для меня' : 'для всех'}</td>
      <td class="w"><div class="row-flex" style="gap:4px;justify-content:flex-end">${x.location ? `<button class="btn sm icon quiet" title="Открыть папку" data-act="open" data-path="${esc(x.location)}">${ic('folder')}</button>` : ''}
        <button class="btn sm" data-act="uninstall" data-key="${esc(x.key)}">Удалить</button></div></td></tr>`).join('');
  m.innerHTML = `<div class="page wide">${head('Программы', `${fmtNum(p.list.length)} ${plural(p.list.length, 'программа', 'программы', 'программ')} занимают ${fmtSize(total)}. Удаление ненужных программ обычно освобождает больше всего места.`,
    `<div class="search">${ic('search')}<input class="input" id="prq" placeholder="Найти программу" value="${esc(p.q || '')}"></div>
     <button class="btn" data-act="measure" ${p.measuring ? 'disabled' : ''}>${ic('analyze')}<span id="msInfo">${p.measuring ? 'Измеряем…' : 'Измерить размеры'}</span></button>`)}
    ${unknown && !p.measuring ? `<div class="banner">${ic('info')}<span>${unknown} ${plural(unknown, 'программа не сообщает', 'программы не сообщают', 'программ не сообщают')} свой размер. «Измерить размеры» посчитает объём их папок.</span></div>` : ''}
    <table class="tbl"><thead><tr>${th('name', 'Программа')}${th('size', 'Размер', 'r')}${th('date', 'Установлена')}<th>Установка</th><th></th></tr></thead>
    <tbody id="prBody">${rows}</tbody></table><p class="faint" id="prEmpty" hidden style="padding:16px 0">Ничего не найдено</p></div>`;
  const inp = $('#prq');
  inp.oninput = () => { p.q = inp.value; filterPrograms(); };
  filterPrograms();
  acts({
    sort: (el) => { const s = el.dataset.s; if (p.sort === s) p.dir = -p.dir; else { p.sort = s; p.dir = s === 'name' ? 1 : -1; } render(false); },
    measure: async () => {
      p.measuring = true; render(false);
      try { const s = await startJob('measure', '/api/programs/measure'); if (s.result) p.list = s.result; } catch (e) { toast(e.message, true); }
      p.measuring = false; rerender('programs');
    },
    uninstall: async (el) => {
      const x = p.list.find((y) => y.key === el.dataset.key);
      if (!(await ask(`Удалить «${x.name}»?`, `<p>Запустится деинсталлятор программы, дальше следуйте его указаниям.${x.size ? ` Освободится около ${fmtSize(x.size)}.` : ''}</p>`, 'Запустить удаление'))) return;
      try { await api('/api/programs/uninstall', { key: x.key }); toast('Деинсталлятор запущен'); S.prDirty = true; } catch (e) { toast(e.message, true); }
    },
  });
  pageHooks.measure = (s) => { const e = $('#msInfo'); if (e) e.textContent = `Измеряем ${s.done} из ${s.total}…`; };
  ready(m);
};
addEventListener('focus', () => {
  if (S.prDirty && S.page === 'programs') { S.prDirty = false; S.pr.list = null; setTimeout(() => render(false), 600); }
});

// ============================================================ АВТОЗАГРУЗКА
PAGES.startup = (m) => {
  if (!S.st) {
    m.innerHTML = loadingPage();
    api('/api/startup').then((x) => { S.st = x || []; }, (e) => { toast(e.message, true); S.st = []; }).then(() => rerender('startup'));
    return;
  }
  const admin = S.info && S.info.admin;
  const on = S.st.filter((x) => x.enabled).length;
  const rows = S.st.map((x, k) => {
    const locked = x.needsAdmin && !admin;
    return `<tr><td class="w"><input type="checkbox" class="sw" data-act="toggle" data-i="${k}" ${x.enabled ? 'checked' : ''} ${locked ? 'disabled title="Нужны права администратора"' : ''} aria-label="${esc(x.name)}"></td>
      <td><div class="nm"><div class="tx"><div>${esc(x.name)}${x.exists ? '' : ' <span class="faint small">файл не найден</span>'}</div><small title="${esc(x.command)}">${esc(x.command)}</small></div></div></td>
      <td class="w small faint">${esc(x.source)}</td>
      <td class="w small" data-state="${k}">${x.enabled ? 'включено' : '<span class="faint">отключено</span>'}</td>
      <td class="w"><div class="ra">${x.exists && x.path ? `<button class="btn sm icon quiet" title="Показать в Проводнике" data-act="reveal" data-path="${esc(x.path)}">${ic('reveal')}</button>` : ''}</div></td></tr>`;
  }).join('');
  m.innerHTML = `<div class="page wide">${head('Автозагрузка', `Программы, которые стартуют вместе с Windows: включено ${on} из ${S.st.length}. Отключение ничего не удаляет и работает как в Диспетчере задач.`,
    `<button class="btn" data-act="reload">${ic('refresh')}Обновить</button>`)}
    ${adminBanner('элементы для всех пользователей не изменить.')}
    <table class="tbl"><thead><tr><th></th><th>Программа</th><th>Где прописана</th><th>Состояние</th><th></th></tr></thead>
    <tbody>${rows || '<tr><td colspan="5" class="faint">Автозагрузка пуста</td></tr>'}</tbody></table>
    <p class="small faint" style="margin-top:16px;max-width:70ch">Отключайте то, что не нужно сразу после включения: лаунчеры игр, мессенджеры, помощники обновлений. Антивирус и драйверы оставьте.</p></div>`;
  acts({
    reload: () => { S.st = null; render(false); },
    toggle: async (el) => {
      const x = S.st[+el.dataset.i];
      el.disabled = true;
      try {
        await api('/api/startup/toggle', { id: x.id, enabled: el.checked });
        x.enabled = el.checked;
        toast(`${x.name}: ${el.checked ? 'включено' : 'отключено'}`);
      } catch (e) { toast(e.message, true); el.checked = !el.checked; }
      el.disabled = false;
      const c = $(`[data-state="${el.dataset.i}"]`); if (c) c.innerHTML = x.enabled ? 'включено' : '<span class="faint">отключено</span>';
    },
  });
  ready(m);
};

// ============================================================ ИНСТРУМЕНТЫ
async function runToolById(id) {
  if (!S.tools) { try { S.tools = await api('/api/tools'); } catch (e) { toast(e.message, true); return; } }
  const t = S.tools.tools.find((x) => x.id === id);
  if (!t) { toast('Инструмент не найден', true); return; }
  await runTool(t);
}
async function runTool(t) {
  if (t.admin && S.info && !S.info.admin) {
    info('Нужны права администратора', '<p>Этот инструмент меняет системные настройки. Перезапустите программу с правами администратора: ссылка внизу слева.</p>');
    return;
  }
  if (t.confirm && !(await ask(t.name, `<p>${esc(t.confirm)}</p>`, 'Запустить'))) return;
  if (t.open) {
    try { await api('/api/tools/run', { id: t.id }); toast('Открыто: ' + t.name); } catch (e) { toast(e.message, true); }
    return;
  }
  const d = openDialog(`<h2>${esc(t.name)}</h2><p id="tPhase">Выполняется…</p>
    <div class="prog indet" id="tProg" style="margin:14px 0"><i></i></div><div class="console" id="tCon"></div>
    <div class="btns"><button class="btn" id="tBtn">${ic('stop')}Остановить</button></div>`, { wide: true, sticky: true });
  const btn = $('#tBtn', d.el);
  let finished = false;
  btn.onclick = () => (finished ? d.close() : cancelJob('tool'));
  const draw = (s) => {
    const con = $('#tCon', d.el);
    if (!con) return;
    const atBottom = con.scrollHeight - con.scrollTop - con.clientHeight < 30;
    con.innerHTML = s.lines.map((l) => `<div class="${/ошибк|error|fail|не удалось/i.test(l) ? 'err' : /готово|успешно|завершен|completed|success/i.test(l) ? 'ok' : ''}">${esc(l)}</div>`).join('');
    if (atBottom) con.scrollTop = con.scrollHeight;
  };
  dialogHooks.tool = draw;
  let s;
  try { s = await startJob('tool', '/api/tools/run', { id: t.id }); } catch (e) { delete dialogHooks.tool; d.close(); toast(e.message, true); return; }
  delete dialogHooks.tool;
  finished = true;
  draw(s);
  const r = s.result || {};
  $('#tProg', d.el).remove();
  $('#tPhase', d.el).innerHTML = s.err ? esc(s.err) : r.freed > 0 ? `Готово, освобождено ${fmtSize(r.freed)}.` : r.ok === false ? 'Команда завершилась с ошибкой, подробности в журнале.' : 'Готово.';
  btn.textContent = 'Закрыть';
  btn.className = 'btn primary';
  btn.focus();
  await refreshInfo();
  S.tools = null;
  S.hist = null;
  if (S.page === 'tools') render(false);
}

PAGES.tools = (m) => {
  if (!S.tools) {
    m.innerHTML = loadingPage();
    api('/api/tools').then((x) => { S.tools = x; rerender('tools'); }, (e) => toast(e.message, true));
    return;
  }
  const h = S.tools.hiber || {};
  const groups = [['deep', 'Глубокая очистка системы'], ['service', 'Обслуживание'], ['open', 'Системные окна Windows']];
  const rows = (g) => S.tools.tools.filter((t) => t.group === g).map((t) => {
    let extra = '';
    if (t.id.startsWith('hiber_')) extra = h.enabled ? ` Сейчас гибернация включена${h.size ? `, файл занимает ${fmtSize(h.size)}` : ''}.` : ' Сейчас гибернация выключена.';
    const locked = t.admin && S.info && !S.info.admin;
    const tags = [t.caution ? 'осторожно' : '', locked ? 'нужны права' : ''].filter(Boolean).join(', ');
    return `<div class="tool"><div><h3>${esc(t.name)}${tags ? `<span class="tags">${tags}</span>` : ''}</h3><p>${esc(t.desc + extra)}</p></div>
      <button class="btn ${t.open ? '' : 'primary'}" data-act="run" data-id="${t.id}" ${locked ? 'disabled' : ''}>${t.open ? ic('external') + 'Открыть' : ic('play') + 'Запустить'}</button></div>`;
  }).join('');
  m.innerHTML = `<div class="page">${head('Инструменты', 'Средства Windows для глубокой очистки и обслуживания, с понятными описаниями.')}
    ${adminBanner('большинство инструментов недоступно.')}
    ${groups.map(([g, n]) => `<h2 class="sec">${n}</h2><div class="panel">${rows(g)}</div>`).join('')}</div>`;
  acts({ run: (el) => runToolById(el.dataset.id) });
  ready(m);
};

// ============================================================ ИСТОРИЯ
PAGES.history = (m) => {
  if (!S.hist) {
    m.innerHTML = loadingPage();
    api('/api/history').then((h) => { S.hist = (h || []).slice().reverse(); rerender('history'); }, (e) => toast(e.message, true));
    return;
  }
  const h = S.hist;
  const total = sum(h, (e) => e.freed);
  const nCleans = h.filter((e) => e.mode !== 'delete').length;
  const nDel = h.length - nCleans;
  const chart = h.slice(0, 48).reverse();
  const max = Math.max(1, ...chart.map((e) => e.freed));
  const mode = { manual: 'Вручную', auto: 'По расписанию', tool: 'Инструмент', delete: 'Удаление вручную' };
  m.innerHTML = `<div class="page">${head('История', 'Все очистки и сколько места они освободили.', h.length ? `<button class="btn quiet" data-act="clear">${ic('trash')}Очистить историю</button>` : '')}
    ${h.length ? `<div class="score"><b class="num">${fmtSize(total)}</b><span>освобождено${nCleans ? ` за ${fmtNum(nCleans)} ${plural(nCleans, 'очистку', 'очистки', 'очисток')}` : ''}${nDel ? `${nCleans ? ' и' : ''} ${fmtNum(nDel)} ${plural(nDel, 'удаление', 'удаления', 'удалений')} вручную` : ''}</span></div>
      <div class="panel pad"><div class="hist-chart" aria-hidden="true">${chart.map((e) => `<i class="${e.mode === 'auto' ? 'auto' : e.mode === 'tool' ? 'tool' : ''}" style="height:${Math.max(2, pct(e.freed, max))}%" title="${fmtDate(e.time, true)}: ${fmtSize(e.freed)}"></i>`).join('')}</div>
      <div class="tm-legend"><span><i style="background:var(--ink)"></i>вручную</span><span><i style="background:var(--ink-3)"></i>по расписанию</span><span><i style="background:var(--klein)"></i>инструменты</span></div></div>
      <table class="tbl" style="margin-top:28px"><thead><tr><th>Когда</th><th>Как</th><th class="r">Освобождено</th><th class="r">Файлов</th><th>Что очищено</th><th></th></tr></thead><tbody>
        ${h.map((e, k) => `<tr><td class="w num">${fmtDate(e.time, true)}</td><td class="w small">${mode[e.mode] || esc(e.mode)}</td>
          <td class="r w num">${fmtSize(e.freed)}${e.held ? `<small class="held">+${fmtSize(e.held)} в карантине</small>` : ''}</td><td class="r w num faint">${e.files ? fmtNum(e.files) : ''}</td>
          <td class="small faint">${esc((e.items || []).slice(0, 4).map((i) => i.name).join(', '))}${(e.items || []).length > 4 ? ` и ещё ${(e.items || []).length - 4}` : ''}</td>
          <td class="w r">${e.journal ? `<button class="btn sm quiet" data-act="journal" data-k="${k}">${ic('journal')}Файлы</button>` : ''}</td></tr>`).join('')}
      </tbody></table>`
    : `<div class="empty">${ic('history')}<h3>Очисток ещё не было</h3><p>Здесь появится статистика, когда вы что-нибудь очистите.</p><button class="btn primary" data-act="go" data-page="clean">Перейти к очистке</button></div>`}</div>`;
  acts({
    journal: (el) => { const e = h[+el.dataset.k]; if (e) showJournal(e.journal, `${mode[e.mode] || 'Очистка'}, ${fmtDate(e.time, true)}`); },
    clear: async () => {
      if (!(await ask('Очистить историю?', '<p>Статистика освобождённого места будет сброшена.</p>', 'Очистить историю'))) return;
      await api('/api/history/clear', {}).catch((e) => toast(e.message, true));
      S.hist = []; await refreshInfo(); render(false);
    },
  });
  ready(m);
};

// ============================================================ КАРАНТИН
async function loadQuarantine() {
  try {
    S.q = await api('/api/quarantine');
  } catch (e) {
    S.q = { batches: [], size: 0, days: qDays(), error: e.message };
  }
  rerender('quarantine');
}
function qBatchHTML(x) {
  const open = S.qOpen.has(x.id);
  const left = Math.max(0, Math.ceil((new Date(x.expires) - Date.now()) / 864e5));
  const items = open ? (x.items || []).map((e, k) => `<div class="qitem"><span class="qp" title="${esc(e.orig)}">${ic(e.dir ? 'folder' : 'document')}<span class="path">${ltr(e.orig)}</span></span>
      <span class="num faint">${fmtSize(e.size)}</span><button class="btn sm quiet" data-act="qone" data-id="${x.id}" data-k="${k}">${ic('restore')}Вернуть</button></div>`).join('')
    + (x.more ? `<p class="small faint qmore">Показаны первые ${fmtNum(x.items.length)} из ${fmtNum(x.count)}. «Вернуть всё» вернёт все.</p>` : '') : '';
  return `<section class="cat qb" data-id="${x.id}">
    <div class="qhead"><button class="linkbtn qtoggle" data-act="qtoggle" data-id="${x.id}" aria-expanded="${open}">${ic(open ? 'down' : 'chevron')}</button>
      <div class="t"><b>${esc(x.source)}</b><span>${fmtDate(x.time, true)} · ${fmtNum(x.count)} ${plural(x.count, 'объект', 'объекта', 'объектов')} · ${left > 0 ? `удалится через ${left} ${plural(left, 'день', 'дня', 'дней')}` : 'удалится при следующем запуске'}</span></div>
      <b class="num">${fmtSize(x.size)}</b>
      <div class="row-flex"><button class="btn sm" data-act="qall" data-id="${x.id}">${ic('restore')}Вернуть всё</button><button class="btn sm quiet" data-act="qpurge" data-id="${x.id}" title="Удалить насовсем">${ic('trash')}</button></div></div>
    ${open ? `<div class="qitems">${items}</div>` : ''}</section>`;
}
PAGES.quarantine = (m) => {
  if (!S.q) { m.innerHTML = loadingPage(); loadQuarantine(); return; }
  const q = S.q, b = q.batches || [], days = q.days;
  const count = sum(b, (x) => x.count);
  m.innerHTML = `<div class="page">${head('Карантин', days > 0
    ? `Файлы, удалённые в анализе диска и дубликатах, хранятся здесь ${days} ${plural(days, 'день', 'дня', 'дней')}: их можно вернуть на место. Потом они удаляются сами.`
    : 'Карантин выключен в настройках: удалённое вручную сразу уходит в Корзину. Здесь остаются только прежние записи.',
  b.length ? `<button class="btn" data-act="qpurgeAll">${ic('trash')}Очистить карантин</button>` : '')}
    ${q.error ? note('warn', esc(q.error), true) : ''}
    ${b.length ? `<div class="score"><b class="num">${fmtSize(q.size)}</b><span>в карантине, ${fmtNum(count)} ${plural(count, 'объект', 'объекта', 'объектов')}. Это место освободится, когда карантин очистится.</span></div>
      ${b.map(qBatchHTML).join('')}`
    : `<div class="empty">${ic('quarantine')}<h3>Карантин пуст</h3><p>Когда вы удаляете файлы в анализе диска или дубликатах, они сначала попадают сюда. Кэш и временные файлы при очистке удаляются сразу.</p></div>`}</div>`;
  const find = (id) => b.find((x) => x.id === id);
  const restore = async (id, paths) => {
    let r;
    try {
      r = await api('/api/quarantine/restore', { id, paths });
    } catch (e) { toast(e.message, true); return; }
    const n = (r.restored || []).length, bad = Object.keys(r.failed || {}).length, moved = Object.keys(r.renamed || {}).length;
    toast(bad ? `Возвращено ${n}, не удалось ${bad}: ${Object.values(r.failed)[0]}` : `Возвращено на место: ${fmtNum(n)}${moved ? `, ${moved} с новым именем, потому что место было занято` : ''}`, bad > 0);
    S.q = null;
    refreshInfo(); render(false);
  };
  acts({
    qtoggle: (el) => { const id = el.dataset.id; if (S.qOpen.has(id)) S.qOpen.delete(id); else S.qOpen.add(id); render(false); },
    qall: (el) => restore(el.dataset.id, []),
    qone: (el) => { const x = find(el.dataset.id), e = x && x.items[+el.dataset.k]; if (e) restore(x.id, [e.orig]); },
    qpurge: async (el) => {
      const x = find(el.dataset.id);
      if (!x || !(await ask('Удалить насовсем?', `<p>${fmtNum(x.count)} ${plural(x.count, 'объект', 'объекта', 'объектов')} из «${esc(x.source)}» будут удалены без возможности вернуть. Освободится ${fmtSize(x.size)}.</p>`, 'Удалить насовсем'))) return;
      try { const r = await api('/api/quarantine/purge', { id: x.id }); toast(`Освобождено ${fmtSize(r.freed)}`); } catch (e) { toast(e.message, true); }
      S.q = null; refreshInfo(); render(false);
    },
    qpurgeAll: async () => {
      if (!(await ask('Очистить карантин?', `<p>Всё содержимое карантина будет удалено без возможности вернуть. Освободится ${fmtSize(q.size)}.</p>`, 'Очистить карантин'))) return;
      try { const r = await api('/api/quarantine/purge', { all: true }); toast(`Освобождено ${fmtSize(r.freed)}`); } catch (e) { toast(e.message, true); }
      S.q = null; refreshInfo(); render(false);
    },
  });
  ready(m);
};

// ============================================================ НАСТРОЙКИ
function applyTheme(t) {
  document.documentElement.dataset.theme = t === 'light' || t === 'auto' ? t : 'dark';
  if (S.page === 'analyze') requestAnimationFrame(drawTreemap);
}
async function saveSettings(patch) {
  const prev = Object.assign({}, S.settings);
  S.settings = Object.assign({}, S.settings, patch);
  try {
    await api('/api/settings', S.settings);
    return true;
  } catch (e) {
    S.settings = prev;
    toast(e.message, true);
    return false;
  }
}
PAGES.settings = (m) => {
  const s = S.settings;
  if (!s) { m.innerHTML = loadingPage(); return; }
  const sc = S.schedDraft || s.schedule || { mode: 'off', time: '12:00', day: 'SUN' };
  const days = [['MON', 'понедельник'], ['TUE', 'вторник'], ['WED', 'среда'], ['THU', 'четверг'], ['FRI', 'пятница'], ['SAT', 'суббота'], ['SUN', 'воскресенье']];
  const seg = (name, cur, opts) => `<div class="seg">${opts.map(([v, n]) => `<button class="${cur === v ? 'on' : ''}" data-act="${name}" data-v="${v}">${n}</button>`).join('')}</div>`;
  const i = S.info || {};
  m.innerHTML = `<div class="page">${head('Настройки')}
    <h2 class="sec">Внешний вид</h2>
    <div class="panel"><div class="setting"><div><h3>Тема</h3><p>Тёмная, светлая или как в Windows.</p></div>${seg('theme', s.theme || 'dark', [['dark', 'Тёмная'], ['light', 'Светлая'], ['auto', 'Как в Windows']])}</div>
    <div class="setting"><div><h3>Режим</h3><p>«Простой» — основные разделы и только безопасные пункты. «Эксперт» — все разделы, необратимые пункты, папки каждого правила и поиск.</p></div>${seg('xmode', s.expert ? 'expert' : 'simple', [['simple', 'Простой'], ['expert', 'Эксперт']])}</div></div>

    <h2 class="sec">Очистка</h2>
    <div class="panel"><div class="setting"><div><h3>Не трогать свежие временные файлы</h3><p>Их может использовать идущая сейчас установка или программа.</p></div>
      <select class="input" id="recent">${[[0, 'не пропускать'], [6, 'моложе 6 часов'], [24, 'моложе суток'], [72, 'моложе 3 дней'], [168, 'моложе недели']].map(([v, n]) => `<option value="${v}" ${s.skipRecentHours === v ? 'selected' : ''}>${n}</option>`).join('')}</select></div>
    <div class="setting"><div><h3>Карантин для удалённого вручную</h3><p>Файлы, удалённые в анализе диска и дубликатах, сначала хранятся в карантине, и их можно вернуть. Кэш и временные файлы при очистке удаляются сразу.</p></div>
      ${seg('qdays', String(qDays()), [['0', 'Не хранить'], ['3', '3 дня'], ['7', '7 дней'], ['30', '30 дней']])}</div>
    <div class="setting"><div><h3>Удалять занятые файлы при перезагрузке</h3><p>Если файл держит запущенная программа, Windows удалит его при следующем включении. Нужны права администратора.</p></div>
      <input type="checkbox" class="sw" data-act="dor" ${s.deleteOnReboot ? 'checked' : ''} aria-label="Удалять занятые файлы при перезагрузке"></div>
    <div class="setting"><div><h3>Запрашивать права администратора при запуске</h3><p>Без них не очищаются системные папки: кэш обновлений, Windows\\Temp, дампы памяти. Права получает отдельный помощник, окно программы работает без них. Если выключить, права можно получить позже кнопкой «Получить права».</p></div>
      <input type="checkbox" class="sw" data-act="elev" ${s.autoElevate ? 'checked' : ''} aria-label="Запрашивать права администратора"></div>
    <div class="setting col"><div><h3>Исключения</h3><p>Эти папки и файлы никогда не удаляются при очистке.</p>
      <div class="excl"><input class="input" id="exclIn" placeholder="Например, C:\\Users\\Имя\\AppData\\Local\\Temp\\Важное"><button class="btn" data-act="exclPick">${ic('folder')}Обзор…</button><button class="btn" data-act="exclAdd">${ic('plus')}Добавить</button></div>
      <div class="excl-list">${(s.excludes || []).map((e, k) => `<div>${ic('shield')}<span title="${esc(e)}">${esc(e)}</span><button class="btn sm icon quiet" data-act="exclDel" data-k="${k}" title="Убрать">${ic('x')}</button></div>`).join('')}</div></div></div></div>

    <h2 class="sec">Автоочистка по расписанию</h2>
    <div class="panel"><div class="setting"><div><h3>Регулярная очистка</h3><p>Планировщик Windows запускает очистку отмеченных пунктов в фоне, без пунктов «осторожно». Окно не открывается.</p></div>
      ${seg('smode', sc.mode || 'off', [['off', 'Выключена'], ['daily', 'Каждый день'], ['weekly', 'Раз в неделю']])}</div>
    ${sc.mode && sc.mode !== 'off' ? `<div class="setting"><div><h3>Время запуска</h3><p>Если компьютер был выключен, очистка пройдёт при следующей возможности.</p></div>
      <div class="row-flex">${sc.mode === 'weekly' ? `<select class="input" id="sday">${days.map(([v, n]) => `<option value="${v}" ${sc.day === v ? 'selected' : ''}>${n}</option>`).join('')}</select>` : ''}
      <input type="time" class="input" id="stime" value="${esc(sc.time || '12:00')}"><button class="btn primary" data-act="ssave">Сохранить</button></div></div>` : ''}</div>

    <h2 class="sec">Проверка программы</h2>
    <div class="panel"><div class="setting"><div><h3>Полная самопроверка</h3><p>Проверяет все части Prostor на этом компьютере: поиск мусора, анализ диска, дубликаты, реестр и интерфейс. Удаляет только собственные тестовые файлы. Отчёт откроется в браузере.</p></div>
      <button class="btn" data-act="selftest">${ic('check')}Запустить проверку</button></div>
    <div class="setting"><div><h3>Журнал работы</h3><p>${esc(i.dataDir || '')}</p></div><button class="btn" data-act="open" data-path="${esc(i.dataDir || '')}">${ic('folder')}Открыть папку</button></div></div>

    <h2 class="sec">О программе</h2>
    <div class="panel pad row-flex" style="gap:16px"><img src="icon.png" width="44" height="44" alt="">
      <div><b>Prostor ${esc(i.version || '')}</b><p class="small muted">Очистка и анализ диска для Windows 10 и 11.</p>
      ${S.rules ? `<p class="small muted">Правила очистки: ${fmtNum(S.rules.builtin)} собственных и ${fmtNum(S.rules.imported)} из базы <a href="https://github.com/MoscaDotTo/Winapp2" target="_blank" rel="noopener">Winapp2</a>${S.rules.w2Version ? ' (версия ' + esc(S.rules.w2Version) + ')' : ''}, лицензия CC BY-SA 4.0. Из базы взяты только кэш, журналы, временные файлы и дампы.</p>` : ''}</div></div></div>`;
  acts({
    theme: async (el) => { applyTheme(el.dataset.v); await saveSettings({ theme: el.dataset.v, themeChosen: true }); render(false); },
    xmode: (el) => setMode(el.dataset.v === 'expert'),
    dor: (el) => saveSettings({ deleteOnReboot: el.checked }),
    qdays: async (el) => { if (await saveSettings({ quarantineDays: +el.dataset.v })) { S.q = null; render(false); } },
    elev: (el) => saveSettings({ autoElevate: el.checked }),
    exclAdd: async () => {
      const v = $('#exclIn').value.trim();
      if (v && await saveSettings({ excludes: (s.excludes || []).concat([v]) })) render(false);
    },
    exclPick: async () => { const p = await pickFolder('Папка, которую нельзя очищать'); if (p) $('#exclIn').value = p; },
    exclDel: async (el) => { const ex = (s.excludes || []).slice(); ex.splice(+el.dataset.k, 1); if (await saveSettings({ excludes: ex })) render(false); },
    smode: async (el) => {
      const v = el.dataset.v;
      if (v === 'off') {
        S.schedDraft = null;
        if ((s.schedule || {}).mode && s.schedule.mode !== 'off' && await saveSettings({ schedule: Object.assign({}, s.schedule, { mode: 'off' }) })) toast('Автоочистка выключена');
        render(false);
        return;
      }
      S.schedDraft = Object.assign({}, sc, { mode: v });
      render(false);
    },
    ssave: async () => {
      const next = Object.assign({}, sc, { time: $('#stime').value || '12:00' });
      const d = $('#sday'); if (d) next.day = d.value;
      if (await saveSettings({ schedule: next })) { S.schedDraft = null; toast('Расписание сохранено'); }
      render(false);
    },
    selftest: async (el) => {
      el.disabled = true;
      try { await api('/api/selftest', {}); toast('Проверка запущена. Отчёт откроется, когда она закончится.'); } catch (e) { toast(e.message, true); }
      setTimeout(() => { el.disabled = false; }, 5000);
    },
  });
  $('#recent').onchange = (e) => saveSettings({ skipRecentHours: +e.target.value });
  ready(m);
};

// ============================================================ запуск
async function restoreJobs() {
  // окно перезагрузили посреди работы: подхватываем задачи и результаты
  const kinds = ['analyze', 'dupes', 'advisor', 'clean'];
  const snaps = await Promise.all(kinds.map((k) => api('/api/job?kind=' + k).catch(() => null)));
  const [an, du, adv, cl] = snaps;
  if (adv && adv.exists) {
    if (adv.running) {
      S.advising = true;
      watch('advisor').then((s) => { S.advising = false; if (s.result) S.advice = s.result; rerender('overview', 'advice'); });
    } else if (adv.result) S.advice = adv.result;
  }
  if (an && an.exists) {
    if (an.running) {
      S.an = { root: '', running: true, tab: 'map', sel: new Set(), shown: 100 };
      watch('analyze').then(async (s) => {
        S.an.running = false;
        if (s.result) { S.an.summary = s.result; S.an.root = s.result.root; await loadAnalysis(s.result.root); } else rerender('analyze');
      });
    } else if (an.result) {
      S.an = { root: an.result.root, summary: an.result, tab: 'map', sel: new Set(), shown: 100 };
      loadAnalysis(an.result.root);
    }
  }
  if (du && du.exists && !du.running && du.result) {
    S.du = { root: du.result.root, minMB: (S.settings && S.settings.minDupMB) || 1, sel: new Set(), shown: 50 };
    setDupes(du.result);
  }
  if (cl && cl.running) {
    S.cleaning = true;
    watch('clean').then(async (s) => { S.cleaning = false; await refreshInfo(); rerender(); if (s.result) showCleanResult(s.result); runScan(); });
  }
}

async function boot() {
  buildNav();
  await refreshInfo();
  if (!S.info) {
    $('#main').innerHTML = `<div class="page"><div class="empty">${ic('warn')}<h3>Нет связи с программой</h3><p>Закройте окно и запустите Prostor заново.</p></div></div>`;
    return;
  }
  applyTheme(S.settings.theme);
  if (!location.hash) history.replaceState(null, '', location.pathname + location.search + '#overview');
  render(true);
  try { await loadTargets(); } catch (e) { toast(e.message, true); }
  await restoreJobs().catch(() => {});
  render(false);
  await runScan();
  if (!S.advice && !S.advising) runAdvisor();
}

window.__prostor = { S, go, render, drawTreemap, fmtSize, squarify };
boot();
