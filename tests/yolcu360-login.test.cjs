const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '../CarRentalAutomation.Infrastructure/Yolcu360LoginPage.js'), 'utf8');

function fixture(url = 'https://yolcu360.com/login') {
    const location = new URL(url);
    let document;
    class Element {
        constructor(tag, attrs = {}, text = '') {
            this.tagName = tag.toUpperCase(); this.attrs = { ...attrs }; this._text = text;
            this.children = []; this.parentElement = null; this.isConnected = true;
            this.hidden = false; this.disabled = false; this.readOnly = false; this.open = false;
            this.style = { display: 'block', visibility: 'visible', opacity: '1' };
            this.rect = { left: 10, top: 10, width: 200, height: 32 };
            this.events = []; this.clicks = 0;
        }
        get className() { return this.attrs.class || ''; }
        get innerText() { return this._text + this.children.map(e => e.innerText).join(''); }
        set innerText(value) { this._text = value; }
        get textContent() { return this.innerText; }
        get src() { return this.attrs.src || ''; }
        get title() { return this.attrs.title || ''; }
        get href() { return new URL(this.attrs.href || '', location.href).href; }
        append(...children) { for (const child of children) { child.parentElement = this; this.children.push(child); } return this; }
        getAttribute(name) { return this.attrs[name] ?? null; }
        matches(selector) { return selector.split(',').some(s => matches(this, s.trim())); }
        closest(selector) { for (let p = this; p; p = p.parentElement) if (p.matches(selector)) return p; return null; }
        contains(e) { return e === this || this.children.some(c => c.contains(e)); }
        querySelectorAll(selector) {
            if (selector === ':scope > summary') return this.children.filter(e => e.tagName === 'SUMMARY');
            const result = [];
            for (const e of this.children) { if (e.matches(selector)) result.push(e); result.push(...e.querySelectorAll(selector)); }
            return result;
        }
        querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
        getBoundingClientRect() { return this.rect; }
        scrollIntoView() { document.hit = this; }
        focus() { document.activeElement = this; }
        blur() { if (document.activeElement === this) document.activeElement = null; }
        dispatchEvent(event) { this.events.push(event); return true; }
        click() { this.clicks++; this.onClick?.(); }
    }
    class Input extends Element {
        constructor(attrs) { super('input', attrs); this._value = ''; }
        get value() { return this._value; }
        set value(value) { this._value = String(value); }
    }
    function matches(e, selector) {
        const tag = selector.match(/^[a-zA-Z][\w-]*/)?.[0];
        if (tag && e.tagName !== tag.toUpperCase()) return false;
        const id = selector.match(/#([\w-]+)/)?.[1];
        if (id && e.attrs.id !== id) return false;
        for (const match of selector.matchAll(/\.([\w-]+)/g)) if (!e.className.split(/\s+/).includes(match[1])) return false;
        for (const match of selector.matchAll(/\[([^\]=*]+)(\*?=)?(?:(?:"([^"]*)")|(?:'([^']*)')|([^\]]*))?\]/g)) {
            const [, name, op, double, single, bare] = match, value = double ?? single ?? bare;
            if (!(name in e.attrs)) return false;
            if (op === '=' && String(e.attrs[name]) !== value) return false;
            if (op === '*=' && !String(e.attrs[name]).includes(value)) return false;
        }
        return true;
    }
    const body = new Element('body');
    document = {
        body, readyState: 'complete', hit: null, overlay: null,
        querySelectorAll: selector => body.querySelectorAll(selector),
        querySelector: selector => body.querySelector(selector),
        elementFromPoint: () => document.overlay || document.hit
    };
    class Event { constructor(type, options = {}) { this.type = type; Object.assign(this, options); } }
    const context = vm.createContext({ document, location, window: {}, URL, getComputedStyle: e => e.style, HTMLInputElement: Input, Event, InputEvent: Event });
    const run = vm.runInContext(source, context, { filename: 'Yolcu360LoginPage.js' });
    const make = (tag, attrs = {}, text = '') => tag.toLowerCase() === 'input' ? new Input(attrs) : new Element(tag, attrs, text);
    return { document, body, make, run, location };
}

function phoneForm(f, country = 'DE +49') {
    const details = f.make('details', { id: 'id-dropdown-phone' });
    const summary = f.make('summary', { id: 'id-summary-dropdown-phone' }, country);
    const option = f.make('li', {}, 'TR +90');
    summary.onClick = () => { details.open = !details.open; };
    option.onClick = () => { summary.innerText = 'TR +90'; details.open = false; };
    details.append(summary, option);
    const phone = f.make('input', { id: 'phn-input', type: 'tel' });
    const button = f.make('button', { 'data-cms-key': 'text_continue' }, 'Devam Et');
    f.body.append(details, phone, button);
    return { details, summary, option, phone, button };
}

function account(f, { href = '/account', marker = true, hidden = false } = {}) {
    const link = f.make('a', { href });
    if (marker) link.append(f.make('span', { class: 'header-user-icon-wrap' }));
    link.hidden = hidden; f.body.append(link); return link;
}

test('production JavaScript parses; a homepage redirect alone is not authenticated', () => {
    const f = fixture('https://yolcu360.com/');
    assert.equal(f.run({ action: 'state' }).state, 'unknown');
});

test('generic, foreign-origin and nonaccount paths cannot report authenticated', () => {
    for (const opts of [{ marker: false }, { href: 'https://unrelated.invalid/account' }, { href: '/unrelated/account' }]) {
        const f = fixture('https://yolcu360.com/'); account(f, opts);
        assert.notEqual(f.run({ action: 'state' }).state, 'authenticated');
    }
});

test('same-origin conditional account marker is recognized and login route stays unauthenticated', () => {
    const f = fixture('https://yolcu360.com/'); account(f);
    assert.equal(f.run({ action: 'state' }).state, 'authenticated');
    const login = fixture(); account(login);
    assert.notEqual(login.run({ action: 'state' }).state, 'authenticated');
});

test('conditional account marker in a collapsed responsive menu authenticates without opening the menu', () => {
    const f = fixture('https://yolcu360.com/');
    const menu = f.make('div', { id: 'headerMenuButton' });
    const collapsed = f.make('div'); collapsed.style.display = 'none';
    const link = f.make('a', { href: '/account' }).append(f.make('span', { class: 'logged-user' }));
    collapsed.append(link); f.body.append(menu, collapsed);
    for (let check = 0; check < 3; check++) assert.equal(f.run({ action: 'state' }).state, 'authenticated');
    assert.equal(menu.clicks, 0); assert.equal(link.clicks, 0);
    assert.equal(f.document.hit, null);
});

test('loading document cannot authenticate even when previous account markup is present', () => {
    const f = fixture('https://yolcu360.com/'); account(f);
    for (const readyState of ['loading', 'interactive']) {
        f.document.readyState = readyState;
        assert.equal(f.run({ action: 'state' }).state, 'loading');
    }
    f.document.readyState = 'complete'; assert.equal(f.run({ action: 'state' }).state, 'authenticated');
});

test('server-rendered account markup cannot authenticate until Nuxt hydration has completed', () => {
    const f = fixture('https://yolcu360.com/'); account(f);
    const root = f.make('div', { id: '__nuxt' }); f.body.append(root);
    assert.equal(f.run({ action: 'state' }).state, 'loading');
    root.__vue_app__ = { $nuxt: { isHydrating: true } };
    assert.equal(f.run({ action: 'state' }).state, 'loading');
    root.__vue_app__.$nuxt.isHydrating = false;
    assert.equal(f.run({ action: 'state' }).state, 'authenticated');
});

test('hydrated guest marker reports signed out without clicking login or country controls', () => {
    const f = fixture('https://yolcu360.com/');
    const root = f.make('div', { id: '__nuxt' });
    root.__vue_app__ = { config: { globalProperties: { $nuxt: { isHydrating: true } } } };
    const guest = f.make('button', { class: 'header-login' }, 'Giriş yap');
    f.body.append(root, guest);
    assert.equal(f.run({ action: 'state' }).state, 'loading');
    root.__vue_app__.config.globalProperties.$nuxt.isHydrating = false;
    assert.equal(f.run({ action: 'state' }).state, 'signed-out');
    assert.equal(guest.clicks, 0); assert.equal(f.document.hit, null);
});

test('session detection never fills a phone field or submits a retained phone form', () => {
    const f = fixture(), form = phoneForm(f, 'TR +90');
    form.phone.value = '5551234567';
    for (let check = 0; check < 3; check++) assert.equal(f.run({ action: 'state', phase: 'ready', phone: '5559999999' }).state, 'phone');
    assert.equal(form.phone.value, '5551234567'); assert.equal(form.phone.events.length, 0);
    assert.equal(form.button.clicks, 0); assert.equal(form.summary.clicks, 0);
});

test('session checks retain pending OTP without clearing, typing or verifying it', () => {
    const f = fixture(), otp = f.make('input', { id: 'sms_input' }); otp.value = '123456';
    const submit = f.make('button', { 'data-cms-key': 'button_apply' }, 'Doğrula'); f.body.append(otp, submit);
    account(f);
    for (let check = 0; check < 3; check++) assert.equal(f.run({ action: 'state', phase: 'ready' }).state, 'otp');
    assert.equal(otp.value, '123456'); assert.equal(otp.events.length, 0); assert.equal(submit.clicks, 0);
});

test('interactive challenge takes priority over conditional account markup and resumes after it closes', () => {
    const f = fixture('https://yolcu360.com/'); account(f);
    const root = f.make('div', { id: '__nuxt' }); root.__vue_app__ = { $nuxt: { isHydrating: true } }; f.body.append(root);
    const challenge = f.make('form', { id: 'challenge-form' }); f.body.append(challenge);
    assert.equal(f.run({ action: 'state' }).state, 'challenge'); assert.equal(challenge.clicks, 0);
    challenge.hidden = true;
    assert.equal(f.run({ action: 'state' }).state, 'loading');
    root.__vue_app__.$nuxt.isHydrating = false;
    assert.equal(f.run({ action: 'state' }).state, 'authenticated'); assert.equal(challenge.clicks, 0);
});

test('unexpected origins cannot fill phone or OTP', () => {
    for (const url of ['https://yolcu360.com.attacker.invalid/login', 'http://yolcu360.com/login']) {
        const f = fixture(url), form = phoneForm(f, 'TR +90');
        assert.equal(f.run({ action: 'fill-phone', phone: '5551234567' }).state, 'unexpected-origin');
        assert.equal(form.phone.value, '');
        const otp = f.make('input', { id: 'sms_input' }); f.body.append(otp);
        assert.equal(f.run({ action: 'fill-otp', otp: '123456' }).state, 'unexpected-origin');
        assert.equal(otp.value, '');
    }
});

test('a reCAPTCHA badge does not block the ready phone form', () => {
    const f = fixture(); phoneForm(f);
    const badge = f.make('div', { class: 'grecaptcha-badge' });
    const iframe = f.make('iframe', { src: 'https://www.google.com/recaptcha/api2/bframe', title: 'challenge' });
    iframe.rect.height = 200; badge.append(iframe); f.body.append(badge);
    assert.equal(f.run({ action: 'state', phase: 'ready' }).state, 'phone');
    assert.equal(iframe.clicks, 0);
});

test('an actual visible challenge is reported without clicking it', () => {
    const f = fixture(); phoneForm(f);
    const iframe = f.make('iframe', { src: 'https://www.google.com/recaptcha/api2/bframe', title: 'challenge' });
    iframe.rect.height = 250; f.body.append(iframe);
    assert.equal(f.run({ action: 'state', phase: 'ready' }).state, 'challenge');
    assert.equal(iframe.clicks, 0);
});

test('wrong country prevents entering or submitting the phone', () => {
    const f = fixture(), form = phoneForm(f); form.phone.value = '5551234567';
    assert.equal(f.run({ action: 'fill-phone', phone: '5551234567' }).state, 'country-missing');
    assert.equal(f.run({ action: 'submit-phone', phone: '5551234567' }).state, 'phone-mismatch');
    assert.equal(form.button.clicks, 0);
});

test('country selection, phone input events, readback and normal Continue all run', () => {
    const f = fixture(), form = phoneForm(f);
    assert.equal(f.run({ action: 'open-country' }).state, 'country-open');
    assert.equal(f.run({ action: 'select-country' }).state, 'country-changing');
    assert.equal(f.run({ action: 'select-country' }).state, 'country-ready');
    assert.equal(form.option.clicks, 1);
    assert.equal(f.run({ action: 'fill-phone', phone: '5551234567' }).state, 'phone-filled');
    assert.equal(form.phone.value, '5551234567');
    assert.deepEqual(form.phone.events.map(e => e.type), ['input', 'change']);
    assert.ok(form.phone.events.every(e => e.bubbles));
    assert.equal(f.run({ action: 'check-phone', phone: '5551234567' }).state, 'phone-verified');
    assert.equal(f.run({ action: 'submit-phone', phone: '5551234567' }).state, 'phone-submitted');
    assert.equal(form.button.clicks, 1);
});

test('mismatched, disabled, or covered controls do not submit', () => {
    const f = fixture(), form = phoneForm(f, 'TR +90'); form.phone.value = '5551234567';
    assert.equal(f.run({ action: 'submit-phone', phone: '5551234568' }).state, 'phone-mismatch');
    form.button.disabled = true;
    assert.equal(f.run({ action: 'submit-phone', phone: '5551234567' }).state, 'continue-unavailable');
    form.button.disabled = false; f.document.overlay = f.make('div');
    assert.equal(f.run({ action: 'submit-phone', phone: '5551234567' }).state, 'continue-unavailable');
    assert.equal(form.button.clicks, 0);
});

test('OTP transfers via input/change events, validates readback, submits and clears', () => {
    const f = fixture(), otp = f.make('input', { id: 'sms_input' });
    const submit = f.make('button', { 'data-cms-key': 'button_apply' }, 'Doğrula'); f.body.append(otp, submit);
    assert.equal(f.run({ action: 'state', phase: 'ready' }).state, 'otp');
    assert.equal(f.run({ action: 'fill-otp', otp: '123456' }).state, 'otp-filled');
    assert.equal(otp.value, '123456');
    assert.deepEqual(otp.events.map(e => e.type), ['input', 'change']);
    assert.equal(f.run({ action: 'check-otp', otp: '123456' }).state, 'otp-verified');
    assert.equal(f.run({ action: 'submit-otp', otp: '654321' }).state, 'otp-mismatch');
    assert.equal(submit.clicks, 0);
    assert.equal(f.run({ action: 'submit-otp', otp: '123456' }).state, 'otp-submitted');
    assert.equal(submit.clicks, 1);
    assert.equal(f.run({ action: 'clear-otp' }).state, 'cleared');
    assert.equal(otp.value, '');
    assert.deepEqual(otp.events.slice(-2).map(e => e.type), ['input', 'change']);
});

test('six separate OTP inputs receive one digit each', () => {
    const f = fixture(), boxes = Array.from({ length: 6 }, () => f.make('input', { maxlength: '1' }));
    f.body.append(...boxes);
    assert.equal(f.run({ action: 'fill-otp', otp: '123456' }).state, 'otp-filled');
    assert.equal(boxes.map(e => e.value).join(''), '123456');
    assert.ok(boxes.every(e => e.events.map(event => event.type).join(',') === 'input,change'));
    assert.equal(f.run({ action: 'check-otp', otp: '123456' }).state, 'otp-verified');
    f.run({ action: 'clear-otp' });
    assert.ok(boxes.every(e => e.value === ''));
});

test('semantic error alerts and expiration are returned as errors, not success', () => {
    const f = fixture(), alert = f.make('p', { role: 'alert' }, 'Geçersiz kod'); f.body.append(alert);
    assert.equal(f.run({ action: 'state', phase: 'verify' }).state, 'invalid');
    alert.innerText = 'Kodun süresi doldu';
    assert.equal(f.run({ action: 'state', phase: 'verify' }).state, 'expired');
});
