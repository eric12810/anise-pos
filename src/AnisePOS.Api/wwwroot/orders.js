'use strict';
const message = document.querySelector('#message');
const stripeAlert = document.createElement('p');
stripeAlert.style.cssText = 'margin:0 24px 16px;padding:12px;background:#fff2d9;color:#7a3000;font-weight:700';
stripeAlert.hidden = true;
message.after(stripeAlert);
async function checkStripeAlerts() {
    try {
        const response = await fetch('/api/admin/stripe-checkout-alerts', {credentials:'same-origin', cache:'no-store'});
        if (response.status === 403) return;
        if (!response.ok) throw new Error();
        const alerts = await response.json();
        stripeAlert.hidden = alerts.length === 0;
        stripeAlert.textContent = alerts.length ? `${alerts.length} website card checkout${alerts.length === 1 ? '' : 's'} need payment review. Check Stripe and the POS logs before taking another order from these customers.` : '';
    } catch (_) { stripeAlert.hidden = false; stripeAlert.textContent = 'Could not check website card payment alerts. Ask a manager to review Stripe.'; }
}
checkStripeAlerts();
setInterval(checkStripeAlerts, 60000);
const updated = document.querySelector('#updated');
const refresh = document.querySelector('#refresh');
const money = new Intl.NumberFormat('en-GB', { style: 'currency', currency: 'GBP' });
let loading = false;
let saving = false;
let soundContext = null;
const selectedOrderSound = 'happy-bells';
let awaitingOrders = false;
const soundStatus = document.querySelector('#sound-status');
const detailDialog = document.querySelector('#order-detail-dialog');
const detailContent = document.querySelector('#order-detail-content');
const detailStatus = document.querySelector('#order-detail-status');
const cancelDialog = document.querySelector('#cancel-dialog'), cancelError = document.querySelector('#cancel-error'), cancelConfirm = document.querySelector('#cancel-confirm');
let cancellingOrder = null;
document.querySelector('#order-detail-close').addEventListener('click', () => detailDialog.close());
detailDialog.addEventListener('close', () => { detailContent.replaceChildren(); load(); });
new MutationObserver(() => { if (detailDialog.open) detailStatus.textContent = message.textContent; }).observe(message, {childList: true, characterData: true, subtree: true});
function closeOrderDetails() { if (detailDialog.open) detailDialog.close(); }
function deliveryPostcode(address) {
    const match = String(address || '').toUpperCase().match(/\b(?:GIR\s*0AA|[A-Z]{1,2}\d[A-Z\d]?\s*\d[A-Z]{2})\b/);
    if (!match) return null;
    const compact = match[0].replace(/\s/g, '');
    return compact.slice(0, -3) + ' ' + compact.slice(-3);
}
async function enableOrderSound() {
    try {
        const AudioContextClass = window.AudioContext || window.webkitAudioContext;
        if (!AudioContextClass) throw new Error();
        if (!soundContext || soundContext.state === 'closed') soundContext = new AudioContextClass();
        if (soundContext.state !== 'running') await soundContext.resume();
        soundStatus.textContent = soundContext.state === 'running' ? 'Order sound is on.' : 'Tap anywhere on this page to enable order sound.';
    } catch (_) {
        soundStatus.textContent = 'Order sound is unavailable. Check your browser and device sound settings.';
    }
}

function playOrderSound() { return playSelectedOrderSound(soundContext, selectedOrderSound); }

function checkNewWebsiteOrders(orders) {
    const wasAwaiting = awaitingOrders;
    awaitingOrders = orders.some(order => order.status === 'Placed' && ['WebsiteCash', 'StripeCheckout'].includes(order.paymentProvider));
    if (awaitingOrders && !wasAwaiting && !playOrderSound()) soundStatus.textContent = 'Tap anywhere on this page to enable order sound.';
}

document.addEventListener('pointerdown', enableOrderSound);
document.addEventListener('keydown', enableOrderSound);
document.addEventListener('visibilitychange', () => { if (!document.hidden) enableOrderSound(); });
enableOrderSound();
setInterval(() => {
    if (awaitingOrders && !playOrderSound()) soundStatus.textContent = 'Tap anywhere on this page to enable order sound.';
}, 3000);

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

function buildOrderCard(order, id, compact = false) {
            const card = element('article', undefined, compact ? 'card queue-summary' : 'card');
            const estimatedTime = Date.parse(order.estimatedAtUtc);
            if (Number.isFinite(estimatedTime)) {
                card.dataset.estimatedTime = String(estimatedTime);
                const overdue = element('p', 'Past estimated time', 'overdue-label');
                overdue.hidden = estimatedTime > Date.now();
                card.classList.toggle('overdue', !overdue.hidden);
                card.append(overdue);
            }
            card.append(element('h3', '#' + order.orderNumber + ' · ' + order.orderType));
            card.append(element('p', new Date(order.createdAtUtc).toLocaleString('en-GB') +
                (order.status === 'Draft' ? ' · Draft' : ''), 'detail queue-private'));
            if (order.orderType === 'Delivery' && compact) card.append(element('p', deliveryPostcode(order.deliveryAddress) || 'Postcode unavailable', 'detail'));
            const websiteOrder = ['WebsiteCash', 'StripeCheckout'].includes(order.paymentProvider);
            if (websiteOrder && order.status === 'Placed') card.append(element('p', 'Website order · awaiting acceptance', 'note'));
            if (websiteOrder && order.status === 'Accepted') card.append(element('p', 'Website order · accepted', 'note'));
            if (order.estimatedAtUtc) card.append(element('p', 'Estimated ' + order.orderType.toLowerCase() + ': ' + new Date(order.estimatedAtUtc).toLocaleTimeString('en-GB', {hour: '2-digit', minute: '2-digit'}), 'detail'));
            if (order.customerName) card.append(element('p', order.customerName, 'queue-private'));
            if (order.customerPhone) card.append(element('p', order.customerPhone, 'detail queue-private'));
            if (order.orderType === 'Delivery' && order.deliveryAddress)
                card.append(element('p', order.deliveryAddress, 'detail queue-private'));
            const lines = element('ul', undefined, 'queue-private');
            for (const line of order.lines) {
                const item = element('li', line.quantity + ' × ' + line.productName + (line.variantName === 'Regular' ? '' : ' — ' + line.variantName));
                if (line.modifiers.length) item.append(element('div', line.modifiers.join(', '), 'detail'));
                if (line.notes) item.append(element('div', 'Kitchen note: ' + line.notes, 'detail'));
                lines.append(item);
            }
            card.append(lines);
            if (order.notes) card.append(element('p', order.notes, 'note queue-private'));
            card.append(element('strong', 'Order total ' + money.format(order.total)));
            const receipts = element('div', undefined, 'receipt-actions');
            const receipt = element('a', 'Receipt');
            receipt.href = '/receipt.html?order=' + order.orderNumber + '&type=customer';
            receipts.append(receipt);
            const paymentActions = [];
            if (order.paymentMethod) {
                card.append(element('p', order.paymentMethod + ' · ' + order.paymentStatus, 'payment ' + order.paymentStatus.toLowerCase()));
                if (order.paymentStatus === 'Pending' || order.paymentStatus === 'Authorised') {
                    const stripePayment = ['Stripe', 'StripeConfirming', 'StripeCancelled'].includes(order.paymentProvider);
                    if (order.paymentStatus === 'Pending' && (order.paymentProvider === 'WebsiteCash' || (!order.paymentProvider && !order.hasPaymentReference))) {
                        const method = order.paymentMethod === 'Cash' ? 'Card' : 'Cash';
                        const change = element('button', 'Change payment', 'payment-action');
                        change.setAttribute('aria-label', 'Order ' + order.orderNumber + ': change payment to ' + method);
                        change.addEventListener('click', () => changePaymentMethod(order, method));
                        receipts.append(change);
                    }
                    if (stripePayment) {
                        card.append(element('p', 'Previous sandbox payment. Check its record in Stripe test mode.', 'note'));
                    } else {
                        const paid = element('button', 'Mark paid', 'payment-action');
                        paid.setAttribute('aria-label', 'Order ' + order.orderNumber + ': mark paid');
                        paid.addEventListener('click', () => markPaid(order));
                        paymentActions.push(paid);
                    }
                }
            } else card.append(element('p', 'Payment not recorded', 'payment missing'));
            card.append(receipts, ...paymentActions);
            if (!compact && !['Completed', 'Cancelled', 'Refunded'].includes(order.status)) {
                const cancel = element('button', 'Cancel order', 'decline-order');
                cancel.type = 'button'; cancel.addEventListener('click', () => cancelOrder(order)); card.append(cancel);
            }
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
                const accept = element('button', 'Accept order', 'advance'); accept.type = 'submit'; accept.disabled = order.paymentStatus === 'RefundPending'; acceptForm.append(accept);
                acceptForm.addEventListener('submit', event => { event.preventDefault(); if (acceptForm.reportValidity()) acceptWebsiteOrder(order, estimate.value); });
                const decline = element('button', 'Decline order', 'decline-order'); decline.type = 'button';
                decline.addEventListener('click', () => declineWebsiteOrder(order));
                card.append(acceptForm, decline);
                if (order.paymentProvider === 'StripeCheckout') card.append(element('p', order.paymentStatus === 'RefundPending' ? 'Refund is being checked. Retry decline to finish; this order cannot be accepted.' : 'Declining automatically refunds the full card payment.', 'detail'));
            } else {
                if (id === 'ready' && order.orderType === 'Delivery') {
                    if (order.deliveryNotificationState) {
                        card.append(element('p', 'Delivery started', 'detail'));
                    } else {
                        const start = element('button', 'Start delivery', 'payment-action');
                        if (order.paymentStatus !== 'Paid') { start.disabled = true; start.classList.add('completion-unpaid'); card.append(element('p', 'Mark payment as paid before starting delivery.', 'note')); }
                        start.setAttribute('aria-label', 'Order ' + order.orderNumber + ': start delivery');
                        start.addEventListener('click', () => startDelivery(order));
                        card.append(start);
                    }
                }
                const next = id === 'new' ? 'Preparing' : id === 'preparing' ? 'Ready' : 'Completed';
                const button = element('button', id === 'new' ? 'Start preparing' : 'Mark ' + next.toLowerCase(), 'advance');
                if (next === 'Completed' && order.paymentStatus !== 'Paid') { button.disabled = true; button.classList.add('completion-unpaid'); card.append(element('p', 'Mark payment as paid before completing this order.', 'note')); }
                button.setAttribute('aria-label', 'Order ' + order.orderNumber + ': ' + button.textContent);
                button.addEventListener('click', () => advance(order, next));
                card.append(button);
            }
            return card;
}

async function cancelOrder(order) {
    if (saving) return;
    cancellingOrder = order; cancelError.textContent = ''; document.querySelector('#cancel-order-label').textContent = 'Order #' + order.orderNumber + ' will leave the active queue. Payments are not refunded automatically.';
    document.querySelector('input[name="cancel-reason"][value="Customer Request"]').checked = true; document.querySelector('#cancel-other-input').value = ''; document.querySelector('#cancel-other').hidden = true; cancelDialog.showModal();
}
document.querySelectorAll('input[name="cancel-reason"]').forEach(input => input.addEventListener('change', () => { document.querySelector('#cancel-other').hidden = input.value !== 'Other' || !input.checked; }));
document.querySelector('#cancel-keep').addEventListener('click', () => cancelDialog.close());
cancelConfirm.addEventListener('click', async () => { if (!cancellingOrder || saving) return; const selected = document.querySelector('input[name="cancel-reason"]:checked').value; const reason = selected === 'Other' ? document.querySelector('#cancel-other-input').value.trim() : selected; if (!reason) { cancelError.textContent = 'Enter a cancellation reason.'; return; } saving = true; cancelConfirm.disabled = true; cancelError.textContent = 'Cancelling order…'; try { await request('/api/orders/' + cancellingOrder.orderNumber + '/cancel', {method:'POST', headers:{'Content-Type':'application/json', 'X-HTTP-Method-Override':'PUT'}, body:JSON.stringify({reason})}); cancelDialog.close(); closeOrderDetails(); } catch (error) { cancelError.textContent = error.message; } finally { saving = false; cancelConfirm.disabled = false; } });

function updateOverdueCards() {
    const now = Date.now();
    document.querySelectorAll('.card[data-estimated-time]').forEach(card => {
        const overdue = Number(card.dataset.estimatedTime) <= now;
        card.classList.toggle('overdue', overdue);
        const label = card.querySelector('.overdue-label');
        if (label) label.hidden = !overdue;
    });
}
setInterval(updateOverdueCards, 15000);

function render(orders) {
    for (const [id, title] of [['new', 'New'], ['preparing', 'Preparing'], ['ready', 'Ready']]) {
        const items = orders.filter(o => id === 'new' ? ['Draft', 'Placed', 'Accepted'].includes(o.status)
            : o.status.toLowerCase() === id).sort((a, b) => { const direction = id === 'new' ? -1 : 1; return direction * ((Date.parse(a.createdAtUtc) || 0) - (Date.parse(b.createdAtUtc) || 0) || a.orderNumber - b.orderNumber); });
        const target = document.getElementById(id);
        target.replaceChildren();
        document.getElementById(id + '-title').textContent = title + ' (' + items.length + ')';
        if (!items.length) target.append(element('p', 'No orders here.', 'empty'));
        for (const order of items) {
            const card = buildOrderCard(order, id, true);
            const openDetails = () => {
                detailStatus.textContent = '';
                detailContent.replaceChildren(buildOrderCard(order, id));
                detailDialog.showModal();
            };
            const open = element('button', 'View order details ›', 'open-hint');
            open.type = 'button';
            open.setAttribute('aria-label', 'View details for order ' + order.orderNumber);
            open.addEventListener('click', openDetails);
            const heading = card.querySelector('h3');
            const headingRow = element('div', undefined, 'queue-card-heading');
            card.insertBefore(headingRow, heading);
            headingRow.append(heading, open);
            card.addEventListener('click', event => {
                if (!event.target.closest('button, a, input, select, textarea, form, label')) openDetails();
            });
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
        const editing = document.activeElement?.matches('input, textarea, select, [contenteditable="true"]') || document.querySelector('dialog[open]:not(#order-detail-dialog), .modal-backdrop.open');
        if (!editing) render(orders);
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

    async function autoPrintReceipt(orderNumber){
      try {
        const response=await fetch('/api/orders/'+orderNumber+'/receipt',{credentials:'same-origin',cache:'no-store',signal:AbortSignal.timeout(15000)});
        if(!response.ok)throw new Error();
        const order=await response.json(),type='customer';
const xmlEscape=value=>String(value??'').replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f]/g,'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
function printerContent(order){ return receiptPrinterContent(order); }
        const body=`<?xml version="1.0" encoding="utf-8"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><epos-print xmlns="http://www.epson-pos.com/schemas/2011/03/epos-print">${printerContent(order)}<cut type="feed"/>${printerContent(order)}<cut type="feed"/></epos-print></s:Body></s:Envelope>`;
        const printed=await fetch('https://'+await receiptPrinterAddress()+'/cgi-bin/epos/service.cgi?devid=local_printer&timeout=10000',{method:'POST',headers:{'Content-Type':'text/xml; charset=utf-8'},body,signal:AbortSignal.timeout(15000),credentials:'omit'});
        if(!printed.ok)throw new Error();
        const xml=new DOMParser().parseFromString(await printed.text(),'application/xml'),result=xml.getElementsByTagNameNS('*','response')[0];
        return result?.getAttribute('success')==='true';
      } catch {return false;}
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
        document.activeElement?.blur();
        message.textContent = 'Order #' + order.orderNumber + ' accepted. Sending two receipts to the printer…';
        const printed = await autoPrintReceipt(order.orderNumber);
        saving = false;
        closeOrderDetails();
        await load();
        message.textContent = printed
            ? 'Order #' + order.orderNumber + ' accepted. Two receipts sent to the printer; check the receipts.'
            : 'Order #' + order.orderNumber + ' accepted, but printing could not be confirmed. Check the printer before reprinting from Customer receipt.';
    } catch (error) { message.textContent = error.message + ' Check the order queue before trying again.'; }
    finally { saving = false; }
}

async function declineWebsiteOrder(order) {
    if (saving || loading) return;
    const prompt = order.paymentProvider === 'StripeCheckout'
        ? 'Decline order #' + order.orderNumber + ' and automatically refund the full card payment?'
        : 'Decline order #' + order.orderNumber + '?';
    if (!window.confirm(prompt)) return;
    saving = true;
    try {
        await request('/api/order-queue/' + order.orderNumber + '/decline', {method: 'PUT', timeoutMs: 30000});
        saving = false; closeOrderDetails(); await load(); message.textContent = 'Order #' + order.orderNumber + ' declined.';
    } catch (error) { saving = false; await load(); message.textContent = error.message; }
    finally { saving = false; }
}

async function advance(order, status) {
    if (saving || loading) return;
    saving = true;
    document.querySelectorAll('.advance,.payment-action').forEach(b => b.disabled = true);
    refresh.disabled = true;
    try {
        await request('/api/order-queue/' + order.orderNumber + '/advance', { method: 'POST', headers: { 'X-HTTP-Method-Override': 'PUT' } });
        saving = false;
        closeOrderDetails();
        await load();
    } catch (error) {
        message.textContent = error.message;
    } finally {
        saving = false;
        refresh.disabled = false;
        document.querySelectorAll('.advance,.payment-action').forEach(b => b.disabled = b.classList.contains('completion-unpaid'));
    }
}

async function changePaymentMethod(order, method) {
    if (saving || loading) return;
    saving = true;
    document.querySelectorAll('.advance,.payment-action').forEach(b => b.disabled = true);
    refresh.disabled = true;
    try {
        await request('/api/order-queue/' + order.orderNumber + '/payment/method', {
            method: 'POST', headers: { 'Content-Type': 'application/json', 'X-HTTP-Method-Override': 'PUT' }, body: JSON.stringify({ method })
        });
        saving = false;
        closeOrderDetails();
        await load();
    } catch (error) {
        message.textContent = error.message;
    } finally {
        saving = false;
        refresh.disabled = false;
        document.querySelectorAll('.advance,.payment-action').forEach(b => b.disabled = b.classList.contains('completion-unpaid'));
    }
}

async function startDelivery(order) {
    if (saving || loading) return;
    saving = true;
    document.querySelectorAll('.advance,.payment-action').forEach(b => b.disabled = true);
    refresh.disabled = true;
    try {
        await request('/api/order-queue/' + order.orderNumber + '/start-delivery', { method: 'POST' });
        saving = false;
        closeOrderDetails();
        await load();
    } catch (error) { message.textContent = error.message; }
    finally {
        saving = false;
        refresh.disabled = false;
        document.querySelectorAll('.advance,.payment-action').forEach(b => b.disabled = b.classList.contains('completion-unpaid'));
    }
}

async function markPaid(order) {
    if (saving || loading) return;
    saving = true;
    document.querySelectorAll('.advance,.payment-action').forEach(b => b.disabled = true);
    refresh.disabled = true;
    try {
        await request('/api/order-queue/' + order.orderNumber + '/payment/paid', { method: 'POST', headers: { 'X-HTTP-Method-Override': 'PUT' } });
        saving = false;
        closeOrderDetails();
        await load();
    } catch (error) {
        message.textContent = error.message;
    } finally {
        saving = false;
        refresh.disabled = false;
        document.querySelectorAll('.advance,.payment-action').forEach(b => b.disabled = b.classList.contains('completion-unpaid'));
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
