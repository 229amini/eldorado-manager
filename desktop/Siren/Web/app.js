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
let tab = 'offers';
let view = 'live';
let filter = '';
let selected = null;
let pickedOrder = null;
let ordersShown = 'open';
let marketShown = 'missing';
let bulk = null;      // { game, percent, promise, ask } while the bulk panel is open
let asking = null;    // the order action waiting for its second click: 'deliver' or 'extend'
let extendTime = 'Minute15';
let extendReason = 'WaitingForBuyer';
let editing = null;   // the field being typed in, which a redraw must leave alone
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
  s('path', { d: 'M84 172v-58a44 44 0 0 1 88 0v58z' }),
  s('rect', { x: 62, y: 178, width: 132, height: 26, rx: 13 }),
  s('path', { d: 'M58 72a70 70 0 0 0-14 46M198 72a70 70 0 0 1 14 46', class: 'waves' }));
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
  if (isClosed(o)) return h('span', { class: 'plain' }, value);
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
  const tabs = [['offers', 'Offers', state.offers.filter(isLive).length], ['orders', 'Orders', open], ['sales', 'Sales', null], ['insights', 'Insights', null]];
  return h('header', { class: 'top' },
    h('b', { class: 'name' }, h('span', { class: 'mark' }, logo()), 'Siren'),
    h('nav', { role: 'tablist', 'aria-label': 'Sections' },
      tabs.map(([key, label, count]) => h('button', {
        type: 'button', role: 'tab', 'aria-selected': String(tab === key), 'data-do': 'tab', 'data-value': key,
      }, label, count ? h('span', { class: key === 'orders' ? 'count hot' : 'count' }, count) : null))),
    h('div', { class: 'end' }, checked(),
      h('button', { type: 'button', class: 'tool', 'data-do': 'refresh' }, 'Check now'),
      h('button', { type: 'button', class: 'tool round', 'data-do': 'flipTheme', title: state.theme === 'light' ? 'Dark mode' : 'Light mode', 'aria-label': state.theme === 'light' ? 'Dark mode' : 'Light mode' }, state.theme === 'light' ? moon() : sun()),
      h('button', { type: 'button', class: 'tool', 'data-do': 'settings' }, 'Settings')));
}

// Only what needs a decision reaches this strip, and it disappears when nothing does.
function attention() {
  const items = [];
  const push = (key, text, attrs, hot) => items.push(h('button', Object.assign({ type: 'button', 'data-key': key, class: hot ? 'flag hot' : 'flag' }, attrs), text));
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
  if (!items.length && state.checked) items.push(h('span', { class: 'flag calm', 'data-key': 'calm' }, "You're all caught up. Nothing needs you right now."));
  return items.length ? h('div', { class: 'attention', role: 'status' }, items) : null;
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
    h('colgroup', {}, ['offer', 'place', 'rank', 'low', 'price', 'stock', 'sold'].map(c => h('col', { class: 'c-' + c }))),
    h('thead', {}, h('tr', {}, ['Offer', 'Place', 'Price rank', 'Lowest', 'Your price', 'Stock', 'Sold 30 d'].map(t => h('th', { scope: 'col' }, t)))),
    h('tbody', {}, list.map(o => h('tr', {
      'data-key': o.id, 'data-do': 'pick', 'data-id': o.id, tabindex: 0,
      class: pick && o.id === pick.id ? 'on' : null, 'aria-selected': String(!!pick && o.id === pick.id),
    },
      h('td', {}, h('div', { class: 'it' }, picture(o, 'thumb'), h('b', {}, o.title))),
      h('td', { class: 'place' }, gate(o), ' ', move(o), o.site !== null ? h('small', {}, siteNote(o)) : null),
      h('td', {}, o.listed ? o.rank : ''),
      h('td', {}, o.lowest === null ? '' : o.lowest === o.livePrice ? h('span', { class: 'you' }, 'You') : o.lowestText),
      h('td', {}, quick(o, 'price', 'Price', o.priceDraft, o.pendingPrice)),
      h('td', {}, quick(o, 'stock', 'Stock', o.stockDraft, o.pendingStock)),
      h('td', { class: o.sold ? null : 'quiet' }, o.sold))))));
}

function actions(o) {
  if (o.needsConfirm) {
    return h('div', { class: 'acts' },
      h('button', { type: 'button', class: 'solid', 'data-do': 'confirm' }, 'Send this price'),
      h('button', { type: 'button', class: 'line', 'data-do': 'cancelPrice' }, 'Keep my price'));
  }
  if (isClosed(o)) return null;
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
    h('header', {}, h('h3', {}, 'Worth doing'), h('span', {}, 'Suggestions only. Siren changes nothing by itself.')),
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
  const blank = !market.sweeps ? 'Siren checks the public listing every 30 minutes. The first numbers need two checks.'
    : marketShown === 'missing' && all.length ? 'Everything moving right now is already in your shop.' : 'No stock has moved yet. Give it a few hours.';
  return h('section', { class: 'card items market' },
    h('header', {}, h('h3', {}, 'Selling on the market'), h('span', {}, market.early ? h('b', { class: 'pill' }, 'Early') : null, ' ', label)),
    h('div', { class: 'market-bar' },
      segment('Show items', [['missing', 'Not in your shop'], ['all', 'Everything']], marketShown, 'marketShown'),
      h('p', { class: 'hint' }, 'An estimate from other sellers’ stock falling over the last 7 days. Stock can also fall for other reasons.')),
    rows.length ? h('table', {},
      h('colgroup', {}, ['item', 'sellers', 'falls', 'low', 'typical', 'yours'].map(c => h('col', { class: 'm-' + c }))),
      h('thead', {}, h('tr', {}, h('th', { scope: 'col' }, 'Item'), ['Sellers moving', 'Stock falls', 'Cheapest', 'Typical', 'You'].map(t => h('th', { scope: 'col', class: 'n' }, t)))),
      h('tbody', {}, rows.map(r => h('tr', { 'data-key': r.key, 'data-do': r.offer ? 'openOffer' : null, 'data-offer': r.offer || null, class: r.offer ? 'link' : null },
        h('td', {}, h('div', { class: 'it' }, r.picture ? h('img', { class: 'thumb', src: r.picture, alt: '', draggable: 'false' }) : h('span', { class: 'thumb none' }), h('div', { class: 'named' }, h('b', {}, r.name), r.kind ? h('small', {}, r.kind) : null))),
        h('td', { class: 'n' }, r.movers), h('td', { class: 'n' }, r.drops),
        h('td', { class: 'n' }, '$' + r.low), h('td', { class: 'n' }, '$' + r.typical),
        h('td', { class: 'n' }, r.offer ? '$' + r.yours : h('span', { class: 'pill' }, 'Not listed')))))) : h('p', { class: 'hint' }, blank));
}

function changeCard(changes) {
  const place = (before, now) => before && now ? `#${before} to #${now}` : 'Not read yet';
  return h('section', { class: 'card items changes' },
    h('header', {}, h('h3', {}, 'What your price changes did'), h('span', {}, 'Orders in the same number of days either side')),
    changes.length ? h('table', {},
      h('colgroup', {}, ['item', 'day', 'price', 'place', 'orders'].map(c => h('col', { class: 'p-' + c }))),
      h('thead', {}, h('tr', {}, h('th', { scope: 'col' }, 'Item'), ['Day', 'Price', 'Place', 'Orders'].map(t => h('th', { scope: 'col', class: 'n' }, t)))),
      h('tbody', {}, changes.map(c => h('tr', { 'data-key': c.key, 'data-do': 'openOffer', 'data-offer': c.offer, class: 'link' },
        h('td', {}, h('b', {}, c.title)), h('td', { class: 'n quiet' }, c.day),
        h('td', { class: 'n' }, triangle(c.up), `${c.from} to ${c.to}`),
        h('td', { class: 'n' }, place(c.placeBefore, c.placeNow)),
        h('td', { class: 'n' }, c.early ? h('span', { class: 'quiet' }, 'Too soon') : `${c.soldBefore} to ${c.soldAfter}`))))) : h('p', { class: 'hint' }, 'Siren keeps a daily record of each offer from now on. Change a price and its effect shows up here after a few days.'));
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

// ---------- drawing ----------

const PAGES = { offers: offersPage, orders: ordersPage, sales: salesPage, insights: insightsPage };

function draw() {
  document.body.dataset.theme = state.theme === 'light' ? 'light' : 'dark';
  const next = h('div', { id: 'app' }, h('div', { class: 'shell' }, band(state.order), topBar(), attention(), PAGES[tab]()));
  morph(document.getElementById('app'), next);
  // The page's security policy refuses style attributes, but not styles set from script.
  for (const node of document.querySelectorAll('[data-height]')) node.style.height = node.dataset.height + '%';
  for (const node of document.querySelectorAll('[data-width]')) node.style.width = node.dataset.width + '%';
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
  period: target => send('salesDays', { days: +target.dataset.value }),
  openOffer(target) {
    const offer = state.offers.find(o => o.id === target.dataset.offer);
    if (!offer) return;
    tab = 'offers';
    view = isClosed(offer) ? 'closed' : offer.stateText === 'Paused' ? 'paused' : 'live';
    selected = offer.id;
    draw();
  },
  notice: target => send('openUrl', { url: target.dataset.url }),
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
  } else if (node.dataset.bulk === 'percent') {
    bulk.percent = node.value;
    bulk.ask = null;
    draw();
  }
});

document.addEventListener('change', event => {
  const node = event.target;
  if (node.dataset.sortPick) send('sort', { value: node.value });
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
  if (event.ctrlKey && event.key.toLowerCase() === 'f') {
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

window.sirenState = next => {
  state = next;
  draw();
};

setInterval(tick, 1000);

if (host) {
  host.addEventListener('message', event => window.sirenState(event.data));
  draw();
  send('ready');
} else {
  draw();
  const sample = document.createElement('script');
  sample.src = 'sample.js';
  document.body.append(sample);
}
