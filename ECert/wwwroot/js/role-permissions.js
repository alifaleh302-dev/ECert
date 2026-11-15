// Role permission editor: search, per-group and global selection, preset bundles and a live
// summary of what the role will be able to do. The checkboxes themselves are the form payload
// (name="PermissionIds"), so everything here is pure presentation on top of plain inputs.
(function () {
    function initEditor(root) {
        var items = Array.prototype.slice.call(root.querySelectorAll('[data-perm-item]'));
        var boxes = items.map(function (item) { return item.querySelector('[data-perm-check]'); });
        var groups = Array.prototype.slice.call(root.querySelectorAll('[data-perm-group]'));
        var search = root.querySelector('[data-perm-search]');
        var empty = root.querySelector('[data-perm-empty]');
        var presetButtons = Array.prototype.slice.call(root.querySelectorAll('[data-perm-preset]'));

        var totalEl = root.querySelector('[data-summary-total]');
        var sectionList = root.querySelector('[data-summary-sections]');
        var sectionEmpty = root.querySelector('[data-summary-sections-empty]');
        var levels = {
            view: { count: root.querySelector('[data-level-count="view"]'), fill: root.querySelector('[data-level-fill="view"]') },
            manage: { count: root.querySelector('[data-level-count="manage"]'), fill: root.querySelector('[data-level-fill="manage"]') },
            approve: { count: root.querySelector('[data-level-count="approve"]'), fill: root.querySelector('[data-level-fill="approve"]') }
        };

        function levelOf(box) { return box.getAttribute('data-perm-level') || 'manage'; }
        function nameOf(box) { return box.value; }

        function setTriState(box, checkedCount, total) {
            if (!box) return;
            box.checked = total > 0 && checkedCount === total;
            box.indeterminate = checkedCount > 0 && checkedCount < total;
        }

        function updateGroupHeaders() {
            groups.forEach(function (group) {
                var groupBoxes = Array.prototype.slice.call(group.querySelectorAll('[data-perm-check]'));
                var checked = groupBoxes.filter(function (b) { return b.checked; }).length;
                setTriState(group.querySelector('[data-perm-group-toggle]'), checked, groupBoxes.length);
                var count = group.querySelector('[data-perm-group-count]');
                if (count) count.textContent = checked + '/' + groupBoxes.length;
            });
        }

        function updateSummary() {
            var selected = boxes.filter(function (b) { return b.checked; });
            if (totalEl) totalEl.textContent = selected.length;

            var perLevel = { view: 0, manage: 0, approve: 0 };
            var totals = { view: 0, manage: 0, approve: 0 };
            boxes.forEach(function (b) {
                var lvl = levelOf(b);
                totals[lvl] = (totals[lvl] || 0) + 1;
                if (b.checked) perLevel[lvl] = (perLevel[lvl] || 0) + 1;
            });
            Object.keys(levels).forEach(function (key) {
                var target = levels[key];
                if (!target) return;
                if (target.count) target.count.textContent = perLevel[key] + '/' + (totals[key] || 0);
                if (target.fill) {
                    var pct = totals[key] ? Math.round((perLevel[key] / totals[key]) * 100) : 0;
                    target.fill.style.width = pct + '%';
                }
            });

            if (sectionList) {
                var seen = {};
                var chips = [];
                selected.forEach(function (b) {
                    var raw = b.getAttribute('data-perm-sections') || '';
                    raw.split('|').forEach(function (label) {
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
        }

        function refresh() {
            updateGroupHeaders();
            updateSummary();
            presetButtons.forEach(function (btn) {
                var key = btn.getAttribute('data-perm-preset');
                var preset = window.__rpPresets && window.__rpPresets[key];
                if (!preset) return;
                var wanted = preset.reduce(function (acc, id) { acc[String(id)] = true; return acc; }, {});
                var match = boxes.every(function (b) { return !!wanted[nameOf(b)] === b.checked; });
                btn.setAttribute('aria-pressed', match ? 'true' : 'false');
            });
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
                var groupBoxes = Array.prototype.slice.call(group.querySelectorAll('[data-perm-check]'));
                groupBoxes.forEach(function (b) { b.checked = toggle.checked; });
                refresh();
            });
        });

        var selectAll = root.querySelector('[data-perm-select-all]');
        var clearAll = root.querySelector('[data-perm-clear-all]');
        if (selectAll) selectAll.addEventListener('click', function () {
            boxes.forEach(function (b) { if (!b.closest('[data-perm-item]').hidden) b.checked = true; });
            refresh();
        });
        if (clearAll) clearAll.addEventListener('click', function () {
            boxes.forEach(function (b) { b.checked = false; });
            refresh();
        });

        presetButtons.forEach(function (btn) {
            btn.addEventListener('click', function () {
                var preset = (window.__rpPresets || {})[btn.getAttribute('data-perm-preset')];
                if (!preset) return;
                var wanted = preset.reduce(function (acc, id) { acc[String(id)] = true; return acc; }, {});
                boxes.forEach(function (b) { b.checked = !!wanted[nameOf(b)]; });
                refresh();
            });
        });

        if (search) search.addEventListener('input', applyFilter);

        refresh();
    }

    document.addEventListener('DOMContentLoaded', function () {
        Array.prototype.slice.call(document.querySelectorAll('[data-perm-editor]'))
            .forEach(initEditor);
    });
})();
