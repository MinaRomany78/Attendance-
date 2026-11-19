/* ============================================================
   كشف المرتبات الشهري — تحرير مباشر داخل الجدول (شبيه Excel)
   - معاينة فورية للقيم المحسوبة بنفس معادلات السيرفر.
   - الحساب النهائي والاعتماد يكونان على السيرفر دائمًا.
   - مؤشر تغييرات غير محفوظة + حفظ يدوي/تلقائي + تأكيد تجاوز الرصيد.
   ============================================================ */
(function () {
    'use strict';

    var table = document.getElementById('payrollSheet');
    if (!table) return;

    var canEdit = table.dataset.canEdit === '1';
    var runId = parseInt(table.dataset.runId, 10);
    var saveUrl = table.dataset.saveUrl;
    var breakdownUrl = table.dataset.breakdownUrl;
    var columnsToggle = document.getElementById('sheetColumnsToggle');
    if (columnsToggle) columnsToggle.addEventListener('click', function () {
        var compact = table.classList.toggle('is-compact');
        columnsToggle.setAttribute('aria-pressed', compact ? 'false' : 'true');
        columnsToggle.textContent = compact ? 'عرض كل بنود الحساب' : 'العرض المختصر';
    });
    var saveBtn = document.getElementById('saveBtn');
    var discardBtn = document.getElementById('discardBtn');
    var dirtyIndicator = document.getElementById('dirtyIndicator');
    var dirtyCount = document.getElementById('dirtyCount');
    var autoSaveToggle = document.getElementById('autoSaveToggle');
    var dirtyRows = new Set();
    var autoSaveTimer = null;
    var saving = false;
    var revisions = new Map();

    function toNum(v) {
        if (v === null || v === undefined) return 0;
        var n = parseFloat(String(v).replace(/,/g, ''));
        return isNaN(n) ? 0 : n;
    }
    function round2(v) {
        return Math.round((v + Number.EPSILON) * 100) / 100;
    }
    function money(v) {
        return round2(v).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    }
    function days(v) {
        if (Math.abs(v - Math.round(v)) < 0.0001) return String(Math.round(v));
        return round2(v).toLocaleString('en-US', { maximumFractionDigits: 2 });
    }
    function field(tr, name) {
        var el = tr.querySelector('input[data-field="' + name + '"]');
        return el ? el : null;
    }
    function val(tr, name) {
        var el = field(tr, name);
        if (!el || el.value === '') return 0;
        return toNum(el.value);
    }
    function cell(tr, name) {
        return tr.querySelector('[data-cell="' + name + '"]');
    }
    function setCell(tr, name, text) {
        var c = cell(tr, name);
        if (c) c.textContent = text;
    }

    /* ---------- المعاينة الفورية بنفس معادلات PayrollCalculator ---------- */
    // Financial totals stay on the saved server snapshot until SaveAsync returns.
    // Client formulas cannot reproduce configured duration policies and decimal arithmetic.
    function computeRow(tr) {
        function saved(name) { var c = cell(tr, name); return c ? toNum(c.textContent) : 0; }
        return { annualLeaveDays: val(tr, 'annualLeaveDays'), overtimeAmount: saved('overtimeAmount'),
            gross: saved('grossEarnings'), deductions: saved('totalDeductions'), net: saved('netSalary') };
    }
    function refreshTotals() {
        var t = { annualLeave: 0, overtime: 0, gross: 0, deductions: 0, net: 0 };
        table.querySelectorAll('tbody tr.sheet-row').forEach(function (tr) {
            var r = computeRow(tr);
            t.annualLeave += r.annualLeaveDays;
            t.overtime += r.overtimeAmount;
            t.gross += r.gross;
            t.deductions += r.deductions;
            t.net += r.net;
        });
        function setTotal(name, text) {
            var el = table.querySelector('[data-total="' + name + '"]');
            if (el) el.textContent = text;
        }
        setTotal('annualLeave', days(t.annualLeave));
        setTotal('overtime', money(t.overtime));
        setTotal('gross', money(t.gross));
        setTotal('deductions', money(t.deductions));
        setTotal('net', money(t.net));

        function kpi(name, text) {
            var el = document.querySelector('[data-kpi="' + name + '"]');
            if (el) el.textContent = text;
        }
        kpi('overtime', money(t.overtime));
        kpi('gross', money(t.gross));
        kpi('deductions', money(t.deductions));
        kpi('net', money(t.net));
        kpi('annualleave', days(t.annualLeave));
    }

    function updateDirtyUi() {
        var n = dirtyRows.size;
        if (dirtyIndicator) dirtyIndicator.hidden = n === 0;
        if (dirtyCount) dirtyCount.textContent = n;
        if (saveBtn) saveBtn.disabled = saving || n === 0;
        if (discardBtn) discardBtn.disabled = saving || n === 0;
    }

    function markDirty(tr) {
        var id = parseInt(tr.dataset.itemId, 10);
        revisions.set(id, (revisions.get(id) || 0) + 1);
        dirtyRows.add(parseInt(tr.dataset.itemId, 10));
        tr.classList.add('row-dirty');
        updateDirtyUi();
    }

    /* ---------- الحفظ على السيرفر ---------- */
    function antiforgeryToken() {
        var input = document.querySelector('input[name="__RequestVerificationToken"]');
        return input ? input.value : '';
    }

    function collectRows(onlyItemIds) {
        var rows = [];
        table.querySelectorAll('tbody tr.sheet-row').forEach(function (tr) {
            var id = parseInt(tr.dataset.itemId, 10);
            if (onlyItemIds && onlyItemIds.indexOf(id) === -1) return;
            rows.push({
                payrollItemId: id,
                approvedAbsenceDays: val(tr, 'absenceDays'),
                annualLeaveDays: val(tr, 'annualLeaveDays'),
                otherUnpaidLeaveDays: val(tr, 'unpaidLeaveDays'),
                overtimeHours: val(tr, 'overtimeHours'),
                bonuses: val(tr, 'bonuses'),
                allowances: val(tr, 'allowances'),
                advances: val(tr, 'advances'),
                otherDeductions: val(tr, 'otherDeductions'),
                extraDays: val(tr, 'extraDays'),
                extraDaysRate: (field(tr, 'extraDaysRate') && field(tr, 'extraDaysRate').value !== '')
                    ? val(tr, 'extraDaysRate') : null
            });
        });
        return rows;
    }

    function showToast(message, kind) {
        var host = document.getElementById('sheetToast');
        if (!host) {
            host = document.createElement('div');
            host.id = 'sheetToast';
            host.className = 'sheet-toast';
            document.body.appendChild(host);
        }
        host.className = 'sheet-toast ' + (kind || 'ok');
        host.textContent = message;
        host.hidden = false;
        clearTimeout(host._timer);
        host._timer = setTimeout(function () { host.hidden = true; }, 6000);
    }

    function applyResult(tr, r) {
        var v = r.values;
        function setInput(name, value) {
            var el = field(tr, name);
            if (el && value !== null && value !== undefined) el.value = value;
        }
        setInput('absenceDays', v.absenceDays);
        setInput('annualLeaveDays', v.annualLeaveDays);
        setInput('unpaidLeaveDays', v.unpaidLeaveDays);
        setInput('overtimeHours', v.approvedOvertimeHours);
        setInput('bonuses', v.bonuses);
        setInput('allowances', v.allowances);
        setInput('advances', v.advances);
        setInput('otherDeductions', v.otherDeductions);
        setInput('extraDays', v.additionalDaysCount);

        setCell(tr, 'originalAbsenceDays', days(v.originalAbsenceDays));
        setCell(tr, 'annualLeaveBalance', days(v.annualLeaveBalance));
        setCell(tr, 'overtimeAmount', money(v.overtimeAmount));
        setCell(tr, 'additionalDaysAmount', money(v.additionalDaysAmount));
        setCell(tr, 'grossEarnings', money(v.grossEarnings));
        setCell(tr, 'totalDeductions', money(v.totalDeductions));
        setCell(tr, 'netSalary', money(v.netSalary));
        var st = cell(tr, 'reviewStatus');
        if (st) {
            st.textContent = r.reviewStatusText;
            st.className = 'badge ' + (r.needsReview ? 'bad' : 'warn');
        }
        tr.classList.toggle('row-review', !!r.needsReview);
    }

    function save(allowOverdraft) {
        if (!canEdit || saving || dirtyRows.size === 0) return;
        var invalid = Array.from(table.querySelectorAll('input.cell-input')).find(function (input) { return !input.checkValidity(); });
        if (invalid) { invalid.reportValidity(); showToast('أدخل قيماً صحيحة قبل الحفظ.', 'bad'); return; }
        var ids = Array.from(dirtyRows);
        var sentRevisions = new Map(revisions);
        var retryConfirmed = false;
        saving = true;
        updateDirtyUi();
        var controller = new AbortController();
        var timeout = setTimeout(function () { controller.abort(); }, 60000);
        var payload = { runId: runId, allowOverdraft: !!allowOverdraft, rows: collectRows(ids) };

        if (saveBtn) { saveBtn.disabled = true; saveBtn.dataset.text = saveBtn.textContent; saveBtn.textContent = '…جارٍ الحفظ'; }

        fetch(saveUrl, {
            method: 'POST',
            signal: controller.signal,
            headers: {
                'Content-Type': 'application/json',
                'RequestVerificationToken': antiforgeryToken(),
                'X-Requested-With': 'XMLHttpRequest'
            },
            body: JSON.stringify(payload)
        }).then(function (res) {
            return res.json().then(function (data) { return { ok: res.ok, data: data }; });
        }).then(function (out) {
            var data = out.data || {};
            if (!out.ok || !Array.isArray(data.rows)) {
                showToast('تعذّر الحفظ: ' + (data.error || 'استجابة غير صالحة من الخادم. أعد المحاولة.'), 'bad');
                return;
            }

            var needsConfirm = [];
            (data.rows || []).forEach(function (r) {
                var tr = table.querySelector('tr[data-item-id="' + r.payrollItemId + '"]');
                if (!tr) return;
                if (r.saved && revisions.get(r.payrollItemId) === sentRevisions.get(r.payrollItemId)) {
                    applyResult(tr, r);
                    dirtyRows.delete(r.payrollItemId);
                    tr.classList.remove('row-dirty');
                } else if (r.requiresConfirmation) {
                    needsConfirm.push(r);
                } else {
                    showToast(r.employeeName + ': ' + (r.error || 'لم يُحفظ السطر'), 'bad');
                }
                (r.warnings || []).forEach(function (w) { showToast(r.employeeName + ': ' + w, 'warn'); });
            });

            updateDirtyUi();
            refreshTotals();

            if (needsConfirm.length > 0) {
                var msg = needsConfirm.map(function (r) { return r.employeeName + ': ' + r.error; }).join('\n');
                if (window.confirm('تحذير قبل الاعتماد:\n\n' + msg + '\n\nهل تريد المتابعة وتجاوز الرصيد المتاح؟')) {
                    retryConfirmed = true;
                    return;
                }
                showToast('لم يُحفظ تجاوز الرصيد. عدّل الأيام ثم احفظ.', 'warn');
                return;
            }

            if (data.savedCount > 0) {
                showToast('تم حفظ ' + data.savedCount + ' سطر وإعادة حساب الكشف.', 'ok');
            }
        }).catch(function (err) {
            showToast('تعذّر الاتصال بالخادم: ' + err.message, 'bad');
        }).finally(function () {
            clearTimeout(timeout);
            saving = false;
            updateDirtyUi();
            if (saveBtn) {
                saveBtn.disabled = dirtyRows.size === 0;
                saveBtn.textContent = saveBtn.dataset.text || '💾 حفظ التعديلات';
            }
            if (retryConfirmed) save(true);
        });
    }

    /* ---------- الأحداث ---------- */
    table.addEventListener('input', function (e) {
        var input = e.target.closest('input.cell-input');
        if (!input) return;
        var tr = input.closest('tr.sheet-row');
        if (!tr) return;
        computeRow(tr);
        refreshTotals();
        markDirty(tr);

        if (autoSaveToggle && autoSaveToggle.checked) {
            clearTimeout(autoSaveTimer);
            var id = tr.dataset.itemId;
            autoSaveTimer = setTimeout(function () {
                // Save every dirty row; never discard another employee's pending edits.
                save(false);
            }, 900);
        }
    });

    table.addEventListener('keydown', function (e) {
        if (e.key !== 'Enter' || !e.target.closest('input.cell-input')) return;
        e.preventDefault();
        var inputs = Array.from(table.querySelectorAll('tbody input.cell-input:not([disabled])'));
        var i = inputs.indexOf(e.target);
        if (i > -1 && i + 1 < inputs.length) inputs[i + 1].focus();
    });

    if (saveBtn) saveBtn.addEventListener('click', function () { save(false); });

    if (discardBtn) discardBtn.addEventListener('click', function () {
        if (!dirtyRows.size) return;
        if (window.confirm('تراجع عن كل التغييرات غير المحفوظة؟')) window.location.reload();
    });

    window.addEventListener('beforeunload', function (e) {
        if (dirtyRows.size > 0) {
            e.preventDefault();
            e.returnValue = '';
        }
    });

    /* ---------- نافذة تفاصيل الحساب ---------- */
    var modal = document.getElementById('breakdownModal');
    var bdBody = document.getElementById('bdBody');
    var bdTitle = document.getElementById('bdTitle');
    var breakdownRequest = null;

    function escapeHtml(value) { return String(value == null ? '' : value).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
    function row(label, value) {
        return '<div class="bd-row"><span>' + escapeHtml(label) + '</span><strong>' + escapeHtml(value) + '</strong></div>';
    }

    function openBreakdown(id) {
        if (!modal) return;
        if (breakdownRequest) breakdownRequest.abort();
        var request = new AbortController();
        breakdownRequest = request;
        var timeout = setTimeout(function () { request.abort(); }, 30000);
        modal.hidden = false;
        bdBody.innerHTML = '<div class="empty"><p class="loading-label"><span class="spinner"></span>جاري تحميل تفاصيل الراتب...</p></div>';
        fetch(breakdownUrl + '?id=' + encodeURIComponent(id), { signal: request.signal, headers: { 'X-Requested-With': 'XMLHttpRequest' } })
            .then(async function (response) { var data; try { data = await response.json(); } catch (_) { throw new Error('تعذر قراءة التفاصيل. تحقق من تسجيل الدخول.'); } if (!response.ok) throw new Error(data.error || 'تعذر تحميل تفاصيل الراتب.'); return data; })
            .then(function (d) {
                if (breakdownRequest !== request || modal.hidden) return;
                bdTitle.textContent = d.employeeName + ' — ' + (d.period || '');
                var html = '';
                html += '<div class="bd-section">بيانات الموظف</div>';
                html += row('الكود', d.employeeCode || '-');
                html += row('القسم', d.department || '-');
                html += row('حالة الكشف', d.status || '-');
                html += row('آخر تعديل يدوي', d.lastEditedBy ? (d.lastEditedBy + ' — ' + (d.lastEditedAt ? new Date(d.lastEditedAt).toLocaleString('ar-EG') : '')) : 'لا يوجد');
                html += '<div class="bd-section">أساس الحساب</div>';
                html += row('الراتب الأساسي', money(d.basicSalary));
                html += row('المكتسب الأساسي حتى التاريخ', d.earnedBasicSalary == null ? 'غير متاح تاريخياً' : money(d.earnedBasicSalary));
                html += row('المكتسب الصافي حتى التاريخ', d.earnedNetSalary == null ? 'غير متاح تاريخياً' : money(d.earnedNetSalary));
                if (d.attendanceMissingDays > 0) html += row('أيام تحتاج استكمال الحضور', d.attendanceMissingDays);
                html += row('الأجر اليومي', money(d.dailyRate));
                html += row('أجر الساعة (اليوم ÷ 8)', money(d.hourlyRate));
                html += row('معامل الإضافي', days(d.overtimeMultiplier || 1) + ' (بدون مضاعفة)');
                html += '<div class="bd-section">الأيام والإجازات</div>';
                html += row('أيام الحضور', days(d.attendanceDays));
                html += row('أيام راحة مدفوعة (مشمولة بالراتب)', days(d.paidRestDays));
                html += row('إجازة مدفوعة (من الطلبات)', days(d.paidLeaveDays));
                html += row('الغياب من البصمة', days(d.originalAbsenceDays));
                html += row('الغياب المعتمد', days(d.absenceDays));
                html += row('الإجازة السنوية المستخدمة', days(d.annualLeaveDays));
                html += row('رصيد الإجازة السنوية المتبقي', days(d.annualLeaveBalance));
                html += row('إجازة بدون أجر', days(d.unpaidLeaveDays));
                html += row('ساعات التأخير', days(d.lateMinutes / 60));
                html += row('الخروج المبكر (ساعة)', d.actualEarlyDepartureHours == null ? 'غير متاح' : days(d.actualEarlyDepartureHours));
                html += row('العجز (ساعة)', d.actualMissingHours == null ? 'غير متاح' : days(d.actualMissingHours));
                html += '<div class="bd-section">الإضافي والإضافات</div>';
                html += row('إضافي من البصمة (ساعة)', days(d.rawOvertimeHours));
                html += row('إضافي معتمد (ساعة)', days(d.approvedOvertimeHours));
                html += row('قيمة الإضافي', money(d.overtimeAmount));
                html += row('مكافآت', money(d.bonuses));
                html += row('بدلات', money(d.allowances));
                html += row('أيام إضافية / سعر اليوم', days(d.additionalDaysCount) + ' × ' + money(d.additionalDaysRate || d.dailyRate));
                html += row('قيمة الأيام الإضافية', money(d.additionalDaysAmount));
                html += '<div class="bd-section">الخصومات</div>';
                html += row('خصم التأخير', money(d.lateDeduction));
                html += row('خصم الانصراف المبكر', d.earlyLeaveDeduction == null ? 'غير مفصل تاريخياً' : money(d.earlyLeaveDeduction));
                html += row('نقص الوردية الشامل (بديل خصمي الوقت)', d.shortageDeduction == null ? 'غير مفصل تاريخياً' : money(d.shortageDeduction));
                html += row('خصم الغياب', money(d.absenceDeduction));
                html += row('خصم إجازة بدون أجر', money(d.unpaidLeaveDeduction));
                html += row('إجمالي السلف والقروض', money(d.advances));
                if (d.salaryAdvancesPaid !== null && d.salaryAdvancesPaid !== undefined) {
                    html += row('سلف مسجلة ومدفوعة سابقاً', money(d.salaryAdvancesPaid));
                    html += row('أقساط القروض', money(d.loanInstallments));
                    html += row('قروض سابقة', money(d.legacyLoanDeductions));
                    html += row('مصاريف الموظف (ضمن الخصومات الأخرى)', money(d.employeeExpenseRecoveries));
                }
                html += row('خصومات مسجَّلة', money(d.deductionsTotal));
                html += row('خصومات أخرى', money(d.otherDeductions));
                html += '<div class="bd-section">الإجماليات</div>';
                html += row('إجمالي الاستحقاقات', money(d.grossEarnings));
                html += row('إجمالي الخصومات', money(d.totalDeductions));
                html += '<div class="bd-row bd-net"><span>صافي الراتب</span><strong>' + money(d.netSalary) + '</strong></div>';
                if (d.notes) html += '<div class="bd-section">ملاحظات</div><div class="hint">' + escapeHtml(d.notes) + '</div>';
                bdBody.innerHTML = html;
            })
            .catch(function (error) {
                if (breakdownRequest !== request || modal.hidden) return;
                var message = error.name === 'AbortError' ? 'انتهت مهلة تحميل التفاصيل. أغلق النافذة وأعد المحاولة.' : error.name === 'TypeError' ? 'تعذر الاتصال بالخادم. أعد المحاولة.' : error.message;
                bdBody.innerHTML = '<div class="empty"><p class="request-error">' + escapeHtml(message) + '</p></div>';
            }).finally(function () {
                clearTimeout(timeout);
                if (breakdownRequest === request) breakdownRequest = null;
            });
    }

    table.addEventListener('click', function (e) {
        var btn = e.target.closest('[data-breakdown]');
        if (btn) openBreakdown(btn.dataset.breakdown);
    });
    if (modal) {
        modal.addEventListener('click', function (e) {
            if (e.target === modal || e.target.id === 'bdClose') { modal.hidden = true; if (breakdownRequest) breakdownRequest.abort(); }
        });
        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') { modal.hidden = true; if (breakdownRequest) breakdownRequest.abort(); }
        });
    }

    // معاينة أولية + الإجماليات عند فتح الصفحة
    refreshTotals();
})();
