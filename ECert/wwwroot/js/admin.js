/* ECert admin shell behaviours: theme, collapsible nav, user menu, mobile drawer. */
(function () {
    'use strict';

    var THEME_KEY = 'ecert-admin-theme';
    var root = document.documentElement;

    /* ---------- Theme ---------- */
    function syncThemeIcon() {
        var btn = document.getElementById('themeToggle');
        if (!btn) return;
        var dark = root.getAttribute('data-theme') === 'dark';
        btn.innerHTML = '<i class="bi ' + (dark ? 'bi-sun' : 'bi-moon-stars') + '" aria-hidden="true"></i>';
        btn.setAttribute('aria-pressed', dark ? 'true' : 'false');
    }

    var themeToggle = document.getElementById('themeToggle');
    if (themeToggle) {
        syncThemeIcon();
        themeToggle.addEventListener('click', function () {
            var next = root.getAttribute('data-theme') === 'dark' ? 'light' : 'dark';
            root.setAttribute('data-theme', next);
            try { localStorage.setItem(THEME_KEY, next); } catch (e) { /* ignore */ }
            syncThemeIcon();
        });
    }

    /* ---------- Collapsible nav groups ---------- */
    var toggles = Array.prototype.slice.call(document.querySelectorAll('.admin-nav__toggle'));
    toggles.forEach(function (toggle) {
        var panel = document.getElementById(toggle.getAttribute('aria-controls'));
        if (!panel) return;

        toggle.addEventListener('click', function () {
            var open = toggle.getAttribute('aria-expanded') === 'true';
            toggle.setAttribute('aria-expanded', open ? 'false' : 'true');
            panel.classList.toggle('is-open', !open);
        });

        // Persist the collapsed/expanded choice per group.
        try {
            var stored = localStorage.getItem('ecert-nav-' + toggle.dataset.navGroup);
            if (stored === 'open' && toggle.getAttribute('aria-expanded') !== 'true') {
                toggle.setAttribute('aria-expanded', 'true');
                panel.classList.add('is-open');
            } else if (stored === 'closed') {
                toggle.setAttribute('aria-expanded', 'false');
                panel.classList.remove('is-open');
            }
        } catch (e) { /* ignore */ }

        toggle.addEventListener('click', function () {
            try {
                localStorage.setItem('ecert-nav-' + toggle.dataset.navGroup,
                    toggle.getAttribute('aria-expanded') === 'true' ? 'open' : 'closed');
            } catch (e) { /* ignore */ }
        });
    });

    /* ---------- User menu ---------- */
    var trigger = document.getElementById('userMenuTrigger');
    var panel = document.getElementById('userMenuPanel');
    if (trigger && panel) {
        trigger.addEventListener('click', function (e) {
            e.stopPropagation();
            var open = panel.classList.toggle('is-open');
            trigger.setAttribute('aria-expanded', open ? 'true' : 'false');
        });

        document.addEventListener('click', function (e) {
            if (!panel.classList.contains('is-open')) return;
            if (!panel.contains(e.target) && !trigger.contains(e.target)) {
                panel.classList.remove('is-open');
                trigger.setAttribute('aria-expanded', 'false');
            }
        });
    }

    /* ---------- Mobile drawer ---------- */
    var nav = document.getElementById('adminNav');
    var navToggle = document.getElementById('navToggle');
    var navClose = document.getElementById('navClose');
    var scrim = document.getElementById('navScrim');

    function openNav() {
        if (!nav) return;
        nav.classList.add('is-open');
        if (scrim) { scrim.hidden = false; requestAnimationFrame(function () { scrim.classList.add('is-visible'); }); }
        if (navToggle) navToggle.setAttribute('aria-expanded', 'true');
    }

    function closeNav() {
        if (!nav) return;
        nav.classList.remove('is-open');
        if (scrim) { scrim.classList.remove('is-visible'); scrim.hidden = true; }
        if (navToggle) navToggle.setAttribute('aria-expanded', 'false');
    }

    if (navToggle) navToggle.addEventListener('click', openNav);
    if (navClose) navClose.addEventListener('click', closeNav);
    if (scrim) scrim.addEventListener('click', closeNav);

    document.addEventListener('keydown', function (e) {
        if (e.key !== 'Escape') return;
        if (nav && nav.classList.contains('is-open')) closeNav();
        if (panel && panel.classList.contains('is-open')) {
            panel.classList.remove('is-open');
            if (trigger) trigger.setAttribute('aria-expanded', 'false');
        }
    });

    window.addEventListener('resize', function () {
        if (window.innerWidth > 1100) closeNav();
    });
})();
