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
let selectedDay = new Date();
let connectedAccounts = [];
selectedDay.setHours(0, 0, 0, 0);

const greetingHour = new Date().getHours();
greeting.textContent = greetingHour < 12 ? 'Good morning.' : greetingHour < 18 ? 'Good afternoon.' : 'Good evening.';

const sameDay = (left, right) => left.getFullYear() === right.getFullYear()
  && left.getMonth() === right.getMonth() && left.getDate() === right.getDate();

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
  const totalMinutes = events.reduce((total, event) =>
    total + Math.max(0, (new Date(event.endsAt) - new Date(event.startsAt)) / 60000), 0);
  countHost.textContent = `${events.length} ${events.length === 1 ? 'event' : 'events'}`;
  timeHost.textContent = `${durationText(0, totalMinutes * 60000)} scheduled`;
  meter.style.width = `${Math.min(100, totalMinutes / (8 * 60) * 100)}%`;
  summary.textContent = events.length
    ? 'Your calendar is stored locally for an instant, offline-friendly glance.'
    : 'You are clear for the next two days.';

  if (!events.length) {
    eventsHost.innerHTML = '<div class="empty"><b>Your schedule is clear.</b><p>Synced events will appear here automatically.</p></div>';
    return;
  }

  eventsHost.innerHTML = events.map(event => {
    const start = new Date(event.startsAt);
    const end = new Date(event.endsAt);
    const time = event.isAllDay ? 'ALL DAY' : start.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    const detail = [event.location, durationText(start, end)].filter(Boolean).map(escapeHtml).join(' · ');
    return `<article class="event"><time>${escapeHtml(time)}</time><span class="bar" style="--event-color:${safeCalendarColor(event.color)}"></span><div><h3>${escapeHtml(event.title)}</h3><p>${detail}</p></div><span class="pill">${escapeHtml(event.calendarName)}</span></article>`;
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
    const query = new URLSearchParams({ from: selectedDay.toISOString(), to: rangeEnd.toISOString(), limit: '100' });
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
}, 15000);

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
    ? `<i></i> ${calendarCount} ${calendarCount === 1 ? 'calendar' : 'calendars'} connected`
    : '<i></i> Connect iCloud';
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

refreshAccounts().then(loadSyncStatus).catch(() => {});
