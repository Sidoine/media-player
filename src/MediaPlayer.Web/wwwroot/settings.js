window.mediaSettings = {
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
