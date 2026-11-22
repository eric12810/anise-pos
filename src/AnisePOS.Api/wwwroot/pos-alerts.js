(() => {
  'use strict';
  const header = document.querySelector('.pos-header');
  if (!header) return;
  const area = document.createElement('section');
  area.className = 'pos-alerts';
  area.innerHTML = '<div class="pos-alert-status" role="status" hidden><strong>New website order awaiting acceptance.</strong> <a href="/orders.html">Open order queue</a></div>';
  area.hidden = true;
  header.after(area);
  const measure = () => document.documentElement.style.setProperty('--pos-alerts-height', area.offsetHeight + 'px');
  if (window.ResizeObserver) new ResizeObserver(measure).observe(area);
  else window.addEventListener('resize', measure);
  measure();
  const banner = area.querySelector('.pos-alert-status'), button = document.querySelector('.pos-push-toggle'), testButton = document.querySelector('.pos-push-test'), deviceStatus = document.querySelector('.pos-push-device-status'), status = document.querySelector('.pos-push-message');
  let worker = null, audio = null, waiting = false;
  function decodeKey(value) {
    const padded = (value + '='.repeat((4 - value.length % 4) % 4)).replace(/-/g, '+').replace(/_/g, '/');
    return Uint8Array.from(atob(padded), c => c.charCodeAt(0));
  }
  function message(text) { if (status) status.textContent = text; }
  function state(value, enabled = false) {
    if (deviceStatus) deviceStatus.textContent = value;
    if (button) { button.dataset.enabled = String(enabled); button.textContent = enabled ? 'Disable Notifications' : 'Enable Notifications'; }
    if (testButton) testButton.disabled = !enabled;
  }
  const supported = () => 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window && (!/iPad|iPhone|iPod/.test(navigator.userAgent) || window.navigator.standalone === true);
  async function post(path, sub) {
    const response = await fetch('/api/staff/push/' + path, {method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/json'},body:JSON.stringify(sub.toJSON())});
    const result = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(result.message || (response.status === 429 ? 'Please wait before sending another test.' : 'Notification request failed. Please try again.'));
    return result;
  }
  async function updateSubscriptionButton() {
    if (!supported()) { state('Unsupported'); message('On iPhone or iPad, open the POS from its Home Screen icon.'); return; }
    if (Notification.permission === 'denied') { state('Blocked'); return; }
    if (Notification.permission !== 'granted') { state('Permission Required'); return; }
    try {
      const existing = await navigator.serviceWorker.getRegistration('/');
      const sub = existing && await existing.pushManager.getSubscription();
      if (sub) {
        worker = existing;
        const keyResponse = await fetch('/api/staff/push/key', {credentials:'same-origin',cache:'no-store'});
        if (!keyResponse.ok) throw new Error('Could not check push configuration. Sign in and try again.');
        const key = decodeKey((await keyResponse.json()).publicKey);
        const currentKey = sub.options && sub.options.applicationServerKey;
        if (!currentKey || Array.from(new Uint8Array(currentKey)).join(',') !== Array.from(key).join(',')) {
          state('Disabled'); message('This device has an old push key. Tap Enable Notifications to re-register.'); return;
        }
        const result = await post('status', sub);
        if (result.enabled) { state('Enabled', true); return; }
        message('This device needs re-registration. Tap Enable Notifications.');
      }
      state('Disabled');
    } catch (error) { state('Disabled'); message(error.message); }
  }
  if (button) button.addEventListener('click', async () => {
    if (!supported()) {
      message('On iPad, open Share → Add to Home Screen, turn on Open as Web App, then open the POS from its icon.'); return;
    }
    button.disabled = true;
    try {
      if (button.dataset.enabled === 'true') {
        const registration = worker || await navigator.serviceWorker.getRegistration('/');
        const sub = registration && await registration.pushManager.getSubscription();
        if (sub) {
          await post('unsubscribe', sub);
          await sub.unsubscribe();
        }
        state('Disabled'); message('Notifications disabled on this device.'); return;
      }
      // Request permission directly from the tap, as required by iPadOS.
      const permission = await Notification.requestPermission();
      if (permission !== 'granted') { state(permission === 'denied' ? 'Blocked' : 'Permission Required'); message('Notifications were not allowed. Check the device notification settings.'); return; }
      const response = await fetch('/api/staff/push/key', {credentials:'same-origin',cache:'no-store'});
      if (!response.ok) throw new Error('Sign in to enable notifications.');
      const key = await response.json();
      worker = await navigator.serviceWorker.register('/pos-service-worker.js', {scope:'/'});
      await navigator.serviceWorker.ready;
      let sub = await worker.pushManager.getSubscription();
      if (sub) {
        const currentKey = sub.options && sub.options.applicationServerKey;
        const expected = decodeKey(key.publicKey);
        if (!currentKey || Array.from(new Uint8Array(currentKey)).join(',') !== Array.from(expected).join(',')) { await sub.unsubscribe(); sub = null; }
      }
      sub = sub || await worker.pushManager.subscribe({userVisibleOnly:true,applicationServerKey:decodeKey(key.publicKey)});
      await post('subscribe', sub);
      state('Enabled', true); message('Notifications enabled on this device.');
    } catch (error) { message(error.message || 'Could not enable notifications. Please try again.'); }
    finally { button.disabled = false; }
  });
  if (testButton) testButton.addEventListener('click', async () => {
    testButton.disabled = true;
    try {
      const registration = worker || await navigator.serviceWorker.getRegistration('/');
      const sub = registration && await registration.pushManager.getSubscription();
      if (!sub) { state('Disabled'); throw new Error('Device subscription is missing. Enable notifications again.'); }
      const result = await post('test', sub);
      message(result.message);
    } catch (error) { message(error.message); await updateSubscriptionButton(); }
    finally { setTimeout(() => { if (button && button.dataset.enabled === 'true') testButton.disabled = false; }, 10000); }
  });
  if (button) updateSubscriptionButton();
  if (location.pathname === '/orders.html') return;
  const soundScript = document.createElement('script'); soundScript.src = '/order-sounds.js?v=3'; document.head.append(soundScript);
  async function enableSound() {
    try { const Audio = window.AudioContext || window.webkitAudioContext; if (!Audio) return; if (!audio || audio.state === 'closed') audio = new Audio(); if (audio.state !== 'running') await audio.resume(); } catch (_) {}
  }
  document.addEventListener('pointerdown', enableSound, {passive:true});
  document.addEventListener('keydown', enableSound);
  async function check() {
    try {
      const response = await fetch('/api/order-queue', {credentials:'same-origin',cache:'no-store',signal:AbortSignal.timeout(12000)});
      if (!response.ok) return;
      const orders = await response.json();
      const wasWaiting = waiting;
      waiting = orders.some(order => order.status === 'Placed' && ['WebsiteCash','StripeCheckout'].includes(order.paymentProvider));
      banner.hidden = !waiting; area.hidden = !waiting;
      if (waiting && !wasWaiting && typeof playSelectedOrderSound === 'function') playSelectedOrderSound(audio, 'happy-bells');
    } catch (_) { /* Keep the last known state until the next check. */ }
  }
  check(); setInterval(check, 15000);
  setInterval(() => { if (waiting && typeof playSelectedOrderSound === 'function') playSelectedOrderSound(audio, 'happy-bells'); }, 3000);
})();
