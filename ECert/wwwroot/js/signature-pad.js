// On-screen signature capture. Draws with pointer events and mirrors the result into a
// hidden file input so the signature is submitted as a normal multipart PNG upload.
(function () {
    'use strict';

    var STROKE_WIDTH = 2.6;
    var INK = '#111827';

    function SignaturePad(root) {
        this.root = root;
        this.canvas = root.querySelector('canvas');
        this.ctx = this.canvas.getContext('2d');
        this.fileInput = document.getElementById(root.dataset.fileInput || '');
        this.emptyHint = root.querySelector('[data-signature-hint]');
        this.strokes = [];
        this.drawing = false;
        this.current = null;
        this.bind();
        this.resize();
    }

    SignaturePad.prototype.bind = function () {
        var self = this;

        var start = function (e) {
            if (e.button !== undefined && e.button !== 0) return;
            e.preventDefault();
            self.drawing = true;
            self.current = [self.point(e)];
            self.strokes.push(self.current);
            self.canvas.setPointerCapture && self.canvas.setPointerCapture(e.pointerId);
            self.redraw();
        };
        var move = function (e) {
            if (!self.drawing || !self.current) return;
            e.preventDefault();
            self.current.push(self.point(e));
            self.redraw();
        };
        var end = function () {
            if (!self.drawing) return;
            self.drawing = false;
            self.current = null;
            self.sync();
        };

        this.canvas.addEventListener('pointerdown', start);
        this.canvas.addEventListener('pointermove', move);
        this.canvas.addEventListener('pointerup', end);
        this.canvas.addEventListener('pointerleave', end);
        this.canvas.addEventListener('pointercancel', end);

        var clearBtn = this.root.querySelector('[data-signature-clear]');
        if (clearBtn) clearBtn.addEventListener('click', function () { self.clear(); });

        var undoBtn = this.root.querySelector('[data-signature-undo]');
        if (undoBtn) undoBtn.addEventListener('click', function () { self.undo(); });

        window.addEventListener('resize', function () { self.resize(); });
    };

    SignaturePad.prototype.point = function (e) {
        var rect = this.canvas.getBoundingClientRect();
        return { x: e.clientX - rect.left, y: e.clientY - rect.top };
    };

    SignaturePad.prototype.resize = function () {
        var rect = this.canvas.getBoundingClientRect();
        if (!rect.width || !rect.height) return;
        var dpr = window.devicePixelRatio || 1;
        this.canvas.width = Math.round(rect.width * dpr);
        this.canvas.height = Math.round(rect.height * dpr);
        this.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        this.redraw();
    };

    SignaturePad.prototype.redraw = function () {
        var ctx = this.ctx;
        var rect = this.canvas.getBoundingClientRect();
        ctx.clearRect(0, 0, rect.width, rect.height);
        ctx.lineWidth = STROKE_WIDTH;
        ctx.lineCap = 'round';
        ctx.lineJoin = 'round';
        ctx.strokeStyle = INK;

        this.strokes.forEach(function (stroke) {
            if (!stroke.length) return;
            ctx.beginPath();
            if (stroke.length === 1) {
                ctx.arc(stroke[0].x, stroke[0].y, STROKE_WIDTH / 2, 0, Math.PI * 2);
                ctx.fillStyle = INK;
                ctx.fill();
                return;
            }
            stroke.forEach(function (p, i) {
                if (i === 0) ctx.moveTo(p.x, p.y); else ctx.lineTo(p.x, p.y);
            });
            ctx.stroke();
        });

        if (this.emptyHint) this.emptyHint.style.display = this.strokes.length ? 'none' : '';
    };

    SignaturePad.prototype.clear = function () {
        this.strokes = [];
        this.redraw();
        this.sync();
    };

    SignaturePad.prototype.undo = function () {
        this.strokes.pop();
        this.redraw();
        this.sync();
    };

    // Mirrors the drawing into the hidden file input. An empty pad clears the input so an
    // untouched canvas never overwrites a previously stored signature.
    SignaturePad.prototype.sync = function () {
        var input = this.fileInput;
        if (!input) return;

        if (!this.strokes.length) {
            input.value = '';
            return;
        }

        var self = this;
        this.canvas.toBlob(function (blob) {
            if (!blob) return;
            try {
                var data = new DataTransfer();
                data.items.add(new File([blob], 'signature.png', { type: 'image/png' }));
                input.files = data.files;
            } catch (err) {
                // Browsers without DataTransfer support fall back to the drawn preview only.
            }
            self.root.dispatchEvent(new CustomEvent('signature:change', { bubbles: true }));
        }, 'image/png');
    };

    function initAll() {
        document.querySelectorAll('[data-signature-pad]').forEach(function (root) {
            if (root.dataset.signatureReady) return;
            root.dataset.signatureReady = '1';
            new SignaturePad(root);
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initAll);
    } else {
        initAll();
    }
    document.addEventListener('htmx:afterSwap', initAll);
})();
