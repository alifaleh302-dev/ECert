// Role permission matrix: feature search, per-group tri-state selection and a live count of the
// sections a role will open. The checkboxes are the form payload (name="PermissionIds"), so this
// file is pure presentation on top of plain inputs — removing it degrades gracefully.
(function () {
    function initEditor(root) {
        var boxes = Array.prototype.slice.call(root.querySelectorAll('[data-perm-check]'));
        var groups = Array.prototype.slice.call(root.querySelectorAll('[data-perm-group]'));
        var search = root.querySelector('[data-perm-search]');
        var empty = root.querySelector('[data-perm-empty]');

        var totalEl = root.querySelector('[data-summary-total]');
        var sectionList = root.querySelector('[data-summary-sections]');
        var sectionEmpty = root.querySelector('[data-summary-sections-empty]');

        function setTriState(box, checkedCount, total) {
            if (!box) return;
            box.checked = total > 0 && checkedCount === total;
            box.indeterminate = checkedCount > 0 && checkedCount < total;
        }

        function updateGroups() {
            groups.forEach(function (group) {
                var groupBoxes = Array.prototype.slice.call(group.querySelectorAll('[data-perm-check]'));
                var checked = groupBoxes.filter(function (b) { return b.checked; }).length;
                setTriState(group.querySelector('[data-perm-group-toggle]'), checked, groupBoxes.length);
            });
        }

        function updateSummary() {
            var selected = boxes.filter(function (b) { return b.checked; });
            if (totalEl) totalEl.textContent = selected.length;

            if (!sectionList) return;
            var seen = {};
            var chips = [];
            selected.forEach(function (box) {
                (box.getAttribute('data-perm-sections') || '').split('|').forEach(function (label) {
                    label = label.trim();
                    if (label && !seen[label]) { seen[label] = true; chips.push(label); }
                });
            });
            sectionList.innerHTML = '';
            chips.forEach(function (label) {
                var chip = document.createElement('span');
                chip.className = 'rp-section-chip';
                chip.textContent = label;
                sectionList.appendChild(chip);
            });
            if (sectionEmpty) sectionEmpty.hidden = chips.length > 0;
        }

        function refresh() {
            updateGroups();
            updateSummary();
        }

        function applyFilter() {
            var term = (search && search.value ? search.value : '').trim().toLowerCase();
            var visibleGroups = 0;
            groups.forEach(function (group) {
                var groupVisible = 0;
                Array.prototype.slice.call(group.querySelectorAll('[data-perm-item]')).forEach(function (item) {
                    var haystack = (item.getAttribute('data-perm-search') || '').toLowerCase();
                    var show = !term || haystack.indexOf(term) !== -1;
                    item.hidden = !show;
                    if (show) groupVisible++;
                });
                group.hidden = groupVisible === 0;
                if (groupVisible > 0) visibleGroups++;
            });
            if (empty) empty.hidden = visibleGroups > 0;
        }

        boxes.forEach(function (box) {
            box.addEventListener('change', refresh);
        });

        groups.forEach(function (group) {
            var toggle = group.querySelector('[data-perm-group-toggle]');
            if (!toggle) return;
            toggle.addEventListener('change', function () {
                Array.prototype.slice.call(group.querySelectorAll('[data-perm-check]'))
                    .forEach(function (b) { b.checked = toggle.checked; });
                refresh();
            });
        });

        var selectAll = root.querySelector('[data-perm-select-all]');
        var clearAll = root.querySelector('[data-perm-clear-all]');
        if (selectAll) selectAll.addEventListener('click', function () {
            boxes.forEach(function (b) {
                if (!b.closest('[data-perm-item]').hidden) b.checked = true;
            });
            refresh();
        });
        if (clearAll) clearAll.addEventListener('click', function () {
            boxes.forEach(function (b) { b.checked = false; });
            refresh();
        });

        if (search) search.addEventListener('input', applyFilter);

        refresh();
    }

    document.addEventListener('DOMContentLoaded', function () {
        Array.prototype.slice.call(document.querySelectorAll('[data-perm-editor]')).forEach(initEditor);
    });
})();
