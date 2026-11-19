'use strict';
/*
 * Значки Prostor — собственный набор.
 * Сетка 20×20, линия 1,5, прямые концы и углы. У большинства значков есть
 * закрашенный «блок» (class="b") — единица данных на диске. У активного
 * раздела меню блок подсвечивается акцентным цветом.
 */
const ICONS = {
  // ---- разделы
  overview: '<path d="M2 15.25h16M5.75 12v3.25M8.75 13v2.25"/><rect class="b" x="11" y="8.25" width="5.5" height="5.5"/>',
  advice: '<path d="M8.5 5h8.75M8.5 10h8.75M8.5 15h6"/><rect class="b" x="2.75" y="3" width="4" height="4"/><rect x="3.5" y="8.75" width="2.5" height="2.5"/><rect x="3.5" y="13.75" width="2.5" height="2.5"/>',
  clean: '<path d="M14.25 2.25l-2.5 6.5"/><path class="b" d="M7 8.75h7.75l1.25 3.25H5.75z"/><path d="M6.5 12.5l-1 5M9.25 12.5l-.25 5M12.25 12.5l.25 5M15.25 12.5l1 5"/>',
  analyze: '<rect x="2.75" y="2.75" width="14.5" height="14.5"/><path d="M10.25 2.75v14.5M10.25 10.25h7M2.75 12.25h7.5"/><rect class="b" x="4.5" y="4.5" width="4" height="6"/>',
  dupes: '<rect x="2.75" y="6.75" width="10.5" height="10.5"/><rect x="6.75" y="2.75" width="10.5" height="10.5"/><rect class="b" x="6.75" y="6.75" width="6.5" height="6.5"/>',
  programs: '<rect x="3" y="3" width="5.75" height="5.75"/><rect x="11.25" y="3" width="5.75" height="5.75"/><rect x="3" y="11.25" width="5.75" height="5.75"/><rect class="b" x="11.25" y="11.25" width="5.75" height="5.75"/>',
  startup: '<path d="M3.75 3.5v13"/><path class="b" d="M7.5 4.25L16.5 10l-9 5.75z"/>',
  tools: '<path d="M10 2.5l6.5 3.75v7.5L10 17.5l-6.5-3.75v-7.5z"/><rect class="b" x="7.75" y="7.75" width="4.5" height="4.5"/>',
  history: '<rect x="2.75" y="2.75" width="14.5" height="14.5"/><path d="M10 5.5V10h3.75"/><rect class="b" x="8.75" y="8.75" width="2.5" height="2.5"/>',
  quarantine: '<path d="M2.25 3.5h15.5v3.75H2.25z"/><path d="M3.5 7.25v10h13v-10"/><rect class="b" x="7.75" y="10" width="4.5" height="4.5"/>',
  settings: '<path d="M2.5 6.25h8.25M15.25 6.25h2.25M2.5 13.75h2.25M9.25 13.75h8.25"/><rect class="b" x="10.75" y="4" width="4.5" height="4.5"/><rect x="4.75" y="11.5" width="4.5" height="4.5"/>',

  // ---- действия
  chevron: '<path d="M7.5 4.5L13 10l-5.5 5.5"/>',
  down: '<path d="M4.5 7.5L10 13l5.5-5.5"/>',
  up: '<path d="M10 16.5V4M4.75 9.25L10 4l5.25 5.25"/>',
  restore: '<path d="M7 3.75L3.5 7.25 7 10.75"/><path d="M3.5 7.25h8.25a4.5 4.5 0 0 1 0 9H6.5"/>',
  journal: '<rect x="3.75" y="2.75" width="12.5" height="14.5"/><path d="M6.75 6.5h6.5M6.75 10h6.5M6.75 13.5h3.5"/>',
  back: '<path d="M16 10H4M9.25 4.75L4 10l5.25 5.25"/>',
  x: '<path d="M4.75 4.75l10.5 10.5M15.25 4.75L4.75 15.25"/>',
  check: '<path d="M3.75 10.5l4 4 8.5-9"/>',
  minus: '<path d="M4.5 10h11"/>',
  plus: '<path d="M10 3.75v12.5M3.75 10h12.5"/>',
  search: '<rect x="2.75" y="2.75" width="10" height="10"/><path d="M12.75 12.75l4.5 4.5"/>',
  refresh: '<path d="M16.25 9.25A6.25 6.25 0 1 0 14.5 14"/><path class="b" d="M13.25 4.25h4v4z"/>',
  play: '<path class="b" d="M6 4L16 10 6 16z"/>',
  stop: '<rect class="b" x="5" y="5" width="10" height="10"/>',
  external: '<path d="M11 3h6v6M17 3l-7.5 7.5M14.75 12v5.25H2.75V5.25H8"/>',
  reveal: '<path d="M2.75 4.25h5l1.5 2h8v10.5H2.75z"/><path d="M8 11.5h6M11.25 8.75L14 11.5l-2.75 2.75"/>',
  trash: '<path d="M2.75 5h14.5M7.25 5V2.75h5.5V5M4.5 5l1 12.25h9L15.5 5"/><rect class="b" x="8.25" y="8.5" width="3.5" height="5.5"/>',
  folder: '<path d="M2.75 4.25h5l1.5 2h8v10.5H2.75z"/><rect class="b" x="5" y="9" width="3.5" height="3.5"/>',
  disk: '<rect x="2.25" y="5.5" width="15.5" height="9"/><path d="M10.5 10h4.75"/><rect class="b" x="4.5" y="8" width="3" height="4"/>',
  shield: '<path d="M10 2.5l6.75 2.5v5c0 4-2.9 6.6-6.75 7.75C6.15 16.6 3.25 14 3.25 10V5z"/><rect class="b" x="8" y="7.75" width="4" height="4"/>',
  warn: '<path d="M10 2.75l7.75 14.25H2.25z"/><path d="M10 8v4"/><rect class="b" x="9.1" y="13.6" width="1.8" height="1.8"/>',
  info: '<rect x="2.75" y="2.75" width="14.5" height="14.5"/><path d="M10 9v5.25"/><rect class="b" x="9.1" y="5.6" width="1.8" height="1.8"/>',
  moon: '<path d="M15.75 12.75A6.75 6.75 0 0 1 7.25 4.25a6.75 6.75 0 1 0 8.5 8.5z"/>',
  memory: '<rect x="2.25" y="6" width="15.5" height="8"/><path d="M5.5 14v2.5M10 14v2.5M14.5 14v2.5"/><rect class="b" x="5" y="8.25" width="3.5" height="3.5"/><rect x="11.5" y="8.25" width="3.5" height="3.5"/>',
  download: '<path d="M10 2.75v9.5M5.75 8L10 12.25 14.25 8M2.75 13.5v3.75h14.5V13.5"/>',
  send: '<path d="M17.5 2.5L2.5 9l6 2.5 2.5 6z"/><path d="M8.5 11.5l4-4"/>',
  layers: '<path d="M10 2.5l7.5 4-7.5 4-7.5-4z"/><path d="M2.5 10.25l7.5 4 7.5-4M2.5 14l7.5 4 7.5-4"/>',
  auto: '<path d="M16.25 8.5A6.5 6.5 0 0 0 4.5 6.25M3.75 11.5a6.5 6.5 0 0 0 11.75 2.25"/><path class="b" d="M3 3.25v4.5h4.5zM17 16.75v-4.5h-4.5z"/>',
  user: '<rect x="6.5" y="2.75" width="7" height="7"/><path d="M3.25 17.25c.5-3.75 3.25-5.5 6.75-5.5s6.25 1.75 6.75 5.5"/>',
  power: '<path d="M10 2.5v7"/><path d="M6 5a6.25 6.25 0 1 0 8 0"/>',
  pin: '<rect class="b" x="7.5" y="7.5" width="5" height="5"/>',

  // ---- типы файлов
  video: '<rect x="2.25" y="4.25" width="15.5" height="11.5"/><path d="M2.25 7.5h15.5M2.25 12.5h15.5"/><path class="b" d="M8.5 8.5l3.5 1.5-3.5 1.5z"/>',
  image: '<rect x="2.75" y="3.75" width="14.5" height="12.5"/><path d="M2.75 14l4.5-4.5 4 4 2-2 4 4"/><rect class="b" x="12" y="6" width="2.75" height="2.75"/>',
  audio: '<path d="M7.75 15V4.25l8.5-1.5v10.5"/><rect class="b" x="3.75" y="12.75" width="4" height="4"/><rect class="b" x="12.25" y="11" width="4" height="4"/>',
  archive: '<rect x="3" y="2.75" width="14" height="14.5"/><path d="M9 2.75v2M11 4.75v2M9 6.75v2"/><rect class="b" x="8.25" y="10" width="3.5" height="4"/>',
  diskimage: '<circle cx="10" cy="10" r="7.25"/><rect class="b" x="8.25" y="8.25" width="3.5" height="3.5"/>',
  program: '<rect x="2.25" y="3.25" width="15.5" height="13.5"/><path d="M2.25 7h15.5"/><rect class="b" x="4.5" y="9.25" width="4.5" height="4.5"/>',
  document: '<path d="M4 2.75h8l4 4v10.5H4z"/><path d="M12 2.75v4h4M6.75 10.75h6.5M6.75 13.75h4.5"/>',
  code: '<path d="M7 5.5L2.75 10 7 14.5M13 5.5l4.25 4.5L13 14.5"/><rect class="b" x="8.75" y="8.75" width="2.5" height="2.5"/>',
  model: '<path d="M10 2.5l7 3.75v7.5L10 17.5l-7-3.75v-7.5z"/><path d="M3 6.25L10 10l7-3.75M10 10v7.5"/>',
  data: '<path d="M3.5 4.5c0-1 2.9-1.75 6.5-1.75s6.5.75 6.5 1.75v11c0 1-2.9 1.75-6.5 1.75S3.5 16.5 3.5 15.5z"/><path d="M3.5 4.5c0 1 2.9 1.75 6.5 1.75s6.5-.75 6.5-1.75M3.5 10c0 1 2.9 1.75 6.5 1.75s6.5-.75 6.5-1.75"/>',
  other: '<path d="M4 2.75h8l4 4v10.5H4z"/><path d="M12 2.75v4h4"/><rect class="b" x="7" y="10.5" width="3.5" height="3.5"/>',

  // ---- категории очистки
  windows: '<rect x="2.75" y="2.75" width="6.25" height="6.25"/><rect x="11" y="2.75" width="6.25" height="6.25"/><rect x="2.75" y="11" width="6.25" height="6.25"/><rect class="b" x="11" y="11" width="6.25" height="6.25"/>',
  globe: '<circle cx="10" cy="10" r="7.25"/><path d="M2.75 10h14.5M10 2.75c2 2 3 4.4 3 7.25s-1 5.25-3 7.25c-2-2-3-4.4-3-7.25s1-5.25 3-7.25z"/>',
  box: '<path d="M2.75 6.5L10 2.75l7.25 3.75v7.25L10 17.5l-7.25-3.75z"/><path d="M2.75 6.5L10 10.25l7.25-3.75M10 10.25v7.25"/>',
  game: '<path d="M2.75 6.25h14.5v8.5H2.75z"/><path d="M5.5 10.5h3.5M7.25 8.75v3.5"/><rect class="b" x="12.5" y="9" width="2.5" height="2.5"/>',
  terminal: '<rect x="2.25" y="3.25" width="15.5" height="13.5"/><path d="M5.25 7.5L8.25 10l-3 2.5"/><rect class="b" x="10" y="11.75" width="4.5" height="1.75"/>',
  eye: '<path d="M1.75 10S5 4.75 10 4.75 18.25 10 18.25 10 15 15.25 10 15.25 1.75 10 1.75 10z"/><rect class="b" x="8" y="8" width="4" height="4"/>',
};

/** SVG-разметка значка. cls — дополнительные классы. */
function ic(name, cls) {
  return '<svg class="i' + (cls ? ' ' + cls : '') + '" viewBox="0 0 20 20" aria-hidden="true">' + (ICONS[name] || ICONS.pin) + '</svg>';
}
