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
    const index = cards.findIndex(card => card.eventId === id);
    if (index !== -1) {
      dismissed.add(cards[index].callId || id);
      cards.splice(index, 1);
    }
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
      line(item, 'Call: ', {ringing:'Ringing',answered:'Answered',ended:'Ended',missed:'Missed'}[card.callStatus] || 'Ringing');
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
    // Existing staging workers may emit PascalCase during a rolling deployment.
    if (!notification.eventId && notification.EventId) notification = {
      eventId:notification.EventId, timestamp:notification.Timestamp, callerNumber:notification.CallerNumber,
      customerId:notification.CustomerId, customerName:notification.CustomerName,
      callId:notification.CallId, callStatus:notification.CallStatus
    };
    const id = notification.eventId;
    const callId = notification.callId || id;
    if (!id || dismissed.has(callId) || dismissed.has(id)) { persist(); return; }
    const existing = cards.find(card => (card.callId || card.eventId) === callId);
    if (existing) {
      const rank = {ringing:0,answered:1,ended:2,missed:2};
      const next = notification.callStatus || 'ringing';
      if ((rank[next] ?? -1) > (rank[existing.callStatus || 'ringing'] ?? 0)) {
        existing.callStatus = next; persist(); render();
      }
      return;
    }
    if ((notification.callStatus || 'ringing') !== 'ringing') { persist(); return; }
    const card = {eventId:id, timestamp:notification.timestamp, callerNumber:notification.callerNumber,
      callId, callStatus:'ringing', name:notification.customerName || null, existing:!!notification.customerId, known:!!notification.customerId};
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
  let checking = false;
  async function catchUp() {
    if (checking || document.hidden) return;
    checking = true;
    try {
      const response = await fetch('/api/caller/recent?after=' + encodeURIComponent(cursor), {cache:'no-store'});
      if (response.ok) for (const item of await response.json()) accept(item.notification, item.sequence);
    } catch { /* The next check will retry. */ }
    finally { checking = false; }
  }
  let stream;
  function connect() {
    if (stream || document.hidden) return;
    stream = new EventSource('/api/caller/stream');
    stream.onmessage = event => { try { accept(JSON.parse(event.data), Number(event.lastEventId)); } catch {} };
  }
  catchUp(); connect();
  setInterval(catchUp, 15000);
  document.addEventListener('visibilitychange', () => { if (!document.hidden) { catchUp(); connect(); } });
  window.addEventListener('focus', catchUp);
  window.addEventListener('pagehide', () => { stream?.close(); stream = null; });
  window.addEventListener('pageshow', () => { catchUp(); connect(); });
})();
