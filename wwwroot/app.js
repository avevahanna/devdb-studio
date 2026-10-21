window.dbServe = {
    storageGet: (key) => { try { return localStorage.getItem(key); } catch { return null; } },
    storageSet: (key, value) => { try { localStorage.setItem(key, value); } catch { } },
    download: (filename, content, mime) => {
        const blob = new Blob([content], { type: mime });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    },
    toggleSidebar: () => document.body.classList.toggle('sidebar-open'),
    toggleTheme: () => {
        const root = document.documentElement;
        const next = root.getAttribute('data-theme') === 'dark' ? 'light' : 'dark';
        root.setAttribute('data-theme', next);
        try { localStorage.setItem('dbserve-theme', next); } catch { }
    },
    copyText: async (text) => {
        try {
            if (navigator.clipboard && window.isSecureContext) {
                await navigator.clipboard.writeText(text);
                return true;
            }
        } catch { /* fall through to legacy path */ }
        const ta = document.createElement('textarea');
        ta.value = text;
        ta.style.position = 'fixed';
        ta.style.opacity = '0';
        document.body.appendChild(ta);
        ta.select();
        let ok = false;
        try { ok = document.execCommand('copy'); } catch { }
        document.body.removeChild(ta);
        return ok;
    }
};

// Ctrl/Cmd+Enter in a textarea with data-run-target clicks the referenced button.
document.addEventListener('keydown', (e) => {
    if (e.key !== 'Enter' || !(e.ctrlKey || e.metaKey)) return;
    const target = e.target instanceof Element ? e.target.getAttribute('data-run-target') : null;
    if (!target) return;
    e.preventDefault();
    const btn = document.querySelector(target);
    if (btn && !btn.disabled) btn.click();
});
