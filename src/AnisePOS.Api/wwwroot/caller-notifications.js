(() => {
  'use strict';
  const key = 'anise.caller.notifications.v1';
  const dismissedKey = 'anise.caller.dismissed.v1';
  const cursorKey = 'anise.caller.cursor.v1';
  const read = (name, fallback) => { try { return JSON.parse(localStorage.getItem(name)) || fallback; } catch { return fallback; } };
  const cards = read(key, []);
  const dismissed = new Set(read(dismissedKey, []));
  let cursor = Number(localStorage.getItem(cursorKey)) || 0;
  const panel = document.createElement('aside');
  panel.className = 'caller-notifications';
  panel.setAttribute('aria-label', 'Incoming calls');
  document.body.append(panel);
  const persist = () => {
    try {
      localStorage.setItem(key, JSON.stringify(cards));
      localStorage.setItem(dismissedKey, JSON.stringify([...dismissed].slice(-500)));
      localStorage.setItem(cursorKey, String(cursor));
    } catch { /* Keep visible cards in memory if storage is unavailable. */ }
  };
  const dismiss = id => {
    dismissed.add(id);
    const index = cards.findIndex(card => card.eventId === id);
    if (index !== -1) cards.splice(index, 1);
    persist(); render();
  };
  function line(parent, label, value) {
    if (!value) return;
    const item = document.createElement('div');
    item.textContent = label + value;
    parent.append(item);
  }
  function render() {
    panel.replaceChildren();
    for (const card of cards.sort((a,b) => Date.parse(b.timestamp) - Date.parse(a.timestamp))) {
      const item = document.createElement('section'); item.className = 'caller-card';
      const heading = document.createElement('strong'); heading.textContent = 'Incoming call · ' + (card.callerNumber || 'Withheld / unavailable');
      item.append(heading);
      line(item, '', card.name);
      line(item, 'Status: ', card.callerNumber ? (card.existing ? 'Existing customer' : card.known ? 'New customer' : 'Checking customer…') : 'Number unavailable');
      line(item, 'Address: ', card.address);
      line(item, 'Last order: ', card.lastOrder && new Date(card.lastOrder).toLocaleDateString('en-GB'));
      const actions = document.createElement('div'); actions.className = 'caller-actions';
      if (card.callerNumber && /^[0-9]+$/.test(card.callerNumber)) {
        const open = document.createElement('button'); open.type = 'button'; open.textContent = 'Open Order';
        open.onclick = () => {
          if (location.pathname === '/pos.html') {
            window.dispatchEvent(new CustomEvent('anise-open-caller-order', {detail:{phone:card.callerNumber,eventId:card.eventId,name:card.name}}));
          } else {
            location.href = '/pos.html?caller=' + encodeURIComponent(card.eventId);
          }
        };
        actions.append(open);
      }
      const close = document.createElement('button'); close.type = 'button'; close.textContent = 'Dismiss';
      close.setAttribute('aria-label', 'Dismiss call from ' + (card.callerNumber || 'unavailable number'));
      close.onclick = () => dismiss(card.eventId); actions.append(close); item.append(actions); panel.append(item);
    }
  }
  async function enrich(card) {
    if (!card.callerNumber) return;
    try {
      const response = await fetch('/api/caller/customer?phone=' + encodeURIComponent(card.callerNumber), {cache:'no-store'});
      if (!response.ok) return;
      Object.assign(card, await response.json(), {known:true}); persist(); render();
    } catch { /* Retry on the next page load. */ }
  }
  function accept(notification, sequence) {
    if (sequence > cursor) cursor = sequence;
    const id = notification.eventId;
    if (!id || dismissed.has(id) || cards.some(card => card.eventId === id)) { persist(); return; }
    const card = {eventId:id, timestamp:notification.timestamp, callerNumber:notification.callerNumber,
      name:notification.customerName || null, existing:!!notification.customerId, known:!!notification.customerId};
    cards.push(card); persist(); render(); enrich(card);
  }
  window.addEventListener('anise-caller-order-opened', event => dismiss(event.detail.eventId));
  render();
  for (const card of cards) if (!card.known) enrich(card);
  const requested = new URLSearchParams(location.search).get('caller');
  if (requested && location.pathname === '/pos.html') {
    const card = cards.find(item => item.eventId === requested);
    if (card?.callerNumber) setTimeout(() => window.dispatchEvent(new CustomEvent('anise-open-caller-order',
      {detail:{phone:card.callerNumber,eventId:card.eventId,name:card.name}})), 0);
    history.replaceState(null, '', '/pos.html');
  }
  fetch('/api/caller/recent', {cache:'no-store'}).then(r => r.ok ? r.json() : []).then(items => {
    for (const item of items) accept(item.notification, item.sequence);
  }).catch(() => {});
  const stream = new EventSource('/api/caller/stream');
  stream.onmessage = event => { try { accept(JSON.parse(event.data), Number(event.lastEventId)); } catch {} };
  window.addEventListener('pagehide', () => stream.close(), {once:true});
})();
