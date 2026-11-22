// Academic catalog: collapse/expand the tree and filter it as you type.
// Everything here is an enhancement — selection, "add here" and every form are plain links and
// server-rendered posts, so the page still works with this file blocked.
(function () {
    'use strict';

    const tree = document.getElementById('acTree');
    if (!tree) return;

    tree.classList.add('ac-js');

    const nodes = () => Array.from(tree.querySelectorAll('.ac-node'));
    const childrenOf = node => Array.from(node.querySelectorAll(':scope > .ac-node__children > .ac-node'));
    const nameOf = node => node.querySelector(':scope > .ac-node__row .ac-node__name');

    /* ---- collapse / expand ---- */

    tree.addEventListener('click', function (event) {
        const toggle = event.target.closest('[data-ac-toggle]');
        if (!toggle) return;
        const node = toggle.closest('.ac-node');
        if (!node || childrenOf(node).length === 0) return;
        setOpen(node, !node.classList.contains('is-open'));
        syncExpandButton();
    });

    function setOpen(node, open) {
        node.classList.toggle('is-open', open);
        node.setAttribute('aria-expanded', open ? 'true' : 'false');
    }

    const expandButton = document.querySelector('[data-ac-expand]');
    function syncExpandButton() {
        if (!expandButton) return;
        const parents = nodes().filter(node => childrenOf(node).length > 0);
        const allOpen = parents.length > 0 && parents.every(node => node.classList.contains('is-open'));
        expandButton.textContent = allOpen ? 'طي الكل' : 'توسيع الكل';
        expandButton.dataset.mode = allOpen ? 'collapse' : 'expand';
    }

    if (expandButton) {
        expandButton.addEventListener('click', function () {
            const collapse = expandButton.dataset.mode === 'collapse';
            nodes().forEach(node => {
                if (childrenOf(node).length > 0) setOpen(node, !collapse);
            });
            syncExpandButton();
        });
    }

    syncExpandButton();

    /* ---- search ---- */

    const search = document.getElementById('acSearch');
    const hint = document.getElementById('acSearchHint');
    if (!search) return;

    const originalNames = new Map();
    nodes().forEach(node => {
        const element = nameOf(node);
        if (element) originalNames.set(node, element.textContent);
    });

    search.addEventListener('input', function () {
        filter(search.value.trim());
    });

    function filter(query) {
        let matches = 0;

        nodes().forEach(node => {
            const name = originalNames.get(node) || '';
            const isMatch = query !== '' && name.includes(query);
            node.dataset.acMatch = isMatch ? '1' : '';
            if (isMatch) matches += 1;
        });

        // Children before parents, so a node can inherit visibility from its subtree.
        nodes().reverse().forEach(node => {
            const hasVisibleChild = childrenOf(node).some(child => child.dataset.acVisible === '1');
            const visible = query === '' || node.dataset.acMatch === '1' || hasVisibleChild;
            node.dataset.acVisible = visible ? '1' : '';
            node.classList.toggle('is-filtered-out', !visible);
            if (visible && (node.dataset.acMatch === '1' || hasVisibleChild)) {
                setOpen(node, true);
            }
        });

        originalNames.forEach((name, node) => {
            const element = nameOf(node);
            if (!element) return;
            element.textContent = '';
            const index = query === '' ? -1 : name.indexOf(query);
            if (index < 0) {
                element.textContent = name;
                return;
            }
            element.append(name.slice(0, index));
            const mark = document.createElement('mark');
            mark.textContent = name.slice(index, index + query.length);
            element.append(mark);
            element.append(name.slice(index + query.length));
        });

        if (hint) {
            hint.textContent = query === '' ? '' : `نتائج البحث: ${matches}`;
        }
        syncExpandButton();
    }
})();
