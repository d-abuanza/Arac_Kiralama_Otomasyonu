(request => {
    // Each action is synchronous; C# waits between Vue renders and never retries a submitted search.
    const visible = e => {
        if (!e || !e.isConnected || e.closest('[inert],[hidden]')) return false;
        const r = e.getBoundingClientRect(), s = getComputedStyle(e);
        return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none';
    };
    const usable = e => visible(e) && !e.disabled && e.getAttribute('aria-disabled') !== 'true';
    const interact = e => {
        if (!usable(e)) return false;
        e.scrollIntoView({ block: 'center', inline: 'nearest' });
        const r = e.getBoundingClientRect();
        const hit = document.elementFromPoint(r.x + r.width / 2, r.y + r.height / 2);
        return !!hit && (hit === e || e.contains(hit));
    };
    const click = e => { if (!interact(e)) return false; e.click(); return true; };
    const text = e => (e?.textContent || '').replace(/\s+/g, ' ').trim();
    const norm = s => (s || '').toLocaleLowerCase('tr-TR').normalize('NFD')
        .replace(/[\u0300-\u036f]/g, '').replace(/ı/g, 'i').replace(/havalimani|airport/g, 'airport').replace(/\s+/g, ' ').trim();
    if (location.protocol !== 'https:' || !['yolcu360.com','www.yolcu360.com'].includes(location.hostname)) return { state: 'wrong-origin' };
    const challenge = () => visible(document.querySelector('#challenge-form')) || [...document.querySelectorAll('iframe')].some(e =>
        !e.closest('.grecaptcha-badge') && /recaptcha|hcaptcha/.test(e.src) && /bframe|challenge/i.test(e.src + e.title) && visible(e) && e.getBoundingClientRect().height > 100);
    if (challenge()) return { state: 'challenge' };
    if (/\/login\/?$/.test(location.pathname) || visible(document.querySelector('#phn-input,#sms_input'))) return { state: 'login-required' };
    const root = document.querySelector('#search')?.closest('.flight-search-bar-wrapper');
    const loc = root?.querySelector('#inputPickUpLocation');
    const layer = document.querySelector('#location-layer[data-field="pickup"]');
    const calendar = [...document.querySelectorAll('.modal-overlay')].find(e => e.querySelector('.cal-month'));
    const calendarEndpoints = () => [...(calendar?.querySelectorAll('td.endpoint[data-day]') || [])].map(e=>e.getAttribute('data-day'));
    const hasReturnDate = () => !!calendar?.querySelector('[data-cms-key="date_picker_dropoff_date"]');
    const wanted = norm(request.location);
    const acceptedNames = (request.locationNames || [request.location]).map(norm).filter(Boolean);
    const wantedCity = norm(request.locationCity);
    const cityMatches = value => !wantedCity || String(value || '').split(/[\r\n,/()\-]+/).map(norm).includes(wantedCity);
    const selection = () => window.__carRentalPickupSelection;
    const locationMatches = () => !!wanted && selection()?.operationId === request.operationId
        && selection().location === request.location && norm(loc?.value) === selection().pageName
        && acceptedNames.includes(norm(loc?.value)) && !visible(layer)
        && !visible(root?.querySelector('[data-cms-key="location_layer_selection_error"]'));
    const dates = [...(root?.querySelectorAll('[data-cms-key="pickup_date"]') || [])];
    const times = [...(root?.querySelectorAll('span') || [])].filter(e => !e.children.length && /^(Alış|Bırakış) Saati$/.test(text(e)));
    const timeValue = i => times[i]?.parentElement?.querySelector('.cursor-pointer');
    const timeMatches = () => text(timeValue(0)) === request.pickupTime && text(timeValue(1)) === request.returnTime;
    const dateMatches = (i, iso) => {
        const date = new Date(iso + 'T12:00:00');
        if (Number.isNaN(date.getTime())) return false;
        const month = new Intl.DateTimeFormat('tr-TR', { month: 'short' }).format(date).replace('.', '');
        return norm(text(dates[i])).startsWith(norm(date.getDate() + ' ' + month));
    };
    const formMatches = () => locationMatches() && timeMatches() && dateMatches(0,request.pickupDate) && dateMatches(1,request.returnDate) && !visible(calendar);
    const accountMarker = [...document.querySelectorAll('a[href]')].some(a => {
        const u = new URL(a.href, location.href);
        return u.origin === location.origin && /^\/account\/?$/.test(u.pathname) && !!a.querySelector('.header-user-icon-wrap,.logged-user');
    });
    switch (request.action) {
        case 'session': return { state: accountMarker && document.readyState === 'complete' ? 'authenticated' : 'session-pending' };
        case 'form-ready': return { state: visible(loc) && usable(root?.querySelector('#search')) && dates.length === 2 && times.length === 2 ? 'ready' : 'form-pending' };
        case 'location-open':
            if (visible(layer)) return {state:'location-open'};
            delete window.__carRentalPickupSelection;
            return { state: click(root?.querySelector('#inputPickUpLocation-label')) ? 'location-opening' : 'location-control-missing' };
        case 'location-type': {
            const input = layer?.querySelector('input[role="combobox"]');
            if (!interact(input) || input.readOnly) return { state: 'location-control-missing' };
            delete window.__carRentalPickupSelection;
            const query = request.locationQuery || request.location;
            input.focus();
            Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,query);
            input.dispatchEvent(new InputEvent('input',{bubbles:true,inputType:'insertText',data:query}));
            return { state: 'location-typed' };
        }
        case 'location-select': {
            if (locationMatches()) return { state: 'location-selected' };
            const options = [...(layer?.querySelectorAll('[role="option"]') || [])].filter(e => usable(e) && !e.classList.contains('location-layer__row--history'));
            const placeName = e => text(e.querySelector('.location-layer__row-title')) || (e.innerText || '').split('\n')[0].trim();
            const exact = options.filter(e => acceptedNames.includes(norm(placeName(e))) && cityMatches(e.innerText || ''));
            if (exact.length > 1) return { state: 'location-ambiguous' };
            if (!exact.length) return { state: 'location-option-pending' };
            const pageName = norm(placeName(exact[0]));
            if (!click(exact[0])) return { state: 'location-option-pending' };
            window.__carRentalPickupSelection = { operationId:request.operationId, location:request.location, pageName };
            return { state: 'location-clicked' };
        }
        case 'location-check': return { state: locationMatches() ? 'location-selected' : 'location-pending' };
        case 'calendar-open':
            if (visible(calendar)) return { state:'calendar-open' };
            return { state: click(dates[0]?.parentElement?.parentElement) ? 'calendar-opening' : 'calendar-control-missing' };
        case 'calendar-pickup-day':
        case 'calendar-return-day': {
            const pickup = request.action === 'calendar-pickup-day';
            const iso = pickup ? request.pickupDate : request.returnDate;
            const endpoints = calendarEndpoints();
            const start = endpoints[0], end = endpoints.at(-1);
            const hasReturn = hasReturnDate();
            if (pickup && start === iso && !hasReturn) return {state:'pickup-date-selected'};
            if (!pickup && start === request.pickupDate && end === iso && hasReturn) return {state:'return-date-selected'};
            const day = calendar?.querySelector(`td[data-day="${iso}"]`);
            if (!day) return {state:'date-outside-calendar'};
            if (day.matches('.past,.disabled,[aria-disabled="true"]')) return {state:'date-unavailable'};
            click(day);
            return {state:'calendar-day-pending'};
        }
        case 'calendar-check': return {state: hasReturnDate() && calendarEndpoints()[0] === request.pickupDate
            && calendarEndpoints().at(-1) === request.returnDate ? 'dates-verified':'dates-pending'};
        case 'calendar-confirm': return { state:click(calendar?.querySelector('[data-cms-key="date_picker_confirm"]')?.closest('button')) ? 'dates-confirmed':'calendar-control-missing' };
        case 'pickup-time':
        case 'return-time': {
            const i = request.action === 'pickup-time' ? 0 : 1;
            const wantedTime = i === 0 ? request.pickupTime : request.returnTime;
            if (text(timeValue(i)) === wantedTime) return {state:'time-selected'};
            const option = [...(times[i]?.parentElement?.querySelectorAll('li') || [])].find(e => text(e) === wantedTime);
            if (option) return {state:click(option) ? 'time-clicked':'time-option-pending'};
            return {state:click(timeValue(i)) ? 'time-opening':'time-control-missing'};
        }
        case 'form-check': return {state:formMatches() ? 'ready':'incomplete'};
        case 'submit':
            if (!formMatches()) return {state:'incomplete'};
            if (window.__carRentalSearchSubmitted === request.operationId) return {state:'already-submitted'};
            if (!interact(root?.querySelector('#search'))) return {state:'search-button-unavailable'};
            window.__carRentalSearchSubmitted = request.operationId;
            root.querySelector('#search').click();
            return {state:'submitted'};
        case 'results-state': {
            if (!/^\/arac-kiralama\/search\/?$/.test(location.pathname)) return {state:'navigation-pending'};
            const cards = [...document.querySelectorAll('.car-card')].filter(e => e.querySelector('[id="car_total_price"]'));
            const countLabel = [...document.querySelectorAll('[data-cms-key="text_car_listing"]')].find(visible);
            const countText = countLabel?.tagName === 'SPAN'
                ? [...countLabel.childNodes].filter(n=>n.nodeType===Node.TEXT_NODE).map(n=>n.textContent).join(' ')
                : text(countLabel?.previousElementSibling);
            const body = document.body.innerText || '';
            const total = /\d/.test(countText || '') ? Number(countText.replace(/\D/g,'')) : null;
            const busy = [...document.querySelectorAll('[data-cms-key="loading_title"],[aria-busy="true"],[class*="skeleton"]')].some(visible);
            const error = [...document.querySelectorAll('[role="alert"],[data-cms-key*="error"],[data-cms-key*="no_car"],[data-cms-key*="no_result"]')].find(e=>visible(e) && text(e));
            if (error && /hata|error|tekrar deney|try again|bağlantı/i.test(text(error))) return {state:'site-error'};
            if (!busy && !cards.length && (visible(document.querySelector('[data-cms-key="loading_search_result_title"]')) || total === 0 || /araç bulunamadı|sonuç bulunamadı|no (cars|results)|uygun araç bulun/i.test(body))) return {state:'empty',total:0,count:0};
            const signature = cards.map(e=>e.querySelector('a[href*="/detail"]')?.getAttribute('href') + '|' + text(e.querySelector('[id="car_total_price"]'))).join('\n');
            return {state:cards.length && !busy ? 'results':'loading',count:cards.length,total,signature};
        }
        case 'scroll-results': {
            const cards=[...document.querySelectorAll('.car-card')];
            const last=cards.at(-1);
            if (last) {
                last.scrollIntoView({block:'end'});
                window.dispatchEvent(new Event('scroll'));
            }
            const more=[...document.querySelectorAll('button')].find(e=>visible(e) && /^(daha fazla (göster|yükle)|load more|show more)$/i.test(text(e)));
            if (more) click(more);
            return {state:'scrolled'};
        }
        case 'extract': return {state:'extracted',rows:[...document.querySelectorAll('.car-card')].map(n=>{
            const lines=(n.innerText||'').split('\n').map(x=>x.trim()).filter(Boolean);
            const model=text(n.querySelector('.text-dark-gray.text-lg.font-bold')) || lines.find((x,i)=>lines[i+1] === 'veya benzeri') || '';
            const company=[...n.querySelectorAll('img[alt]')].find(e=>/\/supplier\//.test(e.src))?.alt || '';
            const priceNodes=[...n.querySelectorAll('[id="car_total_price"]')];
            const feature=key=>text([...n.querySelectorAll(`[data-cms-key^="${key}"]`)].find(visible));
            return {model,company,transmission:feature('filter_transmission_') || lines.find(x=>/^(Otomatik|Manuel|Automatic|Manual)$/i.test(x))||'',
                fuel:feature('filter_fuel_') || lines.find(x=>/^(Benzin|Dizel|Benzin\/Dizel|Hibrit|Elektrik|Hybrid|Petrol|Diesel|Electric)$/i.test(x))||'',
                price:text(priceNodes.find(visible)||priceNodes[0]),url:n.querySelector('a[href*="/detail"]')?.href || null};
        })};
        default: return {state:'unknown-action'};
    }
})
