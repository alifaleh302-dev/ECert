// Course-scope picker: search, category filter, per-panel select-all/deselect-all toggles and a
// live summary. The category and course checkboxes post independently (CategoryIds / CourseIds).
//
// Two deliberate rules:
//  * Ticking every course of a category never ticks the category by itself — a category grant
//    also covers courses added to it later, and the user may only want the current ones.
//  * Ticking a category does tick its courses, so the visible selection matches the grant.
(function () {
    function initPicker(root) {
        var courseItems = Array.prototype.slice.call(root.querySelectorAll('[data-scope-course-item]'));
        var courseBoxes = Array.prototype.slice.call(root.querySelectorAll('[data-scope-course-checkbox]'));
        var categoryBoxes = Array.prototype.slice.call(root.querySelectorAll('[data-scope-cat-checkbox]'));
        var search = root.querySelector('[data-scope-search]');
        var filter = root.querySelector('[data-scope-filter]');
        var emptyNote = root.querySelector('[data-scope-empty]');
        var countBadge = root.querySelector('[data-scope-count]');
        var modeBadge = root.querySelector('[data-scope-mode]');
        var catToggle = root.querySelector('[data-scope-cat-toggle]');
        var courseToggle = root.querySelector('[data-scope-course-toggle]');

        function coursesOfCategory(catId) {
            return courseItems.filter(function (item) { return item.getAttribute('data-cat-id') === String(catId); });
        }

        function courseBoxOf(item) { return item.querySelector('[data-scope-course-checkbox]'); }

        function visibleCourseItems() {
            return courseItems.filter(function (item) { return !item.hidden; });
        }

        function setGroupToggle(toggle, boxes) {
            if (!toggle) return;
            var checked = boxes.filter(function (b) { return b.checked; }).length;
            toggle.checked = checked > 0 && checked === boxes.length;
            toggle.indeterminate = checked > 0 && checked < boxes.length;
        }

        function applyFilter() {
            var term = (search && search.value ? search.value : '').trim().toLowerCase();
            var cat = filter && filter.value ? filter.value : '';
            var visible = 0;
            courseItems.forEach(function (item) {
                var matchesTerm = !term || (item.getAttribute('data-search') || '').indexOf(term) !== -1;
                var itemCat = item.getAttribute('data-cat-id');
                var matchesCat = !cat || (cat === 'none' ? !itemCat : itemCat === cat);
                var show = matchesTerm && matchesCat;
                item.hidden = !show;
                if (show) visible++;
            });
            if (emptyNote) emptyNote.hidden = visible !== 0 || courseItems.length === 0;
            setGroupToggle(courseToggle, visibleCourseItems().map(courseBoxOf));
        }

        function updateSummary() {
            var granted = new Set();
            courseBoxes.forEach(function (box) { if (box.checked) granted.add(box.value); });
            categoryBoxes.forEach(function (box) {
                if (!box.checked) return;
                coursesOfCategory(box.value).forEach(function (item) {
                    var courseBox = courseBoxOf(item);
                    if (courseBox) granted.add(courseBox.value);
                });
            });

            var total = courseBoxes.length;
            var grantedCount = granted.size;
            if (countBadge) countBadge.textContent = grantedCount + ' دورة محددة';
            if (modeBadge) {
                if (grantedCount === 0) {
                    modeBadge.textContent = 'كل الدورات';
                    modeBadge.className = 'a-badge a-badge--neutral';
                } else if (grantedCount === total) {
                    modeBadge.textContent = 'كل الدورات (محدد صراحة)';
                    modeBadge.className = 'a-badge a-badge--success';
                } else {
                    modeBadge.textContent = 'محدد';
                    modeBadge.className = 'a-badge a-badge--brand';
                }
            }

            setGroupToggle(catToggle, categoryBoxes);
            setGroupToggle(courseToggle, visibleCourseItems().map(courseBoxOf));
        }

        categoryBoxes.forEach(function (box) {
            box.addEventListener('change', function () {
                coursesOfCategory(box.value).forEach(function (item) {
                    var courseBox = courseBoxOf(item);
                    if (courseBox) courseBox.checked = box.checked;
                });
                updateSummary();
            });
        });

        // Ticking a course never ticks its category: the user may want only the current courses.
        courseBoxes.forEach(function (box) {
            box.addEventListener('change', updateSummary);
        });

        if (catToggle) catToggle.addEventListener('change', function () {
            categoryBoxes.forEach(function (box) { box.checked = catToggle.checked; });
            if (catToggle.checked) {
                courseBoxes.forEach(function (box) { box.checked = true; });
            } else {
                categoryBoxes.forEach(function (box) {
                    coursesOfCategory(box.value).forEach(function (item) {
                        var courseBox = courseBoxOf(item);
                        if (courseBox) courseBox.checked = false;
                    });
                });
            }
            updateSummary();
        });

        if (courseToggle) courseToggle.addEventListener('change', function () {
            visibleCourseItems().forEach(function (item) {
                var courseBox = courseBoxOf(item);
                if (courseBox) courseBox.checked = courseToggle.checked;
            });
            updateSummary();
        });

        if (search) search.addEventListener('input', applyFilter);
        if (filter) filter.addEventListener('change', applyFilter);

        applyFilter();
        updateSummary();
    }

    document.querySelectorAll('[data-scope-picker]').forEach(initPicker);
})();
