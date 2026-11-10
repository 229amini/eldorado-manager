'use strict';
// The dashboard view. The C# host owns every offer and every edit; this file draws what it is told
// and sends back what the seller did. Opened in a plain browser it runs on sample.js instead.

const host = window.chrome && window.chrome.webview;
const PAGE = 24;
const SORTS = [['attention', 'Needs attention'], ['site', 'Site place'], ['price', 'Price rank'], ['cost', 'Your price'], ['stock', 'Stock'], ['sold', 'Sold'], ['name', 'Name']];
const VIEWS = [['live', 'Live'], ['paused', 'Paused'], ['closed', 'Closed']];
const PERIODS = [7, 30, 90];
const EXTEND_TIMES = [['Minute5', '5 min'], ['Minute15', '15 min'], ['Minute30', '30 min'], ['Hour1', '1 hour'], ['Hour3', '3 hours'], ['Hour6', '6 hours']];
const EXTEND_REASONS = [['WaitingForBuyer', 'Waiting for the buyer'], ['InGameCoordination', 'Meeting in game'], ['PreparationDelay', 'Still preparing'], ['TechnicalIssue', 'Technical issue']];
const PROMISES = [['Minute5', '5 min'], ['Minute20', '20 min'], ['Hour1', '1 hour'], ['Hour2', '2 hours'], ['Hour3', '3 hours'], ['Hour5', '5 hours'], ['Hour8', '8 hours'], ['Hour12', '12 hours'], ['Day1', '1 day'], ['Day2', '2 days'], ['Day3', '3 days'], ['Day7', '7 days']];
const MAX_BULK = 50;
const SVG = 'http://www.w3.org/2000/svg';
// Currency offers carry no image of their own.
const COIN = 'data:image/svg+xml,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 200"><rect width="200" height="200" fill="#16181d"/><circle cx="100" cy="100" r="66" fill="#f2b632"/><circle cx="100" cy="100" r="50" fill="none" stroke="#a8740f" stroke-width="7"/><rect x="92" y="68" width="16" height="64" fill="#a8740f"/></svg>');

let state = { theme: 'dark', sort: 'attention', offers: [], orders: [], notices: [], sales: { days: 30, ready: false }, best: [], back: [], order: null };
let tab = 'home';
let view = 'live';
let filter = '';
let selected = null;
let pickedOrder = null;
let ordersShown = 'open';
let marketShown = 'missing';
let bulk = null;      // { game, percent, promise, ask } while the bulk panel is open
let asking = null;    // the order action waiting for its second click: 'deliver' or 'extend'
let settingPrice = null; // the offer whose Home "Set" waits for its second click
let extendTime = 'Minute15';
let extendReason = 'WaitingForBuyer';
let editing = null;   // the field being typed in, which a redraw must leave alone
let updateNotes = false;   // the update's list of changes, opened from the strip
let drag = null;      // { id, price, held }: a price picked on the sales line, not sent until Save price
let fresh = null;     // a field just focused by the pointer, whose selection the click would clear

const send = (type, data) => host ? host.postMessage(Object.assign({ type }, data)) : window.sampleSend && window.sampleSend(type, data);

// ---------- elements ----------

function h(tag, attrs, ...kids) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs || {})) {
    if (value === false || value === null || value === undefined) continue;
    if (key === 'value') node.value = value;
    node.setAttribute(key, value === true ? '' : value);
  }
  for (const kid of kids.flat(Infinity)) {
    if (kid === null || kid === undefined || kid === false) continue;
    node.append(kid.nodeType ? kid : document.createTextNode(String(kid)));
  }
  return node;
}

function s(tag, attrs, ...kids) {
  const node = document.createElementNS(SVG, tag);
  for (const [key, value] of Object.entries(attrs || {})) node.setAttribute(key, value);
  node.append(...kids);
  return node;
}

const icon = path => s('svg', { viewBox: '0 0 24 24', class: 'icon', 'aria-hidden': 'true' }, s('path', { d: path }));
const bell = () => icon('M12 2a7 7 0 0 0-7 7v5l-2 3h18l-2-3V9a7 7 0 0 0-7-7zm0 20a3 3 0 0 0 3-3H9a3 3 0 0 0 3 3z');
const logo = () => s('svg', { viewBox: '0 0 256 256', class: 'logo', 'aria-hidden': 'true' },
  s('path', { d: 'M150 80H102a25 25 0 0 0 0 50h52a25 25 0 0 1 0 50H76', class: 'glyph' }),
  s('circle', { cx: 200, cy: 66, r: 22, class: 'ping' }));
const plus = () => icon('M11 5h2v6h6v2h-6v6h-2v-6H5v-2h6z');
const minus = () => icon('M5 11h14v2H5z');
const moon = () => icon('M20 14.5A8.5 8.5 0 0 1 9.5 4a8.5 8.5 0 1 0 10.5 10.5z');
const sun = () => icon('M12 7a5 5 0 1 0 0 10 5 5 0 0 0 0-10zM11 1h2v3h-2zM11 20h2v3h-2zM1 11h3v2H1zM20 11h3v2h-3zM4.2 2.8l2.1 2.1-1.4 1.4-2.1-2.1zM19.1 17.7l2.1 2.1-1.4 1.4-2.1-2.1zM2.8 19.8l2.1-2.1 1.4 1.4-2.1 2.1zM17.7 4.9l2.1-2.1 1.4 1.4-2.1 2.1z');
const okMark = () => icon('M9.5 16.2 5.3 12l-1.4 1.4 5.6 5.6L20.5 8 19 6.6z');
const warn = () => icon('M12 3 1.8 20.5h20.4zm-1 6h2v6h-2zm0 7.5h2v2h-2z');
const cross = () => icon('M6.4 5 12 10.6 17.6 5 19 6.4 13.4 12l5.6 5.6-1.4 1.4-5.6-5.6L6.4 19 5 17.6 10.6 12 5 6.4z');

const triangle = up => s('svg', { viewBox: '0 0 10 8', class: 'tri', 'aria-hidden': 'true' },
  s('path', { d: up ? 'M0 8 5 0l5 8z' : 'M0 0l5 8 5-8z' }));

// Keeps focus, scroll and loaded pictures across redraws, which plain replacement would lose.
function morph(old, next) {
  if (old.nodeType !== next.nodeType || old.nodeName !== next.nodeName) {
    old.replaceWith(next);
    return;
  }
  if (old.nodeType === 3) {
    if (old.nodeValue !== next.nodeValue) old.nodeValue = next.nodeValue;
    return;
  }
  if (old.nodeType !== 1) return;
  const flipped = old.getAttribute('data-flip') !== next.getAttribute('data-flip') && old.hasAttribute('data-flip');
  for (const { name } of [...old.attributes]) if (!next.hasAttribute(name)) old.removeAttribute(name);
  for (const { name, value } of [...next.attributes]) if (old.getAttribute(name) !== value) old.setAttribute(name, value);
  if (old.nodeName === 'INPUT' && old !== editing && old.value !== next.value) old.value = next.value;
  if (flipped) flip(old);

  const keyed = new Map();
  for (const kid of old.childNodes) if (kid.nodeType === 1 && kid.dataset.key) keyed.set(kid.dataset.key, kid);
  let at = old.firstChild;
  for (const kid of [...next.childNodes]) {
    const key = kid.nodeType === 1 && kid.dataset.key;
    const match = key ? keyed.get(key) : at;
    if (!match) {
      old.insertBefore(kid, at);
      continue;
    }
    if (match !== at) old.insertBefore(match, at);
    at = match.nextSibling;
    morph(match, kid);
  }
  while (at) {
    const gone = at;
    at = at.nextSibling;
    gone.remove();
  }
  if (old.nodeName === 'SELECT' && old.dataset.value !== undefined) old.value = old.dataset.value;
}

// A place that changes turns over like a departures board, the one flourish in the dashboard.
function flip(node) {
  if (matchMedia('(prefers-reduced-motion: reduce)').matches) return;
  node.animate([{ transform: 'rotateX(-88deg)' }, { transform: 'rotateX(0)' }],
    { duration: 420, easing: 'cubic-bezier(.16,1,.3,1)' });
}

// ---------- derived values ----------

const isLive = o => o.listed || o.stateText === 'Offline';
const isClosed = o => o.stateText === 'Closed';
const inView = o => ({
  live: isLive(o),
  paused: o.stateText === 'Paused',
  closed: isClosed(o),
  slipped: o.move < 0,
  empty: isLive(o) && Number(o.stockDraft) === 0,
}[view]);

function shown() {
  const word = filter.toLowerCase();
  return state.offers.filter(o => inView(o) && (!word || o.title.toLowerCase().includes(word) || o.kind.toLowerCase().includes(word)));
}

function current(list) {
  return list.find(o => o.id === selected) || list[0] || null;
}

const openOrders = () => state.orders.filter(o => o.open);
const shownOrders = () => ordersShown === 'open' ? openOrders() : state.orders.filter(o => o.placed >= Date.now() - 30 * 864e5);

function ago(stamp) {
  if (!stamp) return '';
  const seconds = Math.max(0, Math.round((Date.now() - stamp) / 1000));
  return seconds < 60 ? seconds + ' s' : Math.floor(seconds / 60) + ' min';
}

function clock(order) {
  if (!order.deadline) return '';
  const left = order.deadline - Date.now() / 1000;
  const total = Math.abs(Math.trunc(left));
  const pad = n => String(n).padStart(2, '0');
  const days = Math.floor(total / 86400), hours = Math.floor(total % 86400 / 3600), minutes = Math.floor(total % 3600 / 60), seconds = total % 60;
  const text = days > 0 ? `${days}d ${hours}h` : hours > 0 ? `${hours}:${pad(minutes)}:${pad(seconds)}` : `${pad(minutes)}:${pad(seconds)}`;
  return left < 0 ? '+' + text : text;
}

function fuseShare(order) {
  if (!order.window || !order.deadline) return 0;
  return Math.max(0, Math.min((order.deadline - Date.now() / 1000) / order.window, 1));
}

const dayOf = stamp => new Date(stamp).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });
const money = value => value.toFixed(2);
const fit = price => +price.toFixed(price >= 0.01 ? 2 : 5);   // Eldorado's limit on decimals
const promise = code => {
  const match = /^(Minute|Hour|Day)(\d+)$/.exec(code || '');
  if (!match) return code === 'Instant' ? 'Instant' : '';
  const unit = { Minute: 'min', Hour: match[2] === '1' ? 'hour' : 'hours', Day: match[2] === '1' ? 'day' : 'days' }[match[1]];
  return `${match[2]} ${unit}`;
};

const onPageOne = o => o.site !== null && o.site <= PAGE;
const siteNote = o => o.site === null ? (o.listed ? 'Checking' : '') : 'Page ' + (Math.floor((o.site - 1) / PAGE) + 1);
const placeText = o => o.site !== null ? String(o.site) : o.listed ? '...' : o.stateText;
const rowOf = node => {
  const row = node.closest('[data-id]');
  return row ? row.dataset.id : selected;
};

// ---------- shared pieces ----------

function picture(o, cls) {
  const src = o.picture || (o.kind === 'Currency' ? COIN : '');
  return src ? h('img', { class: cls, src, alt: '', draggable: 'false' }) : h('span', { class: cls + ' none' });
}

function move(o) {
  if (!o.move) return null;
  return h('span', { class: o.move < 0 ? 'move down' : 'move up' }, triangle(o.move > 0), Math.abs(o.move));
}

function gate(o) {
  const look = o.site === null ? ' off' : onPageOne(o) ? ' ok' : '';
  return h('span', { class: 'gate' + look, 'data-flip': placeText(o), title: siteNote(o) }, placeText(o));
}

// The edit lives on the row, so changing a price never needs a second place to look.
function quick(o, field, label, value, waiting) {
  return h('div', { class: waiting ? 'quick wait' : 'quick' },
    h('button', { type: 'button', tabindex: -1, 'data-do': 'step', 'data-field': field, 'data-dir': -1, 'aria-label': label + ' down' }, minus()),
    h('input', { 'data-edit': field, value, inputmode: 'decimal', autocomplete: 'off', spellcheck: 'false', 'aria-label': `${label} for ${o.title}` }),
    h('button', { type: 'button', tabindex: -1, 'data-do': 'step', 'data-field': field, 'data-dir': 1, 'aria-label': label + ' up' }, plus()));
}

function checked() {
  if (state.problem) return h('span', { class: 'status problem', role: 'alert' }, state.problem);
  if (!state.checked) return h('span', { class: 'status' }, 'First check is running');
  return h('span', { class: 'status', 'data-ago': '1' }, agoText());
}

const agoText = () => `Prices ${ago(state.checked)} ago, site order ${ago(state.siteChecked)} ago`;

function segment(name, items, active, attr) {
  return h('div', { class: 'seg', role: 'tablist', 'aria-label': name },
    items.map(([key, label]) => h('button', {
      type: 'button', role: 'tab', 'aria-selected': String(active === key), 'data-do': attr, 'data-value': key,
    }, label)));
}

function choice(attrs, items, value) {
  return h('select', Object.assign({ 'data-value': value }, attrs),
    items.map(([key, label]) => h('option', { value: key, selected: value === key }, label)));
}

// ---------- the frame ----------

function topBar() {
  const open = openOrders().length;
  const unassigned = state.finance && state.finance.ready ? sales90().filter(o => assignedUnits(o) < o.quantity).length : null;
  const tabs = [['home', 'Home', null], ['offers', 'Offers', state.offers.filter(isLive).length], ['orders', 'Orders', open], ['sales', 'Sales', null], ['customers', 'Customers', null], ['finance', 'Finance', unassigned], ['insights', 'Insights', null]];
  return h('header', { class: 'top' },
    h('b', { class: 'name', title: state.version ? 'SellerDesk ' + state.version : 'SellerDesk' }, h('span', { class: 'mark' }, logo()), h('span', { class: 'label' }, 'SellerDesk')),
    h('nav', { role: 'tablist', 'aria-label': 'Sections' },
      tabs.map(([key, label, count]) => h('button', {
        type: 'button', role: 'tab', 'aria-selected': String(tab === key), 'data-do': 'tab', 'data-value': key,
      }, label, count ? h('span', { class: key === 'orders' ? 'count hot' : 'count' }, count) : null))),
    h('div', { class: 'end' }, checked(),
      state.alarms ? h('button', {
        type: 'button', class: state.ring ? 'tool' : 'tool off', 'data-do': 'ring', 'aria-pressed': String(!!state.ring),
        title: state.ring ? 'New orders ring on this PC. Click to stay silent, for example when another app already rings for them.' : 'This PC stays silent for new orders. Click to ring again.',
      }, state.ring ? 'Alarm on' : 'Alarm off') : null,
      h('button', { type: 'button', class: 'tool', 'data-do': 'refresh' }, 'Check now'),
      h('button', { type: 'button', class: 'tool round', 'data-do': 'flipTheme', title: state.theme === 'light' ? 'Dark mode' : 'Light mode', 'aria-label': state.theme === 'light' ? 'Dark mode' : 'Light mode' }, state.theme === 'light' ? moon() : sun()),
      h('button', { type: 'button', class: 'tool', 'data-do': 'settings' }, 'Settings')));
}

// Only what needs a decision reaches this strip, and it disappears when nothing does.
function attention() {
  const items = [];
  const push = (key, text, attrs, hot) => items.push(h('button', Object.assign({ type: 'button', 'data-key': key, class: hot ? 'flag hot' : 'flag' }, attrs), text));
  const out = state.outbox;
  if (out) items.push(h('span', { class: 'flag' + (out.until ? ' hot' : ''), 'data-key': 'outbox' }, out.until
    ? `Eldorado asked for fewer requests. ${plural(out.count, 'change')} wait${out.count === 1 ? 's' : ''} and go at ${out.until}`
    : `Sending ${plural(out.count, 'change')}, one at a time`));
  if (state.unsaved) push('unsaved', state.unsaved === 1 ? '1 change is not on Eldorado. Send again' : `${state.unsaved} changes are not on Eldorado. Send all again`, { 'data-do': 'retryAll' }, true);
  if (state.offline) items.push(h('span', { class: 'flag hot', 'data-key': 'offline' }, "You're offline on Eldorado, so buyers can't see your offers"));
  const open = openOrders();
  const disputed = open.filter(o => o.state === 'Disputed').length;
  const paid = open.length - disputed;
  if (paid) push('paid', paid === 1 ? '1 order to deliver' : `${paid} orders to deliver`, { 'data-do': 'tab', 'data-value': 'orders' }, true);
  if (disputed) push('disputed', disputed === 1 ? '1 dispute' : `${disputed} disputes`, { 'data-do': 'tab', 'data-value': 'orders' }, true);
  const slipped = state.offers.filter(o => o.move < 0).length;
  if (slipped) push('slipped', slipped === 1 ? '1 offer slipped' : `${slipped} offers slipped`, { 'data-do': 'view', 'data-value': 'slipped' });
  const empty = state.offers.filter(o => isLive(o) && Number(o.stockDraft) === 0).length;
  if (empty) push('empty', empty === 1 ? '1 out of stock' : `${empty} out of stock`, { 'data-do': 'view', 'data-value': 'empty' });
  for (const notice of state.notices.slice(0, 3)) {
    items.push(h('span', { class: 'flag notice', 'data-key': 'n' + notice.id },
      h('button', { type: 'button', 'data-do': 'notice', 'data-url': notice.link || '' }, h('b', {}, notice.title), notice.text ? ' ' + notice.text : ''),
      h('button', { type: 'button', class: 'x', 'data-do': 'dismiss', 'data-value': notice.id, 'aria-label': 'Dismiss' }, cross())));
  }
  const update = updateFlag();
  if (update) items.unshift(update);
  if (!items.length && state.checked) items.push(h('span', { class: 'flag calm', 'data-key': 'calm' }, "You're all caught up. Nothing needs you right now."));
  if (!items.length) return null;
  const u = state.update;
  const notes = u && updateNotes && u.notes && u.notes.length && u.percent == null && !u.starting
    ? h('ul', { class: 'update-notes', 'data-key': 'update-notes', 'aria-label': `What's new in ${u.version}` }, u.notes.map((n, i) => h('li', { 'data-key': 'un' + i }, n))) : null;
  return [h('div', { class: 'attention', role: 'status' }, items), notes];
}

// A newer SellerDesk on GitHub. One click downloads it, checks it against GitHub's checksum and installs it.
function updateFlag() {
  const u = state.update;
  if (!u) return null;
  if (u.starting) return h('span', { class: 'flag update', 'data-key': 'update' }, h('b', {}, `Installing ${u.version}.`), ' SellerDesk opens again in a moment.');
  if (u.percent != null) return h('span', { class: 'flag update', 'data-key': 'update' }, h('b', {}, `Downloading ${u.version}`), ` ${u.percent}%`);
  return h('span', { class: 'flag update', 'data-key': 'update' },
    h('b', {}, `SellerDesk ${u.version} is out.`),
    u.problem ? h('span', { class: 'why' }, ' ' + u.problem) : null,
    u.notes && u.notes.length ? h('button', { type: 'button', class: 'link', 'data-do': 'updateNotes', 'aria-expanded': String(updateNotes) }, updateNotes ? 'Hide changes' : "What's new") : null,
    h('button', { type: 'button', class: 'solid small', 'data-do': 'update' }, u.problem ? 'Try again' : 'Update now'),
    h('button', { type: 'button', class: 'x', 'data-do': 'updateLater', 'aria-label': 'Later', title: 'Later. It asks again next time SellerDesk starts.' }, cross()));
}

function band(order) {
  if (!order) return null;
  return [
    h('section', { class: 'band', 'aria-label': 'Open order' },
      h('span', { class: 'pic' }, bell()),
      h('div', { class: 'what' }, h('small', {}, ['New order', order.price, order.more].filter(Boolean).join(', ')), h('b', {}, order.title)),
      h('span', { class: 'time', 'data-clock': '1' }, clock(order)),
      order.link ? h('button', { type: 'button', class: 'line', 'data-do': 'openOrder' }, 'Open order') : null,
      h('button', { type: 'button', class: 'solid', 'data-do': 'ack' }, 'Got it')),
    fuse(order),
  ];
}

function fuse(order) {
  return s('svg', { class: 'fuse', viewBox: '0 0 1000 1', preserveAspectRatio: 'none', 'aria-hidden': 'true' },
    s('rect', { width: 1000, height: 1, class: 'spent' }),
    s('rect', { width: Math.round(fuseShare(order) * 1000), height: 1, class: 'left', 'data-fuse': '1' }));
}

// ---------- offers ----------

function offersBar() {
  const counts = { live: state.offers.filter(isLive).length, paused: state.offers.filter(o => o.stateText === 'Paused').length, closed: state.offers.filter(isClosed).length };
  const views = VIEWS.filter(([key]) => key === 'live' || counts[key]).map(([key, label]) => [key, `${label} ${counts[key]}`]);
  if (view === 'slipped') views.push(['slipped', 'Slipped']);
  if (view === 'empty') views.push(['empty', 'Out of stock']);
  return h('div', { class: 'bar' },
    segment('Show offers', views, view, 'view'),
    h('input', { class: 'filter', id: 'filter', 'data-filter': '1', value: filter, placeholder: 'Find an offer', title: 'Ctrl+F', 'aria-label': 'Find an offer', autocomplete: 'off', spellcheck: 'false' }),
    h('div', { class: 'end' },
      h('label', { class: 'sortby' }, 'Order by ', choice({ 'data-sort-pick': '1' }, SORTS, state.sort)),
      state.sort === 'attention' ? null : h('button', { type: 'button', class: 'tool', 'data-do': 'sortFlip' }, state.descending ? 'High to low' : 'Low to high'),
      h('button', { type: 'button', class: 'tool', 'data-do': 'bulk', 'aria-pressed': String(!!bulk) }, 'Change many')));
}

function offerTable(list, pick) {
  if (!list.length) {
    const text = state.offers.length ? 'Nothing here.' : state.checked ? 'This account has no offers yet.' : 'Reading your offers. This takes a minute.';
    return h('p', { class: 'empty' }, text);
  }
  return h('div', { class: 'scroll' }, h('table', { class: 'offers' },
    h('colgroup', {}, ['offer', 'place', 'rank', 'low', 'soldat', 'price', 'stock', 'sold'].map(c => h('col', { class: 'c-' + c }))),
    h('thead', {}, h('tr', {}, [['Offer'], ['Place'], ['Price rank'], ['Lowest'], ['Others sold at', 'The typical price other sellers sold this item for this week. Green: your price is in the usual range. Red: it is outside it.'], ['Your price'], ['Stock'], ['Sold 30 d']]
      .map(([t, tip]) => h('th', { scope: 'col', title: tip || null }, t)))),
    h('tbody', {}, list.flatMap(o => [h('tr', {
      'data-key': o.id, 'data-do': 'pick', 'data-id': o.id, tabindex: 0,
      class: [pick && o.id === pick.id ? 'on' : '', o.unsaved ? 'unsaved' : ''].join(' ').trim() || null,
      'aria-selected': String(!!pick && o.id === pick.id),
    },
      h('td', {}, h('div', { class: 'it' }, picture(o, 'thumb'), h('b', {}, o.title))),
      h('td', { class: 'place' }, gate(o), ' ', move(o), o.site !== null ? h('small', {}, siteNote(o)) : null),
      h('td', {}, o.listed ? o.rank : ''),
      h('td', {}, o.lowest === null ? '' : o.lowest === o.livePrice ? h('span', { class: 'you' }, 'You') : o.lowestText),
      h('td', {}, soldAtCell(o)),
      h('td', {}, quick(o, 'price', 'Price', o.priceDraft, o.pendingPrice), o.checking ? h('small', { class: 'checking' }, 'Checking it stuck') : null),
      h('td', {}, quick(o, 'stock', 'Stock', o.stockDraft, o.pendingStock)),
      h('td', { class: o.sold ? null : 'quiet' }, o.sold)),
      o.unsaved ? unsavedLine(o) : null].filter(Boolean)))));
}

const priceOf = o => (state.pricing || {})[o.id] || null;

// The price sales actually happened at, which is the number to price against, not the cheapest listing.
function soldAtCell(o) {
  const p = priceOf(o);
  if (!p) return h('span', { class: 'quiet' }, o.kind === 'Currency' ? '' : 'No data');
  if (!p.soldAt) return h('span', { class: 'quiet', title: 'No sales seen for this item this week' }, 'No sales');
  return h('span', { class: 'sold-at ' + (p.verdict || ''), title: p.advice || '' }, fit(p.soldAt), h('small', {}, `${plural(p.sales, 'sale')} this week`));
}

// The sales line's ends, padded so a price can be dragged a little past the cheapest and dearest sale.
function bandScale(o, p) {
  const lo = Math.min(p.low ?? p.from, p.from, o.livePrice), hi = Math.max(p.to, p.topPrice, o.livePrice) || 1;
  const pad = (hi - lo || hi) * 0.1;
  return { lo: Math.max(0, lo - pad), hi: hi + pad };
}

// Where a new price would sit among the listings seen, leaving out the one nearest your own live price.
function rankAt(o, p, price) {
  const others = (p.listed || []).slice();
  const own = others.findIndex(v => Math.abs(v - o.livePrice) <= o.livePrice * 0.005);
  if (own >= 0) others.splice(own, 1);
  return { place: others.filter(v => v < price - 1e-9).length + 1, of: others.length + 1 };
}

const canDrag = o => o.listed && o.kind !== 'Currency';

// Under the place: the range sales happen in, with your price marked on it. Dragging the line picks a new price.
function priceBand(o) {
  const p = priceOf(o);
  if (!p || !p.soldAt) return null;
  const { lo, hi } = bandScale(o, p);
  const at = v => Math.max(0, Math.min(100, (v - lo) / ((hi - lo) || 1) * 100));
  const mark = v => Math.max(1, Math.min(99, at(v)));   // a mark at either end stays inside the line
  const picked = drag && drag.id === o.id ? drag : null;
  const you = picked ? picked.price : o.livePrice;
  const low = p.low ?? null;
  const rank = picked ? rankAt(o, p, you) : null;
  const versus = picked ? Math.round((you / p.soldAt - 1) * 100) : 0;
  return h('section', { class: 'band-card' },
    h('h3', {}, `What other sellers sold it for this week, ${plural(p.sales, 'sale')}`),
    h('p', { class: 'band-size' }, 'Prices for ',
      h('input', { class: 'size', 'data-bundle': o.id, value: String(p.size || 1), inputmode: 'numeric', autocomplete: 'off', spellcheck: 'false', 'aria-label': 'Items in one unit of your offer', title: 'How many items one unit of your offer holds. Other sellers\' prices are worked out per item and shown for this many.' }),
      (p.size || 1) === 1 ? ' item' : ' items',
      h('small', {}, p.sizeYours ? 'set by you' : 'from your title')),
    h('div', {
      class: 'band-line' + (canDrag(o) ? ' can-drag' : '') + (picked && picked.held ? ' held' : ''),
      'data-drag': canDrag(o) ? o.id : null, 'data-lo': lo, 'data-hi': hi,
      role: canDrag(o) ? 'slider' : 'img', tabindex: canDrag(o) ? 0 : null,
      'aria-label': canDrag(o) ? 'Your price. Drag, or use the left and right arrow keys' : `Most sales between ${fit(p.from)} and ${fit(p.to)}, you at ${fit(o.livePrice)}`,
      'aria-valuemin': canDrag(o) ? fit(lo) : null, 'aria-valuemax': canDrag(o) ? fit(hi) : null, 'aria-valuenow': canDrag(o) ? fit(you) : null,
    },
      h('i', { class: 'range', 'data-left': at(p.from), 'data-width': Math.max(2, at(p.to) - at(p.from)) }),
      low === null ? null : h('i', { class: 'mark low', 'data-left': mark(low), title: 'Cheapest listed ' + fit(low) }),
      h('i', { class: 'mark mid', 'data-left': mark(p.soldAt), title: 'Typical sale ' + fit(p.soldAt) }),
      picked ? h('i', { class: 'mark was', 'data-left': mark(o.livePrice), title: 'Your live price ' + fit(o.livePrice) }) : null,
      h('i', { class: 'mark you', 'data-left': mark(you), title: 'You ' + fit(you) })),
    h('div', { class: 'band-key' },
      low === null ? null : h('span', {}, h('i', { class: 'low' }), 'Cheapest'), h('span', {}, h('i', { class: 'range' }), 'Most sales'),
      h('span', {}, h('i', { class: 'mid' }), 'Typical'), h('span', {}, h('i', { class: 'you' }), canDrag(o) && !picked ? 'You, drag to change' : 'You')),
    picked ? h('div', { class: 'band-pick', role: 'status' },
      h('span', { class: 'quiet' }, 'New price'), h('b', {}, fit(you)),
      h('span', {}, `#${rank.place} of ${rank.of} listed`),
      h('span', { class: 'quiet' }, versus === 0 ? 'at the typical sale' : `${Math.abs(versus)}% ${versus > 0 ? 'above' : 'below'} the typical sale`),
      h('div', { class: 'acts' },
        h('button', { type: 'button', class: 'solid', 'data-do': 'dragSave', disabled: Math.abs(you - o.livePrice) < 0.00001 }, 'Save price'),
        h('button', { type: 'button', class: 'line', 'data-do': 'dragCancel' }, 'Cancel'))) : null,
    h('dl', { class: 'band-facts' },
      h('dt', {}, 'Cheapest listed now'), h('dd', {}, low === null ? 'Next check' : fit(low)),
      h('dt', {}, 'Most sales between'), h('dd', {}, `${fit(p.from)} to ${fit(p.to)}`),
      h('dt', {}, 'Typical sale price'), h('dd', {}, fit(p.soldAt)),
      h('dt', {}, 'Average sale price'), h('dd', {}, fit(p.average)),
      h('dt', {}, 'Sold the most'), h('dd', {}, p.topLink ? h('button', { type: 'button', class: 'seller', 'data-do': 'openTop', 'data-url': p.topLink }, `${p.topSeller} at ${fit(p.topPrice)}, ${plural(p.topSales, 'sale')}`) : p.topSeller)),
    h('p', { class: 'insight ' + (p.verdict || '') }, p.advice),
    p.suggest && Math.abs(p.suggest - o.livePrice) > 0.00001 && o.listed
      ? h('div', { class: 'acts' }, h('button', { type: 'button', class: 'line', 'data-do': 'useSuggested', 'data-value': String(p.suggest) }, `Set price to ${fit(p.suggest)}`))
      : null,
    null);
}

// A span wider than the visible columns makes the table invent empty ones, which squeeze the offer names.
// These breakpoints mirror the ones in style.css that hide Price rank, Lowest and Sold.
const shownColumns = () => 8 - (innerWidth <= 1240 ? 1 : 0) - (innerWidth <= 1100 ? 2 : 0);

// A refused edit gets its own line under the offer, so it can't be missed the way a side note was.
function unsavedLine(o) {
  return h('tr', { class: 'unsaved-why', 'data-key': o.id + '-why', 'data-id': o.id },
    h('td', { colspan: shownColumns() },
      h('div', { class: 'why', role: 'alert' },
        h('b', {}, 'Not on Eldorado'), h('span', {}, o.unsaved),
        h('button', { type: 'button', class: 'solid', 'data-do': 'retry' }, isClosed(o) ? 'Reopen again' : 'Send again'),
        h('button', { type: 'button', class: 'line', 'data-do': 'discard' }, 'Discard my change'))));
}

function actions(o) {
  if (o.needsConfirm) {
    return h('div', { class: 'acts' },
      h('button', { type: 'button', class: 'solid', 'data-do': 'confirm' }, 'Send this price'),
      h('button', { type: 'button', class: 'line', 'data-do': 'cancelPrice' }, 'Keep my price'));
  }
  if (isClosed(o)) {
    return h('div', { class: 'acts' },
      h('button', { type: 'button', class: 'solid', 'data-do': 'reopen', disabled: !(Number(o.stockDraft) > 0) }, 'Reopen offer'));
  }
  return h('div', { class: 'acts' },
    h('button', { type: 'button', class: 'solid', 'data-do': 'match', disabled: o.lowest === null || o.lowest === o.livePrice }, 'Match lowest price'),
    o.showUndo ? h('button', { type: 'button', class: 'line', 'data-do': 'undo' }, 'Undo price') : null,
    o.canToggle ? h('button', { type: 'button', class: 'line', 'data-do': 'toggle' }, o.toggleText) : null);
}

function rivals(o) {
  if (!o.rivals || !o.rivals.length) return null;
  return h('section', { class: 'rivals' },
    h('h3', {}, 'Top of the site order'),
    h('table', {},
      h('thead', {}, h('tr', {}, ['#', 'Seller', 'Price', 'Delivers', 'Rating'].map(t => h('th', { scope: 'col' }, t)))),
      h('tbody', {}, o.rivals.map(r => h('tr', { class: r.you ? 'me' : null, 'data-key': r.place + r.seller },
        h('td', {}, r.place), h('td', { class: 'who' }, r.seller), h('td', {}, r.price), h('td', {}, r.delivers),
        h('td', {}, r.score, r.score ? h('small', {}, r.ratings) : null))))));
}

function where(o) {
  if (isClosed(o)) return h('p', { class: 'insight' }, 'Eldorado closed this offer, usually because its stock ran out. Set the price and stock on its row, then Reopen offer.');
  if (o.site === null) return h('p', { class: 'insight' }, o.insight || `This offer is ${o.stateText.toLowerCase()}, so buyers can't see it.`);
  const away = o.site - PAGE;
  return h('div', { class: 'where' },
    h('b', { 'data-flip': placeText(o) }, '#' + o.site),
    h('span', {}, siteNote(o), h('small', {}, away > 0 ? `${away} places from page 1` : 'Buyers see this first')),
    move(o));
}

function gapRow(label, you, top, behind) {
  if (!you || !top) return null;
  return h('tr', { class: behind ? 'behind' : 'ahead' },
    h('td', {}, behind ? warn() : okMark(), label), h('td', {}, you), h('td', {}, top));
}

function gaps(o) {
  const g = o.gaps;
  if (!g) return null;
  const biggest = g.deliveryBehind ? 'delivery time' : g.ratingsBehind ? 'ratings' : g.priceBehind ? 'price' : '';
  return [
    h('table', { class: 'gaps' },
      h('thead', {}, h('tr', {}, h('th', { scope: 'col' }, ''), h('th', { scope: 'col' }, 'You'), h('th', { scope: 'col' }, 'Top 5'))),
      h('tbody', {},
        gapRow('Price', o.priceDraft, g.priceTop, g.priceBehind),
        gapRow('Delivery', g.deliveryYou, g.deliveryTop, g.deliveryBehind),
        gapRow('Ratings', g.ratingsYou, g.ratingsTop, g.ratingsBehind))),
    h('p', { class: biggest ? 'take' : 'take good' }, biggest ? `Your biggest gap is ${biggest}` : 'You keep up with the top sellers'),
  ];
}

function cheaperLine(o) {
  if (o.lowest === null || !o.listed) return null;
  if (!o.cheaperCount) return h('p', { class: 'hint' }, 'Nobody is cheaper than you.');
  return h('p', { class: 'hint' }, `${o.cheaperCount === 1 ? '1 seller is' : o.cheaperCount + ' sellers are'} cheaper, lowest ${o.lowestText}.`);
}

function panel(o) {
  const about = [o.game, promise(o.delivery) && 'promises ' + promise(o.delivery)].filter(Boolean).join(', ');
  return h('aside', { 'aria-label': 'Selected offer' },
    h('div', { class: 'head' },
      picture(o, 'thumb'),
      h('div', {}, h('h2', {}, o.title), about ? h('small', {}, about) : null)),
    where(o),
    gaps(o),
    cheaperLine(o),
    priceBand(o),
    h('p', { class: 'note', role: 'status' }, o.note),
    actions(o),
    rivals(o));
}

function bulkPanel() {
  const games = [...new Map(state.offers.filter(o => o.listed).map(o => [o.gameId, o.game])).entries()];
  if (!bulk.game || !games.some(([id]) => id === bulk.game)) bulk.game = games.length ? games[0][0] : '';
  const targets = state.offers.filter(o => o.listed && o.gameId === bulk.game);
  const percent = parseFloat(bulk.percent);
  const valid = Number.isFinite(percent) && percent !== 0 && Math.abs(percent) <= MAX_BULK;
  const example = targets[0];
  const offers = targets.length === 1 ? '1 offer' : `${targets.length} offers`;
  return h('aside', { 'aria-label': 'Change many offers' },
    h('div', { class: 'head' }, h('div', {}, h('h2', {}, 'Change many'), h('small', {}, `${offers} live in this game`))),
    games.length > 1 ? h('label', { class: 'field' }, 'Game', choice({ 'data-bulk': 'game' }, games, bulk.game)) : null,
    h('label', { class: 'field' }, 'Change every price by, in %',
      h('input', { 'data-bulk': 'percent', value: bulk.percent, inputmode: 'decimal', placeholder: '-5', autocomplete: 'off', spellcheck: 'false' })),
    valid && example ? h('p', { class: 'insight' }, `${example.title}: ${example.livePrice} to ${fit(example.livePrice * (1 + percent / 100))}`) : null,
    bulk.ask === 'price'
      ? h('div', { class: 'acts' },
        h('button', { type: 'button', class: 'solid', 'data-do': 'bulkPriceYes' }, `Yes, change ${offers}`),
        h('button', { type: 'button', class: 'line', 'data-do': 'bulkNo' }, 'Cancel'))
      : h('div', { class: 'acts' }, h('button', { type: 'button', class: 'solid', 'data-do': 'bulkPriceAsk', disabled: !valid || state.bulkBusy }, 'Review price change')),
    h('label', { class: 'field' }, 'Delivery time on every offer', choice({ 'data-bulk': 'promise' }, PROMISES, bulk.promise)),
    bulk.ask === 'delivery'
      ? h('div', { class: 'acts' },
        h('button', { type: 'button', class: 'solid', 'data-do': 'bulkDeliveryYes' }, `Yes, change ${offers}`),
        h('button', { type: 'button', class: 'line', 'data-do': 'bulkNo' }, 'Cancel'))
      : h('div', { class: 'acts' }, h('button', { type: 'button', class: 'line', 'data-do': 'bulkDeliveryAsk', disabled: state.bulkBusy }, 'Review delivery change')),
    h('p', { class: 'note', role: 'status' }, state.bulkNote),
    h('button', { type: 'button', class: 'tool', 'data-do': 'bulk' }, 'Close'));
}

function offersPage() {
  // A reopened or resumed offer can empty its view, and an empty view has no button to leave it.
  if ((view === 'closed' || view === 'paused') && !state.offers.some(inView)) view = 'live';
  const list = shown();
  const pick = current(list);
  selected = pick ? pick.id : selected;
  return [offersBar(), h('div', { class: 'work' },
    h('div', { class: 'list' }, offerTable(list, pick)),
    bulk ? bulkPanel() : pick ? panel(pick) : null)];
}

// ---------- orders ----------

function dueCell(o) {
  if (!o.open) return h('span', { class: 'quiet' }, o.took ? 'took ' + o.took : '');
  const late = o.deadline && o.deadline < Date.now() / 1000;
  return h('span', { class: late ? 'due late' : 'due', 'data-due': o.id }, clock(o));
}

function ordersPage() {
  const list = shownOrders();
  const pick = list.find(o => o.id === pickedOrder) || list[0] || null;
  pickedOrder = pick ? pick.id : null;
  return [
    h('div', { class: 'bar' }, segment('Show orders', [['open', `To do ${openOrders().length}`], ['all', 'Last 30 days']], ordersShown, 'ordersShown')),
    h('div', { class: 'work' },
      h('div', { class: 'list' }, list.length ? h('div', { class: 'scroll' }, h('table', { class: 'orders' },
        h('colgroup', {}, ['due', 'item', 'buyer', 'qty', 'total', 'state', 'review'].map(c => h('col', { class: 'c-' + c }))),
        h('thead', {}, h('tr', {}, ['Time left', 'Order', 'Buyer', 'Qty', 'Total', 'State', 'Review'].map(t => h('th', { scope: 'col' }, t)))),
        h('tbody', {}, list.map(o => h('tr', {
          'data-key': o.id, 'data-do': 'pickOrder', 'data-order': o.id, tabindex: 0,
          class: pick && o.id === pick.id ? 'on' : null, 'aria-selected': String(!!pick && o.id === pick.id),
        },
          h('td', {}, dueCell(o)), h('td', {}, h('b', {}, o.title)), h('td', {}, o.buyer), h('td', {}, o.quantity), h('td', {}, o.total),
          h('td', {}, o.state), h('td', { class: o.review === 'Negative' ? 'bad' : null }, o.review))))))
        : h('p', { class: 'empty' }, ordersShown === 'open' ? 'Nothing to deliver right now.' : 'Reading your orders.')),
      pick ? orderPanel(pick) : null)];
}

function orderPanel(o) {
  const facts = [['Buyer', o.buyer], ['Quantity', o.quantity], ['Total', o.total], ['Placed', o.placed ? dayOf(o.placed) : ''], ['State', o.state], ['Review', o.review], ['Dispute', o.dispute]];
  return h('aside', { 'aria-label': 'Selected order' },
    h('div', { class: 'head' }, h('div', {}, h('h2', {}, o.title))),
    h('dl', { class: 'facts' }, facts.filter(([, value]) => value).map(([label, value]) => [h('dt', {}, label), h('dd', {}, value)])),
    o.state === 'Paid' ? orderActions() : null,
    h('p', { class: 'note', role: 'status' }, o.note),
    h('div', { class: 'acts' }, h('button', { type: 'button', class: 'line', 'data-do': 'openOnSite' }, 'Open on Eldorado')));
}

function orderActions() {
  if (asking === 'deliver') {
    return h('div', { class: 'acts' },
      h('button', { type: 'button', class: 'solid', 'data-do': 'deliverYes' }, 'Yes, mark delivered'),
      h('button', { type: 'button', class: 'line', 'data-do': 'orderNo' }, 'Cancel'));
  }
  return [
    h('div', { class: 'acts' }, h('button', { type: 'button', class: 'solid', 'data-do': 'deliverAsk' }, 'Mark delivered')),
    h('div', { class: 'extend' },
      h('label', { class: 'field' }, 'Need more time', choice({ 'data-extend': 'time' }, EXTEND_TIMES, extendTime)),
      h('label', { class: 'field' }, 'Reason', choice({ 'data-extend': 'reason' }, EXTEND_REASONS, extendReason))),
    asking === 'extend'
      ? h('div', { class: 'acts' },
        h('button', { type: 'button', class: 'solid', 'data-do': 'extendYes' }, 'Yes, extend it'),
        h('button', { type: 'button', class: 'line', 'data-do': 'orderNo' }, 'Cancel'))
      : h('div', { class: 'acts' }, h('button', { type: 'button', class: 'line', 'data-do': 'extendAsk' }, 'Extend delivery time')),
  ];
}

// ---------- sales ----------

const dollars = value => '$' + value.toFixed(2);
const inCurrency = (amount, currency) => currency === 'USD' || !currency ? '$' + amount : `${amount} ${currency}`;

function trend(now, before, days) {
  if (!before) return h('small', {}, `Last ${days} days`);
  const change = Math.round((now - before) / before * 100);
  if (!change) return h('small', {}, `Same as the ${days} days before`);
  return h('small', { class: change > 0 ? 'rise' : 'fall' }, triangle(change > 0), `${Math.abs(change)}% vs the ${days} days before`);
}

function stat(label, value, note, main) {
  return h('div', { class: main ? 'stat main' : 'stat' }, h('span', {}, label), h('b', {}, value), note);
}

// A round top for the scale, so its labels read as plain numbers.
function roundUp(top) {
  if (top <= 0) return 1;
  const step = 10 ** Math.floor(Math.log10(top));
  return Math.ceil(top / step * 2) / 2 * step;
}

function bars(daily) {
  const peak = roundUp(Math.max(...daily.map(d => d.amount)));
  const best = daily.reduce((a, b) => b.amount > a.amount ? b : a);
  const bestLine = `Best day: ${best.day}, ${dollars(best.amount)}`;
  const middle = daily[Math.floor(daily.length / 2)];
  return h('section', { class: 'card chart' },
    h('header', {}, h('h3', {}, 'Sold per day'), h('span', { id: 'tip', role: 'status', 'data-rest': bestLine }, bestLine)),
    h('div', { class: 'plot' },
      h('div', { class: 'scale', 'aria-hidden': 'true' }, [peak, peak / 2, 0].map(v => h('span', {}, '$' + +v.toFixed(2)))),
      h('div', { class: 'bars', role: 'img', 'aria-label': `Sold per day. ${bestLine}.` },
        daily.map(d => h('div', { class: 'col', 'data-tip': `${d.day}: ${dollars(d.amount)} from ${d.count === 1 ? '1 sale' : d.count + ' sales'}` },
          h('i', { 'data-height': Math.min(d.amount / peak * 100, 100) }))))),
    h('div', { class: 'axis' }, h('span', {}, daily[0].day), h('span', {}, middle.day), h('span', {}, daily[daily.length - 1].day)));
}

function kinds(sales) {
  const total = sales.amount || 1;
  return h('section', { class: 'card kinds' },
    h('header', {}, h('h3', {}, 'Where it came from')),
    sales.kinds.map(k => h('div', { class: 'kind', 'data-key': k.name },
      h('div', {}, h('b', {}, k.name), h('span', {}, dollars(k.amount))),
      h('div', { class: 'share' }, h('i', { 'data-width': k.amount / total * 100 })),
      h('small', {}, `${k.count === 1 ? '1 sale' : k.count + ' sales'}, ${Math.round(k.amount / total * 100)}% of sold`))));
}

function itemCard(title, note, items, blank) {
  return h('section', { class: 'card items' },
    h('header', {}, h('h3', {}, title), note ? h('span', {}, note) : null),
    items.length ? h('table', {},
      h('colgroup', {}, ['item', 'orders', 'amount', 'last', 'now'].map(c => h('col', { class: 'i-' + c }))),
      h('thead', {}, h('tr', {}, h('th', { scope: 'col' }, 'Item'), ['Orders', 'Amount', 'Last sold'].map(t => h('th', { scope: 'col', class: 'n' }, t)), h('th', { scope: 'col', class: 'n' }, 'Now'))),
      h('tbody', {}, items.map(item => {
        const offer = state.offers.find(o => o.id === item.offer);
        return h('tr', { 'data-key': title + item.title, 'data-do': offer ? 'openOffer' : null, 'data-offer': offer ? offer.id : null, class: offer ? 'link' : null },
          h('td', {}, h('div', { class: 'it' }, offer ? picture(offer, 'thumb') : h('span', { class: 'thumb none' }), h('b', {}, item.title))),
          h('td', { class: 'n' }, item.orders),
          h('td', { class: 'n' }, inCurrency(item.amount, item.currency)),
          h('td', { class: 'n quiet' }, new Date(item.last).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' })),
          h('td', { class: 'n' }, h('span', { class: item.why ? 'pill warn' : 'pill' }, item.why || 'Live')));
      }))) : h('p', { class: 'hint' }, blank));
}

function salesPage() {
  const sales = state.sales;
  const periods = PERIODS.map(days => [String(days), `${days} days`]);
  const bar = h('div', { class: 'bar' }, segment('Period', periods, String(sales.days), 'period'));
  if (!sales.ready) return [bar, h('p', { class: 'empty' }, 'Reading your sales.')];
  return [bar, h('div', { class: 'scroll sales' },
    h('div', { class: 'stats' },
      stat('Sold', dollars(sales.amount), trend(sales.amount, sales.earlierAmount, sales.days), true),
      stat('Sales', sales.count, trend(sales.count, sales.earlierCount, sales.days)),
      stat('Net income cleared', dollars(sales.net), h('small', {}, sales.clearingCount ? `${dollars(sales.clearing)} still clearing, ${sales.clearingCount === 1 ? '1 order' : sales.clearingCount + ' orders'}` : 'Nothing left to clear')),
      stat('Fees', dollars(sales.fees), h('small', {}, `On ${dollars(sales.cleared)} cleared`))),
    sales.count ? h('div', { class: 'duo wide' }, bars(sales.daily), kinds(sales)) : h('p', { class: 'empty' }, 'No sales in this period yet.'),
    h('div', { class: 'duo' },
      itemCard('Best sellers', `Last ${sales.days} days`, state.best, 'No orders in this period yet.'),
      itemCard('Worth bringing back', 'Sold in the last 90 days, but gone or nearly out', state.back, 'Everything that sold lately is still on sale.')))];
}

// ---------- insights ----------

const plural = (count, word) => count === 1 ? `1 ${word}` : `${count} ${word}s`;

// The four hours in a row with the most sales, which can run past midnight.
function busiest(hours) {
  let best = 0, top = -1;
  for (let start = 0; start < 24; start++) {
    const sum = [0, 1, 2, 3].reduce((all, i) => all + hours[(start + i) % 24].sold, 0);
    if (sum > top) [best, top] = [start, sum];
  }
  const pad = hour => String(hour % 24).padStart(2, '0') + ':00';
  return { text: `${pad(best)} to ${pad(best + 4)}`, count: top };
}

function hourChart(data) {
  const peak = Math.max(...data.hours.map(x => x.sold + x.late), 1);
  const top = Math.max(peak + peak % 2, 2);
  const busy = busiest(data.hours);
  const rest = data.sold ? `Busiest: ${busy.text}, ${plural(busy.count, 'sale')}` : 'No sales yet';
  return h('section', { class: 'card chart hours' },
    h('header', {}, h('h3', {}, 'When buyers buy'), h('span', { id: 'tip', role: 'status', 'data-rest': rest }, rest)),
    h('div', { class: 'plot' },
      h('div', { class: 'scale', 'aria-hidden': 'true' }, [top, top / 2, 0].map(v => h('span', {}, v))),
      h('div', { class: 'bars', role: 'img', 'aria-label': `Orders by hour, your local time. ${rest}.` },
        data.hours.map(x => h('div', { class: 'col', 'data-tip': `${String(x.hour).padStart(2, '0')}:00: ${plural(x.sold, 'sale')}${x.late ? `, ${x.late} cancelled late` : ''}` },
          x.late ? h('i', { class: 'late', 'data-height': x.late / top * 100 }) : null,
          x.sold ? h('i', { 'data-height': x.sold / top * 100 }) : null)))),
    h('div', { class: 'axis' }, ['00:00', '06:00', '12:00', '18:00', '23:00'].map(t => h('span', {}, t))),
    h('div', { class: 'legend' }, h('span', {}, h('i', {}), 'Sold'), h('span', {}, h('i', { class: 'late' }), 'Cancelled late'), h('span', { class: 'zone' }, 'Your local time, last ' + data.days + ' days')));
}

function moveList(moves) {
  const labels = { fix: 'Fix', price: 'Price', stock: 'Stock', note: 'Note' };
  return h('section', { class: 'card moves' },
    h('header', {}, h('h3', {}, 'Worth doing'), h('span', {}, 'Suggestions only. Nothing changes by itself.')),
    moves.length ? moves.map(m => {
      const offer = m.offer && state.offers.find(o => o.id === m.offer);
      return h('div', { class: 'move-row', 'data-key': m.key },
        h('span', { class: m.kind === 'fix' ? 'pill warn' : 'pill' }, labels[m.kind]),
        h('div', {}, h('b', {}, m.title), h('p', {}, m.text)),
        offer ? h('button', { type: 'button', class: 'tool', 'data-do': 'openOffer', 'data-offer': offer.id }, 'Open offer') : null);
    }) : h('p', { class: 'hint' }, 'Nothing stands out right now.'));
}

function marketCard(market) {
  const all = market.rows || [];
  const rows = marketShown === 'missing' ? all.filter(r => !r.offer) : all;
  const label = market.sweeps ? `Watching since ${market.since}, ${plural(market.sweeps, 'check')}, the top ${market.seen.toLocaleString('en-US')} of ${market.total.toLocaleString('en-US')} offers` : '';
  const blank = !market.sweeps ? 'The public listing is checked every 30 minutes. The first numbers need two checks.'
    : marketShown === 'missing' && all.length ? 'Everything moving right now is already in your shop.' : 'No stock has moved yet. Give it a few hours.';
  return h('section', { class: 'card items market' },
    h('header', {}, h('h3', {}, 'Selling on the market'), h('span', {}, market.early ? h('b', { class: 'pill' }, 'Early') : null, ' ', label)),
    h('div', { class: 'market-bar' },
      segment('Show items', [['missing', 'Not in your shop'], ['all', 'Everything']], marketShown, 'marketShown'),
      h('p', { class: 'hint' }, 'Estimate from rival stock falls, 7 days')),
    rows.length ? h('table', {},
      h('colgroup', {}, ['item', 'sellers', 'falls', 'low', 'sold', 'top', 'yours'].map(c => h('col', { class: 'm-' + c }))),
      h('thead', {}, h('tr', {}, h('th', { scope: 'col' }, 'Item'),
        [['Sellers moving'], ['Stock falls'], ['Cheapest now', 'The lowest price listed right now'], ['Typical sale', 'The middle price of the sales seen, one vote per sale'], ['Most sales', 'The offer with the most sales seen. Click to open it on Eldorado']]
          .map(([t, tip]) => h('th', { scope: 'col', class: 'n', title: tip || null }, t)),
        h('th', { scope: 'col', class: 'n' }, 'You'))),
      h('tbody', {}, rows.map(r => h('tr', { 'data-key': r.key, 'data-do': r.offer ? 'openOffer' : null, 'data-offer': r.offer || null, class: r.offer ? 'link' : null },
        h('td', {}, h('div', { class: 'it' }, r.picture ? h('img', { class: 'thumb', src: r.picture, alt: '', draggable: 'false' }) : h('span', { class: 'thumb none' }), h('div', { class: 'named' }, h('b', {}, r.name), r.kind || r.per > 1 ? h('small', {}, [r.kind, r.per > 1 ? `prices for ${r.per}` : ''].filter(Boolean).join(', ')) : null))),
        h('td', { class: 'n' }, r.movers), h('td', { class: 'n' }, r.drops),
        h('td', { class: 'n' }, r.low ? '$' + r.low : h('span', { class: 'quiet' }, 'Next check')),
        h('td', { class: 'n' }, r.soldAt ? '$' + r.soldAt : h('span', { class: 'quiet' }, 'Not yet')),
        h('td', { class: 'n' }, r.topLink
          ? h('button', { type: 'button', class: 'seller', 'data-do': 'openTop', 'data-url': r.topLink, title: `${r.topSeller}: ${plural(r.topSales, 'sale')} seen at $${r.topPrice}` },
            h('span', {}, '$' + r.topPrice), h('small', {}, `${r.topSeller}, ${r.topSales}`))
          : h('span', { class: 'quiet' }, 'Not yet')),
        h('td', { class: 'n' }, r.offer ? h('span', { class: r.above ? 'above' : null, title: r.above ? 'Above the price these sales happened at' : null }, '$' + r.yours) : h('span', { class: 'pill' }, 'Not listed')))))) : h('p', { class: 'hint' }, blank));
}

// Each price change against the orders before and after it, said as a plain result.
function changeCard(changes) {
  const verdict = c => c.early ? ['quiet', 'Too soon'] : c.soldAfter > c.soldBefore ? ['up', 'Helped'] : c.soldAfter < c.soldBefore ? ['warn', 'Fewer orders'] : ['', 'No change'];
  const days = c => Math.max(1, c.daysAfter);
  return h('section', { class: 'card items changes' },
    h('header', {}, h('h3', {}, 'Did your price changes help?'), h('span', {}, 'Orders before and after each change')),
    h('p', { class: 'hint' }, 'Each row counts an offer’s orders in the days before a price change and the same number of days after it, up to a week. Other things move sales too, so take it as a hint.'),
    changes.length ? h('table', {},
      h('colgroup', {}, ['item', 'price', 'orders', 'place', 'verdict'].map(c => h('col', { class: 'p-' + c }))),
      h('thead', {}, h('tr', {}, h('th', { scope: 'col' }, 'Item'), ['Price change', 'Orders', 'Site place', 'Result'].map(t => h('th', { scope: 'col' }, t)))),
      h('tbody', {}, changes.map(c => {
        const [tone, word] = verdict(c);
        return h('tr', { 'data-key': c.key, 'data-do': 'openOffer', 'data-offer': c.offer, class: 'link' },
          h('td', {}, h('b', {}, c.title)),
          h('td', {}, h('span', { class: 'change' }, triangle(c.up), `${c.from} → ${c.to}`), h('small', { class: 'quiet' }, `on ${c.day}`)),
          h('td', {}, c.early ? h('span', { class: 'quiet' }, `Only ${plural(c.daysAfter, 'day')} since`)
            : h('span', {}, `${plural(c.soldBefore, 'order')} in the ${plural(days(c), 'day')} before, ${c.soldAfter} after`)),
          h('td', { class: 'quiet' }, c.placeBefore && c.placeNow ? `#${c.placeBefore} → #${c.placeNow}` : 'Not read yet'),
          h('td', {}, h('span', { class: ('pill ' + tone).trim() }, word)));
      }))) : h('p', { class: 'hint' }, 'A daily record of each offer is kept from now on. Change a price and its result shows up here after a few days.'));
}

function insightsPage() {
  const data = state.insights;
  if (!data) return h('p', { class: 'empty' }, 'Reading your orders.');
  const small = text => h('small', {}, text);
  return h('div', { class: 'scroll sales insights' },
    h('div', { class: 'stats' },
      stat('Lost to late delivery', dollars(data.lostAmount), small(data.lostCount ? `${plural(data.lostCount, 'order')} in ${data.days} days` : `None in ${data.days} days`), data.lostCount > 0),
      stat('Typical delivery', data.typical || 'None yet', small(data.promise ? `You promise ${data.promise}, ${plural(data.over, 'order')} ran over` : 'No deliveries yet')),
      stat('Repeat buyers', `${data.again} of ${data.buyers}`, small('Bought from you more than once')),
      stat('Reviews', `${data.good} good`, small(`${data.bad} bad, ${data.unreviewed} sales without one`))),
    h('div', { class: 'duo' }, moveList(data.moves), hourChart(data)),
    marketCard(data.market),
    changeCard(data.changes));
}

// ---------- home ----------

const sameDay = (a, b) => new Date(a).toDateString() === new Date(b).toDateString();

// One screen that answers "what needs me, how am I doing, where is money left on the table".
function homePage() {
  const now = Date.now();
  const sold = state.orders.filter(o => o.sale);
  const today = sold.filter(o => sameDay(o.placed, now));
  const yesterday = sold.filter(o => sameDay(o.placed, now - 864e5));
  const sum = list => list.reduce((a, o) => a + (o.amount || 0), 0);
  const live = state.offers.filter(isLive);
  const front = live.filter(onPageOne).length;
  const owed = ledger() ? (ledger().summary || []).reduce((a, s) => a + Math.max(0, s.balance), 0) : null;
  const open = openOrders();
  const change = yesterday.length ? Math.round((sum(today) - sum(yesterday)) / Math.max(sum(yesterday), 0.01) * 100) : null;
  const small = text => h('small', {}, text);
  return h('div', { class: 'scroll sales home' },
    h('div', { class: 'stats' },
      stat('Sold today', dollars(sum(today)), change === null ? small(`${plural(today.length, 'order')}, none yesterday`)
        : h('small', { class: change >= 0 ? 'rise' : 'fall' }, triangle(change >= 0), `${Math.abs(change)}% vs yesterday, ${plural(today.length, 'order')}`), true),
      stat('To deliver', open.length, small(open.length ? 'Waiting for you' : 'Nothing waiting')),
      stat('On page 1', `${front} of ${live.length}`, small('Live offers')),
      owed === null ? stat('Live offers', live.length, small(`${state.offers.filter(isClosed).length} closed, ${state.offers.filter(o => o.stateText === 'Paused').length} paused`))
        : stat('You owe', dollars(owed), small('Finance balance'))),
    h('div', { class: 'duo home-top' }, doNow(open), opportunities()),
    h('div', { class: 'duo' },
      state.sales.ready && state.sales.daily ? bars(state.sales.daily.slice(-14)) : h('section', { class: 'card' }, h('p', { class: 'hint' }, 'Reading your sales.')),
      moversCard()));
}

// Everything that costs money if it waits, most urgent first.
function doNow(open) {
  const rows = [];
  const add = (key, kind, title, text, attrs, button) => rows.push(h('div', { class: 'move-row', 'data-key': key },
    h('span', { class: kind === 'fix' ? 'pill warn' : 'pill' }, { fix: 'Now', stock: 'Stock', price: 'Price' }[kind]),
    h('div', {}, h('b', {}, title), h('p', {}, text)),
    button ? h('button', Object.assign({ type: 'button', class: 'tool' }, attrs), button) : null));
  for (const o of open.slice(0, 4)) {
    add('o' + o.id, 'fix', o.title, `${o.buyer}, ${o.quantity} for ${o.total}${o.deadline ? ', ' + clock(o) + ' left' : ''}`, { 'data-do': 'tab', 'data-value': 'orders' }, 'Deliver');
  }
  for (const o of state.offers.filter(x => x.unsaved).slice(0, 3)) add('u' + o.id, 'fix', `${o.title}: not on Eldorado`, o.unsaved, { 'data-do': 'openOffer', 'data-offer': o.id }, 'Open');
  const empty = state.offers.filter(o => isLive(o) && Number(o.stockDraft) === 0).sort((a, b) => b.sold - a.sold);
  for (const o of empty.slice(0, 3)) add('e' + o.id, 'stock', `${o.title} is out of stock`, o.sold ? `${plural(o.sold, 'sale')} in 30 days` : 'No stock', { 'data-do': 'openOffer', 'data-offer': o.id }, 'Restock');
  // Days left at the 30-day pace. Currency stock counts another unit than its orders, so it stays out.
  const low = state.offers.filter(o => isLive(o) && o.kind !== 'Currency' && o.sold >= 2 && Number(o.stockDraft) > 0)
    .map(o => ({ o, days: Number(o.stockDraft) / (o.sold / 30) })).filter(x => x.days < 5).sort((a, b) => a.days - b.days);
  for (const { o, days } of low.slice(0, 3)) add('l' + o.id, 'stock', `${o.title}: ${o.stockDraft} left`, `About ${Math.max(1, Math.round(days))} ${Math.round(days) <= 1 ? 'day' : 'days'} at this pace`, { 'data-do': 'openOffer', 'data-offer': o.id }, 'Restock');
  return h('section', { class: 'card moves' },
    h('header', {}, h('h3', {}, 'Do now'), h('span', {}, rows.length ? plural(rows.length, 'thing') : '')),
    rows.length ? rows : h('p', { class: 'hint' }, 'Nothing waiting.'));
}

// Offers priced away from where sales happen. Below the range leaves money behind, above it loses buyers.
function opportunities() {
  const list = state.offers.filter(o => o.listed).map(o => ({ o, p: priceOf(o) }))
    .filter(x => x.p && (x.p.verdict === 'low' || x.p.verdict === 'high') && x.p.suggest)
    .map(x => Object.assign(x, { gain: (x.p.suggest - x.o.livePrice) * Math.max(x.o.sold, 1) }))
    .sort((a, b) => Math.abs(b.gain) - Math.abs(a.gain)).slice(0, 6);
  return h('section', { class: 'card moves' },
    h('header', {}, h('h3', {}, 'Price checks'), h('span', {}, 'vs this week’s sales')),
    list.length ? list.map(({ o, p }) => h('div', { class: 'move-row', 'data-key': 'p' + o.id, 'data-id': o.id },
      h('span', { class: p.verdict === 'low' ? 'pill up' : 'pill warn' }, p.verdict === 'low' ? 'Raise' : 'High'),
      h('div', {}, h('b', {}, o.title), h('p', {}, `You ${fit(o.livePrice)} · sales ${fit(p.from)}–${fit(p.to)}`)),
      h('div', { class: 'acts' },
        // One click from Home would reprice a live offer, so Set asks once more before it sends.
        settingPrice === String(o.id)
          ? [h('button', { type: 'button', class: 'solid', 'data-do': 'useSuggested', 'data-value': String(p.suggest) }, 'Send ' + fit(p.suggest)),
             h('button', { type: 'button', class: 'tool', 'data-do': 'setNo' }, 'Cancel')]
          : [h('button', { type: 'button', class: 'tool', 'data-do': 'setAsk', 'data-value': o.id }, 'Set ' + fit(p.suggest)),
             h('button', { type: 'button', class: 'tool', 'data-do': 'openOffer', 'data-offer': o.id }, 'Open')])))
      : h('p', { class: 'hint' }, Object.keys(state.pricing || {}).length
        ? 'All prices are in range.'
        : 'Waiting for market data.'));
}

function moversCard() {
  const rows = ((state.insights && state.insights.market && state.insights.market.rows) || []).filter(r => !r.offer).slice(0, 6);
  return h('section', { class: 'card moves' },
    h('header', {}, h('h3', {}, 'Selling, not in your shop'), h('span', {}, '7 days')),
    rows.length ? rows.map(r => h('div', { class: 'move-row mover', 'data-key': 'm' + r.key },
      r.picture ? h('img', { class: 'thumb', src: r.picture, alt: '', draggable: 'false' }) : h('span', { class: 'thumb none' }),
      h('div', {}, h('b', {}, r.name), h('p', {}, `${plural(r.drops, 'sale')}${r.soldAt ? ' · ' + r.soldAt : ''}`)),
      r.topLink ? h('button', { type: 'button', class: 'tool', 'data-do': 'openTop', 'data-url': r.topLink }, 'See offer') : null))
      : h('p', { class: 'hint' }, 'Waiting for market data.'));
}

// ---------- customers ----------

const DAYS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
const shortDay = stamp => new Date(stamp).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' });

// Seven rows of 24 cells. Darker means more orders in that hour, so quiet nights and busy evenings show at a glance.
function heatmap(grid) {
  const peak = Math.max(1, ...grid.flat());
  return h('section', { class: 'card heat' },
    h('header', {}, h('h3', {}, 'When orders come in'), h('span', { id: 'tip', role: 'status', 'data-rest': 'Local time' }, 'Local time')),
    h('div', { class: 'heat-grid', role: 'img', 'aria-label': 'Orders by weekday and hour' },
      h('span', {}), [0, 3, 6, 9, 12, 15, 18, 21].map(hour => h('span', { class: 'heat-hour' }, String(hour).padStart(2, '0'))),
      grid.map((row, d) => [
        h('span', { class: 'heat-day' }, DAYS[d]),
        h('div', { class: 'heat-row' }, row.map((count, hour) => h('i', {
          'data-level': count ? Math.ceil(count / peak * 4) : 0,
          'data-tip': `${DAYS[d]} ${String(hour).padStart(2, '0')}:00, ${plural(count, 'order')}`,
        }))),
      ])));
}

function buyerTable(title, note, list, blank) {
  return h('section', { class: 'card items buyers' },
    h('header', {}, h('h3', {}, title), note ? h('span', {}, note) : null),
    list.length ? h('table', {},
      h('thead', {}, h('tr', {}, h('th', { scope: 'col' }, 'Buyer'), ['Orders', 'Spent', 'Buys every', 'Last'].map(t => h('th', { scope: 'col', class: 'n' }, t)), h('th', { scope: 'col' }, 'Buys'))),
      h('tbody', {}, list.map(b => h('tr', { 'data-key': title + b.name },
        h('td', {}, h('b', {}, b.name), b.problems.length ? h('div', { class: 'tags' }, b.problems.map(p => h('span', { class: 'pill warn' }, p))) : null),
        h('td', { class: 'n' }, b.orders), h('td', { class: 'n' }, dollars(b.spent)),
        h('td', { class: 'n quiet' }, b.every || 'Once'), h('td', { class: 'n quiet' }, shortDay(b.last)),
        h('td', { class: 'buys' }, b.items.join(', ')))))) : h('p', { class: 'hint' }, blank));
}

function customersPage() {
  const c = state.customers;
  if (!c) return h('p', { class: 'empty' }, 'Reading your orders.');
  const small = text => h('small', {}, text);
  return h('div', { class: 'scroll sales customers' },
    h('div', { class: 'stats' },
      stat('Buyers', c.buyers, small(`${c.newThisWeek} new this week, last ${c.days} days`), true),
      stat('Came back', `${c.repeat} of ${c.buyers}`, small(c.repeat ? `${c.repeatShare}% of what you sold came from them` : 'Nobody bought twice yet')),
      stat('Average order', dollars(c.average), small('What one order brings in')),
      stat('Time between orders', c.gap || 'None yet', small('Typical gap for buyers who come back'))),
    heatmap(c.grid),
    h('div', { class: 'duo' },
      buyerTable('Best buyers', 'By what they spent', c.top, 'No buyers yet.'),
      h('div', { class: 'stack' },
        buyerTable('Worth watching', 'Disputes, bad reviews or repeated cancels', c.trouble, 'None.'),
        h('section', { class: 'card pairs' },
          h('header', {}, h('h3', {}, 'Bought together'), h('span', {}, 'By the same buyer')),
          c.pairs.length ? c.pairs.map(p => h('div', { class: 'pair', 'data-key': p.a + p.b },
            h('b', {}, p.a), h('span', { class: 'plus' }, '+'), h('b', {}, p.b), h('small', {}, plural(p.buyers, 'buyer'))))
            : h('p', { class: 'hint' }, 'None yet.')))));
}

// ---------- finance ----------

let financeShown = 'open';     // open: still has unassigned units
let financePick = null;        // { kind: 'order' | 'person' | 'new', id }
let financeFix = null;         // with a person picked: the order of theirs opened under its row to change
const assignDraft = {};        // orderId -> { personId: units }
const finDraft = { name: '', note: '', amount: '', date: '', payNote: '', from: '', until: '', editName: null, editNote: null, editSeen: 0 };

const ledger = () => (state.finance && state.finance.ledger) || null;
const people = (all) => (ledger() ? ledger().people : []).filter(p => all || !p.archived);
const personName = id => (people(true).find(p => p.id === id) || {}).name || 'Removed';
const ledgerOrder = id => (ledger() && ledger().orders[id]) || null;
const assignedUnits = o => { const l = ledgerOrder(o.id); return l ? l.shares.reduce((a, s) => a + s.units, 0) : 0; };
const balanceOf = id => (ledger() ? ledger().summary : []).find(s => s.person === id) || { earned: 0, paid: 0, balance: 0, units: 0, orders: 0 };
const sales90 = () => state.orders.filter(o => o.sale && o.placed >= Date.now() - 90 * 864e5);

function financeOrders() {
  const list = sales90();
  if (financeShown === 'open') return list.filter(o => assignedUnits(o) < o.quantity);
  if (financeShown === 'people') return [];
  return list;
}

// Orders carry their offer id; older ones fall back to the title, so the finance rows can show the item's picture.
const offerFor = o => state.offers.find(x => x.id === o.offer) || state.offers.find(x => x.title === o.title) || null;
const orderPicture = o => { const offer = offerFor(o); return offer ? picture(offer, 'thumb') : h('span', { class: 'thumb none' }); };
// A ledger order that has left the 90-day order list still opens, from what the ledger kept of it.
function orderById(id) {
  const o = state.orders.find(x => x.id === id);
  if (o) return o;
  const l = ledgerOrder(id);
  return l ? { id, title: l.title, quantity: l.quantity, placed: Date.parse(l.placed), buyer: l.buyer || '', total: dollars(l.net), offer: l.offer || '' } : null;
}
const fixOrderId = () => financePick && financePick.kind === 'person' ? financeFix : financePick && financePick.kind === 'order' ? financePick.id : null;
const fixOrder = () => { const id = fixOrderId(); return id ? orderById(id) : null; };
const unitsOf = (o, person) => ((ledgerOrder(o.id) || {}).shares || []).filter(s => s.person === person).reduce((a, s) => a + s.units, 0);

// The ledger's "changed" stamp each draft started from; the server refuses a save if another PC saved since.
const draftSeen = {};
const changedOf = record => (record && record.changed) || 0;

function draftFor(o) {
  if (!assignDraft[o.id]) {
    const l = ledgerOrder(o.id);
    assignDraft[o.id] = {};
    draftSeen[o.id] = changedOf(l);
    if (l) for (const s of l.shares) assignDraft[o.id][s.person] = s.units;
  }
  return assignDraft[o.id];
}

function personCard(p) {
  const b = balanceOf(p.id);
  const on = financePick && financePick.kind === 'person' && financePick.id === p.id;
  return h('button', { type: 'button', class: on ? 'person on' : 'person', 'data-do': 'pickPerson', 'data-value': p.id, 'data-key': 'p' + p.id },
    h('span', { class: 'avatar', 'aria-hidden': 'true' }, p.name.slice(0, 1).toUpperCase()),
    h('span', { class: 'who' }, h('b', {}, p.name), h('small', {}, `${plural(b.units, 'unit')}, earned ${dollars(b.earned)}`)),
    h('span', { class: b.balance > 0.004 ? 'owed' : 'owed clear' }, h('small', {}, b.balance > 0.004 ? 'You owe' : 'Settled'), dollars(Math.abs(b.balance))));
}

function sharesCell(o) {
  const l = ledgerOrder(o.id);
  if (!l || !l.shares.length) return h('span', { class: 'pill warn' }, 'Not assigned');
  const left = o.quantity - assignedUnits(o);
  return h('div', { class: 'tags' }, l.shares.map(s => h('span', { class: 'pill' }, `${personName(s.person)} ${s.units}`)),
    left > 0 ? h('span', { class: 'pill warn' }, `${left} left`) : null);
}

// One click gives every unit still unassigned to that person and saves; Split opens the panel for anything finer.
function assignCell(o) {
  const l = ledgerOrder(o.id);
  const left = o.quantity - assignedUnits(o);
  return h('div', { class: 'give' },
    l && l.shares.length ? l.shares.map(s => h('span', { class: 'pill' }, `${personName(s.person)} ${s.units}`)) : null,
    people(false).map(p => h('button', {
      type: 'button', class: 'chip', 'data-do': 'quickAssign', 'data-order': o.id, 'data-person': p.id,
      title: `Give ${left === o.quantity ? 'all' : 'the other'} ${plural(left, 'unit')} to ${p.name} and save`,
    }, l && l.shares.length ? `Rest to ${p.name}` : p.name)),
    h('button', { type: 'button', class: 'chip quiet', 'data-do': 'pickFinOrder', 'data-order': o.id }, 'Split'));
}

// Per person: one column each, so a wrong assignment is visible at a glance and one click away from a fix.
function personColumn(p, pickOrder) {
  const rows = sales90().map(o => ({ o, units: unitsOf(o, p.id) })).filter(r => r.units > 0).sort((a, b) => b.o.placed - a.o.placed);
  const money = rows.reduce((a, r) => { const l = ledgerOrder(r.o.id); return a + (l ? l.net * r.units / Math.max(1, l.quantity) : 0); }, 0);
  return h('section', { class: 'card person-col', 'data-key': 'c' + p.id },
    h('div', { class: 'head' },
      h('span', { class: 'avatar', 'aria-hidden': 'true' }, p.name.slice(0, 1).toUpperCase()),
      h('span', { class: 'who' }, h('b', {}, p.name), h('small', {}, `${plural(rows.length, 'order')}, ${dollars(money)} in 90 days`))),
    rows.length
      ? h('div', { class: 'scroll' }, rows.map(({ o, units }) => h('div', {
        class: pickOrder && pickOrder.id === o.id ? 'job on' : 'job', 'data-do': 'pickFinOrder', 'data-order': o.id, tabindex: 0, 'data-key': o.id,
      },
        orderPicture(o),
        h('span', { class: 'named' }, h('b', {}, o.title), h('small', {}, `${shortDay(o.placed)}, ${o.buyer}`)),
        h('span', { class: 'units', title: `${units} of the order's ${o.quantity} units` }, units < o.quantity ? `${units} of ${o.quantity}` : units),
        h('span', { class: 'fix' }, 'Fix'))))
      : h('p', { class: 'hint' }, 'Nothing assigned in the last 90 days.'));
}

function assignPanel(o) {
  const l = ledgerOrder(o.id);
  return h('aside', { 'aria-label': 'Assign order' },
    h('div', { class: 'head' }, orderPicture(o), h('div', {}, h('h2', {}, o.title), h('small', {}, `${o.buyer}, ${dayOf(o.placed)}`))),
    h('dl', { class: 'facts' },
      h('dt', {}, 'Units'), h('dd', {}, o.quantity),
      h('dt', {}, 'Order total'), h('dd', {}, o.total),
      l ? [h('dt', {}, 'After Eldorado fee'), h('dd', {}, dollars(l.net), ' ', h('small', { class: 'quiet' }, l.netExact ? 'exact' : 'estimate until it clears'))] : null),
    shareEditor(o),
    h('p', { class: 'note', role: 'status' }, state.finance.note));
}

// Whose units an order's are: a row per person with − + and All, Rest, Half, then Save.
function shareEditor(o) {
  const draft = draftFor(o);
  const total = Object.values(draft).reduce((a, n) => a + (Number(n) || 0), 0);
  const l = ledgerOrder(o.id);
  const list = people(false);
  return list.length ? [
      h('h3', { class: 'sub' }, 'Whose units are these?'),
      h('div', { class: 'shares' }, list.map(p => h('div', { class: 'share-row', 'data-key': 's' + p.id },
        h('div', { class: 'share-head' }, h('b', {}, p.name),
          l && l.quantity ? h('small', { class: 'quiet' }, dollars(l.net * (Number(draft[p.id]) || 0) / l.quantity)) : null),
        h('div', { class: 'share-ctl' },
          h('div', { class: 'quick' },
            h('button', { type: 'button', tabindex: -1, 'data-do': 'shareStep', 'data-value': p.id, 'data-dir': -1, 'aria-label': p.name + ' fewer' }, minus()),
            h('input', { 'data-fin': 'units', 'data-person': p.id, value: draft[p.id] || '', placeholder: '0', inputmode: 'numeric', autocomplete: 'off', 'aria-label': p.name + ' units' }),
            h('button', { type: 'button', tabindex: -1, 'data-do': 'shareStep', 'data-value': p.id, 'data-dir': 1, 'aria-label': p.name + ' more' }, plus())),
          h('button', { type: 'button', class: 'tool', 'data-do': 'giveAll', 'data-value': p.id, title: `All ${o.quantity} units to ${p.name}` }, 'All'),
          h('button', { type: 'button', class: 'tool', 'data-do': 'giveRest', 'data-value': p.id, title: `The units nobody has yet, to ${p.name}` }, 'Rest'),
          h('button', { type: 'button', class: 'tool', 'data-do': 'giveHalf', 'data-value': p.id, title: `Half of the order, ${Math.round(o.quantity / 2)} units, to ${p.name}` }, 'Half'))))),
      h('p', { class: total > o.quantity ? 'note bad' : 'note' }, total > o.quantity ? `That's ${total} units, the order has ${o.quantity}.` : `${total} of ${o.quantity} units assigned`),
      h('div', { class: 'acts' },
        h('button', { type: 'button', class: 'solid', 'data-do': 'saveShares', disabled: total > o.quantity || state.finance.busy }, 'Save'),
        l ? h('button', { type: 'button', class: 'line', 'data-do': 'clearShares' }, 'Unassign') : null),
    ] : h('p', { class: 'insight' }, 'Add a person first.');
}

// A person's work, newest first, in the wide part of the page. Clicking a row opens the split under it,
// so a unit given to the wrong person moves without leaving the list.
function personList(p) {
  const l = ledger();
  const rows = Object.entries(l.orders).map(([id, o]) => ({ id, o, units: o.shares.filter(s => s.person === p.id).reduce((a, s) => a + s.units, 0) }))
    .filter(r => r.units > 0).sort((a, b) => Date.parse(b.o.placed) - Date.parse(a.o.placed));
  const units = rows.reduce((a, r) => a + r.units, 0);
  const money = rows.reduce((a, r) => a + r.o.net * r.units / Math.max(1, r.o.quantity), 0);
  return h('section', { class: 'card person-list', 'data-key': 'pl' + p.id },
    h('div', { class: 'head' },
      h('span', { class: 'avatar', 'aria-hidden': 'true' }, p.name.slice(0, 1).toUpperCase()),
      h('span', { class: 'who' }, h('b', {}, `${p.name}'s orders`), h('small', {}, `${plural(rows.length, 'order')}, ${plural(units, 'unit')}, ${dollars(money)} earned. Click an order to change whose units it is.`))),
    rows.length ? h('div', { class: 'scroll' }, h('table', { class: 'orders finance mine' },
      h('colgroup', {}, ['placed', 'item', 'buyer', 'qty', 'total', 'whose'].map(c => h('col', { class: 'f-' + c }))),
      h('thead', {}, h('tr', {}, ['Placed', 'Order', 'Buyer', 'Units', 'Their share', 'Shared with'].map(t => h('th', { scope: 'col' }, t)))),
      h('tbody', {}, rows.flatMap(({ id, o, units: theirs }) => {
        const order = orderById(id);
        const open = financeFix === id;
        const others = o.shares.filter(s => s.person !== p.id);
        return [h('tr', { 'data-key': id, 'data-do': 'fixFinOrder', 'data-order': id, tabindex: 0, class: open ? 'on' : null, 'aria-expanded': String(open) },
          h('td', { class: 'quiet' }, dayOf(Date.parse(o.placed))),
          h('td', {}, h('div', { class: 'it' }, orderPicture(order), h('b', {}, o.title))),
          h('td', {}, order.buyer || h('span', { class: 'quiet' }, '')),
          h('td', {}, theirs < o.quantity ? `${theirs} of ${o.quantity}` : theirs),
          h('td', {}, dollars(o.net * theirs / Math.max(1, o.quantity)), o.netExact ? null : h('small', { class: 'quiet', title: 'Estimate until Eldorado clears the order' }, ' est')),
          h('td', {}, others.length ? h('div', { class: 'tags' }, others.map(s => h('span', { class: 'pill' }, `${personName(s.person)} ${s.units}`))) : h('span', { class: 'quiet' }, 'Nobody'))),
        open ? h('tr', { class: 'fix-row', 'data-key': id + '-fix' }, h('td', { colspan: innerWidth <= 1100 ? 5 : 6 },   // style.css hides Buyer at 1100
          h('div', { class: 'fix-box' }, shareEditor(order), h('p', { class: 'note', role: 'status' }, state.finance.note)))) : null].filter(Boolean);
      })))) : h('p', { class: 'hint' }, 'Nothing assigned to them yet.'));
}

function personPanel(p) {
  const b = balanceOf(p.id);
  const l = ledger();
  const pays = l.payments.filter(x => x.person === p.id).sort((a, b2) => (b2.date > a.date ? 1 : -1));
  const editing2 = finDraft.editName !== null;
  return h('aside', { 'aria-label': 'Person' },
    h('div', { class: 'head' },
      h('span', { class: 'avatar big', 'aria-hidden': 'true' }, p.name.slice(0, 1).toUpperCase()),
      h('div', {}, h('h2', {}, p.name), p.note ? h('small', {}, p.note) : null)),
    h('div', { class: 'balance' },
      h('div', {}, h('small', {}, 'Earned'), h('b', {}, dollars(b.earned))),
      h('div', {}, h('small', {}, 'Paid'), h('b', {}, dollars(b.paid))),
      h('div', { class: b.balance > 0.004 ? 'due' : '' }, h('small', {}, b.balance > 0.004 ? 'You owe' : b.balance < -0.004 ? 'Paid ahead' : 'Settled'), h('b', {}, dollars(Math.abs(b.balance))))),
    h('h3', { class: 'sub' }, 'Record a payment'),
    h('div', { class: 'payform' },
      h('input', { 'data-fin': 'amount', value: finDraft.amount, placeholder: b.balance > 0 ? b.balance.toFixed(2) : 'Amount', inputmode: 'decimal', 'aria-label': 'Amount paid' }),
      h('input', { 'data-fin': 'date', type: 'date', value: finDraft.date || new Date().toISOString().slice(0, 10), 'aria-label': 'Date' }),
      h('input', { 'data-fin': 'payNote', value: finDraft.payNote, placeholder: 'Note, like card or crypto', 'aria-label': 'Note' }),
      h('button', { type: 'button', class: 'solid', 'data-do': 'addPayment', disabled: state.finance.busy }, 'Add')),
    h('p', { class: 'note', role: 'status' }, state.finance.note),
    h('h3', { class: 'sub' }, 'Work list for settling'),
    h('div', { class: 'payform' },
      h('label', {}, 'From', h('input', { 'data-fin': 'from', type: 'date', value: finDraft.from })),
      h('label', {}, 'To', h('input', { 'data-fin': 'until', type: 'date', value: finDraft.until })),
      h('button', { type: 'button', class: 'line', 'data-do': 'exportPerson' }, 'Export to Excel')),
    pays.length ? [h('h3', { class: 'sub' }, 'Payments'), h('table', { class: 'mini' }, h('tbody', {}, pays.map(x => h('tr', { 'data-key': x.id },
      h('td', { class: 'quiet' }, x.date), h('td', {}, x.note), h('td', { class: 'n' }, dollars(x.amount)),
      h('td', { class: 'n' }, h('button', { type: 'button', class: 'x', 'data-do': 'deletePayment', 'data-value': x.id, 'aria-label': 'Remove payment' }, cross()))))))] : null,
    editing2
      ? h('div', { class: 'payform' },
        h('input', { 'data-fin': 'editName', value: finDraft.editName, 'aria-label': 'Name' }),
        h('input', { 'data-fin': 'editNote', value: finDraft.editNote, placeholder: 'Note', 'aria-label': 'Note' }),
        h('button', { type: 'button', class: 'solid', 'data-do': 'savePerson' }, 'Save'))
      : h('div', { class: 'acts' },
        h('button', { type: 'button', class: 'line', 'data-do': 'editPerson' }, 'Rename'),
        h('button', { type: 'button', class: 'line', 'data-do': 'archivePerson' }, p.archived ? 'Bring back' : 'Hide from lists')));
}

function newPersonPanel() {
  return h('aside', { 'aria-label': 'Add person' },
    h('div', { class: 'head' }, h('div', {}, h('h2', {}, 'Add person'), h('small', {}, 'Someone whose stock you sell'))),
    h('label', { class: 'field' }, 'Name', h('input', { 'data-fin': 'name', value: finDraft.name, autocomplete: 'off' })),
    h('label', { class: 'field' }, 'Note', h('input', { 'data-fin': 'note', value: finDraft.note, placeholder: 'Optional, like how you pay them', autocomplete: 'off' })),
    h('div', { class: 'acts' }, h('button', { type: 'button', class: 'solid', 'data-do': 'addPerson', disabled: state.finance.busy }, 'Add')),
    h('p', { class: 'note', role: 'status' }, state.finance.note));
}

function financePage() {
  if (!state.hub) {
    return h('div', { class: 'empty setup' },
      h('h2', {}, 'Finance needs your server'),
      h('p', {}, 'Settings, Eldorado key, Shared server.'),
      h('button', { type: 'button', class: 'solid', 'data-do': 'settings' }, 'Open Settings'));
  }
  if (!state.finance.ready) return h('p', { class: 'empty' }, state.finance.note || 'Reading the ledger from your server.');
  const list = financeOrders();
  const open = sales90().filter(o => assignedUnits(o) < o.quantity).length;
  if (!financePick || (financePick.kind === 'person' && !people(true).some(p => p.id === financePick.id))) financePick = list[0] ? { kind: 'order', id: list[0].id } : null;
  const byPerson = financeShown === 'people';
  const pickOrder = financePick && financePick.kind === 'order' ? state.orders.find(o => o.id === financePick.id) : null;
  const pickPerson = financePick && financePick.kind === 'person' ? people(true).find(p => p.id === financePick.id) : null;
  const owed = (ledger().summary || []).reduce((a, s) => a + Math.max(0, s.balance), 0);
  return [
    h('div', { class: 'people' },
      people(false).map(personCard),
      h('button', { type: 'button', class: financePick && financePick.kind === 'new' ? 'person add on' : 'person add', 'data-do': 'newPerson', 'data-key': 'add' }, plus(), 'Add person'),
      h('div', { class: 'end' },
        h('span', { class: 'owed-all' }, h('small', {}, 'You owe in total'), dollars(owed)),
        h('button', { type: 'button', class: 'tool', 'data-do': 'exportAll' }, 'Export everyone'))),
    h('div', { class: 'bar' }, segment('Show orders', [['open', `To assign ${open}`], ['people', 'Per person'], ['all', 'All, 90 days']], pickPerson ? null : financeShown, 'financeShown')),
    h('div', { class: 'work' },
      h('div', { class: 'list' }, pickPerson ? personList(pickPerson) : byPerson
        ? (people(false).length ? h('div', { class: 'person-cols' }, people(false).map(p => personColumn(p, pickOrder))) : h('p', { class: 'empty' }, 'Add a person first.'))
        : list.length ? h('div', { class: 'scroll' }, h('table', { class: 'orders finance' },
          h('colgroup', {}, ['placed', 'item', 'buyer', 'qty', 'total', 'whose'].map(c => h('col', { class: 'f-' + c }))),
          h('thead', {}, h('tr', {}, ['Placed', 'Order', 'Buyer', 'Qty', 'Total', financeShown === 'open' ? 'Give to' : 'Whose'].map(t => h('th', { scope: 'col' }, t)))),
          h('tbody', {}, list.map(o => h('tr', {
            'data-key': o.id, 'data-do': 'pickFinOrder', 'data-order': o.id, tabindex: 0,
            class: pickOrder && o.id === pickOrder.id ? 'on' : null,
          },
            h('td', { class: 'quiet' }, dayOf(o.placed)), h('td', {}, h('div', { class: 'it' }, orderPicture(o), h('b', {}, o.title))), h('td', {}, o.buyer),
            h('td', {}, o.quantity), h('td', {}, o.total), h('td', {}, financeShown === 'open' ? assignCell(o) : sharesCell(o)))))))
          : h('p', { class: 'empty' }, financeShown === 'open' ? 'All assigned.' : 'No orders here.')),
      pickPerson ? personPanel(pickPerson) : financePick && financePick.kind === 'new' ? newPersonPanel() : pickOrder ? assignPanel(pickOrder)
        : byPerson ? h('aside', { 'aria-label': 'Fix an assignment' }, h('p', { class: 'insight' }, 'Click an order to change whose units it is.')) : null)];
}

// ---------- drawing ----------

const PAGES = { home: homePage, offers: offersPage, orders: ordersPage, sales: salesPage, customers: customersPage, finance: financePage, insights: insightsPage };

function draw() {
  document.body.dataset.theme = state.theme === 'light' ? 'light' : 'dark';
  const next = h('div', { id: 'app' }, h('div', { class: 'shell' }, band(state.order), topBar(), attention(), PAGES[tab]()));
  morph(document.getElementById('app'), next);
  // The page's security policy refuses style attributes, but not styles set from script.
  for (const node of document.querySelectorAll('[data-height]')) node.style.height = node.dataset.height + '%';
  for (const node of document.querySelectorAll('[data-width]')) node.style.width = node.dataset.width + '%';
  for (const node of document.querySelectorAll('[data-left]')) node.style.left = node.dataset.left + '%';
  tick();
}

// Only the clocks and ages change each second, so they skip the redraw.
function tick() {
  for (const node of document.querySelectorAll('[data-ago]')) node.textContent = agoText();
  for (const node of document.querySelectorAll('[data-due]')) {
    const order = state.orders.find(o => o.id === node.dataset.due);
    if (order) node.textContent = clock(order);
  }
  if (!state.order) return;
  for (const node of document.querySelectorAll('[data-clock]')) node.textContent = clock(state.order);
  for (const node of document.querySelectorAll('[data-fuse]')) node.setAttribute('width', Math.round(fuseShare(state.order) * 1000));
}

// ---------- input ----------

function editMessage(node, kind) {
  const price = node.dataset.edit === 'price';
  return [(kind === 'typed' ? (price ? 'typedPrice' : 'typedStock') : price ? 'enterPrice' : 'enterStock'), { id: rowOf(node), text: node.value }];
}

const CLICKS = {
  tab(target) {
    tab = target.dataset.value;
    bulk = null;
    draw();
  },
  view(target) {
    tab = 'offers';
    view = target.dataset.value;
    draw();
  },
  marketShown(target) {
    marketShown = target.dataset.value;
    draw();
  },
  ordersShown(target) {
    ordersShown = target.dataset.value;
    asking = null;
    draw();
  },
  pick(target) {
    if (target.dataset.id !== selected) drag = null;
    selected = target.dataset.id;
    draw();
  },
  pickOrder(target) {
    pickedOrder = target.dataset.order;
    asking = null;
    draw();
  },
  step(target) {
    editing = null;
    send(target.dataset.field === 'price' ? 'stepPrice' : 'stepStock', { id: rowOf(target), dir: +target.dataset.dir });
  },
  flipTheme: () => send('theme', { value: state.theme === 'light' ? 'dark' : 'light' }),
  sortFlip: () => send('sortFlip', {}),
  retry: target => send('retry', { id: rowOf(target) }),
  discard: target => send('cancel', { id: rowOf(target) }),
  retryAll: () => send('retryAll', {}),
  useSuggested(target) {
    settingPrice = null;
    send('enterPrice', { id: rowOf(target), text: target.dataset.value });
  },
  setAsk(target) {
    settingPrice = target.dataset.value;
    draw();
  },
  setNo() {
    settingPrice = null;
    draw();
  },
  dragSave() {
    if (drag) send('enterPrice', { id: drag.id, text: String(drag.price) });
    drag = null;
    draw();
  },
  updateNotes() {
    updateNotes = !updateNotes;
    draw();
  },
  dragCancel() {
    drag = null;
    draw();
  },
  financeShown(target) {
    financeShown = target.dataset.value;
    financePick = null;
    draw();
  },
  pickFinOrder(target) {
    financePick = { kind: 'order', id: target.dataset.order };
    draw();
  },
  fixFinOrder(target) {
    financeFix = financeFix === target.dataset.order ? null : target.dataset.order;
    draw();
  },
  pickPerson(target) {
    if (!(financePick && financePick.kind === 'person' && financePick.id === target.dataset.value)) financeFix = null;
    financePick = { kind: 'person', id: target.dataset.value };
    finDraft.editName = finDraft.editNote = null;
    finDraft.amount = finDraft.payNote = '';
    draw();
  },
  newPerson() {
    financePick = { kind: 'new' };
    draw();
  },
  addPerson() {
    send('addPerson', { name: finDraft.name, note: finDraft.note });
    finDraft.name = finDraft.note = '';
  },
  giveAll(target) {
    const o = fixOrder();
    if (!o) return;
    if (!(o.id in draftSeen)) draftSeen[o.id] = changedOf(ledgerOrder(o.id));
    assignDraft[o.id] = { [target.dataset.value]: o.quantity };
    draw();
  },
  giveRest(target) {
    const o = fixOrder();
    if (!o) return;
    const draft = draftFor(o);
    const left = o.quantity - Object.values(draft).reduce((a, n) => a + (Number(n) || 0), 0);
    if (left > 0) draft[target.dataset.value] = (Number(draft[target.dataset.value]) || 0) + left;
    draw();
  },
  giveHalf(target) {
    const o = fixOrder();
    if (!o) return;
    draftFor(o)[target.dataset.value] = Math.round(o.quantity / 2);
    draw();
  },
  quickAssign(target) {
    const o = state.orders.find(x => x.id === target.dataset.order);
    if (!o) return;
    const l = ledgerOrder(o.id);
    const shares = {};
    for (const s of l ? l.shares : []) shares[s.person] = (shares[s.person] || 0) + s.units;
    const left = o.quantity - Object.values(shares).reduce((a, n) => a + n, 0);
    if (left <= 0) return;
    shares[target.dataset.person] = (shares[target.dataset.person] || 0) + left;
    send('assign', { order: o.id, changed: changedOf(l), shares: Object.entries(shares).map(([person, units]) => ({ person, units })) });
    delete assignDraft[o.id];
    delete draftSeen[o.id];
  },
  shareStep(target) {
    const o = fixOrder();
    if (!o) return;
    const draft = draftFor(o);
    draft[target.dataset.value] = Math.max(0, (Number(draft[target.dataset.value]) || 0) + Number(target.dataset.dir));
    draw();
  },
  saveShares() {
    const id = fixOrderId();
    if (!id) return;
    const draft = assignDraft[id] || {};
    const changed = id in draftSeen ? draftSeen[id] : changedOf(ledgerOrder(id));
    send('assign', { order: id, changed, shares: Object.entries(draft).filter(([, n]) => Number(n) > 0).map(([person, units]) => ({ person, units: Number(units) })) });
    delete assignDraft[id];
    delete draftSeen[id];
  },
  clearShares() {
    const id = fixOrderId();
    if (!id) return;
    send('assign', { order: id, changed: changedOf(ledgerOrder(id)), shares: [] });
    delete assignDraft[id];
    delete draftSeen[id];
  },
  addPayment() {
    const b = balanceOf(financePick.id);
    send('addPayment', { person: financePick.id, amount: finDraft.amount || (b.balance > 0 ? b.balance.toFixed(2) : ''), date: finDraft.date || new Date().toISOString().slice(0, 10), note: finDraft.payNote });
    finDraft.amount = finDraft.payNote = '';
  },
  deletePayment: target => send('deletePayment', { payment: target.dataset.value }),
  exportPerson: () => send('exportFinance', { person: financePick.id, from: finDraft.from, until: finDraft.until }),
  exportAll: () => send('exportFinance', { person: '', from: finDraft.from, until: finDraft.until }),
  editPerson() {
    const p = people(true).find(x => x.id === financePick.id);
    finDraft.editName = p.name;
    finDraft.editNote = p.note || '';
    finDraft.editSeen = changedOf(p);
    draw();
  },
  savePerson() {
    send('editPerson', { person: financePick.id, changed: finDraft.editSeen, name: finDraft.editName, note: finDraft.editNote });
    finDraft.editName = finDraft.editNote = null;
  },
  archivePerson() {
    const p = people(true).find(x => x.id === financePick.id);
    send('editPerson', { person: p.id, changed: changedOf(p), archived: !p.archived });
  },
  period: target => send('salesDays', { days: +target.dataset.value }),
  openOffer(target) {
    const offer = state.offers.find(o => o.id === target.dataset.offer);
    if (!offer) return;
    tab = 'offers';
    view = isClosed(offer) ? 'closed' : offer.stateText === 'Paused' ? 'paused' : 'live';
    selected = offer.id;
    if (!shown().some(o => o.id === offer.id)) filter = '';   // a search typed earlier must not hide the offer asked for
    draw();
    // Bring the row into sight and give it focus, so the arrow keys and Enter carry on from there.
    const row = document.querySelector(`.offers [data-id="${CSS.escape(offer.id)}"]`);
    if (!row) return;
    row.focus({ preventScroll: true });
    row.scrollIntoView({ block: 'center' });
  },
  notice: target => send('openUrl', { url: target.dataset.url }),
  openTop: target => send('openUrl', { url: target.dataset.url }),
  dismiss: target => send('dismissNotice', { id: target.dataset.value }),
  ack: () => send('ack', { id: state.order && state.order.id }),
  openOrder: () => send('openOrder', { id: state.order && state.order.id }),
  openOnSite: () => send('openUrl', { url: '/order/' + pickedOrder }),
  bulk() {
    bulk = bulk ? null : { game: '', percent: '', promise: 'Minute20', ask: null };
    draw();
  },
  bulkPriceAsk() {
    bulk.ask = 'price';
    draw();
  },
  bulkDeliveryAsk() {
    bulk.ask = 'delivery';
    draw();
  },
  bulkNo() {
    bulk.ask = null;
    draw();
  },
  bulkPriceYes() {
    send('bulkPrice', { game: bulk.game, percent: parseFloat(bulk.percent) });
    bulk.ask = null;
    draw();
  },
  bulkDeliveryYes() {
    send('bulkDelivery', { game: bulk.game, value: bulk.promise });
    bulk.ask = null;
    draw();
  },
  deliverAsk() {
    asking = 'deliver';
    draw();
  },
  extendAsk() {
    asking = 'extend';
    draw();
  },
  orderNo() {
    asking = null;
    draw();
  },
  deliverYes() {
    send('deliver', { id: pickedOrder });
    asking = null;
    draw();
  },
  extendYes() {
    send('extend', { id: pickedOrder, time: extendTime, reason: extendReason });
    asking = null;
    draw();
  },
};

document.addEventListener('click', event => {
  const target = event.target.closest('[data-do]');
  if (!target) return;
  const action = target.dataset.do;
  if (CLICKS[action]) CLICKS[action](target);
  else send(action, { id: selected });
});

document.addEventListener('input', event => {
  const node = event.target;
  if (node.dataset.filter) {
    filter = node.value;
    draw();
  } else if (node.dataset.edit) {
    editing = node;
    const [type, data] = editMessage(node, 'typed');
    send(type, data);
  } else if (node.dataset.fin) {
    editing = node;
    if (node.dataset.fin === 'units') {
      draftFor(fixOrder())[node.dataset.person] = node.value.replace(/\D/g, '');
      draw();
    } else {
      finDraft[node.dataset.fin] = node.value;
    }
  } else if (node.dataset.bundle) {
    editing = node;
  } else if (node.dataset.bulk === 'percent') {
    bulk.percent = node.value;
    bulk.ask = null;
    draw();
  }
});

document.addEventListener('change', event => {
  const node = event.target;
  if (node.dataset.sortPick) send('sort', { value: node.value });
  else if (node.dataset.bundle) {
    editing = null;
    send('bundle', { id: node.dataset.bundle, text: node.value });
  }
  else if (node.dataset.extend === 'time') extendTime = node.value;
  else if (node.dataset.extend === 'reason') extendReason = node.value;
  else if (node.dataset.bulk) {
    bulk[node.dataset.bulk] = node.value;
    bulk.ask = null;
    draw();
  }
});

document.addEventListener('focusin', event => {
  const node = event.target;
  if (node.tagName === 'TR' && node.dataset.id && node.dataset.id !== selected) {
    selected = node.dataset.id;
    draw();
  }
  if (!node.dataset.edit) return;
  const id = rowOf(node);
  if (id !== selected) {
    selected = id;
    draw();
  }
  node.select();
  fresh = node;
});

document.addEventListener('mouseup', event => {
  if (event.target === fresh) event.preventDefault();
  fresh = null;
});

document.addEventListener('focusout', event => {
  if (event.target === editing) editing = null;
});

document.addEventListener('mouseover', event => {
  const column = event.target.closest('[data-tip]');
  const tip = document.getElementById('tip');
  if (tip) tip.textContent = column ? column.dataset.tip : tip.dataset.rest;
});

// ---------- dragging a price on the sales line ----------

let dragFrame = 0;

function priceFrom(line, clientX) {
  const box = line.getBoundingClientRect();
  const lo = Number(line.dataset.lo), hi = Number(line.dataset.hi);
  const share = Math.max(0, Math.min(1, (clientX - box.left) / (box.width || 1)));
  let price = lo + share * (hi - lo);
  // Within a few pixels of Typical, Cheapest or either end of Most sales, the price sticks to it.
  const o = state.offers.find(x => x.id === line.dataset.drag), p = o && priceOf(o);
  const near = 8 / (box.width || 1) * (hi - lo);
  for (const v of p ? [p.soldAt, p.low, p.from, p.to] : []) if (v != null && Math.abs(price - v) <= near) return fit(v);
  return Math.max(price >= 0.01 ? 0.01 : 0.00001, fit(price));
}

function dragTo(line, clientX) {
  drag = { id: line.dataset.drag, price: priceFrom(line, clientX), held: true };
  if (!dragFrame) dragFrame = requestAnimationFrame(() => { dragFrame = 0; draw(); });
}

document.addEventListener('pointerdown', event => {
  const line = event.target.closest('[data-drag]');
  if (!line || event.button !== 0) return;
  event.preventDefault();
  line.focus({ preventScroll: true });
  dragTo(line, event.clientX);
});

document.addEventListener('pointermove', event => {
  if (!drag || !drag.held) return;
  const line = document.querySelector(`[data-drag="${CSS.escape(drag.id)}"]`);
  if (line) dragTo(line, event.clientX);
});

document.addEventListener('pointerup', () => {
  if (!drag || !drag.held) return;
  drag.held = false;
  draw();
});

function nudge(line, direction) {
  const o = state.offers.find(x => x.id === line.dataset.drag);
  if (!o) return;
  const from = drag && drag.id === o.id ? drag.price : o.livePrice;
  const step = from < 0.01 ? 0.0001 : from < 1 ? 0.01 : 0.1;
  drag = { id: o.id, price: Math.max(step, fit(from + step * direction)), held: false };
  draw();
}

function moveSelection(key) {
  const list = shown();
  const at = list.findIndex(o => o.id === selected);
  const next = list[Math.max(0, Math.min(at + (key === 'ArrowDown' ? 1 : -1), list.length - 1))];
  if (!next) return;
  selected = next.id;
  draw();
  const row = document.querySelector(`[data-id="${CSS.escape(next.id)}"]`);
  if (!row) return;
  row.focus({ preventScroll: true });
  row.scrollIntoView({ block: 'nearest', inline: 'nearest' });
}

document.addEventListener('keydown', event => {
  const node = event.target;
  const typing = node.tagName === 'INPUT' || node.tagName === 'SELECT';
  const field = node.dataset.edit;
  if (event.ctrlKey && ['=', '+', '-', '0'].includes(event.key)) {
    event.preventDefault();
    send('zoom', { step: event.key === '0' ? 0 : event.key === '-' ? -1 : 1 });
  } else if (event.ctrlKey && event.key.toLowerCase() === 'f') {
    event.preventDefault();
    if (tab !== 'offers') {
      tab = 'offers';
      draw();
    }
    const box = document.getElementById('filter');
    box.focus();
    box.select();
  } else if (field && (event.key === 'ArrowUp' || event.key === 'ArrowDown')) {
    event.preventDefault();
    editing = null;
    send(field === 'price' ? 'stepPrice' : 'stepStock', { id: rowOf(node), dir: event.key === 'ArrowUp' ? 1 : -1 });
  } else if (field && event.key === 'Enter') {
    editing = null;
    const [type, data] = editMessage(node, 'enter');
    send(type, data);
  } else if (node.dataset.drag && (event.key === 'ArrowLeft' || event.key === 'ArrowRight')) {
    event.preventDefault();
    nudge(node, event.key === 'ArrowRight' ? 1 : -1);
  } else if (node.dataset.drag && event.key === 'Enter' && drag) {
    CLICKS.dragSave();
  } else if (event.key === 'Escape' && drag) {
    drag = null;
    draw();
  } else if (event.key === 'Escape') {
    editing = null;
    send('cancel', { id: field ? rowOf(node) : selected });
    if (typing) node.blur();
  } else if (event.key === 'Enter' && node.tagName === 'TR' && node.dataset.id) {
    selected = node.dataset.id;
    draw();
    const box = node.querySelector('[data-edit=price]');
    if (box) box.focus();
  } else if (tab === 'offers' && !typing && (event.key === 'ArrowDown' || event.key === 'ArrowUp')) {
    event.preventDefault();
    moveSelection(event.key);
  }
});

window.managerState = next => {
  state = next;
  draw();
};

setInterval(tick, 1000);
addEventListener('resize', () => draw());
// A drawing error leaves the page stale with no sign, so it goes to the app's log instead.
addEventListener('error', event => send('pageError', { text: `${event.message} at ${event.lineno}:${event.colno}` }));

if (host) {
  host.addEventListener('message', event => window.managerState(event.data));
  draw();
  send('ready');
} else {
  draw();
  const sample = document.createElement('script');
  sample.src = 'sample.js';
  document.body.append(sample);
}
