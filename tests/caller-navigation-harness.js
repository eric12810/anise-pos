// Evaluated by run-caller-navigation.swift with callerSource set to the browser script.
let checks = 0;
function assert(value, message) {
  checks++;
  if (!value) throw new Error(message);
}
class Element {
  constructor(tag) { this.tagName = tag; this.children = []; this.listeners = {}; this.hidden = false; this.disabled = false; this.textContent = ''; }
  after(element) { this.afterElement = element; }
  append(...items) { this.children.push(...items); }
  replaceChildren(...items) { this.children = items; }
  setAttribute(name, value) { this[name] = value; }
  addEventListener(name, callback) { this.listeners[name] = callback; }
  click() { if (!this.disabled) this.listeners.click?.(); }
  get label() { return this.children[1]?.children.map(item => item.textContent).join(' | '); }
}
class Event {
  constructor(type, options) { this.type = type; this.detail = options?.detail; }
}
function boot(saved = {}, path = '/pos.html', search = '') {
  const stored = {'anise.caller.navigation.v2': JSON.stringify(saved)};
  const staff = new Element('span');
  staff.textContent = 'Staff member';
  globalThis.localStorage = {getItem:key => stored[key] || null, setItem:(key,value) => { stored[key] = value; }, removeItem:key => { delete stored[key]; }};
  globalThis.document = {hidden:false, querySelector:() => staff, createElement:tag => new Element(tag), addEventListener:() => {}};
  const listeners = {};
  globalThis.window = {addEventListener:(name,callback) => { listeners[name] = callback; }, dispatchEvent:event => listeners[event.type]?.(event)};
  globalThis.CustomEvent = Event;
  globalThis.location = {pathname:path, search, href:''};
  globalThis.history = {replaceState:() => {}};
  globalThis.URLSearchParams = class {constructor(query) { this.query=query; } get(key) { return new RegExp('[?&]'+key+'=([^&]+)').exec(this.query)?.[1] || null; }};
  globalThis.EventSource = class {constructor() { globalThis.stream = this; } close() {}};
  globalThis.fetch = () => new Promise(() => {});
  globalThis.setInterval = () => {};
  globalThis.setTimeout = callback => callback();
  eval(callerSource);
  const button = staff.afterElement;
  return {button, staff, stored, send:(callId, status, sequence, options={}) => {
    const notification = {eventId:options.eventId || callId+'-'+status, callId, callStatus:status,
      timestamp:new Date().toISOString(), callerNumber:options.phone === undefined ? '02079164976' : options.phone,
      customerId:options.customerId || null, customerName:options.name || null};
    stream.onmessage({data:JSON.stringify(notification), lastEventId:String(sequence)});
  }, dispatch:event => window.dispatchEvent(event)};
}

let app = boot();
assert(app.button.hidden && !app.staff.hidden, 'staff shown before calls');
app.send('a','ringing',1,{customerId:'customer-a'});
assert(!app.button.hidden && app.staff.hidden, 'ringing replaces staff display');
assert(app.button.label.includes('Existing customer'), 'known customer label');
app.send('a','ringing',1,{customerId:'customer-a'});
assert(app.button.label.includes('02079164976'), 'duplicate ring keeps caller');
app.send('a','answered',2);
assert(!app.button.hidden, 'answered call remains active');
app.send('b','ringing',3,{phone:'02070000000'});
assert(app.button.label.includes('02070000000'), 'newest active call shown');
app.send('a','ended',4);
assert(app.button.label.includes('02070000000'), 'older end does not hide newer call');
app.send('b','missed',5);
assert(app.button.hidden && !app.staff.hidden, 'missed call restores staff');
app.send('b','ringing',3);
assert(app.button.hidden, 'late ring cannot revive finished call');

app = boot();
app.send('a','ringing',1);
app.send('b','ringing',2,{phone:'02070000000'});
app.send('b','ended',3);
assert(app.button.label.includes('02079164976'), 'older active call restored');
app.send('a','missed',4);
assert(app.button.hidden, 'last call removal restores staff');

app = boot({calls:[{callId:'new',eventId:'new-ring',sequence:1,timestamp:new Date().toISOString(),
  callerNumber:'02070000000',known:true,existing:false,status:'ringing'}],cursor:1});
assert(app.button.label.includes('New customer'), 'new customer label');
let opened = null;
window.addEventListener('anise-open-caller-order', event => { opened = event.detail; });
app.button.click();
assert(opened?.phone === '02070000000', 'button invokes existing order handoff');
assert(!app.button.hidden, 'button remains until order handoff succeeds');
app.dispatch(new Event('anise-caller-order-opened',{detail:{eventId:'new-ring'}}));
assert(app.button.hidden && !app.staff.hidden, 'successful handoff removes button');
app.send('new','ringing',1);
assert(app.button.hidden, 'handled call cannot reappear');

app = boot();
app.send('private','ringing',1,{phone:null});
assert(!app.button.hidden && app.button.disabled, 'withheld call shown without order action');
assert(app.button.label.includes('Number unavailable'), 'withheld label');
app.send('private','missed',2);
assert(app.button.hidden, 'withheld call removal');

app = boot();
app.send('persist','ringing',1);
const saved = JSON.parse(app.stored['anise.caller.navigation.v2']);
app = boot(saved);
assert(!app.button.hidden, 'active call survives page navigation');
app.send('persist','ended',2);
assert(app.button.hidden, 'end after navigation removes button');
app = boot(JSON.parse(app.stored['anise.caller.navigation.v2']));
assert(app.button.hidden, 'finished call remains hidden after refresh');

app = boot();
app.send('late','ended',2);
app.send('late','ringing',1);
assert(app.button.hidden, 'terminal event before ring blocks stale call');

app = boot({calls:[{callId:'stale',eventId:'stale-ring',sequence:1,
  timestamp:new Date(Date.now()-6*60*1000).toISOString(),callerNumber:'02070000000',known:true,status:'ringing'}]});
assert(app.button.hidden, 'stale ringing call is removed after refresh');

app = boot();
const otherTab = JSON.stringify({calls:[{callId:'shared',eventId:'shared-ring',sequence:2,
  timestamp:new Date().toISOString(),callerNumber:'02070000000',known:true,existing:false,status:'ringing'}],cursor:2});
app.dispatch({type:'storage',key:'anise.caller.navigation.v2',newValue:otherTab});
assert(!app.button.hidden && app.button.label.includes('New customer'), 'same-browser tab updates header');

checks;
