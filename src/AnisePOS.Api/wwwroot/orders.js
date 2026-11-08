'use strict';
const message = document.querySelector('#message');
const updated = document.querySelector('#updated');
const refresh = document.querySelector('#refresh');
const money = new Intl.NumberFormat('en-GB', { style: 'currency', currency: 'GBP' });
let loading = false;
let saving = false;
let seenWebsiteOrders = null;
let soundContext = null;
const soundButton = document.querySelector('#sound-toggle');
const soundStatus = document.querySelector('#sound-status');

function playOrderSound() {
    if (!soundContext || soundContext.state !== 'running') return false;
    const now = soundContext.currentTime;
    for (const [offset, frequency] of [[0, 660], [0.2, 880]]) {
        const oscillator = soundContext.createOscillator();
        const gain = soundContext.createGain();
        oscillator.frequency.value = frequency;
        gain.gain.setValueAtTime(0.0001, now + offset);
        gain.gain.exponentialRampToValueAtTime(0.15, now + offset + 0.02);
        gain.gain.exponentialRampToValueAtTime(0.0001, now + offset + 0.17);
        oscillator.connect(gain); gain.connect(soundContext.destination);
        oscillator.start(now + offset); oscillator.stop(now + offset + 0.18);
    }
    return true;
}

soundButton.addEventListener('click', async () => {
    if (soundContext) {
        await soundContext.close(); soundContext = null;
        soundButton.textContent = 'Enable order sound';
        soundStatus.textContent = 'Order sound is off.';
        return;
    }
    try {
        const AudioContextClass = window.AudioContext || window.webkitAudioContext;
        if (!AudioContextClass) throw new Error();
        soundContext = new AudioContextClass();
        await soundContext.resume();
        if (!playOrderSound()) throw new Error();
        soundButton.textContent = 'Turn off order sound';
        soundStatus.textContent = 'Order sound is on. Keep this page open.';
    } catch (_) {
        if (soundContext) await soundContext.close().catch(() => {});
        soundContext = null;
        soundStatus.textContent = 'Could not enable sound. Check your browser and device sound settings.';
    }
});

function checkNewWebsiteOrders(orders) {
    const current = new Set(orders.filter(order => order.status === 'Placed' && ['WebsiteCash', 'StripeCheckout'].includes(order.paymentProvider)).map(order => order.orderNumber));
    if (seenWebsiteOrders && [...current].some(number => !seenWebsiteOrders.has(number))) {
        if (!playOrderSound() && soundContext) soundStatus.textContent = 'Sound paused. Turn it off and enable it again.';
    }
    seenWebsiteOrders = current;
}

function element(tag, text, className) {
    const node = document.createElement(tag);
    if (text !== undefined) node.textContent = text;
    if (className) node.className = className;
    return node;
}

async function request(url, options = {}) {
    const { timeoutMs = 15000, ...fetchOptions } = options;
    const response = await fetch(url, {
        credentials: 'same-origin', cache: 'no-store',
        signal: AbortSignal.timeout(timeoutMs), ...fetchOptions
    });
    if (response.status === 401) {
        location.replace('/staff-login.html?return=%2Forders.html');
        const error = new Error('Your staff session expired. Sign in again, then return to the queue.');
        error.code = 'unauthorized';
        throw error;
    }
    if (!response.ok) {
        const raw = await response.text();
        let details = {}; try { details = JSON.parse(raw); } catch (_) {}
        if (details.message || details.detail || details.title) throw new Error(details.message || details.detail || details.title);
        if (raw && raw.length < 400) throw new Error('Order service: ' + raw);
        if (response.status === 409) throw new Error('Another staff member updated this order. Refresh the queue.');
        throw new Error('Could not update the order (HTTP ' + response.status + '). Check the order service and try again.');
    }
    return response.json();
}

function render(orders) {
    for (const [id, title] of [['new', 'New'], ['preparing', 'Preparing'], ['ready', 'Ready']]) {
        const items = orders.filter(o => id === 'new' ? ['Draft', 'Placed', 'Accepted'].includes(o.status)
            : o.status.toLowerCase() === id);
        const target = document.getElementById(id);
        target.replaceChildren();
        document.getElementById(id + '-title').textContent = title + ' (' + items.length + ')';
        if (!items.length) target.append(element('p', 'No orders here.', 'empty'));
        for (const order of items) {
            const card = element('article', undefined, 'card');
            card.append(element('h3', '#' + order.orderNumber + ' · ' + order.orderType));
            card.append(element('p', new Date(order.createdAtUtc).toLocaleString('en-GB') +
                (order.status === 'Draft' ? ' · Draft' : ''), 'detail'));
            const websiteOrder = ['WebsiteCash', 'StripeCheckout'].includes(order.paymentProvider);
            if (websiteOrder && order.status === 'Placed') card.append(element('p', 'Website order · awaiting acceptance', 'note'));
            if (websiteOrder && order.status === 'Accepted') card.append(element('p', 'Website order · accepted', 'note'));
            if (order.estimatedAtUtc) card.append(element('p', 'Estimated ' + order.orderType.toLowerCase() + ': ' + new Date(order.estimatedAtUtc).toLocaleTimeString('en-GB', {hour: '2-digit', minute: '2-digit'}), 'detail'));
            if (order.customerName) card.append(element('p', order.customerName));
            if (order.customerPhone) card.append(element('p', order.customerPhone, 'detail'));
            if (order.orderType === 'Delivery' && order.deliveryAddress)
                card.append(element('p', order.deliveryAddress, 'detail'));
            const lines = element('ul');
            for (const line of order.lines) {
                const item = element('li', line.quantity + ' × ' + line.productName + (line.variantName === 'Regular' ? '' : ' — ' + line.variantName));
                if (line.modifiers.length) item.append(element('div', line.modifiers.join(', '), 'detail'));
                if (line.notes) item.append(element('div', 'Kitchen note: ' + line.notes, 'detail'));
                lines.append(item);
            }
            card.append(lines);
            if (order.notes) card.append(element('p', order.notes, 'note'));
            card.append(element('strong', 'Order total ' + money.format(order.total)));
            if (order.paymentMethod) {
                card.append(element('p', order.paymentMethod + ' · ' + order.paymentStatus, 'payment ' + order.paymentStatus.toLowerCase()));
                if (order.paymentStatus === 'Pending' || order.paymentStatus === 'Authorised') {
                    const stripePayment = ['Stripe', 'StripeConfirming', 'StripeCancelled'].includes(order.paymentProvider);
                    if (stripePayment) {
                        card.append(element('p', 'Previous sandbox payment. Check its record in Stripe test mode.', 'note'));
                    } else {
                        const paid = element('button', 'Mark ' + order.paymentMethod.toLowerCase() + ' paid', 'payment-action');
                        paid.setAttribute('aria-label', 'Order ' + order.orderNumber + ': ' + paid.textContent);
                        paid.addEventListener('click', () => markPaid(order));
                        card.append(paid);
                    }
                }
            } else card.append(element('p', 'Payment not recorded', 'payment missing'));
            const receipts = element('div', undefined, 'receipt-actions');
            for (const [type, label] of [['customer', 'Customer receipt']]) {
                const link = element('a', label);
                link.href = '/receipt.html?order=' + order.orderNumber + '&type=' + type;
                receipts.append(link);
            }
            card.append(receipts);
            if (websiteOrder && order.status === 'Placed') {
                const acceptForm = element('form', undefined, 'accept-form');
                const label = element('label', 'Minutes until ' + (order.orderType === 'Delivery' ? 'delivery' : 'collection'));
                const estimate = element('input'); estimate.type = 'number'; estimate.min = '1'; estimate.max = '10080'; estimate.step = '1'; estimate.required = true;
                estimate.value = order.orderType === 'Delivery' ? '30' : '15';
                estimate.setAttribute('aria-label', 'Minutes until ' + order.orderType.toLowerCase() + ' for order ' + order.orderNumber);
                let previousEstimate = estimate.value;
                estimate.addEventListener('focus', () => { previousEstimate = estimate.value; estimate.value = ''; });
                estimate.addEventListener('blur', () => { if (!estimate.value) estimate.value = previousEstimate; });
                label.append(estimate); acceptForm.append(label);
                const accept = element('button', 'Accept order', 'advance'); accept.type = 'submit'; acceptForm.append(accept);
                acceptForm.addEventListener('submit', event => { event.preventDefault(); if (acceptForm.reportValidity()) acceptWebsiteOrder(order, estimate.value); });
                const decline = element('button', 'Decline order', 'decline-order'); decline.type = 'button';
                decline.addEventListener('click', () => declineWebsiteOrder(order));
                card.append(acceptForm, decline);
                if (order.paymentProvider === 'StripeCheckout') card.append(element('p', 'Refund the full payment in Stripe before declining.', 'detail'));
            } else {
                const next = id === 'new' ? 'Preparing' : id === 'preparing' ? 'Ready' : 'Completed';
                const button = element('button', id === 'new' ? 'Start preparing' : 'Mark ' + next.toLowerCase(), 'advance');
                button.setAttribute('aria-label', 'Order ' + order.orderNumber + ': ' + button.textContent);
                button.addEventListener('click', () => advance(order, next));
                card.append(button);
            }
            target.append(card);
        }
    }
}

async function load() {
    if (loading || saving) return;
    loading = true;
    refresh.disabled = true;
    try {
        const orders = await request('/api/order-queue');
        checkNewWebsiteOrders(orders);
        if (!document.activeElement?.closest('.accept-form')) render(orders);
        updated.textContent = 'Updated ' + new Date().toLocaleTimeString('en-GB') + ' · refreshes every 15 seconds';
        message.textContent = '';
    } catch (error) {
        message.textContent = error.message;
        updated.textContent = 'Queue may be out of date';
    } finally {
        loading = false;
        refresh.disabled = false;
    }
}

async function acceptWebsiteOrder(order, estimate) {
    if (saving || loading) return;
    const minutes = Number(estimate);
    if (!Number.isInteger(minutes) || minutes < 1 || minutes > 10080) { message.textContent = 'Enter a whole number of minutes between 1 and 10080.'; return; }
    const at = new Date(Date.now() + minutes * 60000);
    saving = true;
    try {
        await request('/api/order-queue/' + order.orderNumber + '/accept', {
            method: 'POST', headers: {'Content-Type': 'application/json', 'X-HTTP-Method-Override': 'PUT'}, body: JSON.stringify({estimatedAtUtc: at.toISOString()})
        });
        document.activeElement?.blur(); saving = false; await load(); message.textContent = 'Order #' + order.orderNumber + ' accepted.';
    } catch (error) { message.textContent = error.message; }
    finally { saving = false; }
}

async function declineWebsiteOrder(order) {
    if (saving || loading) return;
    const prompt = order.paymentProvider === 'StripeCheckout'
        ? 'Decline order #' + order.orderNumber + '? Refund it in Stripe first; the POS will verify the full refund.'
        : 'Decline order #' + order.orderNumber + '?';
    if (!window.confirm(prompt)) return;
    saving = true;
    try {
        await request('/api/order-queue/' + order.orderNumber + '/decline', {method: 'PUT', timeoutMs: 30000});
        saving = false; await load(); message.textContent = 'Order #' + order.orderNumber + ' declined.';
    } catch (error) { message.textContent = error.message; }
    finally { saving = false; }
}

async function advance(order, status) {
    if (saving || loading) return;
    saving = true;
    document.querySelectorAll('.advance,.payment-action').forEach(b => b.disabled = true);
    refresh.disabled = true;
    try {
        await request('/api/order-queue/' + order.orderNumber + '/advance', { method: 'PUT' });
        saving = false;
        await load();
    } catch (error) {
        message.textContent = error.message;
    } finally {
        saving = false;
        refresh.disabled = false;
        document.querySelectorAll('.advance,.payment-action').forEach(b => b.disabled = false);
    }
}

async function markPaid(order) {
    if (saving || loading) return;
    saving = true;
    document.querySelectorAll('.advance,.payment-action').forEach(b => b.disabled = true);
    refresh.disabled = true;
    try {
        await request('/api/order-queue/' + order.orderNumber + '/payment/paid', { method: 'PUT' });
        saving = false;
        await load();
    } catch (error) {
        message.textContent = error.message;
    } finally {
        saving = false;
        refresh.disabled = false;
        document.querySelectorAll('.advance,.payment-action').forEach(b => b.disabled = false);
    }
}

refresh.addEventListener('click', load);
(async () => {
    try {
        const staff = await request('/api/staff/me', { timeoutMs: 4000 });
        document.querySelector('#staff').textContent = staff.name + ' · ' + staff.role;
        document.body.classList.add('authenticated');
        await load();
        setInterval(load, 15000);
    } catch (error) {
        location.replace('/staff-login.html?return=%2Forders.html');
    }
})();
