document.addEventListener('DOMContentLoaded', function () {
    // Auto-dismiss alerts after 5 seconds
    document.querySelectorAll('.alert-dismissible').forEach(function (alert) {
        setTimeout(function () { bootstrap.Alert.getOrCreateInstance(alert).close(); }, 5000);
    });

    // Confirm actions. notify.js owns [data-confirm] when present (on-brand dialog);
    // this native fallback only applies on pages that load site.js without notify.js.
    document.querySelectorAll('[data-confirm]').forEach(function (btn) {
        btn.addEventListener('click', function (e) {
            if (window.Notify) return;
            if (!confirm(this.dataset.confirm)) {
                e.preventDefault();
            }
        });
    });

    // Active nav link
    var currentPath = window.location.pathname;
    document.querySelectorAll('.navbar-nav .nav-link, .admin-sidebar .nav-link').forEach(function (link) {
        if (link.getAttribute('href') === currentPath) {
            link.classList.add('active');
        }
    });

    // Table search filter
    var searchInput = document.getElementById('tableSearch');
    if (searchInput) {
        searchInput.addEventListener('keyup', function () {
            var filter = this.value.toLowerCase();
            var rows = document.querySelectorAll('table tbody tr');
            rows.forEach(function (row) {
                var text = row.textContent.toLowerCase();
                row.style.display = text.includes(filter) ? '' : 'none';
            });
        });
    }

    // Back to top button
    var backToTop = document.getElementById('backToTop');
    if (backToTop) {
        window.addEventListener('scroll', function () {
            backToTop.classList.toggle('show', window.scrollY > 400);
        }, { passive: true });
        backToTop.addEventListener('click', function () {
            window.scrollTo({ top: 0, behavior: 'smooth' });
        });
    }

    // Navbar elevation on scroll
    var navbar = document.querySelector('.navbar.sticky-top');
    if (navbar) {
        window.addEventListener('scroll', function () {
            navbar.classList.toggle('navbar-scrolled', window.scrollY > 10);
        }, { passive: true });
    }
});

// Photo preview before upload
function previewPhoto(input) {
    var preview = document.getElementById('photoPreview');
    if (input.files && input.files[0]) {
        var reader = new FileReader();
        reader.onload = function (e) {
            preview.src = e.target.result;
            preview.style.display = 'block';
        };
        reader.readAsDataURL(input.files[0]);
    } else {
        preview.style.display = 'none';
    }
}
