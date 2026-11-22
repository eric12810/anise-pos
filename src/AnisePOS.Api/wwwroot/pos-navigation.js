(() => {
  const nav = document.querySelector('.pos-navigation');
  if (!nav) return;
  const header = nav.closest('header');
  const updateHeight = () => document.documentElement.style.setProperty('--pos-header-height', header.offsetHeight + 'px');
  if (window.ResizeObserver) new ResizeObserver(updateHeight).observe(header);
  else window.addEventListener('resize', updateHeight);
  updateHeight();
  const path = location.pathname === '/promotions.html' ? '/website-settings.html' : location.pathname;
  nav.querySelectorAll('a').forEach(link => { if (link.getAttribute('href') === path) link.setAttribute('aria-current', 'page'); });
  fetch('/api/staff/me', {credentials:'same-origin', cache:'no-store'})
    .then(response => response.ok ? response.json() : null)
    .then(staff => {
      if (!staff) return;
      nav.querySelectorAll('[data-roles]').forEach(link => { link.hidden = !link.dataset.roles.split(',').includes(staff.role); });
      const alerts = document.createElement('script'); alerts.src = '/pos-alerts.js?v=4'; document.head.append(alerts);
    })
    .catch(() => {});
})();
