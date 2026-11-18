(function () {
    'use strict';

    var tabs = Array.from(document.querySelectorAll('[data-import-tab]'));
    function activate(tab) {
        tabs.forEach(function (item) {
            var selected = item === tab;
            item.classList.toggle('active', selected);
            item.setAttribute('aria-selected', selected ? 'true' : 'false');
            item.tabIndex = selected ? 0 : -1;
        });
        document.querySelectorAll('[data-import-pane]').forEach(function (pane) {
            pane.hidden = pane.dataset.importPane !== tab.dataset.importTab;
        });
    }
    tabs.forEach(function (tab, index) {
        tab.addEventListener('click', function () { activate(tab); });
        tab.addEventListener('keydown', function (event) {
            var next;
            if (event.key === 'ArrowLeft') next = (index + 1) % tabs.length;
            if (event.key === 'ArrowRight') next = (index + tabs.length - 1) % tabs.length;
            if (event.key === 'Home') next = 0;
            if (event.key === 'End') next = tabs.length - 1;
            if (next === undefined) return;
            event.preventDefault(); activate(tabs[next]); tabs[next].focus();
        });
    });

    document.querySelectorAll('form[data-attendance-upload]').forEach(function (form) {
        var error = form.querySelector('[data-upload-error]');
        var submit = form.querySelector('button[type="submit"]');
        var submitText = submit ? submit.textContent : '';
        var maxSize = 25 * 1024 * 1024;
        // This form validates before starting its own loading state.
        if (submit) submit.setAttribute('data-no-spinner', '');
        function showError(message) { error.textContent = message; error.hidden = false; }
        function refresh(zone) {
            var input = zone.querySelector('[data-upload-input]');
            var file = input.files && input.files[0];
            var selected = zone.querySelector('[data-upload-selected]');
            var feedback = zone.querySelector('[data-upload-feedback]');
            zone.classList.toggle('has-file', !!file);
            zone.classList.remove('has-error');
            selected.hidden = !file;
            zone.querySelector('[data-clear-file]').hidden = !file;
            input.setCustomValidity('');
            if (!file) { feedback.textContent = 'لم يتم اختيار ملف بعد'; return; }
            zone.querySelector('[data-upload-name]').textContent = file.name;
            zone.querySelector('[data-upload-size]').textContent = file.size >= 1024 * 1024 ? (file.size / (1024 * 1024)).toFixed(2) + ' MB' : (file.size / 1024).toFixed(1) + ' KB';
            var invalid = file.size === 0 ? 'الملف فارغ. اختر ملفًا يحتوي على سجلات البصمة.' : file.size > maxSize ? 'حجم الملف يتجاوز الحد المسموح: 25 ميجابايت.' : '';
            input.setCustomValidity(invalid);
            zone.classList.toggle('has-error', !!invalid);
            feedback.textContent = invalid || 'جاهز للتحليل — سيتم التحقق من المحتوى والترميز في الخادم';
        }
        form.querySelectorAll('[data-upload-zone]').forEach(function (zone) {
            var input = zone.querySelector('[data-upload-input]');
            input.addEventListener('change', function () { error.hidden = true; refresh(zone); });
            zone.querySelector('[data-clear-file]').addEventListener('click', function () {
                input.value = ''; error.hidden = true; refresh(zone); input.focus();
            });
            ['dragenter', 'dragover'].forEach(function (name) {
                zone.addEventListener(name, function (event) { event.preventDefault(); zone.classList.add('is-dragging'); });
            });
            zone.addEventListener('dragleave', function (event) {
                if (!zone.contains(event.relatedTarget)) zone.classList.remove('is-dragging');
            });
            zone.addEventListener('drop', function (event) {
                event.preventDefault(); zone.classList.remove('is-dragging');
                if (!event.dataTransfer || !event.dataTransfer.files.length) return;
                if (event.dataTransfer.files.length !== 1) { showError('اسحب ملفًا واحدًا فقط إلى كل منطقة رفع.'); return; }
                try { input.files = event.dataTransfer.files; error.hidden = true; refresh(zone); }
                catch (_) { showError('تعذّر إرفاق الملف بالسحب. استخدم زر اختيار ملف.'); }
            });
            refresh(zone);
        });
        form.addEventListener('submit', function (event) {
            var inputs = Array.from(form.querySelectorAll('[data-upload-input]'));
            if (!inputs.some(function (input) { return input.files && input.files.length; })) {
                event.preventDefault(); showError('اختر ملف بصمة واحدًا على الأقل قبل التحليل.'); return;
            }
            if (!form.checkValidity()) { event.preventDefault(); form.reportValidity(); return; }
            if (form.dataset.submitting === '1') { event.preventDefault(); return; }
            form.dataset.submitting = '1'; form.setAttribute('aria-busy', 'true');
            error.hidden = true; form.querySelector('[data-upload-progress]').hidden = false;
            if (submit) { submit.disabled = true; submit.textContent = 'جارٍ التحليل…'; }
        });
        window.addEventListener('pageshow', function () {
            delete form.dataset.submitting; form.removeAttribute('aria-busy');
            form.querySelector('[data-upload-progress]').hidden = true;
            if (submit) { submit.disabled = false; submit.textContent = submitText; }
        });
    });

    document.querySelectorAll('[data-preview-filter]').forEach(function (container) {
        var search = container.querySelector('[data-filter-search]');
        var employee = container.querySelector('[data-filter-employee]');
        var date = container.querySelector('[data-filter-date]');
        var status = container.querySelector('[data-filter-status]');
        var rows = Array.from(container.querySelectorAll('[data-preview-row]'));
        var indexed = rows.map(function (row) { return { row: row, text: row.textContent.toLocaleLowerCase('ar') }; });
        function filter() {
            var query = search.value.trim().toLocaleLowerCase('ar'); var count = 0;
            indexed.forEach(function (item) {
                var data = item.row.dataset;
                var visible = (!query || item.text.includes(query)) && (!employee.value || data.employee === employee.value) && (!date.value || data.date === date.value) && (!status.value || data.status === status.value);
                item.row.hidden = !visible; if (visible) count++;
            });
            container.querySelector('[data-visible-count]').textContent = count + ' من ' + rows.length + ' سجل ظاهر';
            container.querySelector('[data-preview-empty]').hidden = count !== 0;
        }
        [search, employee, date, status].forEach(function (input) { input.addEventListener('input', filter); input.addEventListener('change', filter); });
        container.querySelector('[data-reset-filters]').addEventListener('click', function () {
            [search, employee, date, status].forEach(function (input) { input.value = ''; }); filter(); search.focus();
        });
        filter();
    });
}());
