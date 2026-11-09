'use strict';
// Made-up offers for reviewing the dashboard in a plain browser: open index.html#dark or #light.
(function () {
  const art = (a, b) => 'data:image/svg+xml,' + encodeURIComponent(
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 400 400"><defs><linearGradient id="g" x2="1" y2="1"><stop stop-color="${a}"/><stop offset="1" stop-color="${b}"/></linearGradient></defs><rect width="400" height="400" fill="url(#g)"/></svg>`);
  const rivals = price => [
    { place: 1, seller: 'QuickLoot', price: (price * 1.2).toFixed(2), delivers: '3 min', score: '100%', ratings: '22,667', you: false },
    { place: 2, seller: 'NorthVault', price: (price * 0.9).toFixed(2), delivers: '5 min', score: '99.8%', ratings: '9,120', you: false },
    { place: 3, seller: 'ArcDepot', price: price.toFixed(2), delivers: '4 min', score: '99.5%', ratings: '4,312', you: false },
    { place: 31, seller: 'You', price: price.toFixed(2), delivers: '11 min', score: '100%', ratings: '312', you: true },
  ];
  const offer = (id, title, kind, site, move, place, total, lowest, price, stock, sold, a, b, stateText = 'Active') => {
    const prices = Array.from({ length: 50 }, (_, i) => +(lowest + (price * 1.4 - lowest) * i / 49).toFixed(4));
    const cheaper = prices.filter(p => p < price).length;
    const gap = site - 24;
    const listed = stateText === 'Active';
    return {
      id, title, kind, game: 'Arc Raiders', gameId: '319', category: 'CustomItem', delivery: 'Minute20', sold, cheaperCount: cheaper,
      gaps: listed ? { deliveryYou: '11 min', deliveryTop: '4 min', deliveryBehind: gap > 0, ratingsYou: '312', ratingsTop: '9,120', ratingsBehind: gap > 0, priceTop: (price * 1.1).toFixed(2), priceBehind: false } : null,
      site: listed ? site : null, move: listed ? move : 0, rank: `${place} of ${total}`, lowest, lowestText: lowest.toFixed(2), livePrice: price,
      priceDraft: price.toFixed(2), stockDraft: String(stock), currency: 'USD', listed, stateText, canToggle: stateText !== 'Closed', toggleText: listed ? 'Pause offer' : 'Resume offer',
      cheaper: cheaper ? `${cheaper} cheaper` : 'Nobody cheaper', picture: art(a, b), largePicture: art(a, b), note: '', needsConfirm: false, showUndo: false,
      pendingPrice: false, pendingStock: false, rivals: listed ? rivals(price) : [],
      insight: !listed ? '' : gap > 0 ? `${gap} places from page 1, with ${cheaper} sellers cheaper than you.` : 'Buyers see this offer on page 1.',
    };
  };
  const now = Date.now();
  const order = (id, title, buyer, quantity, total, state, minutes, extra) => Object.assign({
    id, title, buyer, quantity, total, state, open: state === 'Paid' || state === 'Disputed', placed: now - 3600000, window: 1200,
    deadline: state === 'Paid' ? now / 1000 + minutes * 60 : null, took: '', review: '', dispute: '', note: '',
  }, extra);
  const daily = Array.from({ length: 30 }, (_, i) => {
    const count = [0, 1, 0, 2, 3, 1, 0, 0, 2, 1][i % 10];
    return { day: `Oct ${i + 1}`, amount: +(count * (3.5 + i % 4)).toFixed(2), count };
  });
  const theme = (location.hash || '#dark').slice(1);
  const quiet = location.search.includes('quiet');
  const state = {
    theme, alarms: true, ring: true, sort: 'attention', problem: null, offline: false,
    checked: now - 12000, siteChecked: now - 95000,
    order: quiet ? null : {
      id: 'demo', title: '13,500 V-Bucks top up', price: '64.90 USD', more: '', link: 'https://www.eldorado.gg/',
      deadline: now / 1000 + 480, window: 1200,
    },
    offers: [
      offer('1', 'Copper Coil', 'Recyclables, Copper Coil', 180, -14, 45, 271, 0.01, 0.09, 120, 6, '#7a4dff', '#ffb514'),
      offer('2', 'Rusted Gear', 'Materials, Rusted Gear', 67, -2, 1, 88, 0.05, 0.05, 40, 0, '#00b7c3', '#0b3d91'),
      offer('3', 'Scrap Plate', 'Materials, Scrap Plate', 31, -5, 6, 74, 0.05, 0.08, 210, 2, '#ff6a3d', '#5e1b0b'),
      offer('4', 'Arc Cell', 'Gear, Arc Cell', 44, 0, 13, 92, 0.12, 0.22, 0, 11, '#6d7bff', '#1a1f5e'),
      offer('5', 'Plasma Core', 'Materials, Plasma Core', 7, 3, 4, 96, 0.31, 0.42, 18, 4, '#e3008c', '#3a0b5e'),
      offer('6', 'Night Visor', 'Gear, Night Visor', 2, 0, 1, 41, 1.9, 1.9, 6, 9, '#a4c400', '#14532d'),
      offer('7', 'Heavy Fuze', 'Gear, Heavy Fuze', 12, 0, 9, 120, 0.07, 0.11, 64, 3, '#ffd166', '#7a4a00'),
      offer('8', 'Sun Shard', 'Materials, Sun Shard', 5, 0, 2, 33, 3.1, 3.2, 12, 1, '#ff9a3d', '#7a1b4a'),
      { ...offer('9', 'Arc Raiders Coins', 'Currency', 4, 0, 4, 147, 6, 6.93, 2, 14, '#000', '#000'), unit: '1M', picture: '', largePicture: '', category: 'Currency' },
      offer('10', 'Old Bundle', 'Gear, Old Bundle', 0, 0, 0, 0, 1, 1.4, 3, 0, '#555', '#222', 'Paused'),
      offer('11', 'Season Pass', 'Gear, Season Pass', 0, 0, 0, 0, 1, 2.2, 0, 0, '#444', '#111', 'Closed'),
    ],
    orders: [
      order('o1', 'Plasma Core', 'marko_77', 12, '5.04 USD', 'Paid', 9),
      order('o2', 'Arc Raiders Coins', 'lunatrade', 2000, '11.80 USD', 'Disputed', 0, { dispute: 'Item not received' }),
      order('o3', 'Heavy Fuze', 'sora', 3, '0.33 USD', 'Received', 0, { took: '4 min', review: 'Positive' }),
      order('o4', 'Night Visor', 'dex', 1, '1.90 USD', 'Canceled', 0),
      order('o5', 'Arc Cell', 'hana', 5, '1.10 USD', 'Completed', 0, { took: '12 min', review: 'Negative' }),
    ],
    notices: quiet ? [] : [{ id: 'n1', title: 'A buyer messaged you', text: 'Copper Coil, marko_77', link: '/dashboard/orders/sold' }],
    sales: {
      days: 30, ready: true, count: 41, amount: 318.4, earlierAmount: 251.1, earlierCount: 33, clearing: 96.2, clearingCount: 12, cleared: 222.2, fees: 11.9, net: 210.3, daily,
      kinds: [{ name: 'Coins', count: 19, amount: 240.1 }, { name: 'Items', count: 22, amount: 78.3 }],
    },
    best: [
      { title: 'Coins', orders: 19, units: 38000, amount: '240.10', currency: 'USD', last: now - 864e5, offer: '9', why: '' },
      { title: 'Ember Blade', orders: 3, units: 3, amount: '16.40', currency: 'USD', last: now - 3 * 864e5, offer: '', why: 'Not listed' },
      { title: 'Arc Cell', orders: 2, units: 4, amount: '5.25', currency: 'USD', last: now - 5 * 864e5, offer: '4', why: 'Out of stock' },
    ],
    back: [
      { title: 'Ember Blade', orders: 3, units: 3, amount: '16.40', currency: 'USD', last: now - 3 * 864e5, offer: '', why: 'Not listed' },
      { title: 'Arc Cell', orders: 2, units: 4, amount: '5.25', currency: 'USD', last: now - 5 * 864e5, offer: '4', why: 'Out of stock' },
    ],
    insights: {
      days: 30, sold: 41, lostAmount: 18.6, lostCount: 4, typical: '17 min', promise: '30 min', over: 5, buyers: 36, again: 5, good: 27, bad: 1, unreviewed: 13,
      hours: Array.from({ length: 24 }, (_, hour) => ({ hour, sold: [1, 0, 0, 0, 0, 0, 1, 1, 2, 2, 3, 2, 3, 4, 3, 2, 3, 4, 5, 3, 2, 1, 1, 1][hour], late: [0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 0, 0, 0, 0][hour] })),
      moves: [
        { key: 'late', kind: 'fix', title: 'Late orders cost you $18.60', text: 'Eldorado cancelled 4 orders because delivery missed the promise. 3 of 4 came in between 18:00 and 00:00. A longer promise, or pausing offers while you\u2019re away, stops this.', offer: '' },
        { key: 'raise9', kind: 'price', title: 'Arc Raiders Coins: room to raise', text: 'You\u2019re the only seller at 6.93 and the next is at 7.60. 7.59 still keeps you cheapest. At the last 30 days\u2019 sales that\u2019s about $22.80 more, if sales hold.', offer: '9' },
        { key: 'low4', kind: 'stock', title: 'Night Visor: running low', text: '6 left and 40 sold in 30 days, so about 5 days of stock at this pace.', offer: '6' },
        { key: 'idle', kind: 'note', title: '4 of 9 live offers sold nothing', text: 'Coins brought in 75% of your sales in 30 days. The market list below shows what other sellers are moving, if you want to swap the quiet ones.', offer: '' },
      ],
      changes: [{ key: 'c1', offer: '7', title: 'Heavy Fuze', day: '2 Nov', from: '0.05', to: '0.04', up: false, placeBefore: 92, placeNow: 71, soldBefore: 1, soldAfter: 4, daysAfter: 4, early: false }, { key: 'c2', offer: '1', title: 'Copper Coil', day: '5 Nov', from: '0.08', to: '0.09', up: true, placeBefore: 170, placeNow: 180, soldBefore: 0, soldAfter: 0, daysAfter: 1, early: true }],
      market: {
        sweeps: 34, since: '6 Nov', early: true, seen: 1500, total: 55383,
        rows: [
          { key: 'a', name: 'Mechanical Components', kind: 'Materials', picture: art('#7a4dff', '#1a1f5e'), drops: 9, movers: 6, low: '0.03', soldAt: '0.05', topSeller: 'marko_77', topPrice: '0.04', topSales: 4, topLink: '/arc-raiders-items/oi/a1', offer: '', yours: '', above: false },
          { key: 'b', name: 'Herbal Bandage', kind: 'Quick Use', picture: art('#00b7c3', '#0b3d91'), drops: 7, movers: 5, low: '0.05', soldAt: '0.09', topSeller: 'a_very_long_seller_name_here', topPrice: '0.08', topSales: 3, topLink: '/arc-raiders-items/oi/b1', offer: '', yours: '', above: false },
          { key: 'c', name: 'Heavy Fuze Grenade', kind: 'Quick Use', picture: art('#ffd166', '#7a4a00'), drops: 5, movers: 3, low: '0.04', soldAt: '0.06', topSeller: 'sora', topPrice: '0.05', topSales: 2, topLink: '/arc-raiders-items/oi/c1', offer: '7', yours: '0.11', above: true },
          { key: 'd', name: 'Shield Recharger', picture: '', drops: 3, movers: 2, low: '0.12', soldAt: '', topSeller: '', topPrice: '', topSales: 0, topLink: '', offer: '', yours: '', above: false },
        ],
      },
    },
    bulkNote: '', bulkBusy: false,
  };
  const find = id => state.offers.find(o => o.id === id);
  const fields = { Price: ['priceDraft', 'pendingPrice'], Stock: ['stockDraft', 'pendingStock'] };
  window.sampleLog = [];
  window.sampleSend = (type, data) => {
    window.sampleLog.push(Object.assign({ type }, data));
    const o = data && find(data.id);
    const match = /^(step|typed|enter)(Price|Stock)$/.exec(type);
    if (match && o) {
      const [draft, pending] = fields[match[2]];
      if (match[1] === 'step') o[draft] = (Number(o[draft]) + data.dir * (match[2] === 'Price' ? 0.01 : 1)).toFixed(match[2] === 'Price' ? 2 : 0);
      else o[draft] = data.text;
      o[pending] = match[1] !== 'enter';
      o.note = match[1] === 'enter' ? 'Saved' : 'Sending 6 seconds after your last change.';
    }
    if (type === 'cancel' && o) {
      o.pendingPrice = o.pendingStock = false;
      o.note = '';
    }
    if (type === 'theme') state.theme = data.value;
    if (type === 'sort') state.sort = data.value;
    if (type === 'sortFlip') {
      state.descending = !state.descending;
      state.offers.reverse();
    }
    if (type === 'ack') state.order = null;
    if (type === 'dismissNotice') state.notices = state.notices.filter(n => n.id !== data.id);
    if (type === 'salesDays') state.sales.days = data.days;
    window.sirenState(state);
  };
  window.sirenState(state);
}());
