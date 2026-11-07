// Progressive enhancement: turns a native <select multiple> into a searchable
// checkbox dropdown. The native element stays in the DOM (visually hidden) so
// forms, validation and existing code that reads .selectedOptions keep working.
// Mark a select with data-multiselect and (optionally) data-placeholder.
(function () {
    'use strict';

    const instances = new Map();

    function build(select) {
        if (instances.has(select)) return instances.get(select);

        const placeholder = select.dataset.placeholder || 'اختر...';
        const wrapper = document.createElement('div');
        wrapper.className = 'a-ms';

        const control = document.createElement('button');
        control.type = 'button';
        control.className = 'a-ms__control';
        control.setAttribute('aria-haspopup', 'listbox');
        control.setAttribute('aria-expanded', 'false');
        control.innerHTML = '<span class="a-ms__value"></span><i class="bi bi-chevron-down a-ms__caret"></i>';

        const panel = document.createElement('div');
        panel.className = 'a-ms__panel';
        panel.innerHTML =
            '<div class="a-ms__search"><i class="bi bi-search"></i>' +
            '<input type="text" autocomplete="off" placeholder="بحث..." aria-label="بحث" /></div>' +
            '<div class="a-ms__toolbar">' +
            '<button type="button" data-act="all">تحديد الكل</button>' +
            '<span class="a-ms__count-label" data-count></span>' +
            '<button type="button" data-act="none">مسح</button>' +
            '</div>' +
            '<div class="a-ms__list" role="listbox" aria-multiselectable="true"></div>';

        // Keep the native select in the layout tree (hidden) so form posts still see it.
        select.classList.add('a-ms__native');
        select.setAttribute('aria-hidden', 'true');
        select.tabIndex = -1;
        select.parentNode.insertBefore(wrapper, select);
        wrapper.appendChild(select);
        wrapper.appendChild(control);
        wrapper.appendChild(panel);

        const state = { select, wrapper, control, panel, search: panel.querySelector('input'), list: panel.querySelector('.a-ms__list') };
        instances.set(select, state);

        control.addEventListener('click', e => {
            e.preventDefault();
            toggle(state, !wrapper.classList.contains('is-open'));
        });

        state.search.addEventListener('input', () => filter(state, state.search.value));

        panel.addEventListener('click', e => {
            const act = e.target.closest('[data-act]');
            if (act) {
                e.preventDefault();
                if (act.dataset.act === 'all') setAll(state, true);
                else setAll(state, false);
                return;
            }
        });

        // The panel lives inside the wrapper, but clicks on labels toggle checkboxes natively.
        panel.addEventListener('change', e => {
            if (e.target.matches('input[type="checkbox"]')) {
                const option = optionForValue(select, e.target.value);
                if (option) option.selected = e.target.checked;
                render(state);
                emitChange(select);
            }
        });

        // Toggle when clicking anywhere on the row (not just the tiny checkbox).
        state.list.addEventListener('click', e => {
            const row = e.target.closest('.a-ms__opt');
            if (!row || e.target.matches('input')) return;
            const box = row.querySelector('input');
            box.checked = !box.checked;
            const option = optionForValue(select, box.value);
            if (option) option.selected = box.checked;
            render(state);
            emitChange(select);
        });

        document.addEventListener('click', e => {
            if (!wrapper.contains(e.target)) toggle(state, false);
        });
        wrapper.addEventListener('keydown', e => {
            if (e.key === 'Escape') { toggle(state, false); control.focus(); }
        });

        render(state);
        return state;
    }

    function optionForValue(select, value) {
        return Array.from(select.options).find(o => o.value === value) || null;
    }

    function emitChange(select) {
        select.dispatchEvent(new Event('change', { bubbles: true }));
    }

    function toggle(state, open) {
        // Only one dropdown open at a time.
        if (open) instances.forEach(other => { if (other !== state) close(other); });
        state.wrapper.classList.toggle('is-open', open);
        state.control.setAttribute('aria-expanded', String(open));
        if (open) {
            state.search.value = '';
            filter(state, '');
            positionPanel(state);
            window.addEventListener('scroll', state.reposition = () => positionPanel(state), true);
            window.addEventListener('resize', state.reposition);
            setTimeout(() => state.search.focus(), 0);
        } else {
            releasePosition(state);
        }
    }

    function close(state) {
        state.wrapper.classList.remove('is-open');
        state.control.setAttribute('aria-expanded', 'false');
        releasePosition(state);
    }

    // The panel may sit inside a scroll container (e.g. a table wrapper) that clips
    // absolutely positioned children. Pin it to the viewport instead and track the control.
    function positionPanel(state) {
        const rect = state.control.getBoundingClientRect();
        const panel = state.panel;
        const width = Math.max(rect.width, 220);
        const estimated = 300;
        panel.style.position = 'fixed';
        panel.style.insetInline = 'auto';
        panel.style.right = 'auto';
        panel.style.width = width + 'px';
        panel.style.left = Math.max(8, Math.min(rect.left, window.innerWidth - width - 8)) + 'px';
        if (rect.bottom + 6 + estimated > window.innerHeight && rect.top - 6 - estimated > 0) {
            panel.style.top = 'auto';
            panel.style.bottom = (window.innerHeight - rect.top + 6) + 'px';
        } else {
            panel.style.bottom = 'auto';
            panel.style.top = (rect.bottom + 6) + 'px';
        }
    }

    function releasePosition(state) {
        if (state.reposition) {
            window.removeEventListener('scroll', state.reposition, true);
            window.removeEventListener('resize', state.reposition);
            state.reposition = null;
        }
        const panel = state.panel;
        panel.style.position = '';
        panel.style.insetInline = '';
        panel.style.right = '';
        panel.style.left = '';
        panel.style.top = '';
        panel.style.bottom = '';
        panel.style.width = '';
    }

    function filter(state, term) {
        const needle = (term || '').trim().toLowerCase();
        state.list.querySelectorAll('.a-ms__opt').forEach(row => {
            const match = !needle || row.dataset.label.includes(needle);
            row.classList.toggle('is-hidden', !match);
        });
        const visible = Array.from(state.list.querySelectorAll('.a-ms__opt')).filter(r => !r.classList.contains('is-hidden'));
        let empty = state.list.querySelector('.a-ms__empty');
        if (!visible.length) {
            if (!empty) {
                empty = document.createElement('div');
                empty.className = 'a-ms__empty';
                empty.textContent = 'لا نتائج مطابقة';
                state.list.appendChild(empty);
            }
        } else if (empty) {
            empty.remove();
        }
    }

    function setAll(state, checked) {
        state.list.querySelectorAll('.a-ms__opt').forEach(row => {
            if (row.classList.contains('is-hidden')) return;
            const box = row.querySelector('input');
            box.checked = checked;
            const option = optionForValue(state.select, box.value);
            if (option) option.selected = checked;
        });
        render(state);
        emitChange(state.select);
    }

    function render(state) {
        const select = state.select;

        // Rebuild the option list only when the source options changed (e.g. AJAX search).
        const signature = Array.from(select.options).map(o => o.value + '\u0001' + o.textContent).join('\u0002');
        if (state.signature !== signature) {
            state.signature = signature;
            state.list.replaceChildren();
            Array.from(select.options).forEach(option => {
                const row = document.createElement('label');
                row.className = 'a-ms__opt';
                row.dataset.label = (option.textContent || '').toLowerCase();
                const box = document.createElement('input');
                box.type = 'checkbox';
                box.value = option.value;
                const label = document.createElement('span');
                label.className = 'a-ms__opt-label';
                label.textContent = option.textContent;
                row.appendChild(box);
                row.appendChild(label);
                state.list.appendChild(row);
            });
        }

        // Reflect selection state.
        state.list.querySelectorAll('.a-ms__opt').forEach(row => {
            const box = row.querySelector('input');
            const option = optionForValue(select, box.value);
            const checked = !!(option && option.selected);
            box.checked = checked;
            row.classList.toggle('is-checked', checked);
        });

        const chosen = Array.from(select.selectedOptions);
        const valueEl = state.control.querySelector('.a-ms__value');
        valueEl.replaceChildren();
        if (!chosen.length) {
            const ph = document.createElement('span');
            ph.className = 'a-ms__placeholder';
            ph.textContent = state.placeholder;
            valueEl.appendChild(ph);
        } else if (chosen.length <= 2) {
            chosen.forEach(option => valueEl.appendChild(chip(state, option)));
        } else {
            const chipEl = document.createElement('span');
            chipEl.className = 'a-ms__chip';
            const text = document.createElement('span');
            text.textContent = chosen.length + ' مُحدَّد';
            chipEl.appendChild(text);
            const clear = document.createElement('button');
            clear.type = 'button';
            clear.innerHTML = '<i class="bi bi-x"></i>';
            clear.title = 'مسح التحديد';
            clear.addEventListener('click', e => { e.stopPropagation(); setAll(state, false); });
            chipEl.appendChild(clear);
            valueEl.appendChild(chipEl);
        }

        const countEl = state.panel.querySelector('[data-count]');
        if (countEl) countEl.textContent = chosen.length ? chosen.length + ' مُحدَّد' : '';
    }

    function chip(state, option) {
        const el = document.createElement('span');
        el.className = 'a-ms__chip';
        const text = document.createElement('span');
        text.textContent = option.textContent;
        el.appendChild(text);
        const remove = document.createElement('button');
        remove.type = 'button';
        remove.innerHTML = '<i class="bi bi-x"></i>';
        remove.title = 'إزالة';
        remove.addEventListener('click', e => {
            e.stopPropagation();
            option.selected = false;
            render(state);
            emitChange(state.select);
        });
        el.appendChild(remove);
        return el;
    }

    function init(root) {
        (root || document).querySelectorAll('select[data-multiselect]').forEach(select => {
            const state = build(select);
            state.placeholder = select.dataset.placeholder || 'اختر...';
            render(state);
        });
    }

    function sync(select) {
        const state = instances.get(select);
        if (state) render(state);
    }

    function syncAll(root) {
        (root || document).querySelectorAll('select[data-multiselect]').forEach(select => sync(select));
    }

    window.ECertMultiSelect = { init, sync, syncAll };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => init(document));
    } else {
        init(document);
    }
})();
