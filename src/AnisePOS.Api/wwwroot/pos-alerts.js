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
  const banner = area.querySelector('.pos-alert-status'), button = document.querySelector('.pos-push-toggle'), status = document.querySelector('.pos-push-message');
  let worker = null, audio = null, waiting = false;
  function decodeKey(value) {
    const padded = (value + '='.repeat((4 - value.length % 4) % 4)).replace(/-/g, '+').replace(/_/g, '/');
    return Uint8Array.from(atob(padded), c => c.charCodeAt(0));
  }
  function message(text) { if (status) status.textContent = text; }
  async function updateSubscriptionButton() {
    if (!('serviceWorker' in navigator) || !('PushManager' in window) || !('Notification' in window)) return;
    try {
      const existing = await navigator.serviceWorker.getRegistration('/');
      const sub = existing && await existing.pushManager.getSubscription();
      if (sub) {
        worker = existing;
        const response = await fetch('/api/staff/push/status', {method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/json'},body:JSON.stringify(sub.toJSON())});
        if (response.ok && (await response.json()).enabled) { button.textContent = 'Disable notifications'; button.dataset.enabled = 'true'; }
      }
    } catch (_) {}
  }
  if (button) button.addEventListener('click', async () => {
    if (!('serviceWorker' in navigator) || !('PushManager' in window) || !('Notification' in window)) {
      message('On iPad, open Share → Add to Home Screen, turn on Open as Web App, then open the POS from its icon.'); return;
    }
    button.disabled = true;
    try {
      if (button.dataset.enabled === 'true') {
        const sub = worker && await worker.pushManager.getSubscription();
        if (sub) {
          const response = await fetch('/api/staff/push/unsubscribe', {method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/json'},body:JSON.stringify(sub.toJSON())});
          if (!response.ok) throw new Error('Could not disable notifications. Please try again.');
          await sub.unsubscribe();
        }
        button.dataset.enabled = 'false'; button.textContent = 'Enable notifications'; message('Notifications disabled on this device.'); return;
      }
      // Request permission directly from the tap, as required by iPadOS.
      const permission = await Notification.requestPermission();
      if (permission !== 'granted') { message('Notifications were not allowed. Check the device notification settings.'); return; }
      const response = await fetch('/api/staff/push/key', {credentials:'same-origin',cache:'no-store'});
      if (!response.ok) throw new Error('Sign in to enable notifications.');
      const key = await response.json();
      worker = await navigator.serviceWorker.register('/pos-service-worker.js', {scope:'/'});
      await navigator.serviceWorker.ready;
      const sub = await worker.pushManager.getSubscription() || await worker.pushManager.subscribe({userVisibleOnly:true,applicationServerKey:decodeKey(key.publicKey)});
      const saved = await fetch('/api/staff/push/subscribe', {method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/json'},body:JSON.stringify(sub.toJSON())});
      if (!saved.ok) throw new Error('Could not save this device. Please try again.');
      button.dataset.enabled = 'true'; button.textContent = 'Disable notifications'; message('Notifications enabled on this device.');
    } catch (error) { message(error.message || 'Could not enable notifications. Please try again.'); }
    finally { button.disabled = false; }
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
