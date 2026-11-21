import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const pos=fs.readFileSync(path.join(root,'src/AnisePOS.Api/wwwroot/pos.html'),'utf8');
const start=pos.indexOf('function clearCurrentOrder(confirmed=false){');
const end=pos.indexOf("document.querySelector('#clear-order').onclick=()=>clearCurrentOrder();",start);
assert.ok(start>0&&end>start);
const source=pos.slice(start,end);

function setup(confirmed=true,widgetReplaced=false){
  const ids=['customer-name','customer-phone','delivery-address','delivery-address-2','manual-postcode','notes','custom-fee','custom-fee-description','delivery-fee','delivery-fee-override','delivery-fields','address-select','address-results','phone-lookup-status','address-lookup-status','places-status','delivery-fee-status','route-summary','copy-status','save-address-status','notice'];
  const elements=Object.fromEntries(ids.map(id=>[id,{value:'',checked:false,hidden:false,textContent:'',className:'',innerHTML:'',_addresses:[1]}]));
  Object.assign(elements['customer-name'],{value:'Customer'});
  elements['manual-postcode'].value='AB1 2CD';
  Object.assign(elements['delivery-address'],{value:'1 High Street'});
  Object.assign(elements['notes'],{value:'No onion'});
  Object.assign(elements['custom-fee'],{value:'2.00'});
  Object.assign(elements['delivery-fee'],{value:'3.00'});
  elements['delivery-fee-override'].checked=true;
  const typeButtons=[{dataset:{type:'Collection'},classList:{toggle(_,active){this.active=active}}},{dataset:{type:'Delivery'},classList:{toggle(_,active){this.active=active}}}];
  const paymentButtons=[{dataset:{payment:'Cash'},classList:{toggle(_,active){this.active=active}}},{dataset:{payment:'Card'},classList:{toggle(_,active){this.active=active}}}];
  const state={cart:[{name:'Tofu'}],type:'Delivery',payment:'Card',deliveryAddress:{postcode:'AB1 2CD',line1:'1 High Street',line2:''}};
  const calls={render:0,estimate:0,postcode:[],quotes:0,confirm:0,fetch:0};
  const context={state,printingDraft:false,placingOrder:false,savingAddress:false,manualPostcode:elements['manual-postcode'],phoneLookupTimer:null,phoneLookupVersion:1,deliveryEditVersion:1,postcodeBlurTimer:null,postcodeSelectionPending:true,routeVersion:1,selectedPlace:{id:'old'},
    document:{querySelector:selector=>widgetReplaced&&selector==='#manual-postcode'?null:elements[selector.slice(1)],querySelectorAll:selector=>selector==='#types button'?typeButtons:paymentButtons},
    window:{confirm:()=>{calls.confirm++;return confirmed}},clearTimeout:()=>{},
    setPostcodeSearchValue:value=>calls.postcode.push(value),invalidateDeliveryQuote:()=>calls.quotes++,
    updateEstimateOptions:()=>calls.estimate++,renderCart:()=>calls.render++,fetch:()=>{calls.fetch++;throw Error('Unexpected request')}
  };
  vm.createContext(context);vm.runInContext(source,context);
  return {context,state,elements,typeButtons,paymentButtons,calls};
}

test('clear button sits next to Current order and resets the in-progress form',()=>{
  assert.match(pos,/<h2>Current order<\/h2><button id="clear-order"/);
  const {context,state,elements,typeButtons,paymentButtons,calls}=setup();
  context.clearCurrentOrder();
  assert.equal(calls.confirm,1);
  assert.equal(state.cart.length,0);
  assert.equal(state.type,'Collection');assert.equal(state.payment,'Cash');
  assert.equal(state.deliveryAddress.postcode,'');
  assert.equal(elements['manual-postcode'].value,'');
  for(const id of ['customer-name','customer-phone','delivery-address','delivery-address-2','notes','custom-fee-description'])assert.equal(elements[id].value,'');
  assert.equal(elements['custom-fee'].value,'0.00');
  assert.equal(elements['delivery-fee'].value,'0.00');
  assert.equal(elements['delivery-fee-override'].checked,false);
  assert.equal(elements['delivery-fields'].hidden,true);
  assert.equal(elements['address-results'].hidden,true);
  assert.equal(typeButtons[0].classList.active,true);assert.equal(paymentButtons[0].classList.active,true);
  assert.equal(calls.render,1);assert.equal(calls.estimate,1);
  assert.deepEqual(calls.postcode,['']);assert.equal(calls.fetch,0);
  assert.equal(context.phoneLookupVersion,2);assert.equal(context.routeVersion,2);
});

test('cancelled confirmation and active submission leave the order intact',()=>{
  const cancelled=setup(false);cancelled.context.clearCurrentOrder();
  assert.equal(cancelled.state.cart.length,1);assert.equal(cancelled.calls.render,0);
  const busy=setup();busy.context.placingOrder=true;busy.context.clearCurrentOrder();
  assert.equal(busy.state.cart.length,1);assert.equal(busy.calls.confirm,0);
});

test('clear redraws selected dishes after Google replaces the postcode field',()=>{
  const replaced=setup(true,true);
  replaced.context.clearCurrentOrder();
  assert.equal(replaced.state.cart.length,0);
  assert.equal(replaced.calls.render,1);
  assert.equal(replaced.elements['manual-postcode'].value,'');
});
