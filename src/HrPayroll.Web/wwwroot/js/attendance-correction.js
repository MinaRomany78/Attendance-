(function () {
    'use strict';
    var form = document.querySelector('[data-correction-preview]');
    if (!form) return;
    var checkIn = form.querySelector('input[name="CheckIn"]');
    var checkOut = form.querySelector('input[name="CheckOut"]');
    var shiftStart = new Date(form.dataset.shiftStart).getTime();
    var shiftEnd = new Date(form.dataset.shiftEnd).getTime();
    function show(name, value) { form.querySelector('[data-preview-' + name + ']').textContent = value === null ? 'غير محسوم' : String(value); }
    function refresh() {
        var start = checkIn.value ? new Date(checkIn.value).getTime() : NaN;
        var end = checkOut.value ? new Date(checkOut.value).getTime() : NaN;
        function minutes(value) { return Math.max(0, Math.floor(value / 60000)); }
        show('late', Number.isFinite(start) ? minutes(start - shiftStart) : null);
        show('early', Number.isFinite(end) ? minutes(shiftEnd - end) : null);
        var valid = Number.isFinite(start) && Number.isFinite(end) && end >= start;
        show('worked', valid ? minutes(end - start) : null);
        var covered = valid ? Math.max(0, Math.min(end, shiftEnd) - Math.max(start, shiftStart)) : 0;
        show('missing', valid ? minutes(shiftEnd - shiftStart - covered) : null);
    }
    checkIn.addEventListener('input', refresh);
    checkOut.addEventListener('input', refresh);
    refresh();
})();
