const assert=require('node:assert/strict');
const vm=require('node:vm');
const fs=require('node:fs');
const path=require('node:path');
const source=fs.readFileSync(path.resolve(__dirname,'../../src/HrPayroll.Web/wwwroot/js/attendance-correction.js'),'utf8');
function preview(start,end,inTime,outTime){
 const events={},values={};const input={value:inTime,addEventListener(name,fn){events.in=fn;}},output={value:outTime,addEventListener(name,fn){events.out=fn;}};
 const form={dataset:{shiftStart:start,shiftEnd:end},querySelector(q){if(q==='input[name="CheckIn"]')return input;if(q==='input[name="CheckOut"]')return output;return values[q]||(values[q]={textContent:''});}};
 vm.runInNewContext(source,{document:{querySelector(){return form;}}});
 return {value(name){return values['[data-preview-'+name+']'].textContent;},correct(value){output.value=value;events.out();}};
}
const day=preview('2026-11-01T07:00','2026-11-01T15:00','2026-11-01T08:03','2026-11-01T12:16');
assert.equal(day.value('late'),'63');assert.equal(day.value('early'),'164');assert.equal(day.value('worked'),'253');assert.equal(day.value('missing'),'227');
day.correct('2026-11-01T15:00');assert.equal(day.value('early'),'0');assert.equal(day.value('worked'),'417');assert.equal(day.value('missing'),'63');
const incomplete=preview('2026-11-01T07:00','2026-11-01T15:00','2026-11-01T07:00','');assert.equal(incomplete.value('worked'),'غير محسوم');assert.equal(incomplete.value('missing'),'غير محسوم');
const night=preview('2026-11-01T23:00','2026-11-02T07:00','2026-11-01T23:10','2026-11-02T06:30');assert.equal(night.value('late'),'10');assert.equal(night.value('early'),'30');assert.equal(night.value('worked'),'440');assert.equal(night.value('missing'),'40');
console.log('Passed 4 attendance correction preview scenarios.');
