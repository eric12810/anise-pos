'use strict';
const orderSoundPatterns={classic:[[0,660],[0.2,880]],chime:[[0,523],[0.16,659],[0.32,784]],bell:[[0,880],[0.24,880],[0.48,880]],urgent:[[0,1000],[0.2,1250],[0.4,1000],[0.6,1250],[0.8,1000],[1,1250]],double:[[0,880],[0.2,880],[0.65,880],[0.85,880],[1.3,880],[1.5,880]],rising:[[0,660],[0.17,830],[0.34,1046],[0.75,660],[0.92,830],[1.09,1046]],alarm:[[0,1250],[0.18,950],[0.36,1250],[0.54,950],[0.72,1250],[0.9,950],[1.08,1250],[1.26,950]]};
const happyBellsBuffers=new WeakMap();
let happyBellsPlaying=false;
async function prepareHappyBells(context){
 if(!happyBellsBuffers.has(context)){
  const pending=fetch('/happy-bells-notification.wav',{cache:'force-cache'}).then(r=>{if(!r.ok)throw new Error('Sound unavailable');return r.arrayBuffer()}).then(data=>context.decodeAudioData(data));
  happyBellsBuffers.set(context,pending);
 }
 try{return await happyBellsBuffers.get(context)}catch(error){happyBellsBuffers.delete(context);throw error}
}
function playSelectedOrderSound(context,name){
 if(!context||context.state!=='running')return false;
 if(name==='happy-bells'){
  if(happyBellsPlaying)return true;
  prepareHappyBells(context).then(buffer=>{if(context.state!=='running'||happyBellsPlaying)return;const source=context.createBufferSource();source.buffer=buffer;source.connect(context.destination);happyBellsPlaying=true;source.onended=()=>{happyBellsPlaying=false};source.start()}).catch(()=>{});
  return true;
 }
 const now=context.currentTime, volume=['urgent','double','rising','alarm'].includes(name)?0.24:0.15;
 for(const [offset,frequency] of (orderSoundPatterns[name]||orderSoundPatterns.classic)){
  const oscillator=context.createOscillator(),gain=context.createGain();oscillator.frequency.value=frequency;
  gain.gain.setValueAtTime(0.0001,now+offset);gain.gain.exponentialRampToValueAtTime(volume,now+offset+0.02);gain.gain.exponentialRampToValueAtTime(0.0001,now+offset+0.17);
  oscillator.connect(gain);gain.connect(context.destination);oscillator.start(now+offset);oscillator.stop(now+offset+0.18);
 }
 return true;
}
