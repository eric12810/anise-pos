import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const pos=fs.readFileSync(path.join(root,'src/AnisePOS.Api/wwwroot/pos.html'),'utf8');
const start=pos.indexOf("document.querySelector('#save-address').onclick=async()=>{");
const end=pos.indexOf('function clearCurrentOrder(){',start);
assert.ok(start>0&&end>start);
const source=pos.slice(start,end);

function setup(ok=true){
  const elements={
    '#save-address':{disabled:false},'#save-address-status':{className:'field-status',textContent:''},
    '#customer-name':{value:'Jane Smith'},'#customer-phone':{value:'07123456789'},
    '#delivery-address':{value:'10 High Street'},'#delivery-address-2':{value:'Flat 2'},
  };
  const calls=[];
  const state={cart:[{name:'Tofu'}],deliveryAddress:{postcode:'NW1 8ET'}};
  const context={state,savingAddress:false,placingOrder:false,printingDraft:false,postcodeSearch:{value:'NW1 8ET'},
    document:{querySelector:selector=>elements[selector]},AbortSignal:{timeout:()=>undefined},
    fetch:async(url,options)=>{calls.push({url,options});return {ok}},
  };
  vm.createContext(context);vm.runInContext(source,context);
  return {context,state,elements,calls};
}

test('Save address posts only customer details and keeps the cart',async()=>{
  assert.match(pos,/<button id="find-address"[^>]*>Map<\/button><\/div><button id="save-address"/);
  const {context,state,elements,calls}=setup();
  await elements['#save-address'].onclick();
  assert.equal(calls.length,1);
  assert.equal(calls[0].url,'/api/orders/customer-address');
  assert.equal(calls[0].options.method,'POST');
  assert.deepEqual(JSON.parse(calls[0].options.body),{name:'Jane Smith',phone:'07123456789',postcode:'NW1 8ET',addressLine1:'10 High Street',addressLine2:'Flat 2'});
  assert.equal(state.cart.length,1);
  assert.equal(elements['#save-address'].disabled,false);
  assert.equal(context.savingAddress,false);
  assert.match(elements['#save-address-status'].textContent,/No order placed/);
});

test('Save address validates fields and reports server failure',async()=>{
  const invalid=setup();invalid.elements['#customer-phone'].value='123';
  await invalid.elements['#save-address'].onclick();
  assert.equal(invalid.calls.length,0);
  assert.match(invalid.elements['#save-address-status'].textContent,/Enter name, phone, postcode/);
  const failed=setup(false);await failed.elements['#save-address'].onclick();
  assert.equal(failed.calls.length,1);
  assert.equal(failed.state.cart.length,1);
  assert.match(failed.elements['#save-address-status'].textContent,/Could not save/);
});
