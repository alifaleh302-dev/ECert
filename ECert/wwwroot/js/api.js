// API Client - handles all communication with the backend API
const API = {
    baseUrl: '/api',

    async request(url, options = {}) {
        try {
            const response = await fetch(this.baseUrl + url, {
                credentials: 'include',
                headers: {
                    ...(options.headers || {}),
                    ...(options.body && !(options.body instanceof FormData)
                        ? { 'Content-Type': 'application/json' } : {})
                },
                ...options
            });

            const data = await response.json();

            if (response.status === 401) {
                window.location.href = '/Auth/Login';
                return null;
            }

            return data;
        } catch (error) {
            console.error('API Error:', error);
            API.showToast('حدث خطأ في الاتصال بالخادم', 'danger');
            return null;
        }
    },

    async get(url) {
        return this.request(url, { method: 'GET' });
    },

    async post(url, data) {
        return this.request(url, {
            method: 'POST',
            body: JSON.stringify(data)
        });
    },

    async put(url, data) {
        return this.request(url, {
            method: 'PUT',
            body: JSON.stringify(data)
        });
    },

    async del(url) {
        return this.request(url, { method: 'DELETE' });
    },

    async upload(url, formData, method = 'POST') {
        return this.request(url, {
            method: method,
            body: formData
        });
    },

    // Toast notification — delegates to the shared Notify module so API results
    // and server flash messages render identically. Kept for backwards compatibility.
    showToast(message, type = 'success') {
        if (window.Notify) {
            Notify.toast(message, type);
            return;
        }
        // Fallback for any page that loads api.js without notify.js.
        const container = document.getElementById('toastContainer') || (() => {
            const div = document.createElement('div');
            div.id = 'toastContainer';
            div.style.cssText = 'position:fixed;top:20px;left:50%;transform:translateX(-50%);z-index:9999;min-width:300px;';
            document.body.appendChild(div);
            return div;
        })();

        const toast = document.createElement('div');
        toast.className = `alert alert-${type} alert-dismissible fade show shadow`;
        toast.style.cssText = 'margin-bottom:10px;text-align:center;';
        const icon = document.createElement('i');
        icon.className = `bi bi-${type === 'success' ? 'check-circle' : 'exclamation-triangle'}`;
        const text = document.createTextNode(' ' + message + ' ');
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'btn-close';
        btn.setAttribute('data-bs-dismiss', 'alert');
        toast.appendChild(icon);
        toast.appendChild(text);
        toast.appendChild(btn);
        container.appendChild(toast);

        setTimeout(() => {
            if (toast.parentNode) toast.remove();
        }, 4000);
    },

    // Handle API response
    handleResponse(response, successCallback) {
        if (!response) return;
        if (response.success) {
            this.showToast(response.message, 'success');
            if (successCallback) successCallback(response.data);
        } else {
            this.showToast(response.message, 'danger');
        }
    }
};
