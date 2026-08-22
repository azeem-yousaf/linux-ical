const eventsHost = document.querySelector('#events');
const connectButton = document.querySelector('#connect-button');
const connectDialog = document.querySelector('#connect-dialog');
const connectForm = document.querySelector('#connect-form');
const accountsDialog = document.querySelector('#accounts-dialog');
const accountList = document.querySelector('#account-list');
const accountStatus = document.querySelector('#account-status');
const addAccountButton = document.querySelector('#add-account');
const credentialTitle = document.querySelector('#credential-dialog-title');
const credentialCopy = document.querySelector('#credential-dialog-copy');
const selectedDateHost = document.querySelector('#selected-date');
const agendaTitle = document.querySelector('#agenda-title');
const greeting = document.querySelector('#greeting');
const viewSwitcher = document.querySelector('#view-switcher');
const previousPeriodButton = document.querySelector('#previous-period');
const nextPeriodButton = document.querySelector('#next-period');
const todayButton = document.querySelector('#today-button');
const syncNowButton = document.querySelector('#header-sync');
const syncState = document.querySelector('#sync-state');
const syncDetail = document.querySelector('#sync-detail');
const createEventButton = document.querySelector('#create-event');
const calendarFilterButton = document.querySelector('#calendar-filter');
const calendarsDialog = document.querySelector('#calendars-dialog');
const calendarVisibilityList = document.querySelector('#calendar-visibility-list');
const calendarVisibilityStatus = document.querySelector('#calendar-visibility-status');
const showAllCalendarsButton = document.querySelector('#show-all-calendars');
const hideAllCalendarsButton = document.querySelector('#hide-all-calendars');
const eventDialog = document.querySelector('#event-dialog');
const eventForm = document.querySelector('#event-form');
const updateBanner = document.querySelector('#update-banner');
const updateCopy = document.querySelector('#update-copy');
const viewRelease = document.querySelector('#view-release');
const installUpdate = document.querySelector('#install-update');
const appVersion = document.querySelector('#app-version');
const addressSuggestions = document.querySelector('#address-suggestions');
const eventEndField = document.querySelector('#event-end-field');
const startLabel = document.querySelector('#start-label');
const eventDialogEyebrow = document.querySelector('#event-dialog-eyebrow');
const eventDialogTitle = document.querySelector('#event-dialog-title');
const eventDialogCopy = document.querySelector('#event-dialog-copy');
const eventSubmit = document.querySelector('#event-submit');
const eventDelete = document.querySelector('#event-delete');
let selectedDay = new Date();
let connectedAccounts = [];
let currentEvents = [];
let lastAgendaPayload = { events: [] };
let hiddenCalendarIds = new Set();
let editingEvent = null;
let calendarView = 'week';
selectedDay.setHours(0, 0, 0, 0);
try {
  const savedView = localStorage.getItem('icloud-calendar-view');
  if (['day', 'week', 'month'].includes(savedView)) calendarView = savedView;
  const savedHiddenCalendars = JSON.parse(localStorage.getItem('icloud-calendar-hidden') ?? '[]');
  if (Array.isArray(savedHiddenCalendars)) {
    hiddenCalendarIds = new Set(savedHiddenCalendars.filter(value => typeof value === 'string'));
  }
} catch { /* Storage may be disabled; Week remains the default. */ }

const greetingHour = new Date().getHours();
greeting.textContent = greetingHour < 12 ? 'Good morning.' : greetingHour < 18 ? 'Good afternoon.' : 'Good evening.';

const sameDay = (left, right) => left.getFullYear() === right.getFullYear()
  && left.getMonth() === right.getMonth() && left.getDate() === right.getDate();
const localDateValue = date => `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
const startOfWeek = date => {
  const monday = new Date(date);
  monday.setHours(0, 0, 0, 0);
  monday.setDate(monday.getDate() - ((monday.getDay() + 6) % 7));
  return monday;
};
const addDays = (date, days) => {
  const result = new Date(date);
  result.setDate(result.getDate() + days);
  return result;
};
const eventsForDay = (events, day) => {
  const end = addDays(day, 1);
  const dayValue = localDateValue(day);
  const endValue = localDateValue(end);
  return events.filter(event => event.isAllDay
    ? String(event.startsAt).slice(0, 10) < endValue && String(event.endsAt).slice(0, 10) > dayValue
    : new Date(event.startsAt) < end && new Date(event.endsAt) > day);
};
const clockText = value => value.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });

const updateCalendarHeading = () => {
  const today = new Date();
  today.setHours(0, 0, 0, 0);
  viewSwitcher.querySelectorAll('[data-view]').forEach(button => {
    const active = button.dataset.view === calendarView;
    button.classList.toggle('active', active);
    button.setAttribute('aria-pressed', String(active));
  });
  if (calendarView === 'day') {
    selectedDateHost.textContent = selectedDay.toLocaleDateString([], { weekday: 'long', month: 'long', day: 'numeric' }).toUpperCase();
    agendaTitle.textContent = sameDay(selectedDay, today) ? 'Today' : selectedDay.toLocaleDateString([], { weekday: 'long' });
  } else if (calendarView === 'week') {
    const monday = startOfWeek(selectedDay);
    const sunday = addDays(monday, 6);
    selectedDateHost.textContent = sameDay(monday, startOfWeek(today)) ? 'THIS WEEK' : 'WEEK VIEW';
    if (monday.getMonth() === sunday.getMonth() && monday.getFullYear() === sunday.getFullYear()) {
      agendaTitle.textContent = `${monday.toLocaleDateString([], { month: 'short' })} ${monday.getDate()}–${sunday.getDate()}, ${sunday.getFullYear()}`;
    } else {
      const startLabel = monday.toLocaleDateString([], { month: 'short', day: 'numeric' });
      const endLabel = sunday.toLocaleDateString([], { month: 'short', day: 'numeric', year: 'numeric' });
      agendaTitle.textContent = `${startLabel} – ${endLabel}`;
    }
  } else {
    selectedDateHost.textContent = 'MONTH VIEW';
    agendaTitle.textContent = selectedDay.toLocaleDateString([], { month: 'long', year: 'numeric' });
  }
};

const escapeHtml = value => String(value ?? '').replace(/[&<>'"]/g, character => ({
  '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;'
})[character]);
const safeCalendarColor = value => /^#[0-9a-f]{6}([0-9a-f]{2})?$/i.test(value ?? '') ? value : '#79e6c4';

const durationText = (start, end) => {
  const minutes = Math.max(0, Math.round((end - start) / 60000));
  if (minutes < 60) return `${minutes} min`;
  const hours = Math.floor(minutes / 60);
  const remainder = minutes % 60;
  return remainder ? `${hours}h ${remainder}m` : `${hours}h`;
};

const eventButton = (event, index, compact = false) => {
  const start = new Date(event.startsAt);
  const label = event.isAllDay ? 'All day' : clockText(start);
  return `<button class="calendar-event${compact ? ' compact' : ''}" type="button" data-event-index="${index}" aria-label="Edit ${escapeHtml(event.title)}"><svg class="calendar-marker" viewBox="0 0 4 24" aria-hidden="true"><rect width="4" height="24" rx="2" fill="${safeCalendarColor(event.color)}"></rect></svg><time>${escapeHtml(label)}</time><span>${escapeHtml(event.title)}</span></button>`;
};

const renderDayView = events => {
  if (!events.length) return '<div class="empty"><b>Your schedule is clear.</b><p>Synced events will appear here automatically.</p></div>';
  return `<div class="day-agenda">${events.map((event, index) => {
    const start = new Date(event.startsAt);
    const end = new Date(event.endsAt);
    const time = event.isAllDay ? 'ALL DAY' : `${clockText(start)} - ${clockText(end)}`;
    const detail = [event.location, durationText(start, end)].filter(Boolean).map(escapeHtml).join(' · ');
    return `<article class="event"><time>${escapeHtml(time)}</time><svg class="bar" viewBox="0 0 4 48" aria-hidden="true"><rect width="4" height="48" rx="2" fill="${safeCalendarColor(event.color)}"></rect></svg><div><h3>${escapeHtml(event.title)}</h3><p>${detail}</p></div><span class="pill">${escapeHtml(event.calendarName)}</span><button class="edit-event" type="button" data-event-index="${index}" aria-label="Edit ${escapeHtml(event.title)}" title="Edit event"><span aria-hidden="true">✎</span></button></article>`;
  }).join('')}</div>`;
};

const renderWeekView = events => {
  const today = new Date();
  today.setHours(0, 0, 0, 0);
  const monday = startOfWeek(selectedDay);
  return `<div class="week-grid">${Array.from({ length: 7 }, (_, offset) => {
    const day = addDays(monday, offset);
    const dayEvents = eventsForDay(events, day);
    return `<section class="week-day${sameDay(day, today) ? ' is-today' : ''}">
      <button class="day-heading" type="button" data-open-day="${localDateValue(day)}"><span>${day.toLocaleDateString([], { weekday: 'short' })}</span><b>${day.getDate()}</b></button>
      <div class="week-events">${dayEvents.length ? dayEvents.map(event => eventButton(event, events.indexOf(event))).join('') : '<span class="no-events">No events</span>'}</div>
    </section>`;
  }).join('')}</div>`;
};

const renderMonthView = events => {
  const today = new Date();
  today.setHours(0, 0, 0, 0);
  const first = new Date(selectedDay.getFullYear(), selectedDay.getMonth(), 1);
  const gridStart = startOfWeek(first);
  const weekdays = Array.from({ length: 7 }, (_, offset) => `<span>${addDays(gridStart, offset).toLocaleDateString([], { weekday: 'short' })}</span>`).join('');
  const cells = Array.from({ length: 42 }, (_, offset) => {
    const day = addDays(gridStart, offset);
    const dayEvents = eventsForDay(events, day);
    const visible = dayEvents.slice(0, 3);
    const overflow = dayEvents.length - visible.length;
    return `<section class="month-day${day.getMonth() !== selectedDay.getMonth() ? ' outside-month' : ''}${sameDay(day, today) ? ' is-today' : ''}">
      <button class="month-date" type="button" data-open-day="${localDateValue(day)}" aria-label="Open ${day.toLocaleDateString([], { weekday: 'long', month: 'long', day: 'numeric' })}">${day.getDate()}</button>
      <div class="month-events">${visible.map(event => eventButton(event, events.indexOf(event), true)).join('')}</div>
      ${overflow ? `<button class="more-events" type="button" data-open-day="${localDateValue(day)}">+${overflow} more</button>` : ''}
    </section>`;
  }).join('');
  return `<div class="month-grid"><div class="month-weekdays">${weekdays}</div><div class="month-cells">${cells}</div></div>`;
};

const render = payload => {
  lastAgendaPayload = payload;
  const events = (payload.events ?? []).filter(event => !hiddenCalendarIds.has(event.calendarId));
  currentEvents = events;
  eventsHost.className = `calendar-surface ${calendarView}-view`;
  eventsHost.innerHTML = calendarView === 'day' ? renderDayView(events) : calendarView === 'week' ? renderWeekView(events) : renderMonthView(events);
};

const setSyncPresentation = (state, title, detail) => {
  syncNowButton.dataset.state = state;
  syncState.textContent = title;
  syncDetail.textContent = detail;
};

const relativeSyncTime = date => {
  const elapsedSeconds = Math.max(0, Math.floor((Date.now() - date.getTime()) / 1000));
  if (elapsedSeconds < 60) return 'just now';
  const elapsedMinutes = Math.floor(elapsedSeconds / 60);
  if (elapsedMinutes < 60) return `${elapsedMinutes} ${elapsedMinutes === 1 ? 'minute' : 'minutes'} ago`;
  const elapsedHours = Math.floor(elapsedMinutes / 60);
  if (elapsedHours < 24) return `${elapsedHours} ${elapsedHours === 1 ? 'hour' : 'hours'} ago`;
  const elapsedDays = Math.floor(elapsedHours / 24);
  return `${elapsedDays} ${elapsedDays === 1 ? 'day' : 'days'} ago`;
};

const loadSyncStatus = async () => {
  try {
    const response = await fetch('/api/sync/status');
    if (!response.ok) throw new Error();
    const statuses = await response.json();
    if (!connectedAccounts.length) {
      setSyncPresentation('waiting', 'Not connected', 'Connect iCloud to begin syncing.');
      return;
    }
    if (!statuses.length) {
      setSyncPresentation('waiting', 'Waiting for first sync', 'Your calendar will update automatically.');
      return;
    }
    const failed = statuses.filter(status => !status.succeeded).length;
    const latest = new Date(Math.max(...statuses.map(status => new Date(status.attemptedAt).getTime())));
    setSyncPresentation(
      failed ? 'error' : 'current',
      failed ? 'Attention needed' : 'Up to date',
      failed
        ? `${failed} ${failed === 1 ? 'account needs' : 'accounts need'} attention. Select to retry.`
        : `Synced ${relativeSyncTime(latest)}.`);
  } catch {
    setSyncPresentation('error', 'Status unavailable', 'Your cached agenda remains available.');
  }
};

const loadAgenda = async (showFailure = false) => {
  try {
    let rangeStart = new Date(selectedDay);
    let rangeEnd;
    if (calendarView === 'week') {
      rangeStart = startOfWeek(selectedDay);
      rangeEnd = addDays(rangeStart, 7);
    } else if (calendarView === 'month') {
      rangeStart = startOfWeek(new Date(selectedDay.getFullYear(), selectedDay.getMonth(), 1));
      rangeEnd = addDays(rangeStart, 42);
    } else {
      rangeEnd = addDays(rangeStart, 1);
    }
    const query = new URLSearchParams({ from: rangeStart.toISOString(), to: rangeEnd.toISOString(), limit: '500' });
    if (calendarView === 'day') query.set('day', localDateValue(selectedDay));
    const response = await fetch(`/api/widget/agenda?${query}`);
    if (!response.ok) throw new Error(`Agenda request failed: ${response.status}`);
    render(await response.json());
  } catch {
    if (!showFailure) return;
    eventsHost.innerHTML = '<div class="empty"><b>Calendar unavailable.</b><p>We could not load your local calendar. Try refreshing.</p></div>';
  }
};

updateCalendarHeading();
loadAgenda(true);
setInterval(() => {
  if (document.visibilityState === 'visible') {
    loadAgenda();
    loadSyncStatus();
  }
}, 1000);

const movePeriod = direction => {
  if (calendarView === 'month') selectedDay.setMonth(selectedDay.getMonth() + direction, 1);
  else selectedDay.setDate(selectedDay.getDate() + direction * (calendarView === 'week' ? 7 : 1));
  updateCalendarHeading();
  loadAgenda(true);
};
previousPeriodButton.addEventListener('click', () => movePeriod(-1));
nextPeriodButton.addEventListener('click', () => movePeriod(1));
todayButton.addEventListener('click', () => {
  selectedDay = new Date();
  selectedDay.setHours(0, 0, 0, 0);
  updateCalendarHeading();
  loadAgenda(true);
});
viewSwitcher.addEventListener('click', event => {
  const button = event.target.closest('[data-view]');
  if (!button || button.dataset.view === calendarView) return;
  calendarView = button.dataset.view;
  try { localStorage.setItem('icloud-calendar-view', calendarView); } catch { /* Keep the in-memory selection. */ }
  updateCalendarHeading();
  loadAgenda(true);
});
syncNowButton.addEventListener('click', async () => {
  syncNowButton.disabled = true;
  setSyncPresentation('syncing', 'Syncing…', 'Checking iCloud for changes now.');
  try {
    const response = await fetch('/api/sync', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: '{}'
    });
    if (!response.ok) throw new Error();
    await Promise.all([loadAgenda(true), loadSyncStatus()]);
  } catch {
    setSyncPresentation('error', 'Sync failed', 'Check your connection and credentials, then try again.');
  } finally {
    syncNowButton.disabled = false;
  }
});

const openCredentialDialog = (userName = '') => {
  connectForm.reset();
  connectForm.elements.userName.value = userName;
  credentialTitle.textContent = userName ? 'Change credentials' : 'Connect iCloud';
  credentialCopy.textContent = userName
    ? 'Enter a new app-specific password. The old keyring entry is replaced only after Apple verifies it.'
    : 'Your password is verified with Apple and stored in your Linux keyring. It never reaches browser storage.';
  connectForm.querySelector('.form-status').textContent = '';
  connectDialog.showModal();
};

const refreshAccounts = async () => {
  const response = await fetch('/api/accounts');
  if (!response.ok) throw new Error('Could not load connected accounts.');
  connectedAccounts = await response.json();
  const calendarCount = connectedAccounts.reduce((count, account) => count + account.calendars.length, 0);
  connectButton.innerHTML = connectedAccounts.length
    ? `<i></i><span>${calendarCount} ${calendarCount === 1 ? 'calendar' : 'calendars'} connected</span>`
    : '<i></i><span>Connect iCloud</span>';
  accountList.innerHTML = connectedAccounts.map(account => `
    <article class="account-card">
      <div><strong>${escapeHtml(account.userName)}</strong><span>${account.calendars.length} ${account.calendars.length === 1 ? 'calendar' : 'calendars'}</span></div>
      <div class="account-actions">
        <button type="button" data-action="change" data-account-id="${escapeHtml(account.id)}">Change password</button>
        <button class="danger" type="button" data-action="disconnect" data-account-id="${escapeHtml(account.id)}">Disconnect</button>
      </div>
    </article>`).join('') || '<div class="empty"><b>No iCloud account connected.</b></div>';
  renderCalendarVisibility();
};

const saveCalendarVisibility = () => {
  try { localStorage.setItem('icloud-calendar-hidden', JSON.stringify([...hiddenCalendarIds])); } catch { /* Keep the in-memory selection. */ }
};

const renderCalendarVisibility = () => {
  const calendars = connectedAccounts.flatMap(account => account.calendars.map(calendar => ({ ...calendar, accountName: account.userName })));
  const visibleCount = calendars.filter(calendar => !hiddenCalendarIds.has(calendar.id)).length;
  calendarFilterButton.disabled = calendars.length === 0;
  calendarFilterButton.textContent = calendars.length && visibleCount !== calendars.length
    ? `Calendars ${visibleCount}/${calendars.length}`
    : 'Calendars';
  calendarVisibilityStatus.textContent = calendars.length
    ? `${visibleCount} of ${calendars.length} calendars shown.`
    : '';
  calendarVisibilityList.innerHTML = calendars.map(calendar => `
    <label class="calendar-visibility-option">
      <input type="checkbox" data-calendar-id="${escapeHtml(calendar.id)}"${hiddenCalendarIds.has(calendar.id) ? '' : ' checked'}>
      <svg class="calendar-visibility-color" viewBox="0 0 5 32" aria-hidden="true"><rect width="5" height="32" rx="2.5" fill="${safeCalendarColor(calendar.color)}"></rect></svg>
      <span><b>${escapeHtml(calendar.displayName)}</b><small>${escapeHtml(calendar.accountName)}</small></span>
    </label>`).join('') || '<div class="empty"><b>No calendars connected.</b></div>';
};

const setAllCalendarsVisible = visible => {
  for (const calendar of connectedAccounts.flatMap(account => account.calendars)) {
    if (visible) hiddenCalendarIds.delete(calendar.id);
    else hiddenCalendarIds.add(calendar.id);
  }
  saveCalendarVisibility();
  renderCalendarVisibility();
  render(lastAgendaPayload);
};

calendarFilterButton.addEventListener('click', () => calendarsDialog.showModal());
calendarsDialog.querySelector('.dialog-close').addEventListener('click', () => calendarsDialog.close());
showAllCalendarsButton.addEventListener('click', () => setAllCalendarsVisible(true));
hideAllCalendarsButton.addEventListener('click', () => setAllCalendarsVisible(false));
calendarVisibilityList.addEventListener('change', event => {
  const checkbox = event.target.closest('input[data-calendar-id]');
  if (!checkbox) return;
  if (checkbox.checked) hiddenCalendarIds.delete(checkbox.dataset.calendarId);
  else hiddenCalendarIds.add(checkbox.dataset.calendarId);
  saveCalendarVisibility();
  renderCalendarVisibility();
  render(lastAgendaPayload);
});

connectButton.addEventListener('click', () => connectedAccounts.length ? accountsDialog.showModal() : openCredentialDialog());
connectDialog.querySelector('.dialog-close').addEventListener('click', () => connectDialog.close());
connectDialog.addEventListener('close', () => connectForm.reset());
accountsDialog.querySelector('.dialog-close').addEventListener('click', () => accountsDialog.close());
addAccountButton.addEventListener('click', () => {
  accountsDialog.close();
  openCredentialDialog();
});
accountList.addEventListener('click', async event => {
  const button = event.target.closest('button[data-action]');
  if (!button) return;
  const account = connectedAccounts.find(item => item.id === button.dataset.accountId);
  if (!account) return;
  if (button.dataset.action === 'change') {
    accountsDialog.close();
    openCredentialDialog(account.userName);
    return;
  }
  if (!window.confirm(`Disconnect ${account.userName}? Its locally cached calendar data and keyring credential will be removed.`)) return;
  button.disabled = true;
  accountStatus.textContent = 'Disconnecting securely…';
  accountStatus.classList.remove('error');
  try {
    const response = await fetch(`/api/accounts/${encodeURIComponent(account.id)}`, { method: 'DELETE' });
    if (!response.ok) {
      const result = await response.json().catch(() => ({}));
      throw new Error(result.error ?? 'Could not disconnect this account.');
    }
    accountStatus.textContent = 'Account, local calendar data, and keyring credential removed.';
    await refreshAccounts();
    await Promise.all([loadAgenda(), loadSyncStatus()]);
  } catch (error) {
    accountStatus.textContent = error.message;
    accountStatus.classList.add('error');
    button.disabled = false;
  }
});
connectForm.addEventListener('submit', async event => {
  event.preventDefault();
  const submit = connectForm.querySelector('[type="submit"]');
  const status = connectForm.querySelector('.form-status');
  const data = new FormData(connectForm);
  submit.disabled = true;
  submit.textContent = 'Connecting securely…';
  status.textContent = '';
  status.classList.remove('error');
  try {
    const response = await fetch('/api/accounts/icloud/connect', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ userName: data.get('userName'), appSpecificPassword: data.get('appSpecificPassword') })
    });
    const result = await response.json();
    if (!response.ok) throw new Error(result.error ?? 'Could not connect your calendar.');
    connectForm.reset();
    const synced = result.sync.filter(item => item.succeeded).length;
    status.textContent = `Connected ${result.calendars.length} calendars; ${synced} synced now.`;
    await refreshAccounts();
    await Promise.all([loadAgenda(), loadSyncStatus()]);
    setTimeout(() => connectDialog.close(), 1100);
  } catch (error) {
    status.textContent = error.message;
    status.classList.add('error');
  } finally {
    submit.disabled = false;
    submit.textContent = 'Connect calendar';
  }
});

refreshAccounts().then(() => {
  loadSyncStatus();
  if (new URLSearchParams(window.location.search).get('action') === 'add-event') {
    createEventButton.click();
  }
}).catch(() => {});

const localInputValue = date => {
  const offset = date.getTimezoneOffset();
  return new Date(date.getTime() - offset * 60000).toISOString().slice(0, 16);
};

const openEventDialog = (existingEvent = null) => {
  if (!connectedAccounts.length) {
    openCredentialDialog();
    return;
  }
  editingEvent = existingEvent;
  eventForm.reset();
  const formStatus = eventForm.querySelector('.form-status');
  formStatus.textContent = '';
  formStatus.classList.remove('error');
  hideAddressSuggestions();
  eventEndField.hidden = false;
  eventForm.elements.endsAt.disabled = false;
  eventForm.elements.endsAt.required = true;
  startLabel.textContent = 'Starts';
  eventForm.elements.startsAt.type = 'datetime-local';
  eventForm.elements.endsAt.type = 'datetime-local';
  const start = new Date(selectedDay);
  const now = new Date();
  if (sameDay(start, now)) {
    start.setHours(now.getHours() + 1, 0, 0, 0);
  } else {
    start.setHours(9, 0, 0, 0);
  }
  const end = new Date(start.getTime() + 60 * 60 * 1000);
  eventForm.elements.startsAt.value = localInputValue(start);
  eventForm.elements.endsAt.value = localInputValue(end);
  eventForm.elements.calendarId.innerHTML = connectedAccounts.flatMap(account => account.calendars)
    .map(calendar => `<option value="${escapeHtml(calendar.id)}">${escapeHtml(calendar.displayName)}</option>`).join('');
  eventForm.elements.calendarId.disabled = Boolean(existingEvent);
  eventDialogEyebrow.textContent = existingEvent ? 'EDIT EVENT' : 'NEW EVENT';
  eventDialogTitle.textContent = existingEvent ? 'Update event' : 'Add to calendar';
  eventDialogCopy.textContent = existingEvent ? 'Save your changes to iCloud and keep every device in sync.' : 'Create an event in iCloud and keep it in sync everywhere.';
  eventSubmit.textContent = existingEvent ? 'Save changes' : 'Add event';
  eventDelete.hidden = !existingEvent;
  if (existingEvent) {
    eventForm.elements.title.value = existingEvent.title ?? '';
    eventForm.elements.calendarId.value = existingEvent.calendarId;
    eventForm.elements.location.value = existingEvent.location ?? '';
    eventForm.elements.description.value = existingEvent.description ?? '';
    eventForm.elements.isAllDay.checked = Boolean(existingEvent.isAllDay);
    if (existingEvent.isAllDay) {
      eventForm.elements.startsAt.type = 'date';
      eventForm.elements.endsAt.type = 'date';
      eventForm.elements.startsAt.value = String(existingEvent.startsAt).slice(0, 10);
      eventEndField.hidden = true;
      eventForm.elements.endsAt.disabled = true;
      eventForm.elements.endsAt.required = false;
      startLabel.textContent = 'Date';
    } else {
      eventForm.elements.startsAt.value = localInputValue(new Date(existingEvent.startsAt));
      eventForm.elements.endsAt.value = localInputValue(new Date(existingEvent.endsAt));
    }
  }
  eventDialog.showModal();
};
createEventButton.addEventListener('click', () => openEventDialog());
eventsHost.addEventListener('click', event => {
  const eventButton = event.target.closest('[data-event-index]');
  if (eventButton) {
    const selectedEvent = currentEvents[Number(eventButton.dataset.eventIndex)];
    if (selectedEvent) openEventDialog(selectedEvent);
    return;
  }
  const dayButton = event.target.closest('[data-open-day]');
  if (!dayButton) return;
  const [year, month, day] = dayButton.dataset.openDay.split('-').map(Number);
  selectedDay = new Date(year, month - 1, day);
  calendarView = 'day';
  try { localStorage.setItem('icloud-calendar-view', calendarView); } catch { /* Keep the in-memory selection. */ }
  updateCalendarHeading();
  loadAgenda(true);
});
eventDialog.querySelector('.dialog-close').addEventListener('click', () => eventDialog.close());
eventDialog.querySelector('.event-cancel').addEventListener('click', () => eventDialog.close());
eventDelete.addEventListener('click', async () => {
  if (!editingEvent || !window.confirm(`Delete “${editingEvent.title}”? This will remove it from iCloud and all synced devices.`)) return;
  const status = eventForm.querySelector('.form-status');
  eventDelete.disabled = true;
  eventSubmit.disabled = true;
  status.textContent = 'Deleting from iCloud…';
  status.classList.remove('error');
  try {
    const response = await fetch('/api/events', {
      method: 'DELETE',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ calendarId: editingEvent.calendarId, resourceId: editingEvent.resourceId, originalStartsAt: editingEvent.originalStartsAt ?? editingEvent.startsAt })
    });
    const result = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(result.error ?? Object.values(result.errors ?? {}).flat()[0] ?? 'Could not delete the event.');
    status.textContent = 'Deleted from iCloud.';
    await loadAgenda(true);
    setTimeout(() => eventDialog.close(), 650);
  } catch (error) {
    status.textContent = error.message;
    status.classList.add('error');
  } finally {
    eventDelete.disabled = false;
    eventSubmit.disabled = false;
  }
});
eventForm.elements.isAllDay.addEventListener('change', event => {
  if (event.target.checked) {
    const start = eventForm.elements.startsAt.value.slice(0, 10);
    eventForm.elements.startsAt.type = 'date';
    eventForm.elements.endsAt.type = 'date';
    eventForm.elements.startsAt.value = start;
    eventEndField.hidden = true;
    eventForm.elements.endsAt.disabled = true;
    eventForm.elements.endsAt.required = false;
    startLabel.textContent = 'Date';
  } else {
    const date = eventForm.elements.startsAt.value || localDateValue(selectedDay);
    eventForm.elements.startsAt.type = 'datetime-local';
    eventForm.elements.endsAt.type = 'datetime-local';
    eventForm.elements.endsAt.disabled = false;
    eventForm.elements.endsAt.required = true;
    eventForm.elements.startsAt.value = `${date}T09:00`;
    eventForm.elements.endsAt.value = `${date}T10:00`;
    eventEndField.hidden = false;
    startLabel.textContent = 'Starts';
  }
});
eventForm.addEventListener('submit', async event => {
  event.preventDefault();
  const submit = eventForm.querySelector('[type="submit"]');
  const status = eventForm.querySelector('.form-status');
  const fields = eventForm.elements;
  const allDay = fields.isAllDay.checked;
  const start = new Date(allDay ? `${fields.startsAt.value}T00:00:00` : fields.startsAt.value);
  const end = new Date(allDay ? `${fields.startsAt.value}T00:00:00` : fields.endsAt.value);
  if (allDay) end.setDate(end.getDate() + 1);
  const offsetIso = value => {
    const pad = number => String(Math.abs(number)).padStart(2, '0');
    const offset = -value.getTimezoneOffset();
    const sign = offset >= 0 ? '+' : '-';
    return `${localInputValue(value)}:00${sign}${pad(Math.trunc(offset / 60))}:${pad(offset % 60)}`;
  };
  submit.disabled = true;
  status.textContent = editingEvent ? 'Saving changes to iCloud…' : 'Adding to iCloud…';
  status.classList.remove('error');
  try {
    const response = await fetch('/api/events', {
      method: editingEvent ? 'PUT' : 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ calendarId: fields.calendarId.value, resourceId: editingEvent?.resourceId, originalStartsAt: editingEvent?.originalStartsAt ?? editingEvent?.startsAt, title: fields.title.value, startsAt: offsetIso(start), endsAt: offsetIso(end), isAllDay: allDay, location: fields.location.value, description: fields.description.value })
    });
    const result = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(result.error ?? Object.values(result.errors ?? {}).flat()[0] ?? 'Could not add the event.');
    status.textContent = editingEvent ? 'Updated in iCloud.' : 'Added to iCloud.';
    await loadAgenda(true);
    setTimeout(() => eventDialog.close(), 650);
  } catch (error) {
    status.textContent = error.message;
    status.classList.add('error');
  } finally { submit.disabled = false; }
});

const checkForUpdate = async () => {
  try {
    const response = await fetch('/api/update');
    const update = await response.json();
    appVersion.textContent = `v${update.currentVersion}`;
    if (!update.updateAvailable || !update.releaseUrl) return;
    updateCopy.textContent = `Version ${update.latestVersion} is ready (you have ${update.currentVersion}).`;
    viewRelease.href = update.releaseUrl;
    updateBanner.hidden = false;
  } catch { /* Updates are optional; the calendar remains usable offline. */ }
};
checkForUpdate();
installUpdate.addEventListener('click', async () => {
  installUpdate.disabled = true;
  viewRelease.setAttribute('aria-disabled', 'true');
  updateCopy.textContent = 'Downloading and verifying the update…';
  try {
    const response = await fetch('/api/update/install', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' });
    const result = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(result.error ?? 'The update could not be started.');
    updateCopy.textContent = 'Installing now. The calendar will restart automatically…';
    const expectedVersion = result.latestVersion;
    for (let attempt = 0; attempt < 120; attempt++) {
      await new Promise(resolve => setTimeout(resolve, 1000));
      try {
        const status = await fetch('/api/update', { cache: 'no-store' });
        if (!status.ok) continue;
        const update = await status.json();
        if (update.currentVersion === expectedVersion) {
          updateCopy.textContent = `Updated to version ${expectedVersion}. Reloading…`;
          setTimeout(() => window.location.reload(), 600);
          return;
        }
      } catch { /* The local service is expected to be briefly unavailable while restarting. */ }
    }
    throw new Error('The update did not finish in time. Your existing installation is still available.');
  } catch (error) {
    updateCopy.textContent = error.message;
    installUpdate.disabled = false;
    viewRelease.removeAttribute('aria-disabled');
  }
});

let addressTimer;
let addressRequest;
let addressOptions = [];
let activeAddressIndex = -1;
const locationInput = eventForm.elements.location;
const locationInputShell = eventForm.querySelector('.location-input');
const hideAddressSuggestions = () => {
  addressSuggestions.hidden = true;
  locationInput.setAttribute('aria-expanded', 'false');
  locationInput.removeAttribute('aria-activedescendant');
  activeAddressIndex = -1;
};
const setActiveAddress = index => {
  const options = [...addressSuggestions.querySelectorAll('[role=option]')];
  if (!options.length) return;
  activeAddressIndex = (index + options.length) % options.length;
  options.forEach((option, optionIndex) => option.classList.toggle('active', optionIndex === activeAddressIndex));
  locationInput.setAttribute('aria-activedescendant', options[activeAddressIndex].id);
  options[activeAddressIndex].scrollIntoView({ block: 'nearest' });
};
const chooseAddress = index => {
  const choice = addressOptions[index];
  if (!choice) return;
  locationInput.value = choice.label;
  hideAddressSuggestions();
};
locationInput.addEventListener('input', () => {
  clearTimeout(addressTimer);
  addressRequest?.abort();
  const query = locationInput.value.trim();
  addressOptions = [];
  if (query.length < 3) {
    hideAddressSuggestions();
    locationInputShell.classList.remove('loading');
    return;
  }
  addressTimer = setTimeout(async () => {
    addressRequest = new AbortController();
    locationInputShell.classList.add('loading');
    try {
      const response = await fetch(`/api/locations?query=${encodeURIComponent(query)}`, { signal: addressRequest.signal });
      if (!response.ok) throw new Error();
      addressOptions = await response.json();
      addressSuggestions.innerHTML = addressOptions.length
        ? addressOptions.map((item, index) => `<button id="address-option-${index}" type="button" role="option" data-index="${index}"><i aria-hidden="true"></i><span><strong>${escapeHtml(item.primary)}</strong><small>${escapeHtml(item.secondary)}</small></span></button>`).join('')
        : '<p class="address-empty"><b>No matching places</b><span>You can still use the location you typed.</span></p>';
      addressSuggestions.hidden = false;
      locationInput.setAttribute('aria-expanded', 'true');
      if (window.matchMedia('(max-width: 620px)').matches) {
        requestAnimationFrame(() => addressSuggestions.scrollIntoView({ block: 'nearest' }));
      }
    } catch (error) {
      if (error.name !== 'AbortError') hideAddressSuggestions();
    } finally {
      locationInputShell.classList.remove('loading');
    }
  }, 500);
});
addressSuggestions.addEventListener('click', event => {
  const option = event.target.closest('button');
  if (!option) return;
  chooseAddress(Number(option.dataset.index));
});
locationInput.addEventListener('keydown', event => {
  if (addressSuggestions.hidden) return;
  if (event.key === 'ArrowDown') { event.preventDefault(); setActiveAddress(activeAddressIndex + 1); }
  else if (event.key === 'ArrowUp') { event.preventDefault(); setActiveAddress(activeAddressIndex - 1); }
  else if (event.key === 'Enter' && activeAddressIndex >= 0) { event.preventDefault(); chooseAddress(activeAddressIndex); }
  else if (event.key === 'Escape') { event.preventDefault(); hideAddressSuggestions(); }
});
locationInput.addEventListener('blur', () => setTimeout(hideAddressSuggestions, 150));
