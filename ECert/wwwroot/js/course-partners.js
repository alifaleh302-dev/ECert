// Repeater for a course's supporting organisations (name + logo + signature).
// Row indices bind the uploads, so renaming or reordering a row never attaches its files to a
// different organisation. Rows are seeded from #partnerRowsData as JSON.
(function () {
    'use strict';

    var rowsBox = document.getElementById('partnerRows');
    var seedEl = document.getElementById('partnerRowsData');
    var addBtn = document.getElementById('addPartnerRow');
    if (!rowsBox || !addBtn) return;

    var seed = [];
    try { seed = JSON.parse(seedEl ? seedEl.textContent : '[]') || []; } catch (e) { seed = []; }

    function artworkPreview(url, label) {
        if (!url) return '';
        return '<div class="mt-1"><span class="small text-muted d-block">' + label + ' المحفوظ حالياً:</span>' +
            '<img src="' + url + '" alt="" style="max-height:70px;background:#fff;border:1px solid var(--bs-border-color,#cbd5e1);border-radius:8px;padding:3px;" /></div>';
    }

    function buildRow(data) {
        data = data || {};
        var row = document.createElement('div');
        row.className = 'partner-row border rounded-3 p-3';
        row.innerHTML =
            '<div class="row g-2 align-items-end">' +
            '  <div class="col-md-5">' +
            '    <label class="a-label">اسم الجهة الداعمة</label>' +
            '    <input type="text" class="form-control partner-name" name="PartnerRows[0].Name" maxlength="150" placeholder="مثال: الغرفة التجارية" />' +
            '  </div>' +
            '  <div class="col-md-3">' +
            '    <label class="a-label">الشعار</label>' +
            '    <input type="file" class="form-control partner-logo" name="PartnerRows[0].Logo" accept="image/png,image/jpeg,image/webp" />' +
            '  </div>' +
            '  <div class="col-md-3">' +
            '    <label class="a-label">التوقيع</label>' +
            '    <input type="file" class="form-control partner-signature" name="PartnerRows[0].Signature" accept="image/png,image/jpeg,image/webp" />' +
            '  </div>' +
            '  <div class="col-md-1 text-end">' +
            '    <button type="button" class="btn btn-outline-danger btn-sm remove-partner-row" aria-label="حذف الجهة"><i class="bi bi-trash3"></i></button>' +
            '  </div>' +
            '</div>' +
            '<div class="partner-artwork"></div>';

        row.querySelector('.partner-name').value = data.name || '';
        var art = row.querySelector('.partner-artwork');
        art.innerHTML = artworkPreview(data.logoUrl, 'الشعار') + artworkPreview(data.signatureUrl, 'التوقيع');
        if (data.signatureUrl) {
            var removeWrap = document.createElement('div');
            removeWrap.className = 'form-check mt-2';
            removeWrap.innerHTML =
                '<input type="checkbox" class="form-check-input partner-remove-signature" name="PartnerRows[0].RemoveSignature" value="true" />' +
                '<label class="form-check-label small">إزالة التوقيع المحفوظ</label>';
            art.appendChild(removeWrap);
        }

        row.querySelector('.remove-partner-row').addEventListener('click', function () {
            row.remove();
            reindex();
        });
        return row;
    }

    // Re-number every row so the model binder lines each name/upload up with its own row.
    function reindex() {
        var rows = rowsBox.querySelectorAll('.partner-row');
        rows.forEach(function (row, i) {
            row.querySelector('.partner-name').name = 'PartnerRows[' + i + '].Name';
            row.querySelector('.partner-logo').name = 'PartnerRows[' + i + '].Logo';
            row.querySelector('.partner-signature').name = 'PartnerRows[' + i + '].Signature';
            var remove = row.querySelector('.partner-remove-signature');
            if (remove) remove.name = 'PartnerRows[' + i + '].RemoveSignature';
        });
    }

    addBtn.addEventListener('click', function () {
        rowsBox.appendChild(buildRow({}));
        reindex();
    });

    if (seed.length) {
        seed.forEach(function (data) { rowsBox.appendChild(buildRow(data)); });
    } else {
        rowsBox.appendChild(buildRow({}));
    }
    reindex();
})();
