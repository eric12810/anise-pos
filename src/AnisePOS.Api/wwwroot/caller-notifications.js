(() => {
  'use strict';
  const staff = document.querySelector('.pos-header #staff, .pos-header #current-staff');
  if (!staff) return;
  const storageKey = 'anise.caller.navigation.v2';
  const read = () => { try { return JSON.parse(localStorage.getItem(storageKey)) || {}; } catch { return {}; } };
  const saved = read();
  const calls = Array.isArray(saved.calls) ? saved.calls : [];
  const handled = new Set(Array.isArray(saved.handled) ? saved.handled : []);
  const finished = new Set(Array.isArray(saved.finished) ? saved.finished : []);
  let cursor = Number(saved.cursor) || 0;
  let checking = false;
  let stream;
  try {
    for (const oldKey of ['anise.caller.notifications.v1', 'anise.caller.dismissed.v1', 'anise.caller.cursor.v1'])
      localStorage.removeItem(oldKey);
  } catch { /* Browser storage may be unavailable. */ }

  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'caller-nav-button';
  button.hidden = true;
  button.setAttribute('aria-label', 'Incoming call');
  staff.after(button);
  const persist = () => {
    try { localStorage.setItem(storageKey, JSON.stringify({calls, handled:[...handled].slice(-500), finished:[...finished].slice(-500), cursor})); }
    catch { /* Current page still works without browser storage. */ }
  };
  const active = () => calls.filter(call => !handled.has(call.callId) && !finished.has(call.callId))
    .sort((a, b) => b.sequence - a.sequence)[0];
  function prune(save = true) {
    for (let index = calls.length - 1; index >= 0; index--) {
      const call = calls[index];
      const limit = call.status === 'answered' ? 4 * 60 * 60 * 1000 : 5 * 60 * 1000;
      if (Date.now() - Date.parse(call.lastSeen || call.timestamp) > limit) {
        finished.add(call.callId);
        calls.splice(index, 1);
      }
    }
    if (save) persist();
  }
  function render() {
    const call = active();
    button.hidden = !call;
    staff.hidden = !!call;
    if (!call) return;
    button.replaceChildren();
    const icon = document.createElement('span'); icon.className = 'caller-nav-icon'; icon.textContent = '☎'; icon.setAttribute('aria-hidden', 'true');
    const details = document.createElement('span'); details.className = 'caller-nav-details';
    const number = document.createElement('strong'); number.textContent = call.callerNumber || 'Withheld number';
    const status = document.createElement('small');
    status.textContent = call.callerNumber ? (call.existing ? 'Existing customer' : call.known ? 'New customer' : 'Checking customer…') : 'Number unavailable';
    details.append(number, status); button.append(icon, details);
    button.disabled = !call.callerNumber || !/^[0-9]+$/.test(call.callerNumber);
    button.setAttribute('aria-label', 'Incoming call from ' + (call.callerNumber || 'unavailable number') + ', ' + status.textContent);
  }
  async function enrich(call) {
    if (!call.callerNumber || call.known) return;
    try {
      const response = await fetch('/api/caller/customer?phone=' + encodeURIComponent(call.callerNumber), {cache:'no-store'});
      if (!response.ok) return;
      const result = await response.json();
      if (!calls.includes(call) || finished.has(call.callId)) return;
      call.existing = !!result.existing; call.known = true;
      persist(); render();
    } catch { /* A later page load or call update can retry lookup. */ }
  }
  button.addEventListener('click', () => {
    const call = active();
    if (!call || button.disabled) return;
    if (location.pathname === '/pos.html') {
      window.dispatchEvent(new CustomEvent('anise-open-caller-order', {detail:{phone:call.callerNumber, eventId:call.eventId, name:call.name}}));
    } else {
      location.href = '/pos.html?caller=' + encodeURIComponent(call.callId);
    }
  });
  window.addEventListener('anise-caller-order-opened', event => {
    const call = calls.find(item => item.eventId === event.detail?.eventId);
    if (call) {
      handled.add(call.callId);
      calls.splice(calls.indexOf(call), 1);
      persist(); render();
    }
  });
  window.addEventListener('storage', event => {
    if (event.key !== storageKey || !event.newValue) return;
    try {
      const next = JSON.parse(event.newValue);
      calls.splice(0, calls.length, ...(Array.isArray(next.calls) ? next.calls : []));
      handled.clear(); for (const id of next.handled || []) handled.add(id);
      finished.clear(); for (const id of next.finished || []) finished.add(id);
      cursor = Math.max(cursor, Number(next.cursor) || 0);
      prune(false); render();
    } catch { /* Ignore malformed browser storage. */ }
  });
  function accept(notification, sequence) {
    if (sequence > cursor) cursor = sequence;
    if (!notification.eventId && notification.EventId) notification = {
      eventId:notification.EventId, timestamp:notification.Timestamp, callerNumber:notification.CallerNumber,
      customerId:notification.CustomerId, customerName:notification.CustomerName,
      callId:notification.CallId, callStatus:notification.CallStatus
    };
    const id = notification.callId || notification.eventId;
    if (!id) { persist(); return; }
    const status = notification.callStatus || 'ringing';
    const call = calls.find(item => item.callId === id);
    if (status === 'ended' || status === 'missed') {
      finished.add(id);
      if (call) calls.splice(calls.indexOf(call), 1);
      persist(); render(); return;
    }
    if (finished.has(id) || handled.has(id)) { persist(); return; }
    if (call) {
      if (status === 'answered' && call.status !== 'answered') {
        call.status = 'answered';
        call.lastSeen = notification.timestamp;
      }
      persist(); render(); return;
    }
    if (status !== 'ringing') { persist(); return; }
    const entry = {callId:id, eventId:notification.eventId, sequence:sequence || 0,
      timestamp:notification.timestamp, callerNumber:notification.callerNumber,
      name:notification.customerName || null, existing:!!notification.customerId,
      known:!!notification.customerId, status:'ringing'};
    calls.push(entry); persist(); render(); enrich(entry);
  }
  prune(); render();
  const requested = new URLSearchParams(location.search).get('caller');
  if (requested && location.pathname === '/pos.html') {
    const call = calls.find(item => item.callId === requested && !finished.has(item.callId) && !handled.has(item.callId));
    if (call?.callerNumber) setTimeout(() => window.dispatchEvent(new CustomEvent('anise-open-caller-order',
      {detail:{phone:call.callerNumber, eventId:call.eventId, name:call.name}})), 0);
    history.replaceState(null, '', '/pos.html');
  }
  for (const call of calls) enrich(call);
  async function catchUp() {
    if (checking || document.hidden) return;
    checking = true;
    try {
      let batch;
      do {
        const response = await fetch('/api/caller/recent?after=' + encodeURIComponent(cursor), {cache:'no-store'});
        if (!response.ok) return;
        batch = await response.json();
        for (const item of batch) accept(item.notification, item.sequence);
      } while (batch.length === 100);
    } catch { /* Retry after reconnection. */ }
    finally { checking = false; }
  }
  function connect() {
    if (stream || document.hidden) return;
    stream = new EventSource('/api/caller/stream');
    stream.onmessage = event => { try { accept(JSON.parse(event.data), Number(event.lastEventId)); } catch {} };
  }
  catchUp(); connect();
  setInterval(catchUp, 15000);
  setInterval(() => { prune(); render(); }, 30000);
  document.addEventListener('visibilitychange', () => { if (!document.hidden) { catchUp(); connect(); } });
  window.addEventListener('focus', catchUp);
  window.addEventListener('pagehide', () => { stream?.close(); stream = null; });
  window.addEventListener('pageshow', () => { catchUp(); connect(); });
})();
