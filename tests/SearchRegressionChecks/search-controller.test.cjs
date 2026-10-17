const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

// Run the actual production controller against small synthetic DOM fixtures.
// No page, account, network, API or database is accessed. This is not a browser substitute.
const source = fs.readFileSync(path.join(__dirname, '../../CarRentalAutomation.Infrastructure/Yolcu360SearchPage.js'), 'utf8');
function fixture(url = 'https://yolcu360.com/arac-kiralama/search') {
    const location = new URL(url);
    let document;
    class Element {
        constructor(tag, attrs = {}, text = '') {
            this.tagName = tag.toUpperCase(); this.attrs = { ...attrs }; this._text = text;
            this.children = []; this.parentElement = null; this.isConnected = true;
            this.style = { display: 'block', visibility: 'visible' }; this.disabled = false;
            this.rect = { x: 10, y: 10, width: 200, height: 32 }; this.clicks = 0;
            this.childNodes = text ? [{ nodeType: 3, textContent: text }] : [];
            this.classList = { contains: name => (this.attrs.class || '').split(/\s+/).includes(name) };
        }
        append(...children) { for (const child of children) { child.parentElement = this; this.children.push(child); } return this; }
        get textContent() { return [this._text, ...this.children.map(e => e.textContent)].filter(Boolean).join(' '); }
        get innerText() { return [this._text, ...this.children.map(e => e.innerText)].filter(Boolean).join('\n'); }
        get src() { return this.attrs.src || ''; }
        get title() { return this.attrs.title || ''; }
        get alt() { return this.attrs.alt || ''; }
        get href() { return new URL(this.attrs.href || '', location.href).href; }
        get previousElementSibling() { return this.parentElement?.children[this.parentElement.children.indexOf(this) - 1] || null; }
        getAttribute(name) { return this.attrs[name] ?? null; }
        matches(selector) { return selector.split(',').some(part => matches(this, part.trim())); }
        closest(selector) { for (let e = this; e; e = e.parentElement) if (e.matches(selector)) return e; return null; }
        contains(other) { return other === this || this.children.some(child => child.contains(other)); }
        querySelectorAll(selector) {
            return this.children.flatMap(e => [...(e.matches(selector) ? [e] : []), ...e.querySelectorAll(selector)]);
        }
        querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
        getBoundingClientRect() { return this.rect; }
        scrollIntoView() { document.hit = this; }
        click() { this.clicks++; }
        focus() { this.focused = true; }
        dispatchEvent(event) { (this.events ||= []).push(event); return true; }
    }
    function matches(e, selector) {
        const attrs = [...selector.matchAll(/\[([^\]=*^]+)([\^*]?=)?(?:(?:"([^"]*)")|(?:'([^']*)')|([^\]]*))?\]/g)];
        const bare = selector.replace(/\[[^\]]*\]/g, '');
        const tag = bare.match(/^[a-z][\w-]*/i)?.[0];
        if (tag && e.tagName !== tag.toUpperCase()) return false;
        const id = bare.match(/#([\w-]+)/)?.[1];
        if (id && e.attrs.id !== id) return false;
        for (const match of bare.matchAll(/\.([\w-]+)/g)) if (!e.classList.contains(match[1])) return false;
        for (const [, name, op, double, single, unquoted] of attrs) {
            const value = double ?? single ?? unquoted;
            if (!(name in e.attrs)) return false;
            if (op === '=' && String(e.attrs[name]) !== value) return false;
            if (op === '*=' && !String(e.attrs[name]).includes(value)) return false;
            if (op === '^=' && !String(e.attrs[name]).startsWith(value)) return false;
        }
        return true;
    }
    const make = (tag, attrs = {}, text = '') => new Element(tag, attrs, text);
    const body = make('body');
    document = { body, readyState: 'complete', hit: null, overlay: null,
        querySelector: selector => body.querySelector(selector), querySelectorAll: selector => body.querySelectorAll(selector),
        elementFromPoint: () => document.overlay || document.hit };
    const inputPrototype = {};
    Object.defineProperty(inputPrototype, 'value', { set(value) { this.value = value; } });
    const context = vm.createContext({ document, location, window: {}, URL, Node: { TEXT_NODE: 3 },
        HTMLInputElement: { prototype: inputPrototype }, InputEvent: class { constructor(type, options) { this.type = type; Object.assign(this, options); } },
        getComputedStyle: e => e.style });
    const controller = vm.runInContext(source, context, { filename: 'Yolcu360SearchPage.js' });
    const request = { location: 'Airport Test', pickupDate: '2030-12-05', returnDate: '2030-12-08', pickupTime: '09:30', returnTime: '17:00', operationId: 'operation-1' };
    return { make, body, document, context, run: (action, extra = {}) => controller({ ...request, action, ...extra }) };
}
function card(f, { price = '7.706 TL', model = 'Fiat Egea', company = 'Supplier Fixture', attributes = true, link = true } = {}) {
    const node = f.make('div', { class: 'car-card' });
    node.append(f.make('span', {}, 'Ücretsiz İptal'), f.make('h3', { class: 'text-dark-gray text-lg font-bold' }, model));
    if (company) node.append(f.make('img', { src: 'https://cdn.invalid/supplier/fixture.png', alt: company }));
    if (attributes) node.append(f.make('span', { 'data-cms-key': 'filter_transmission_automatic' }, 'Otomatik'), f.make('span', { 'data-cms-key': 'filter_fuel_petrol' }, 'Benzin'));
    const hiddenPrice = f.make('span', { id: 'car_total_price' }, '999.999 TL'); hiddenPrice.style.display = 'none';
    node.append(hiddenPrice, f.make('span', { id: 'car_total_price' }, price));
    if (link) node.append(f.make('a', { href: '/arac-kiralama/detail/synthetic-1' }, 'Seç'));
    f.body.append(node); return node;
}
function form(f) {
    const root = f.make('div', { class: 'flight-search-bar-wrapper' });
    const search = f.make('button', { id: 'search' }, 'Ara');
    const loc = f.make('input', { id: 'inputPickUpLocation' }); loc.value = 'Airport Test';
    root.append(search, loc);
    for (const [label, value] of [['Alış Saati', '09:30'], ['Bırakış Saati', '17:00']])
        root.append(f.make('div').append(f.make('span', {}, label), f.make('span', { class: 'cursor-pointer' }, value)));
    root.append(f.make('span', { 'data-cms-key': 'pickup_date' }, '5 Ara, Per'), f.make('span', { 'data-cms-key': 'pickup_date' }, '8 Ara, Paz'));
    f.context.window.__carRentalPickupSelection = { operationId: 'operation-1', location: 'Airport Test', pageName: 'airport test' };
    f.body.append(root); return { root, search, loc };
}
function locationLayer(f, names) {
    const layer = f.make('div', { id: 'location-layer', 'data-field': 'pickup' });
    const input = f.make('input', { role: 'combobox' });
    const options = names.map(({ name, city = 'İstanbul, Türkiye', disabled = false, history = false, hidden = false }) => {
        const option = f.make('button', { role: 'option', class: 'location-layer__row ' + (history ? 'location-layer__row--history' : 'location-layer__row--result') });
        option.append(f.make('span', {}, '').append(f.make('span', { class: 'location-layer__row-title' }, name), f.make('span', {}, city)));
        option.disabled = disabled; if (hidden) option.style.display = 'none'; return option;
    });
    layer.append(input, ...options); f.body.append(layer); return { layer, input, options };
}
function calendar(f, { endpoints = ['2030-12-05'], hasReturn = false } = {}) {
    const overlay = f.make('div', { class: 'modal-overlay' });
    const month = f.make('table', { class: 'cal-month' });
    const days = endpoints.map(iso => f.make('td', { class: 'endpoint', 'data-day': iso }, iso.slice(-2)));
    month.append(...days); overlay.append(month);
    if (hasReturn) overlay.append(f.make('span', { 'data-cms-key': 'date_picker_dropoff_date' }, 'Return date'));
    f.body.append(overlay); return { overlay, month, days };
}

test('extracts vehicle and supplier fields separately from promotion, prefers visible total price', () => {
    const f = fixture(); card(f);
    const result = f.run('extract');
    assert.equal(result.state, 'extracted'); assert.equal(result.rows.length, 1);
    const row = result.rows[0];
    assert.equal(row.model, 'Fiat Egea'); assert.equal(row.company, 'Supplier Fixture');
    assert.equal(row.price, '7.706 TL'); assert.equal(row.transmission, 'Otomatik'); assert.equal(row.fuel, 'Benzin');
    assert.equal(row.url, 'https://yolcu360.com/arac-kiralama/detail/synthetic-1');
});
test('missing supplier, attributes and detail link stay empty rather than become invented records', () => {
    const f = fixture(); card(f, { company: '', attributes: false, link: false });
    const row = f.run('extract').rows[0];
    assert.equal(row.company, ''); assert.equal(row.transmission, ''); assert.equal(row.fuel, ''); assert.equal(row.url, null);
});
test('zero result count is detected explicitly without fabricating offers', () => {
    const f = fixture(); f.body.append(f.make('span', { 'data-cms-key': 'text_car_listing' }, '0 araç'));
    const result = f.run('results-state'); assert.equal(result.state, 'empty'); assert.equal(result.total, 0); assert.equal(result.count, 0);
});
test('reads total count separately from loaded cards and waits through visible loading state', () => {
    const f = fixture(); card(f); f.body.append(f.make('span', { 'data-cms-key': 'text_car_listing' }, '125 araç'));
    const loaded = f.run('results-state'); assert.equal(loaded.state, 'results'); assert.equal(loaded.total, 125); assert.equal(loaded.count, 1);
    const busy = f.make('div', { 'aria-busy': 'true' }); f.body.append(busy);
    assert.equal(f.run('results-state').state, 'loading');
    busy.style.display = 'none'; assert.equal(f.run('results-state').state, 'results');
});
test('home-page or stale previous cards cannot count as search results', () => {
    const f = fixture('https://yolcu360.com/'); card(f);
    assert.equal(f.run('results-state').state, 'navigation-pending');
});
test('foreign origins and login redirects cannot be treated as live results', () => {
    for (const url of ['http://yolcu360.com/arac-kiralama/search', 'https://yolcu360.com.evil.invalid/arac-kiralama/search', 'https://elsewhere.invalid/']) {
        const f = fixture(url); card(f); assert.equal(f.run('extract').state, 'wrong-origin');
    }
    const f = fixture('https://yolcu360.com/login'); card(f); assert.equal(f.run('extract').state, 'login-required');
});
test('only conditional account markup on the same site confirms the session', () => {
    const f = fixture('https://yolcu360.com/');
    const generic = f.make('a', { href: '/account' }); f.body.append(generic);
    assert.equal(f.run('session').state, 'session-pending');
    const offsite = f.make('a', { href: 'https://other.invalid/account' }).append(f.make('span', { class: 'logged-user' })); f.body.append(offsite);
    assert.equal(f.run('session').state, 'session-pending');
    generic.append(f.make('span', { class: 'logged-user' })); assert.equal(f.run('session').state, 'authenticated');
});
test('interactive security challenge is reported; badge alone does not block search', () => {
    const f = fixture(); card(f);
    const badge = f.make('div', { class: 'grecaptcha-badge' }).append(f.make('iframe', { src: 'https://google.invalid/recaptcha/anchor', title: 'reCAPTCHA' })); f.body.append(badge);
    assert.equal(f.run('results-state').state, 'results');
    const frame = f.make('iframe', { src: 'https://google.invalid/recaptcha/bframe', title: 'challenge' }); frame.rect.height = 400; f.body.append(frame);
    assert.equal(f.run('results-state').state, 'challenge');
});
test('invalid form and disabled or covered search button never submit', () => {
    const empty = fixture('https://yolcu360.com/'); assert.equal(empty.run('submit').state, 'incomplete');
    const f = fixture('https://yolcu360.com/'); const { search } = form(f);
    assert.equal(f.run('form-check').state, 'ready');
    search.disabled = true; assert.equal(f.run('submit').state, 'search-button-unavailable'); assert.equal(search.clicks, 0);
    search.disabled = false; f.document.overlay = f.make('div');
    assert.equal(f.run('submit').state, 'search-button-unavailable'); assert.equal(search.clicks, 0);
});
test('a verified form submits once per operation even if submit is requested again', () => {
    const f = fixture('https://yolcu360.com/'); const { search } = form(f);
    assert.equal(f.run('submit').state, 'submitted'); assert.equal(f.run('submit').state, 'already-submitted'); assert.equal(search.clicks, 1);
});
test('matching text without a selected website suggestion cannot satisfy the form', () => {
    const f = fixture('https://yolcu360.com/'); const { search } = form(f);
    delete f.context.window.__carRentalPickupSelection;
    assert.equal(f.run('form-check').state, 'incomplete');
    assert.equal(f.run('submit').state, 'incomplete'); assert.equal(search.clicks, 0);
});
const mappedLocations = [
    { location: 'İstanbul - Sabiha Gökçen Havalimanı', locationQuery: 'Sabiha Gökçen Havalimanı', locationNames: ['Sabiha Gökçen Havalimanı'], locationCity: 'İstanbul', pageName: 'Sabiha Gökçen Havalimanı' },
    { location: 'Ankara - Ankara Esenboğa Havalimanı', locationQuery: 'Ankara Esenboğa Havalimanı', locationNames: ['Ankara Esenboğa Havalimanı', 'Esenboğa Havalimanı'], locationCity: 'Ankara', pageName: 'Esenboğa Havalimanı' },
    { location: 'İzmir - İzmir Adnan Menderes Havalimanı', locationQuery: 'İzmir Adnan Menderes Havalimanı', locationNames: ['İzmir Adnan Menderes Havalimanı', 'Adnan Menderes Havalimanı'], locationCity: 'İzmir', pageName: 'Adnan Menderes Havalimanı' },
    { location: 'İstanbul - İstanbul Havalimanı', locationQuery: 'İstanbul Havalimanı', locationNames: ['İstanbul Havalimanı'], locationCity: 'İstanbul', pageName: 'İstanbul Havalimanı' },
    { location: 'İstanbul - Kadıköy', locationQuery: 'Kadıköy', locationNames: ['Kadıköy'], locationCity: 'İstanbul', pageName: 'Kadıköy' },
    { location: 'İstanbul - Yenibosna', locationQuery: 'Yenibosna', locationNames: ['Yenibosna'], locationCity: 'İstanbul', pageName: 'Yenibosna' },
    { location: 'İzmir - Alsancak', locationQuery: 'Alsancak', locationNames: ['Alsancak'], locationCity: 'İzmir', pageName: 'Alsancak' }
];
for (const mapped of mappedLocations) test(`mapped pickup selects the exact city/name and verifies the rendered value: ${mapped.location}`, () => {
    const f = fixture('https://yolcu360.com/'); const { loc } = form(f);
    const { layer, input, options } = locationLayer(f, [
        { name: mapped.pageName + ' AVM', city: mapped.locationCity + ', Türkiye' },
        { name: mapped.pageName, city: mapped.locationCity + ', Türkiye' }
    ]);
    assert.equal(f.run('location-type', mapped).state, 'location-typed');
    assert.equal(input.value, mapped.locationQuery);
    assert.equal(input.events[0].type, 'input'); assert.equal(input.events[0].bubbles, true);
    assert.equal(f.run('location-select', mapped).state, 'location-clicked');
    assert.equal(options[0].clicks, 0); assert.equal(options[1].clicks, 1);
    assert.equal(f.run('location-check', mapped).state, 'location-pending');
    loc.value = mapped.pageName; layer.style.display = 'none';
    assert.equal(f.run('location-check', mapped).state, 'location-selected');
    assert.equal(f.run('form-check', mapped).state, 'ready');
    assert.equal(f.run('location-check', { ...mapped, operationId: 'another-operation' }).state, 'location-pending');
});
test('same place name in a different city, history rows, hidden rows and disabled rows are never chosen', () => {
    const f = fixture('https://yolcu360.com/'); form(f);
    const mapped = mappedLocations.at(-1);
    const { options } = locationLayer(f, [
        { name: 'Alsancak', city: 'Lefkoşa, Kıbrıs' },
        { name: 'Alsancak', city: 'İzmir, Türkiye', history: true },
        { name: 'Alsancak', city: 'İzmir, Türkiye', disabled: true },
        { name: 'Alsancak', city: 'İzmir, Türkiye', hidden: true }
    ]);
    assert.equal(f.run('location-select', mapped).state, 'location-option-pending');
    assert.equal(options.reduce((sum, e) => sum + e.clicks, 0), 0);
});
test('duplicate exact suggestions stop rather than choose the first, and overlays prevent clicks', () => {
    const mapped = mappedLocations[4];
    const f = fixture('https://yolcu360.com/'); form(f);
    const { options } = locationLayer(f, [{ name: 'Kadıköy' }, { name: 'Kadıköy' }]);
    assert.equal(f.run('location-select', mapped).state, 'location-ambiguous');
    assert.equal(options[0].clicks + options[1].clicks, 0);
    options[1].disabled = true; f.document.overlay = f.make('div');
    assert.equal(f.run('location-select', mapped).state, 'location-option-pending'); assert.equal(options[0].clicks, 0);
});
test('selection errors and a changed final input prevent submitting even after a valid click', () => {
    const mapped = mappedLocations[3];
    const f = fixture('https://yolcu360.com/'); const { root, loc, search } = form(f);
    const { layer } = locationLayer(f, [{ name: mapped.pageName }]);
    assert.equal(f.run('location-select', mapped).state, 'location-clicked');
    layer.style.display = 'none'; loc.value = 'Sabiha Gökçen Havalimanı';
    assert.equal(f.run('submit', mapped).state, 'incomplete'); assert.equal(search.clicks, 0);
    loc.value = mapped.pageName;
    const error = f.make('div', { 'data-cms-key': 'location_layer_selection_error' }, 'Selection failed'); root.append(error);
    assert.equal(f.run('submit', mapped).state, 'incomplete'); assert.equal(search.clicks, 0);
    error.style.display = 'none'; assert.equal(f.run('form-check', mapped).state, 'ready');
});
test('a lone selected pickup endpoint without a return header completes pickup without clicking again', () => {
    const f = fixture('https://yolcu360.com/'); const { days } = calendar(f);
    assert.equal(f.run('calendar-pickup-day').state, 'pickup-date-selected');
    assert.equal(days[0].clicks, 0);
    assert.equal(f.run('calendar-check').state, 'dates-pending');
});
test('distinct selected endpoints and the return header verify the complete calendar range', () => {
    const f = fixture('https://yolcu360.com/'); const { days } = calendar(f, { endpoints: ['2030-12-05', '2030-12-08'], hasReturn: true });
    assert.equal(f.run('calendar-return-day').state, 'return-date-selected');
    assert.equal(f.run('calendar-check').state, 'dates-verified');
    assert.equal(days[0].clicks + days[1].clicks, 0);
});
test('one same-day endpoint requires the return header before both selected dates are verified', () => {
    const f = fixture('https://yolcu360.com/'); const { overlay, days } = calendar(f);
    const sameDay = { returnDate: '2030-12-05' };
    assert.equal(f.run('calendar-check', sameDay).state, 'dates-pending');
    overlay.append(f.make('span', { 'data-cms-key': 'date_picker_dropoff_date' }, 'Return date'));
    assert.equal(f.run('calendar-return-day', sameDay).state, 'return-date-selected');
    assert.equal(f.run('calendar-check', sameDay).state, 'dates-verified');
    assert.equal(f.run('calendar-check').state, 'dates-pending');
    assert.equal(days[0].clicks, 0);
});
test('distinct endpoint dates without a return header cannot report complete selection', () => {
    const f = fixture('https://yolcu360.com/'); calendar(f, { endpoints: ['2030-12-05', '2030-12-08'] });
    assert.equal(f.run('calendar-check').state, 'dates-pending');
});
test('stale endpoint dates and range cap decorations cannot satisfy requested calendar dates', () => {
    const f = fixture('https://yolcu360.com/'); const { month } = calendar(f, { endpoints: ['2030-12-04', '2030-12-07'], hasReturn: true });
    month.append(f.make('td', { class: 'range-start-cap', 'data-day': '2030-12-05' }), f.make('td', { class: 'range-end-cap', 'data-day': '2030-12-08' }));
    assert.equal(f.run('calendar-check').state, 'dates-pending');
});
test('unavailable or absent days are reported without clicking any selected endpoint', () => {
    const f = fixture('https://yolcu360.com/'); const { month, days } = calendar(f);
    assert.equal(f.run('calendar-return-day').state, 'date-outside-calendar');
    const unavailable = f.make('td', { class: 'disabled', 'data-day': '2030-12-08' }); month.append(unavailable);
    assert.equal(f.run('calendar-return-day').state, 'date-unavailable');
    assert.equal(days[0].clicks + unavailable.clicks, 0);
});
