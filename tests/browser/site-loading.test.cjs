const assert=require('node:assert/strict');const vm=require('node:vm');const fs=require('node:fs');const path=require('node:path');
const source=fs.readFileSync(path.resolve(__dirname,'../../src/HrPayroll.Web/wwwroot/js/site.js'),'utf8');
function harness(){
 const button={innerHTML:'Save',dataset:{},disabled:false,hasAttribute(){return false;},setAttribute(n,v){this[n]=v;},removeAttribute(n){delete this[n];}};
 const form={dataset:{},checkValidity(){return true;},querySelector(){return button;},querySelectorAll(){return [button];}};
 const events={};const windowEvents={};
 const context={document:{querySelector(){return null;},getElementById(){return null;},querySelectorAll(q){return q==='form[data-busy="1"]'&&form.dataset.busy==='1'?[form]:[];},addEventListener(n,f){events[n]=f;}},window:{confirm(){return true;},addEventListener(n,f){windowEvents[n]=f;}},queueMicrotask,setTimeout,clearTimeout};
 vm.runInNewContext(source,context);return{button,form,submit(){const e={target:form,submitter:button,defaultPrevented:false,preventDefault(){this.defaultPrevented=true;}};events.submit(e);return e;},restore(){windowEvents.pageshow();}};
}
module.exports=(async()=>{
 const cancelled=harness();const event=cancelled.submit();event.preventDefault();await Promise.resolve();assert.equal(cancelled.form.dataset.busy,undefined);assert.equal(cancelled.button.innerHTML,'Save');
 const valid=harness();valid.submit();await Promise.resolve();assert.equal(valid.form.dataset.busy,'1');assert.equal(valid.button.disabled,false);assert.equal(valid.submit().defaultPrevented,true);valid.restore();assert.equal(valid.button.innerHTML,'Save');assert.equal(valid.form.dataset.busy,undefined);
 const invalid=harness();invalid.form.checkValidity=()=>false;invalid.submit();await Promise.resolve();assert.equal(invalid.form.dataset.busy,undefined);
 console.log('Passed 3 global form loading scenarios.');
})();
