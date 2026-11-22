import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import {fileURLToPath} from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const source = fs.readFileSync(path.join(root, 'src/AnisePOS.Api/wwwroot/payment-dropdown.js'), 'utf8');

function fixture(inDialog = false) {
  class Element {
    constructor() { this.children = []; this.style = {}; this.attrs = {}; this.listeners = {}; }
    append(child) { this.children.push(child); child.parent = this; }
    remove() { this.parent.children = this.parent.children.filter(child => child !== this); }
    setAttribute(name, value) { this.attrs[name] = value; }
    addEventListener(name, callback) { this.listeners[name] = callback; }
    contains(target) { return target === this || this.children.some(child => child.contains(target)); }
    querySelector(name) { return name === 'button' ? this.children[0] : null; }
    getBoundingClientRect() { return {left: 300, bottom: 140, width: 120}; }
    closest(name) { return name === 'dialog' ? dialog : null; }
    scrollIntoView() { this.scrolled = true; }
    focus() { this.focused = true; }
  }
  const body = new Element();
  const dialog = inDialog ? new Element() : null;
  if (dialog) dialog.style.overflow = 'auto';
  const anchor = new Element();
  const listeners = {};
  const document = {body, createElement: () => new Element(),
    addEventListener(name, callback) { listeners[name] = callback; },
    removeEventListener(name) { delete listeners[name]; }};
  const window = {innerWidth: 768, innerHeight: 800,
    addEventListener() {}, removeEventListener() {}};
  vm.runInNewContext(source, {document, window});
  return {window, body, dialog, anchor, listeners, Element};
}

test('Card choices open beneath the button, select immediately, and close', () => {
  const {window, body, anchor} = fixture();
  let selected = 'Card';
  const dropdown = window.createPaymentDropdown(anchor, ['SumUp', 'Card', 'Stripe'], () => selected,
    method => { selected = method; });
  dropdown.toggle();
  assert.equal(anchor.scrolled, true);
  assert.equal(anchor.attrs['aria-expanded'], 'true');
  const menu = body.children[0];
  assert.equal(menu.style.top, '144px');
  assert.deepEqual(menu.children.map(button => button.textContent), ['SumUp', 'Card', 'Stripe']);
  menu.children[2].listeners.click();
  assert.equal(selected, 'Stripe');
  assert.equal(body.children.length, 0);
  assert.equal(anchor.attrs['aria-expanded'], 'false');
});

test('Outside tap and Escape close without changing payment inside order details', () => {
  const {window, body, dialog, anchor, listeners, Element} = fixture(true);
  let selected = 'Card';
  const dropdown = window.createPaymentDropdown(anchor, ['SumUp', 'Card', 'Stripe'], () => selected,
    method => { selected = method; });
  dropdown.toggle();
  assert.equal(dialog.children.length, 1);
  assert.equal(dialog.style.overflow, 'visible');
  listeners.pointerdown({target: new Element()});
  assert.equal(selected, 'Card');
  assert.equal(anchor.attrs['aria-expanded'], 'false');
  assert.equal(dialog.style.overflow, 'auto');
  dropdown.toggle();
  listeners.keydown({key: 'Escape'});
  assert.equal(selected, 'Card');
  assert.equal(anchor.focused, true);
  assert.equal(body.children.length, 0);
});
