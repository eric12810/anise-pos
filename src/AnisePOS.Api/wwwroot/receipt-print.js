'use strict';
async function receiptPrinterAddress(){
 const response=await fetch('/api/printer-settings',{credentials:'same-origin',cache:'no-store',signal:AbortSignal.timeout(10000)});
 if(!response.ok)throw new Error('Could not load printer settings.');
 const data=await response.json();if(!/^\d{1,3}(?:\.\d{1,3}){3}$/.test(data.ipAddress))throw new Error('Invalid printer address.');return data.ipAddress;
}

// Render each section at the printer's dot width, so font settings cannot change alignment.
function receiptPrinterContent(order) {
 const width=560, margin=10, parts=[];
 const money=value=>new Intl.NumberFormat('en-GB',{style:'currency',currency:'GBP'}).format(value);
 const date=value=>new Date(value).toLocaleString('en-GB',{timeZone:'Europe/London',day:'2-digit',month:'2-digit',year:'numeric',hour:'2-digit',minute:'2-digit'});
 function image(height,draw){
  const canvas=document.createElement('canvas');canvas.width=width;canvas.height=height;
  const ctx=canvas.getContext('2d');ctx.fillStyle='#fff';ctx.fillRect(0,0,width,height);ctx.fillStyle='#000';draw(ctx);
  const pixels=ctx.getImageData(0,0,width,height).data;let bytes='';
  for(let y=0;y<height;y++)for(let x=0;x<width;x+=8){let b=0;for(let bit=0;bit<8;bit++)if(pixels[(y*width+x+bit)*4]<160)b|=128>>bit;bytes+=String.fromCharCode(b)}
  parts.push(`<image width="${width}" height="${height}" color="color_1" mode="mono">${btoa(bytes)}</image>`);
 }
 const font=(size=30,bold=false)=>`${bold?'bold ':''}${size}px sans-serif`;
 function wrap(ctx,value,max){
  const result=[];
  for(const paragraph of String(value??'').split(/\r?\n/)){
   let line='';
   for(const word of paragraph.split(/\s+/)){
    const next=line?line+' '+word:word;
    if(ctx.measureText(next).width<=max){line=next;continue}
    if(line){result.push(line);line=''}
    for(const char of word){if(line&&ctx.measureText(line+char).width>max){result.push(line);line=''}line+=char}
   }
   result.push(line);
  }
  return result;
 }
 function measure(){const c=document.createElement('canvas');return c.getContext('2d')}
 function text(value,{size=30,bold=false,indent=0}={}){
  const c=measure();c.font=font(size,bold);const lines=wrap(c,value,width-2*margin-indent), step=Math.ceil(size*1.3);
  image(lines.length*step+4,ctx=>{ctx.font=font(size,bold);ctx.textBaseline='top';lines.forEach((line,i)=>ctx.fillText(line,margin+indent,2+i*step))});
 }
 function row(label,value,{bold=false,size=30}={}){
  const c=measure();c.font=font(30,bold);const right=String(value??''),rightWidth=c.measureText(right).width;c.font=font(size,bold);
  const leftWidth=Math.max(100,width-2*margin-rightWidth-20),lines=wrap(c,label,leftWidth), step=Math.ceil(size*1.3);
  if(rightWidth>width-2*margin){text(label,{bold,size});text(right,{bold,size});return}
  image(lines.length*step+4,ctx=>{ctx.font=font(size,bold);ctx.textBaseline='top';lines.forEach((line,i)=>ctx.fillText(line,margin,2+i*step));ctx.font=font(30,bold);ctx.textAlign='right';ctx.fillText(right,width-margin,2)});
 }
 function section(label){image(56,ctx=>{ctx.fillRect(0,10,width,40);ctx.fillStyle='#fff';ctx.font=font(29,true);ctx.textBaseline='middle';ctx.fillText(String(label).toUpperCase(),margin,30)})}
 function box(value){const c=measure();c.font=font(30);const lines=wrap(c,value,width-2*margin-20),step=39;image(lines.length*step+24,ctx=>{ctx.strokeStyle='#000';ctx.lineWidth=2;ctx.strokeRect(1,1,width-2,lines.length*step+22);ctx.font=font(30);ctx.textBaseline='top';lines.forEach((line,i)=>ctx.fillText(line,margin+10,12+i*step))})}
 function paymentBox(paid,unpaid){image(76,ctx=>{ctx.strokeStyle='#000';ctx.lineWidth=2;ctx.strokeRect(1,1,width-2,74);ctx.font=font(30,true);ctx.textBaseline='middle';const labels=['Paid','Not Paid'],states=[paid,unpaid],side=24,gap=36,spacing=10, lengths=labels.map(x=>side+spacing+ctx.measureText(x).width);let x=(width-lengths[0]-lengths[1]-gap)/2;labels.forEach((label,i)=>{ctx.strokeRect(x,26,side,side);if(states[i])ctx.fillRect(x+3,29,side-6,side-6);ctx.fillText(label,x+side+spacing,38);x+=lengths[i]+gap})})}
 function space(height=14){image(height,()=>{})}
 if(order.isUnplacedDraft){section('UNPLACED / DRAFT');text('NOT AN ORDER — NOT SENT TO KITCHEN',{bold:true});space()}
 section(order.orderType||'Order');
 if(order.orderType==='Delivery'&&order.deliveryAddress)box(order.deliveryAddress);
 section('Order details');if(order.isUnplacedDraft){row('Status:','UNPLACED / DRAFT');row('Printed at:',date(order.createdAtUtc));if(order.estimatedMinutes)row('Estimated time:',order.estimatedMinutes+' minutes (if placed)')}else{row('Number:',order.orderNumber);row('Placed at:',date(order.createdAtUtc));row('Status:',order.status);if(order.estimatedAtUtc)row(order.orderType==='Delivery'?'Estimated delivery:':'Estimated collection:',date(order.estimatedAtUtc))}
 if(order.customerName||order.customerPhone){section('Client info');if(order.customerName)row('Name:',order.customerName);if(order.customerPhone)row('Phone:',order.customerPhone)}
 if(order.notes){space();text('NOTE: '+order.notes,{bold:true})}
 space();text('Items:',{bold:true});
 for(const item of order.lines){const displayName=(item.isVegan?'🌱 ':'')+item.productName;row(item.quantity+'x '+displayName,money(item.lineTotal),{bold:true,size:36});if(item.chineseName)text(item.chineseName,{size:36});if(item.variantName&&item.variantName!=='Regular')text('Size: '+item.variantName,{indent:20});for(const mod of item.modifiers)text('+ '+mod.modifierName+(mod.priceAdjustment?' ('+money(mod.priceAdjustment)+')':''),{indent:20});if(item.notes)text('NOTE: '+item.notes,{bold:true,indent:20});space(8)}
 image(16,ctx=>{ctx.fillRect(margin,7,width-2*margin,2)});
 row('Sub-total:',money(order.subtotal));if(order.deliveryFee)row('Delivery:',money(order.deliveryFee));if(order.customFee)row(order.customFeeDescription||'Additional charge',money(order.customFee));if(order.discountTotal)row('Discount:','-'+money(order.discountTotal));row('TOTAL:',money(order.total),{bold:true});space(24);
 if(order.isUnplacedDraft){text('UNPLACED / DRAFT',{bold:true})}else{const status=order.payment?.status,paid=status==='Paid',unpaid=['Pending','Authorised','Failed','Voided'].includes(status);paymentBox(paid,unpaid);if(!paid&&!unpaid)text('Payment: '+(status||'not recorded'));if(order.payment)section(order.payment.method)}
 parts.push('<text>\n\n\n</text>');return parts.join('');
}
