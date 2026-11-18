(function () {
'use strict';
var form=document.getElementById('advanceRegister');
if(!form||form.dataset.readOnly==='true') return;
var status=document.getElementById('advanceStatus'),busy=false;
function rows(){return Array.from(form.querySelectorAll('[data-advance-row]'));}
function amountOf(row){return row.querySelector('[data-amount]');}
function format(value){return Number(value).toLocaleString('en-US',{minimumFractionDigits:2,maximumFractionDigits:2});}
function warning(row){row.querySelector('[data-warning]').textContent=Number(amountOf(row).value||0)>Number(row.dataset.available)?'المبلغ أكبر من المتاح من المكتسب؛ يمكنك حفظه.':'';}
function hasChanges(){return rows().some(function(row){return !amountOf(row).disabled&&Number(amountOf(row).value||0)!==Number(row.dataset.savedAmount||0);});}
form.addEventListener('input',function(e){var row=e.target.closest('[data-advance-row]');if(row&&e.target.matches('[data-amount]')){warning(row);var state=row.querySelector('[data-row-status]');state.className='save-state';state.textContent='تعديل غير محفوظ';}});
window.addEventListener('beforeunload',function(e){if(hasChanges()){e.preventDefault();e.returnValue='';}});
form.addEventListener('keydown',function(e){if(e.key==='Enter'&&e.target.matches('[data-amount]')){var row=e.target.closest('[data-advance-row]');if(row){e.preventDefault();form.requestSubmit(row.querySelector('[data-save-row]'));}}});
form.addEventListener('submit',async function(e){
e.preventDefault();if(busy)return;
var button=e.submitter,row=button&&button.closest('[data-advance-row]');
if(!row){status.textContent='اختر حفظ في صف الموظف المطلوب.';return;}
var input=amountOf(row),amount=Number(input.value||0),state=row.querySelector('[data-row-status]');
if(input.disabled||!Number.isFinite(amount)||amount<0||!input.checkValidity()){state.className='save-state error';state.textContent='أدخل مبلغاً صالحاً؛ الصفر يزيل السلفة.';return;}
warning(row);
var payload=new FormData();
['Year','Month','__RequestVerificationToken'].forEach(function(name){var control=form.querySelector('input[name="'+name+'"]');if(control)payload.append(name,control.value);});
payload.append('Payments[0].EmployeeId',row.dataset.employeeId);payload.append('Payments[0].Amount',String(amount));payload.append('Payments[0].Id',row.querySelector('[data-record-id]').value);payload.append('Payments[0].Revision',row.querySelector('[data-revision]').value);payload.append('Payments[0].Selected','true');
var controller=new AbortController(),timer=setTimeout(function(){controller.abort();},45000);
busy=true;input.disabled=true;button.disabled=true;button.innerHTML='<span class="spinner"></span> حفظ';state.className='save-state';state.textContent='جاري تسجيل السلفة...';status.textContent='';
try{
var response=await fetch(form.action,{method:'POST',body:payload,signal:controller.signal,headers:{'X-Requested-With':'XMLHttpRequest'}});
var data;try{data=await response.json();}catch(_){throw new Error('تعذر قراءة استجابة الخادم. تحقق من تسجيل الدخول وأعد المحاولة.');}
if(!response.ok)throw new Error(data.error||data.message||'تعذر حفظ السلفة. حاول مرة أخرى.');
var saved=(data.rows||[]).find(function(item){return Number(item.employeeId)===Number(row.dataset.employeeId);});
if(!saved)throw new Error('لم يؤكد الخادم حفظ السلفة. أعد تحميل الكشف للتحقق قبل المحاولة مجدداً.');
row.querySelector('[data-record-id]').value=saved.id;row.querySelector('[data-revision]').value=saved.revision;row.dataset.savedAmount=String(saved.amount);input.value=Number(saved.amount).toFixed(2);row.querySelector('[data-existing-amount]').textContent=format(saved.amount);
var total=document.getElementById('registeredTotal');if(total)total.textContent=format(rows().reduce(function(value,item){return value+Number(item.dataset.savedAmount||0);},0))+' ج.م';
if(saved.capacity!==undefined)row.dataset.available=String(saved.capacity);var available=row.querySelector('[data-available-display]');if(available&&saved.available!==undefined)available.textContent=format(saved.available);
warning(row);state.className='save-state success';state.textContent=Number(saved.amount)===0?'أزيلت السلفة مع حفظ السجل':'محفوظة للخصم من مرتب الدورة';status.textContent=data.message||'تم حفظ السلفة.';
}catch(error){state.className='save-state error';state.textContent=error.name==='AbortError'?'انتهت المهلة. تحقق من الكشف قبل إعادة الحفظ.':error.name==='TypeError'?'تعذر الاتصال بالخادم. أعد المحاولة.':error.message;status.textContent=state.textContent;
}finally{clearTimeout(timer);busy=false;input.disabled=false;button.disabled=false;button.innerHTML='حفظ';}
});
})();
