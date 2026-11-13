window.mediaSettings = {
    restartServer() {
        void (async () => {
            try {
                await fetch('/api/server/restart', { method: 'POST', cache: 'no-store' });
            } catch { }

            let wasUnavailable = false;
            const deadline = Date.now() + 60000;
            while (Date.now() < deadline) {
                await new Promise(resolve => setTimeout(resolve, 1000));
                try {
                    const response = await fetch('/api/configuration/media', { cache: 'no-store' });
                    if (!response.ok) wasUnavailable = true;
                    else if (wasUnavailable) {
                        window.location.reload();
                        return;
                    }
                } catch {
                    wasUnavailable = true;
                }
            }

            const status = document.querySelector('[data-server-restart-status]');
            if (status) status.textContent = 'Le serveur ne répond pas encore. Actualisez cette page dans quelques instants.';
        })();
    },

    init() {
        const menu = document.querySelector('.settings-nav');
        if (!menu || menu.dataset.initialized === 'true') return;
        menu.dataset.initialized = 'true';

        const buttons = [...menu.querySelectorAll('[data-section]')];
        const sections = buttons
            .map(button => document.getElementById(button.dataset.section))
            .filter(Boolean);

        const select = id => {
            for (const button of buttons) {
                const active = button.dataset.section === id;
                button.classList.toggle('active', active);
                if (active) button.setAttribute('aria-current', 'location');
                else button.removeAttribute('aria-current');
                if (active && menu.scrollWidth > menu.clientWidth) {
                    button.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'nearest' });
                }
            }
        };

        const updateFromScroll = () => {
            const marker = 150;
            const current = [...sections]
                .filter(section => section.getBoundingClientRect().top <= marker)
                .at(-1) ?? sections[0];
            if (current) select(current.id);
        };

        menu.addEventListener('click', event => {
            const button = event.target.closest('[data-section]');
            if (!button) return;
            const section = document.getElementById(button.dataset.section);
            if (!section) return;
            event.preventDefault();
            select(section.id);
            history.replaceState(null, '', `/configuration#${section.id}`);
            section.scrollIntoView({ behavior: 'smooth', block: 'start' });
        });

        let scheduled = false;
        window.addEventListener('scroll', () => {
            if (scheduled) return;
            scheduled = true;
            requestAnimationFrame(() => {
                updateFromScroll();
                scheduled = false;
            });
        }, { passive: true });

        const hashSection = location.hash.slice(1);
        if (sections.some(section => section.id === hashSection)) {
            requestAnimationFrame(() => {
                document.getElementById(hashSection).scrollIntoView({ block: 'start' });
                select(hashSection);
            });
        } else {
            updateFromScroll();
        }
    }
};

window.mediaLocalization = {
    getPreference() {
        const match = document.cookie.match(/(?:^|;\s*)MediaPlayerCulture=([^;]+)/);
        if (!match) return "auto";
        const value = decodeURIComponent(match[1]);
        return /(?:^|\|)c=fr(?:\||$)/i.test(value) || /(?:^|\|)uic=fr(?:\||$)/i.test(value) ? "fr" : "en";
    },
    setPreference(value) {
        if (value === "auto") {
            document.cookie = "MediaPlayerCulture=; path=/; max-age=0; SameSite=Lax";
        } else {
            const culture = value === "fr" ? "fr" : "en";
            document.cookie = `MediaPlayerCulture=c=${culture}|uic=${culture}; path=/; max-age=31536000; SameSite=Lax`;
        }
        window.location.reload();
    }
};

window.mediaAuth = {
    async postJson(url, body) {
        const response = await fetch(url, {
            method: "POST",
            credentials: "same-origin",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(body)
        });
        let payload = null;
        try { payload = await response.json(); } catch { }
        return { ok: response.ok, status: response.status, error: payload?.error ?? null };
    },
    async logout() {
        const response = await fetch("/api/users/logout", { method: "POST", credentials: "same-origin" });
        return response.ok;
    }
};
