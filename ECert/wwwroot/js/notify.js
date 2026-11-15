// Notify — the single feedback channel for the app.
// Server flash messages (window.__ecertFlash) and client-side results both render
// through here, so success/failure always looks the same and lands above every
// overlay (see --z-toast in admin.css). No dependency on Bootstrap JS.
const Notify = (() => {
    const ICONS = {
        success: 'bi-check-circle-fill',
        info: 'bi-info-circle-fill',
        warning: 'bi-exclamation-triangle-fill',
        danger: 'bi-x-circle-fill'
    };

    // How long each kind stays on screen. Errors persist until dismissed so a
    // failure is never missed; informational messages clear themselves.
    const TIMEOUTS = { success: 5000, info: 6000, warning: 8000, danger: 0 };

    let host = null;
    function getHost() {
        if (host && document.body.contains(host)) return host;
        host = document.getElementById('toastHost');
        if (!host) {
            host = document.createElement('div');
            host.id = 'toastHost';
            document.body.appendChild(host);
        }
        host.className = 'toast-host';
        return host;
    }

    function dismiss(node) {
        if (!node || node.dataset.leaving === '1') return;
        node.dataset.leaving = '1';
        node.classList.add('is-leaving');
        node.addEventListener('animationend', () => node.remove(), { once: true });
        // Fallback in case the animation never fires (reduced motion, hidden tab).
        setTimeout(() => node.remove(), 400);
    }

    function toast(message, type = 'success', options = {}) {
        const kind = ICONS[type] ? type : 'info';
        const text = (message ?? '').toString();
        if (!text) return null;

        const node = document.createElement('div');
        node.className = `toast toast--${kind}`;
        // Errors are assertive so a screen reader interrupts; everything else waits.
        node.setAttribute('role', kind === 'danger' ? 'alert' : 'status');
        node.setAttribute('aria-live', kind === 'danger' ? 'assertive' : 'polite');

        const icon = document.createElement('i');
        icon.className = `bi ${ICONS[kind]} toast__icon`;
        icon.setAttribute('aria-hidden', 'true');

        const body = document.createElement('div');
        body.className = 'toast__body';
        body.textContent = text;

        const close = document.createElement('button');
        close.type = 'button';
        close.className = 'toast__close';
        close.setAttribute('aria-label', 'إغلاق');
        close.innerHTML = '<i class="bi bi-x-lg" aria-hidden="true"></i>';
        close.addEventListener('click', () => dismiss(node));

        node.append(icon, body, close);

        const ms = options.timeout ?? TIMEOUTS[kind];
        if (ms > 0) {
            const timer = document.createElement('span');
            timer.className = 'toast__timer';
            timer.style.animationDuration = `${ms}ms`;
            node.appendChild(timer);

            let handle = setTimeout(() => dismiss(node), ms);
            // Give the reader more time while they hover the message.
            node.addEventListener('mouseenter', () => {
                clearTimeout(handle);
                timer.style.animationPlayState = 'paused';
            });
            node.addEventListener('mouseleave', () => {
                timer.style.animationPlayState = 'running';
                handle = setTimeout(() => dismiss(node), ms);
            });
        }

        const container = getHost();
        container.appendChild(node);
        return node;
    }

    function confirmDialog(options = {}) {
        const {
            title = 'تأكيد العملية',
            text = '',
            confirmLabel = 'تأكيد',
            cancelLabel = 'إلغاء',
            danger = false
        } = options;

        return new Promise(resolve => {
            const backdrop = document.createElement('div');
            backdrop.className = 'confirm-backdrop';
            backdrop.setAttribute('role', 'dialog');
            backdrop.setAttribute('aria-modal', 'true');

            const box = document.createElement('div');
            box.className = `confirm-box${danger ? ' confirm-box--danger' : ''}`;

            const icon = document.createElement('div');
            icon.className = 'confirm-box__icon';
            icon.innerHTML = `<i class="bi ${danger ? 'bi-exclamation-triangle-fill' : 'bi-question-circle-fill'}" aria-hidden="true"></i>`;

            const heading = document.createElement('h2');
            heading.className = 'confirm-box__title';
            heading.textContent = title;

            const para = document.createElement('p');
            para.className = 'confirm-box__text';
            para.textContent = text;

            const actions = document.createElement('div');
            actions.className = 'confirm-box__actions';

            const cancel = document.createElement('button');
            cancel.type = 'button';
            cancel.className = 'btn btn-outline-secondary';
            cancel.textContent = cancelLabel;

            const ok = document.createElement('button');
            ok.type = 'button';
            ok.className = `btn ${danger ? 'btn-danger' : 'btn-primary'}`;
            ok.textContent = confirmLabel;

            actions.append(cancel, ok);
            box.append(icon, heading, para, actions);
            backdrop.appendChild(box);
            document.body.appendChild(backdrop);
            ok.focus();

            const close = answer => {
                document.removeEventListener('keydown', onKey);
                backdrop.remove();
                resolve(answer);
            };
            const onKey = e => {
                if (e.key === 'Escape') close(false);
                if (e.key === 'Enter') close(true);
            };
            cancel.addEventListener('click', () => close(false));
            ok.addEventListener('click', () => close(true));
            backdrop.addEventListener('click', e => { if (e.target === backdrop) close(false); });
            document.addEventListener('keydown', onKey);
        });
    }

    function alertDialog(message, options = {}) {
        return confirmDialog({
            title: options.title || 'تنبيه',
            text: (message ?? '').toString(),
            confirmLabel: options.confirmLabel || 'حسنًا',
            cancelLabel: null,
            danger: options.danger === true
        });
    }

    // Drains window.__ecertFlash into toasts. Server-side messages take precedence
    // over a client success toast fired on the same page, so only one is shown.
    function flush() {
        const queue = Array.isArray(window.__ecertFlash) ? window.__ecertFlash : [];
        window.__ecertFlash = [];
        if (!queue.length) return;
        window.__ecertHadFlash = true;
        queue.forEach(item => {
            if (item && item.message) toast(item.message, item.type || 'info');
        });
    }

    return { toast, success: m => toast(m, 'success'), info: m => toast(m, 'info'),
        warning: m => toast(m, 'warning'), error: m => toast(m, 'danger'),
        confirm: confirmDialog, alert: alertDialog, dismiss, flush };
})();

if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', () => Notify.flush());
} else {
    Notify.flush();
}
