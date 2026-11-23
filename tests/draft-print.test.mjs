import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const pos = fs.readFileSync(path.join(root, 'src/AnisePOS.Api/wwwroot/pos.html'), 'utf8');
const receipt = fs.readFileSync(path.join(root, 'src/AnisePOS.Api/wwwroot/receipt-print.js'), 'utf8');
const draftSource = pos.slice(pos.indexOf('function draftReceipt(){'), pos.indexOf("document.querySelector('#print-draft').onclick"));

test('draft receipt uses entered cart data and has no order or payment status', () => {
  assert.ok(draftSource.startsWith('function draftReceipt(){'));
  const fields = {
    '#delivery-fee': '2.50', '#custom-fee': '1.25', '#delivery-address': '1 High Street',
    '#delivery-address-2': 'Flat 2', '#estimate-minutes': '30', '#customer-name': 'Test Customer',
    '#customer-phone': '07123456789', '#notes': 'Ring bell', '#custom-fee-description': 'Extra',
  };
  const context = {
    state: {
      type: 'Delivery', deliveryAddress: { postcode: 'AB1 2CD' },
      menu: [{ products: [{ name: 'Tofu', chineseName: '豆腐', isVegan: true, variants: [{ id: 'v1', name: 'Large' }] }] }],
      cart: [{ variantId: 'v1', name: 'Tofu — Large', unit: 10, qty: 2, instructions: 'No onion', mods: [{ name: 'Chilli', price: 0.5 }] }],
    },
    document: { querySelector: selector => ({ value: fields[selector] }) },
  };
  vm.createContext(context);
  vm.runInContext(draftSource, context);
  const draft = context.draftReceipt();
  assert.equal(draft.isUnplacedDraft, true);
  assert.equal(draft.lines[0].quantity, 2);
  assert.equal(draft.lines[0].lineTotal, 21);
  assert.equal(draft.lines[0].isVegan, true);
  assert.equal(draft.lines[0].notes, 'No onion');
  assert.equal(draft.lines[0].modifiers[0].modifierName, 'Chilli');
  assert.equal(draft.subtotal, 21);
  assert.equal(draft.total, 24.75);
  assert.equal(draft.deliveryAddress, 'AB1 2CD\n1 High Street\nFlat 2');
  assert.equal(draft.customerName, 'Test Customer');
  assert.equal(draft.notes, 'Ring bell');
  assert.equal('orderNumber' in draft, false);
  assert.equal('payment' in draft, false);
});

test('draft printing does not call the order creation endpoint', () => {
  const handler = pos.slice(pos.indexOf("document.querySelector('#print-draft').onclick"), pos.indexOf('async function autoPrintReceipt'));
  assert.ok(handler.includes('receiptPrinterContent(order)'));
  assert.ok(handler.includes('receiptPrinterAddress()'));
  assert.ok(handler.includes("if(!state.cart.length)"));
  assert.ok(handler.includes("result?.getAttribute('success')!=='true'"));
  assert.ok(!handler.includes("fetch('/api/orders'"));
  assert.ok(receipt.includes("if(order.isUnplacedDraft){section('UNPLACED / DRAFT')"));
});
