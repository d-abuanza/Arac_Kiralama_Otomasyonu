(request => {
    if (!['yolcu360.com', 'www.yolcu360.com'].includes(location.hostname) || location.protocol !== 'https:') return { state: 'unexpected-origin' };
    // Selectors observed in the rendered Yolcu360 login page and its loaded Vue components.
    const normal = s => (s || '').normalize('NFD').replace(/[\u0300-\u036f]/g, '').replace(/ı/g, 'i').toLowerCase().replace(/\s+/g, ' ').trim();
    const digits = s => (s || '').replace(/\D/g, '');
    const visible = e => {
        if (!e || !e.isConnected || e.hidden || e.closest('[inert]')) return false;
        const style = getComputedStyle(e), rect = e.getBoundingClientRect();
        if (style.display === 'none' || style.visibility !== 'visible' || Number(style.opacity) === 0 || rect.width <= 0 || rect.height <= 0) return false;
        for (let p = e.parentElement; p; p = p.parentElement) {
            if (getComputedStyle(p).display === 'none' || getComputedStyle(p).visibility === 'hidden') return false;
            if (p.tagName === 'DETAILS' && !p.open && !p.querySelector(':scope > summary')?.contains(e)) return false;
        }
        return true;
    };
    const enabled = e => visible(e) && !e.disabled && !e.readOnly && e.getAttribute('aria-disabled') !== 'true';
    const interact = e => {
        if (!enabled(e)) return false;
        e.scrollIntoView({ block: 'center', inline: 'nearest' });
        const r = e.getBoundingClientRect(), hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
        return !!hit && (hit === e || e.contains(hit) || hit.contains(e));
    };
    const phone = () => {
        const exact = document.querySelector('input#phn-input');
        if (enabled(exact)) return exact;
        // Account profile pages also contain a telephone input; it is not a login form.
        if (!/^\/login\/?$/.test(location.pathname)) return null;
        const alternatives = [...document.querySelectorAll('input[type=tel],input[autocomplete=tel]')].filter(enabled);
        return alternatives.length === 1 ? alternatives[0] : null;
    };
    const picker = () => document.querySelector('details#id-dropdown-phone');
    const countrySummary = () => document.querySelector('#id-summary-dropdown-phone') || picker()?.querySelector(':scope > summary');
    const turkeySelected = () => visible(countrySummary()) && /^tr\s*\+90$/i.test((countrySummary().innerText || '').trim());
    const otpFields = () => {
        const exact = document.querySelector('input#sms_input');
        if (enabled(exact)) return [exact];
        const single = [...document.querySelectorAll('input[autocomplete="one-time-code"]')].filter(enabled);
        if (single.length === 1) return single;
        const boxes = [...document.querySelectorAll('input[maxlength="1"]')].filter(enabled);
        return boxes.length === 6 ? boxes : [];
    };
    const setValue = (e, value) => {
        if (!interact(e)) return false;
        e.focus();
        Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(e, value);
        e.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: value }));
        e.dispatchEvent(new Event('change', { bubbles: true }));
        e.blur();
        return true;
    };
    const otpMatches = value => {
        const fields = otpFields();
        return fields.length > 0 && fields.map(e => digits(e.value)).join('') === value;
    };
    const phoneMatches = value => turkeySelected() && !!phone() && digits(phone().value) === value;
    const challenge = () => [...document.querySelectorAll('iframe[src*="recaptcha"],iframe[src*="hcaptcha"]')].some(e =>
        !e.closest('.grecaptcha-badge') && /bframe|challenge/i.test(e.src + ' ' + e.title) && visible(e) && e.getBoundingClientRect().height > 100)
        || visible(document.querySelector('#challenge-form'));
    const authenticated = () => {
        if (/\/login\/?$/.test(location.pathname) || phone() || otpFields().length) return false;
        // These exact header nodes are rendered only for a nonanonymous user.
        return [...document.querySelectorAll('a[href]')].some(a => a.isConnected && new URL(a.href, location.href).origin === location.origin
            && /^\/account\/?$/.test(new URL(a.href, location.href).pathname) && a.querySelector('.header-user-icon-wrap,.logged-user'));
    };
    const initialized = () => {
        if (document.readyState !== 'complete') return false;
        const root = document.querySelector('#__nuxt');
        if (!root) return true; // The site's standalone security/login documents need not use Nuxt.
        const app = root.__vue_app__;
        const nuxt = app?.$nuxt || app?.config?.globalProperties?.$nuxt;
        return !!nuxt && nuxt.isHydrating === false;
    };
    const errorState = () => {
        const error = [...document.querySelectorAll('p[data-cms-key],[role=alert],[aria-live=assertive]')].find(e => visible(e)
            && (e.matches('[role=alert],[aria-live=assertive]') || /error|invalid|expired/.test(e.getAttribute('data-cms-key') || '') || /text-red|error/.test(e.className || ''))
            && e.innerText.trim());
        if (!error) return null;
        const text = normal(error.innerText);
        if (/sure.*dol|gecerlilik|expired/.test(text)) return { state: 'expired' };
        if (/baglanti|network|internet|sunucu|server|tekrar deney|try again/.test(text)) return { state: 'network-error' };
        return { state: 'invalid', errorKey: (error.getAttribute('data-cms-key') || '').replace(/[^a-zA-Z_0-9-]/g, '').slice(0, 80) };
    };
    const button = kind => {
        const exact = document.querySelector(kind === 'phone' ? 'button[data-cms-key="text_continue"]' : 'button[data-cms-key="button_apply"]');
        if (enabled(exact)) return exact;
        const pattern = kind === 'phone' ? /^(devam et|devam|continue|next|ileri|dogrula)$/ : /^(dogrula|giris yap|dogrula \/ giris yap|onayla|verify|confirm|continue|devam et)$/;
        return [...document.querySelectorAll('button,[role=button],input[type=submit]')].find(e => enabled(e) && pattern.test(normal(e.innerText || e.value || e.getAttribute('aria-label'))));
    };

    switch (request.action) {
        case 'state': {
            if (challenge()) return { state: 'challenge' };
            if (!initialized()) return { state: 'loading' };
            if (authenticated()) return { state: 'authenticated' };
            if (visible(document.querySelector('[data-cms-key="text_restart_code"]'))) return { state: 'expired' };
            const error = errorState();
            if (error && request.phase !== 'ready') return error;
            if (otpFields().length) return { state: 'otp' };
            if (phone() && visible(countrySummary()) && document.readyState === 'complete') return { state: 'phone' };
            if (visible(document.querySelector('#register_firstName,#register_lastName,#register_email'))) return { state: 'registration' };
            if (document.querySelector('.header-login')) return { state: 'signed-out' };
            return { state: document.readyState === 'complete' ? 'unknown' : 'loading' };
        }
        case 'open-country': {
            if (turkeySelected()) return { state: 'country-ready' };
            const details = picker(), summary = countrySummary();
            if (!details || !interact(summary)) return { state: 'country-missing' };
            if (!details.open) summary.click();
            return { state: details.open ? 'country-open' : 'country-missing' };
        }
        case 'select-country': {
            if (turkeySelected()) return { state: 'country-ready' };
            if (!picker()?.open) return { state: 'country-missing' };
            const option = [...picker().querySelectorAll('li,[role=option]')].find(e => /^tr\s*\+90$/i.test((e.textContent || '').trim()));
            if (!interact(option)) return { state: 'country-option-missing' };
            option.click();
            return { state: 'country-changing' };
        }
        case 'fill-phone': {
            if (!turkeySelected()) return { state: 'country-missing' };
            return { state: setValue(phone(), request.phone) ? 'phone-filled' : 'phone-missing' };
        }
        case 'check-phone': return { state: phoneMatches(request.phone) ? 'phone-verified' : 'phone-mismatch' };
        case 'submit-phone': {
            if (!phoneMatches(request.phone)) return { state: 'phone-mismatch' };
            const submit = button('phone');
            if (!interact(submit)) return { state: 'continue-unavailable' };
            submit.click();
            return { state: 'phone-submitted' };
        }
        case 'fill-otp': {
            const fields = otpFields();
            if (!fields.length) return { state: 'otp-missing' };
            for (let i = 0; i < fields.length; i++) if (!setValue(fields[i], fields.length === 1 ? request.otp : request.otp[i])) return { state: 'otp-unavailable' };
            return { state: 'otp-filled' };
        }
        case 'check-otp': return { state: otpMatches(request.otp) ? 'otp-verified' : 'otp-mismatch' };
        case 'submit-otp': {
            if (!otpMatches(request.otp)) return { state: 'otp-mismatch' };
            const submit = button('otp');
            if (!interact(submit)) return { state: 'verify-unavailable' };
            submit.click();
            return { state: 'otp-submitted' };
        }
        case 'clear-otp': {
            for (const e of otpFields()) setValue(e, '');
            return { state: 'cleared' };
        }
        default: throw new Error('Unsupported login operation');
    }
})
