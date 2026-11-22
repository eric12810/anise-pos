'use strict';

// Keep payment choices in the visible viewport even when the order panel scrolls.
(() => {
  let active = null;

  function closeActive() {
    if (active) active.close();
  }

  window.closePaymentDropdown = closeActive;
  window.createPaymentDropdown = (anchor, methods, selected, onSelect) => {
    let menu = null;
    let parentDialog = null;
    let previousOverflow = '';

    function position() {
      if (!menu) return;
      const rect = anchor.getBoundingClientRect();
      const viewportWidth = window.visualViewport?.width || window.innerWidth;
      const viewportHeight = window.visualViewport?.height || window.innerHeight;
      const width = Math.min(Math.max(rect.width, 180), viewportWidth - 16);
      menu.style.left = `${Math.max(8, Math.min(rect.left, viewportWidth - width - 8))}px`;
      menu.style.top = `${rect.bottom + 4}px`;
      menu.style.width = `${width}px`;
      menu.style.maxHeight = `${Math.max(48, viewportHeight - rect.bottom - 12)}px`;
    }

    function close() {
      if (!menu) return;
      menu.remove();
      menu = null;
      anchor.setAttribute('aria-expanded', 'false');
      document.removeEventListener('pointerdown', outside, true);
      document.removeEventListener('keydown', escape, true);
      window.removeEventListener('resize', position);
      window.removeEventListener('scroll', position, true);
      if (parentDialog) parentDialog.style.overflow = previousOverflow;
      parentDialog = null;
      if (active?.close === close) active = null;
    }

    function outside(event) {
      if (!menu.contains(event.target) && !anchor.contains(event.target)) close();
    }

    function escape(event) {
      if (event.key === 'Escape') { close(); anchor.focus(); }
    }

    function open() {
      if (anchor.disabled) return;
      closeActive();
      anchor.scrollIntoView({block: 'center', inline: 'nearest'});
      menu = document.createElement('div');
      menu.className = 'payment-dropdown';
      menu.setAttribute('role', 'menu');
      menu.setAttribute('aria-label', 'Payment method');
      for (const method of methods) {
        const option = document.createElement('button');
        option.type = 'button';
        option.textContent = method;
        option.setAttribute('role', 'menuitemradio');
        option.setAttribute('aria-checked', String(method === selected()));
        option.addEventListener('click', () => { close(); onSelect(method); });
        menu.append(option);
      }
      parentDialog = anchor.closest('dialog');
      if (parentDialog) {
        previousOverflow = parentDialog.style.overflow;
        parentDialog.style.overflow = 'visible';
      }
      (parentDialog || document.body).append(menu);
      anchor.setAttribute('aria-expanded', 'true');
      active = {close};
      position();
      document.addEventListener('pointerdown', outside, true);
      document.addEventListener('keydown', escape, true);
      window.addEventListener('resize', position);
      window.addEventListener('scroll', position, true);
      menu.querySelector('button')?.focus();
    }

    return {close, toggle() { if (menu) close(); else open(); }};
  };
})();
