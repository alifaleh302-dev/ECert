// Expense attachments: a multiple-image file input with a thumbnail preview, plus an optional
// live camera capture that appends stills to the same input. The server re-validates every image
// (size, extension, magic bytes) and enforces the maximum count, so this script is a convenience
// layer only — it never trusts the client for correctness.
(function () {
    'use strict';

    var MAX = 5;
    var MAX_BYTES = 5 * 1024 * 1024;

    function Attachments() {
        this.input = document.querySelector('[data-attachment-input]');
        this.preview = document.querySelector('[data-attachment-preview]');
        this.files = [];
        if (this.input) this.bindInput();
        if (this.preview) this.bindCamera();
        this.render();
    }

    Attachments.prototype.bindInput = function () {
        var self = this;
        this.input.addEventListener('change', function () {
            self.files = Array.prototype.slice.call(self.input.files || []);
            self.render();
        });
    };

    // The camera modal is wired up only when the attachments preview exists on the page.
    Attachments.prototype.bindCamera = function () {
        var self = this;
        this.modal = document.querySelector('[data-camera-modal]');
        this.video = document.querySelector('[data-camera-video]');
        this.error = document.querySelector('[data-camera-error]');
        this.fallback = document.querySelector('[data-camera-fallback]');
        this.stream = null;

        var open = document.querySelector('[data-camera-open]');
        if (open) open.addEventListener('click', function () { self.openCamera(); });

        Array.prototype.forEach.call(document.querySelectorAll('[data-camera-close]'), function (el) {
            el.addEventListener('click', function () { self.closeCamera(); });
        });
        var shoot = document.querySelector('[data-camera-shoot]');
        if (shoot) shoot.addEventListener('click', function () { self.shoot(); });

        if (this.fallback) {
            this.fallback.addEventListener('change', function () {
                var file = self.fallback.files && self.fallback.files[0];
                if (file) self.addFiles([file]);
                self.fallback.value = '';
            });
        }
    };

    Attachments.prototype.openCamera = function () {
        var self = this;
        if (this.files.length >= MAX) {
            alert('تم بلوغ الحد الأقصى ' + MAX + ' صور.');
            return;
        }
        if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia || !this.modal) {
            // No live preview available: hand off to the native camera app via the fallback input.
            if (this.fallback) this.fallback.click();
            return;
        }
        this.error.hidden = true;
        this.modal.hidden = false;
        navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' }, audio: false })
            .then(function (stream) {
                self.stream = stream;
                self.video.srcObject = stream;
                self.video.play();
            })
            .catch(function () {
                // Permission denied or no camera: fall back to the device's file/camera picker.
                self.closeCamera();
                if (self.fallback) self.fallback.click();
            });
    };

    Attachments.prototype.closeCamera = function () {
        if (this.stream) {
            this.stream.getTracks().forEach(function (t) { t.stop(); });
            this.stream = null;
        }
        if (this.video) this.video.srcObject = null;
        if (this.modal) this.modal.hidden = true;
    };

    Attachments.prototype.shoot = function () {
        var self = this;
        var canvas = document.createElement('canvas');
        canvas.width = this.video.videoWidth;
        canvas.height = this.video.videoHeight;
        canvas.getContext('2d').drawImage(this.video, 0, 0, canvas.width, canvas.height);
        canvas.toBlob(function (blob) {
            if (!blob) return;
            var name = 'camera-' + Date.now() + '.jpg';
            self.addFiles([new File([blob], name, { type: 'image/jpeg' })]);
            self.closeCamera();
        }, 'image/jpeg', 0.9);
    };

    Attachments.prototype.addFiles = function (incoming) {
        var self = this;
        incoming.forEach(function (file) {
            if (self.files.length >= MAX) return;
            if (file.size > MAX_BYTES) {
                alert('حجم الصورة «' + file.name + '» يتجاوز 5 ميغابايت.');
                return;
            }
            self.files.push(file);
        });
        this.syncInput();
        this.render();
    };

    // Mirrors the in-memory list back onto the real <input type="file"> so the form posts it.
    Attachments.prototype.syncInput = function () {
        if (!this.input || !window.DataTransfer) return;
        var dt = new DataTransfer();
        this.files.forEach(function (f) { dt.items.add(f); });
        this.input.files = dt.files;
    };

    Attachments.prototype.render = function () {
        if (!this.preview) return;
        var self = this;
        this.preview.innerHTML = '';
        this.files.forEach(function (file, index) {
            var thumb = document.createElement('div');
            thumb.className = 'exp-thumb';

            var img = document.createElement('img');
            img.alt = file.name;
            img.src = URL.createObjectURL(file);
            img.onload = function () { URL.revokeObjectURL(img.src); };

            var remove = document.createElement('button');
            remove.type = 'button';
            remove.className = 'exp-thumb__remove';
            remove.setAttribute('aria-label', 'إزالة الصورة');
            remove.innerHTML = '<i class="bi bi-x"></i>';
            remove.addEventListener('click', function () {
                self.files.splice(index, 1);
                self.syncInput();
                self.render();
            });

            thumb.appendChild(img);
            thumb.appendChild(remove);
            self.preview.appendChild(thumb);
        });
    };

    document.addEventListener('DOMContentLoaded', function () {
        if (document.querySelector('[data-attachment-input]')) new Attachments();
    });
})();
