const eventsHost = document.querySelector('#events');
const countHost = document.querySelector('#event-count');
const timeHost = document.querySelector('#scheduled-time');
const meter = document.querySelector('#day-meter');
const summary = document.querySelector('#day-summary');
const connectButton = document.querySelector('#connect-button');
const connectDialog = document.querySelector('#connect-dialog');
const connectForm = document.querySelector('#connect-form');
const accountsDialog = document.querySelector('#accounts-dialog');
const accountList = document.querySelector('#account-list');
const accountStatus = document.querySelector('#account-status');
const addAccountButton = document.querySelector('#add-account');
const credentialTitle = document.querySelector('#credential-dialog-title');
const credentialCopy = document.querySelector('#credential-dialog-copy');
const dateStrip = document.querySelector('#date-strip');
const selectedDateHost = document.querySelector('#selected-date');
const agendaTitle = document.querySelector('#agenda-title');
const greeting = document.querySelector('#greeting');
const previousWeekButton = document.querySelector('#previous-week');
const nextWeekButton = document.querySelector('#next-week');
const todayButton = document.querySelector('#today-button');
const syncNowButton = document.querySelector('#sync-now');
const syncState = document.querySelector('#sync-state');
const syncDetail = document.querySelector('#sync-detail');
const createEventButton = document.querySelector('#create-event');
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
let editingEvent = null;
selectedDay.setHours(0, 0, 0, 0);

const greetingHour = new Date().getHours();
greeting.textContent = greetingHour < 12 ? 'Good morning.' : greetingHour < 18 ? 'Good afternoon.' : 'Good evening.';

const sameDay = (left, right) => left.getFullYear() === right.getFullYear()
  && left.getMonth() === right.getMonth() && left.getDate() === right.getDate();
const localDateValue = date => `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;

const renderWeek = () => {
  const today = new Date();
  today.setHours(0, 0, 0, 0);
  const monday = new Date(selectedDay);
  monday.setDate(monday.getDate() - ((monday.getDay() + 6) % 7));
  dateStrip.innerHTML = '';
  for (let offset = 0; offset < 7; offset++) {
    const day = new Date(monday);
    day.setDate(monday.getDate() + offset);
    const button = document.createElement('button');
    button.className = sameDay(day, selectedDay) ? 'today' : '';
    button.setAttribute('aria-pressed', String(sameDay(day, selectedDay)));
    button.setAttribute('aria-label', day.toLocaleDateString([], { weekday: 'long', month: 'long', day: 'numeric' }));
    button.innerHTML = `<small>${day.toLocaleDateString([], { weekday: 'short' }).toUpperCase()}</small><b>${day.getDate()}</b>`;
    button.addEventListener('click', () => {
      selectedDay = day;
      renderWeek();
      loadAgenda(true);
    });
    dateStrip.appendChild(button);
  }
  selectedDateHost.textContent = selectedDay.toLocaleDateString([], { weekday: 'long', month: 'long', day: 'numeric' }).toUpperCase();
  agendaTitle.textContent = sameDay(selectedDay, today) ? 'Today' : selectedDay.toLocaleDateString([], { weekday: 'long' });
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

const render = payload => {
  const events = payload.events ?? [];
  currentEvents = events;
  const totalMinutes = events.reduce((total, event) =>
    total + Math.max(0, (new Date(event.endsAt) - new Date(event.startsAt)) / 60000), 0);
  countHost.textContent = `${events.length} ${events.length === 1 ? 'event' : 'events'}`;
  timeHost.textContent = `${durationText(0, totalMinutes * 60000)} scheduled`;
  meter.value = Math.min(8 * 60, totalMinutes);
  summary.textContent = events.length
    ? 'Your calendar is stored locally for an instant, offline-friendly glance.'
    : 'You are clear for the next two days.';

  if (!events.length) {
    eventsHost.innerHTML = '<div class="empty"><b>Your schedule is clear.</b><p>Synced events will appear here automatically.</p></div>';
    return;
  }

  eventsHost.innerHTML = events.map((event, index) => {
    const start = new Date(event.startsAt);
    const end = new Date(event.endsAt);
    const clock = value => value.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    const time = event.isAllDay ? 'ALL DAY' : `${clock(start)} - ${clock(end)}`;
    const detail = [event.location, durationText(start, end)].filter(Boolean).map(escapeHtml).join(' · ');
    return `<article class="event"><time>${escapeHtml(time)}</time><svg class="bar" viewBox="0 0 4 48" aria-hidden="true"><rect width="4" height="48" rx="2" fill="${safeCalendarColor(event.color)}"></rect></svg><div><h3>${escapeHtml(event.title)}</h3><p>${detail}</p></div><span class="pill">${escapeHtml(event.calendarName)}</span><button class="edit-event" type="button" data-event-index="${index}" aria-label="Edit ${escapeHtml(event.title)}" title="Edit event"><span aria-hidden="true">✎</span></button></article>`;
  }).join('');
};

const loadSyncStatus = async () => {
  try {
    const response = await fetch('/api/sync/status');
    if (!response.ok) throw new Error();
    const statuses = await response.json();
    if (!connectedAccounts.length) {
      syncState.textContent = 'Not connected';
      syncDetail.textContent = 'Connect iCloud to begin syncing.';
      return;
    }
    if (!statuses.length) {
      syncState.textContent = 'Waiting for first sync';
      syncDetail.textContent = 'Your calendar will update automatically.';
      return;
    }
    const failed = statuses.filter(status => !status.succeeded).length;
    const latest = new Date(Math.max(...statuses.map(status => new Date(status.attemptedAt).getTime())));
    syncState.textContent = failed ? 'Attention needed' : 'Up to date';
    syncDetail.textContent = failed
      ? `${failed} ${failed === 1 ? 'account needs' : 'accounts need'} attention. Use Sync now to retry.`
      : `Last checked ${latest.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}.`;
  } catch {
    syncState.textContent = 'Status unavailable';
    syncDetail.textContent = 'Your cached agenda remains available.';
  }
};

const loadAgenda = async (showFailure = false) => {
  try {
    const rangeEnd = new Date(selectedDay);
    rangeEnd.setDate(rangeEnd.getDate() + 1);
    const query = new URLSearchParams({ from: selectedDay.toISOString(), to: rangeEnd.toISOString(), day: localDateValue(selectedDay), limit: '100' });
    const response = await fetch(`/api/widget/agenda?${query}`);
    if (!response.ok) throw new Error(`Agenda request failed: ${response.status}`);
    render(await response.json());
  } catch {
    if (!showFailure) return;
    eventsHost.innerHTML = '<div class="empty"><b>Calendar unavailable.</b><p>We could not load your local agenda. Try refreshing.</p></div>';
    countHost.textContent = 'Offline';
    timeHost.textContent = 'Your data remains on this device';
  }
};

renderWeek();
loadAgenda(true);
setInterval(() => {
  if (document.visibilityState === 'visible') {
    loadAgenda();
    loadSyncStatus();
  }
}, 1000);

const moveSelectedDay = days => {
  selectedDay.setDate(selectedDay.getDate() + days);
  renderWeek();
  loadAgenda(true);
};
previousWeekButton.addEventListener('click', () => moveSelectedDay(-7));
nextWeekButton.addEventListener('click', () => moveSelectedDay(7));
todayButton.addEventListener('click', () => {
  selectedDay = new Date();
  selectedDay.setHours(0, 0, 0, 0);
  renderWeek();
  loadAgenda(true);
});
syncNowButton.addEventListener('click', async () => {
  syncNowButton.disabled = true;
  syncState.textContent = 'Syncing…';
  syncDetail.textContent = 'Checking iCloud for changes now.';
  try {
    const response = await fetch('/api/sync', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: '{}'
    });
    if (!response.ok) throw new Error();
    await Promise.all([loadAgenda(true), loadSyncStatus()]);
  } catch {
    syncState.textContent = 'Sync failed';
    syncDetail.textContent = 'Check your connection and credentials, then try again.';
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
};

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
  const button = event.target.closest('.edit-event');
  if (!button) return;
  const selectedEvent = currentEvents[Number(button.dataset.eventIndex)];
  if (selectedEvent) openEventDialog(selectedEvent);
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
