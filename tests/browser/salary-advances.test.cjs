const assert=require('node:assert/strict');
const vm=require('node:vm');
const fs=require('node:fs');
const path=require('node:path');
const source=fs.readFileSync(path.resolve(__dirname,'../../src/HrPayroll.Web/wwwroot/js/salary-advances.js'),'utf8');
function harness(options={}){
 const events={},windowEvents={},calls=[];let resolve,reject,timer;
 const amount={value:options.amount??'5000',disabled:!!options.locked,checkValidity(){return this.value===''||Number.isFinite(Number(this.value))&&Number(this.value)>=0;},matches(){return true;}};
 const id={value:options.id??'0'},revision={value:'0'},warning={textContent:''},state={textContent:'',className:''},existing={textContent:'0'},available={textContent:'200'},total={textContent:'0'};
 const button={disabled:!!options.locked,innerHTML:'حفظ',closest(){return row;}};
 const row={dataset:{employeeId:'1',savedAmount:options.saved??'0',available:'200'},querySelector(selector){return {'[data-amount]':amount,'[data-record-id]':id,'[data-revision]':revision,'[data-warning]':warning,'[data-row-status]':state,'[data-existing-amount]':existing,'[data-available-display]':available,'[data-save-row]':button}[selector];}};
 amount.closest=()=>row;
 const status={textContent:''};
 const controls={Year:{value:'2026'},Month:{value:'11'},__RequestVerificationToken:{value:'csrf-token'}};
 const form={action:'/SalaryAdvances/Save',dataset:{readOnly:options.readOnly?'true':'false'},addEventListener(name,handler){events[name]=handler;},querySelectorAll(){return [row];},querySelector(selector){return controls[selector.match(/name="([^"]+)"/)[1]];},requestSubmit(submitter){events.submit({preventDefault(){},submitter});}};
 class Data{constructor(){this.values=[];}append(name,value){this.values.push([name,String(value)]);}entries(){return this.values[Symbol.iterator]();}}
 vm.runInNewContext(source,{document:{getElementById(name){return {advanceRegister:form,advanceStatus:status,registeredTotal:total}[name];}},window:{addEventListener(name,handler){windowEvents[name]=handler;},confirm(){throw new Error('Confirmation must not be requested');},location:{reload(){throw new Error('Page reload must not be required');}}},FormData:Data,AbortController,setTimeout(callback){timer=callback;return 1;},clearTimeout(){},fetch(url,settings){calls.push({url,data:Object.fromEntries(settings.body.entries())});return new Promise((yes,no)=>{resolve=yes;reject=no;settings.signal.addEventListener('abort',()=>no(Object.assign(new Error('aborted'),{name:'AbortError'})));});}});
 return {row,amount,id,revision,warning,state,button,existing,available,total,status,calls,events,submit(){return events.submit?.({preventDefault(){},submitter:button});},respond(ok,data){resolve({ok,json:async()=>data});},invalidJson(){resolve({ok:true,json:async()=>{throw new Error('bad JSON');}});},fail(){reject(new TypeError('Failed to fetch'));},timeout(){timer();},dirty(){let prevented=false;windowEvents.beforeunload?.({preventDefault(){prevented=true;}});return prevented;},enter(){events.keydown({key:'Enter',target:amount,preventDefault(){}});}};
}
module.exports=(async()=>{
 let scenarios=0;
 const active=harness();assert.equal(active.dirty(),true);let pending=active.submit();assert.equal(active.calls.length,1);assert.equal(active.calls[0].url,'/SalaryAdvances/Save');assert.equal(active.calls[0].data['Payments[0].Amount'],'5000');assert.equal(active.calls[0].data['Payments[0].Selected'],'true');assert.match(active.warning.textContent,/أكبر/);await active.submit();assert.equal(active.calls.length,1);active.respond(true,{message:'saved',rows:[{employeeId:1,id:8,revision:1,amount:5000,available:0,capacity:200}]});await pending;assert.equal(active.id.value,8);assert.equal(active.revision.value,1);assert.equal(active.dirty(),false);assert.equal(active.button.disabled,false);assert.equal(active.button.innerHTML,'حفظ');assert.match(active.state.textContent,/محفوظة للخصم/);assert.equal(active.available.textContent,'0.00');assert.equal(active.row.dataset.available,'200');scenarios+=3;
 active.amount.value='4000';pending=active.submit();active.respond(true,{rows:[{employeeId:1,id:8,revision:2,amount:4000,available:0,capacity:200}]});await pending;assert.equal(active.id.value,8);assert.equal(active.revision.value,2);assert.equal(active.existing.textContent,'4,000.00');assert.match(active.total.textContent,/4,000.00/);assert.equal(active.dirty(),false);scenarios++;
 for(const value of ['0','']){const zero=harness({amount:value,saved:'2000'});pending=zero.submit();assert.equal(zero.calls[0].data['Payments[0].Amount'],'0');zero.respond(true,{rows:[{employeeId:1,id:9,revision:2,amount:0}]});await pending;assert.match(zero.state.textContent,/أزيلت/);assert.equal(zero.existing.textContent,'0.00');scenarios++;}
 for(const kind of ['network','server','json','timeout','missing-result']){const failed=harness();pending=failed.submit();if(kind==='network')failed.fail();if(kind==='server')failed.respond(false,{error:'تغيرت السلفة. أعد تحميل الصفحة.'});if(kind==='json')failed.invalidJson();if(kind==='timeout')failed.timeout();if(kind==='missing-result')failed.respond(true,{rows:[]});await pending;assert.equal(failed.amount.disabled,false);assert.equal(failed.button.disabled,false);assert.equal(failed.amount.value,'5000');assert.equal(failed.id.value,'0');assert.equal(failed.revision.value,'0');assert.equal(failed.button.innerHTML,'حفظ');assert.match(failed.state.className,/error/);assert.ok(failed.status.textContent.length>0);scenarios++;}
 for(const value of ['-5','not-a-number']){const invalid=harness({amount:value});await invalid.submit();assert.equal(invalid.calls.length,0);scenarios++;}
 const historical=harness({readOnly:true});await historical.submit();assert.equal(historical.calls.length,0);scenarios++;
 const locked=harness({locked:true});await locked.submit();assert.equal(locked.calls.length,0);scenarios++;
 const enter=harness();enter.enter();assert.equal(enter.calls.length,1);enter.respond(true,{rows:[{employeeId:1,id:10,revision:1,amount:5000}]});await new Promise(resolve=>setImmediate(resolve));assert.equal(enter.button.disabled,false);scenarios++;
 console.log('Passed '+scenarios+' immediate-advance JavaScript scenarios.');return scenarios;
})();


