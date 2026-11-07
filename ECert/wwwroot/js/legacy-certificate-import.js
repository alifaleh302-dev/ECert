// Inline editor for the legacy certificate import preview. Rows can be corrected and each course
// group can be configured (category, instructors, create/link) without reloading the page.
(function () {
    'use strict';

    const configEl = document.getElementById('legacyImportConfig');
    if (!configEl) return;

    const config = JSON.parse(configEl.textContent);
    const token = config.token;

    const csrf = document.querySelector('input[name="__RequestVerificationToken"]')?.value
        || document.querySelector('meta[name="csrf-token"]')?.content
        || '';

    function refreshTotals(state) {
        const countEl = document.getElementById('confirmCount');
        const btn = document.getElementById('confirmImportBtn');
        if (countEl && typeof state.validRows === 'number') countEl.textContent = state.validRows;
        if (btn && typeof state.canCommit === 'boolean') btn.disabled = !state.canCommit;
        if (typeof state.newCourseCount === 'number') {
            const el = document.getElementById('newCourseCount');
            if (el) el.textContent = state.newCourseCount;
        }
        if (typeof state.linkedCourseCount === 'number') {
            const el = document.getElementById('linkedCourseCount');
            if (el) el.textContent = state.linkedCourseCount;
        }
    }

    async function postForm(url, params) {
        params.append('token', token);
        params.append('__RequestVerificationToken', csrf);
        const response = await fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
            body: params.toString()
        });
        if (!response.ok) return null;
        return response.json();
    }

    // ---- Certificate rows -------------------------------------------------

    const table = document.getElementById('legacyPreviewTable');
    if (table) {
        function badgeFor(state) {
            if (state.valid) return '<span class="a-badge a-badge--success">سليم</span>';
            if (state.duplicate) return '<span class="a-badge a-badge--warning" title="' + (state.errors || []).join('، ') + '">مكرر (يُتجاهل)</span>';
            return '<span class="a-error">' + (state.errors || []).join('، ') + '</span>';
        }

        function applyRowState(row, state) {
            row.classList.remove('table-danger', 'table-warning');
            if (!state.valid) row.classList.add(state.duplicate ? 'table-warning' : 'table-danger');
            const statusCell = row.querySelector('.js-status');
            if (statusCell) statusCell.innerHTML = badgeFor(state);
        }

        async function saveRow(row) {
            const fields = {};
            row.querySelectorAll('.js-field').forEach(input => { fields[input.dataset.field] = input.value; });

            const params = new URLSearchParams({
                rowNumber: row.dataset.row,
                fullNameArabic: fields.fullNameArabic || '',
                fullNameEnglish: fields.fullNameEnglish || '',
                certificateNumber: fields.certificateNumber || '',
                courseKey: fields.courseKey || '',
                issueDate: fields.issueDate || ''
            });

            const state = await postForm(config.editRowUrl, params);
            if (!state) return;
            applyRowState(row, state);
            refreshTotals(state);
        }

        table.addEventListener('click', event => {
            const button = event.target.closest('.js-save-row');
            if (!button) return;
            const row = button.closest('tr');
            if (row) saveRow(row);
        });

        table.addEventListener('keydown', event => {
            if (event.key !== 'Enter' || !event.target.classList.contains('js-field')) return;
            event.preventDefault();
            const row = event.target.closest('tr');
            if (row) saveRow(row);
        });
    }

    // ---- Course groups ----------------------------------------------------

    const courseTable = document.getElementById('courseTable');
    if (courseTable) {
        function selectedValues(select) {
            return Array.from(select.selectedOptions).map(option => option.value).filter(value => value !== '');
        }

        function toggleLinkBox(row, isLink) {
            const box = row.querySelector('.js-link-box');
            if (box) box.hidden = !isLink;
        }

        async function saveCourse(row) {
            const params = new URLSearchParams();
            params.append('courseKey', row.dataset.courseKey);
            params.append('categoryId', row.querySelector('[data-field="categoryId"]').value || '');
            params.append('nameEnglish', row.querySelector('[data-field="nameEnglish"]').value || '');

            const action = row.querySelector('.js-course-action:checked')?.value || 'create';
            params.append('action', action);

            const linkedSelect = row.querySelector('[data-field="linkedCourseId"]');
            if (action === 'link' && linkedSelect) params.append('linkedCourseId', linkedSelect.value || '');

            selectedValues(row.querySelector('[data-field="instructorIds"]'))
                .forEach(id => params.append('instructorIds', id));

            const state = await postForm(config.editCourseUrl, params);
            if (!state) return;

            row.classList.remove('table-warning');
            if (!state.isConfigured) row.classList.add('table-warning');

            const statusCell = row.querySelector('.js-course-status');
            if (statusCell) {
                statusCell.innerHTML = state.isConfigured
                    ? '<span class="a-badge a-badge--success">جاهزة</span>'
                    : '<span class="a-badge a-badge--warning">تحتاج ضبطاً</span>';
            }

            refreshTotals(state);
        }

        courseTable.addEventListener('click', event => {
            const button = event.target.closest('.js-save-course');
            if (!button) return;
            const row = button.closest('tr');
            if (row) saveCourse(row);
        });

        courseTable.addEventListener('change', event => {
            const row = event.target.closest('tr');
            if (!row) return;
            if (event.target.classList.contains('js-course-action')) {
                toggleLinkBox(row, event.target.value === 'link');
            }
        });

        // Live search inside a group's "link to existing" picker.
        let searchTimer = null;
        courseTable.addEventListener('input', event => {
            if (!event.target.classList.contains('js-link-search')) return;
            const row = event.target.closest('tr');
            const term = event.target.value;
            clearTimeout(searchTimer);
            searchTimer = setTimeout(async () => {
                const response = await fetch(config.searchCoursesUrl + '?term=' + encodeURIComponent(term));
                if (!response.ok) return;
                const courses = await response.json();
                const select = row.querySelector('[data-field="linkedCourseId"]');
                if (!select) return;
                select.replaceChildren();
                const placeholder = document.createElement('option');
                placeholder.value = '';
                placeholder.textContent = courses.length ? '-- اختر الدورة --' : '-- لا نتائج --';
                select.appendChild(placeholder);
                courses.forEach(course => {
                    const option = document.createElement('option');
                    option.value = course.id;
                    option.textContent = course.name;
                    select.appendChild(option);
                });
            }, 250);
        });
    }

    // ---- Apply defaults to every course -----------------------------------

    const applyBtn = document.getElementById('applyDefaultsBtn');
    if (applyBtn) {
        applyBtn.addEventListener('click', async () => {
            const params = new URLSearchParams();
            const category = document.getElementById('applyCategory').value;
            if (category) params.append('categoryId', category);

            const instructorSelect = document.getElementById('applyInstructors');
            Array.from(instructorSelect.selectedOptions)
                .map(option => option.value)
                .filter(value => value !== '')
                .forEach(id => params.append('instructorIds', id));

            const state = await postForm(config.applyDefaultsUrl, params);
            if (!state) return;
            refreshTotals(state);

            if (category) {
                courseTable.querySelectorAll('[data-field="categoryId"]').forEach(select => { select.value = category; });
            }
            const chosenInstructors = Array.from(instructorSelect.selectedOptions).map(option => option.value);
            if (chosenInstructors.length) {
                courseTable.querySelectorAll('[data-field="instructorIds"]').forEach(select => {
                    Array.from(select.options).forEach(option => {
                        option.selected = chosenInstructors.indexOf(option.value) !== -1;
                    });
                });
            }
        });
    }
})();
