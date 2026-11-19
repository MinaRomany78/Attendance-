const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const source = fs.readFileSync(path.resolve(__dirname, '../../src/HrPayroll.Web/wwwroot/js/payroll-sheet.js'), 'utf8');
function harness(withDetails = false) {
  function element() { return { dataset: {}, textContent: '', disabled: false, hidden: false, classList: { add(){}, remove(){}, toggle(){} }, events: {}, addEventListener(n, f){ this.events[n]=f; } }; }
  const fields = new Map(); const cells = new Map();
  for (const n of ['overtimeAmount','grossEarnings','totalDeductions','netSalary']) cells.set(n,{textContent:n === 'netSalary' ? '123.45' : '0'});
  const tr = Object.assign(element(), { dataset: {itemId:'1'}, querySelector(q) { const m = /data-field="([^"]+)"/.exec(q); if(m) return fields.get(m[1]) || null; const c=/data-cell="([^"]+)"/.exec(q); return c ? cells.get(c[1]) || null : null; } });
  const input = Object.assign(element(), { value:'1', checkValidity(){return true;}, reportValidity(){}, closest(){ return tr; } }); fields.set('bonuses',input);
  const table = Object.assign(element(), { dataset:{canEdit:'1',runId:'1',saveUrl:'/save',breakdownUrl:'/details'}, querySelectorAll(q){return q === 'input.cell-input' ? [input] : [tr];}, querySelector(q){return q.startsWith('tr[') ? tr : null;} });
  const els={payrollSheet:table,saveBtn:element(),discardBtn:element(),dirtyIndicator:element(),dirtyCount:element(),sheetToast:element()};
  if(withDetails){els.breakdownModal=element();els.breakdownModal.hidden=true;els.bdBody=element();els.bdTitle=element();}
  let resolve, reject; let calls=0; const timers=new Map(); let sequence=0;
  const context={document:{getElementById(n){return els[n]||null;},querySelector(){return null;},addEventListener(){}},window:{addEventListener(){},confirm(){return false;}},
    fetch(url, options){calls++;return new Promise((ok,fail)=>{resolve=ok;reject=fail; options.signal.addEventListener('abort',()=>fail(Object.assign(new Error('Timeout'),{name:'AbortError'})));});},AbortController,console,Set,Map,
    setTimeout(f,ms){timers.set(++sequence,{f,ms});return sequence;},clearTimeout(id){timers.delete(id);}};
  vm.runInNewContext(source,context);
  return {els,input,cells,details(id='1'){table.events.click({target:{closest(){return {dataset:{breakdown:id}};}}});},closeDetails(){els.breakdownModal.events.click({target:els.breakdownModal});},detailTimeout(){[...timers.values()].find(t=>t.ms===30000).f();},dirty(){table.events.input({target:{closest(){return input;}}});},save(){els.saveBtn.events.click();},
    get calls(){return calls;},respond(ok,data){resolve({ok,json:async()=>data});},network(){reject(new Error('Network unavailable'));},timeout(){[...timers.values()].find(t=>t.ms===60000).f();},
    async flush(){for(let i=0;i<12;i++) await Promise.resolve();}};
}
module.exports = (async()=>{
  let count=0;
  for(const failure of ['server','validation','network','invalidResponse','timeout']) {
    const h=harness();h.dirty();h.save();assert.equal(h.els.saveBtn.disabled,true);h.save();assert.equal(h.calls,1);
    if(failure==='network') h.network();else if(failure==='timeout') h.timeout();else h.respond(failure==='invalidResponse',failure==='invalidResponse'?{}:{error:failure});
    await h.flush();assert.equal(h.els.saveBtn.disabled,false);assert.equal(h.input.value,'1');h.save();assert.equal(h.calls,2);count++;
  }
  const h=harness();h.dirty();h.save();h.respond(true,{rows:[{payrollItemId:1,saved:true,values:{bonuses:1,netSalary:123.45,grossEarnings:150,totalDeductions:26.55}}],savedCount:1});await h.flush();
  assert.equal(h.els.saveBtn.disabled,true);assert.equal(h.cells.get('netSalary').textContent,'123.45');count++;
  const newer=harness();newer.dirty();newer.save();newer.input.value='2';newer.dirty();newer.respond(true,{rows:[{payrollItemId:1,saved:true,values:{bonuses:1,netSalary:0}}],savedCount:1});await newer.flush();
  assert.equal(newer.input.value,'2');assert.equal(newer.els.saveBtn.disabled,false);count++;
  const invalid=harness();invalid.dirty();invalid.input.checkValidity=()=>false;invalid.save();assert.equal(invalid.calls,0);count++;
  for(const failure of ['server','network','timeout']){const d=harness(true);d.details();assert.match(d.els.bdBody.innerHTML,/spinner/);if(failure==='network')d.network();else if(failure==='timeout')d.detailTimeout();else d.respond(false,{error:'تعذر تحميل التفاصيل'});await d.flush();assert.doesNotMatch(d.els.bdBody.innerHTML,/spinner/);assert.match(d.els.bdBody.innerHTML,/request-error/);count++;}
  const closed=harness(true);closed.details();closed.closeDetails();await closed.flush();assert.equal(closed.els.breakdownModal.hidden,true);count++;
  console.log('Passed '+count+' payroll JavaScript regression scenarios.');
})();

