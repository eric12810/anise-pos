'use strict';
self.addEventListener('push', event => {
  let payload = {};
  try { payload = event.data ? event.data.json() : {}; } catch (_) {}
  event.waitUntil(self.registration.showNotification(payload.title || 'New website order', {
    body: payload.body || 'A new order is awaiting acceptance. Tap to open the queue.',
    icon: '/pos-icon.png', badge: '/pos-icon.png', tag: payload.tag || 'anise-new-order', renotify: true,
    data: { path: '/orders.html' }
  }));
});
self.addEventListener('notificationclick', event => {
  event.notification.close();
  event.waitUntil((async () => {
    const target = new URL('/orders.html', self.location.origin).href;
    const windows = await clients.matchAll({type:'window', includeUncontrolled:true});
    for (const client of windows) {
      if (new URL(client.url).origin === self.location.origin) { await client.navigate(target); return client.focus(); }
    }
    return clients.openWindow(target);
  })());
});
