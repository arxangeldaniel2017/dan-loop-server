document.querySelectorAll('.tab').forEach(button => {
  button.addEventListener('click', () => {
    document.querySelectorAll('.tab').forEach(x => x.classList.remove('active'));
    document.querySelectorAll('.tab-panel').forEach(x => x.classList.remove('active'));
    button.classList.add('active');
    document.getElementById(button.dataset.tab)?.classList.add('active');
    history.replaceState(null, '', `#${button.dataset.tab}`);
  });
});

const initialTab = location.hash.slice(1);
if (initialTab) document.querySelector(`.tab[data-tab="${CSS.escape(initialTab)}"]`)?.click();

document.addEventListener('click', event => {
  const button = event.target.closest('[data-disclosure-action]');
  if (!button) return;
  const scope = button.closest('[data-disclosure-scope]');
  if (!scope) return;
  const open = button.dataset.disclosureAction === 'expand';
  scope.querySelectorAll(button.dataset.disclosureTarget).forEach(details => { details.open = open; });
});

const jobsHost = document.getElementById('jobs-list');
let jobsRefreshRunning = false;
let jobsPage = 1;
const jobsPerPage = 10;

function updateJobsPage() {
  if (!jobsHost) return;
  const items = [...jobsHost.querySelectorAll('.job-item')];
  const pages = Math.max(1, Math.ceil(items.length / jobsPerPage));
  jobsPage = Math.min(jobsPage, pages);
  items.forEach((item, index) => {
    const hidden = index < (jobsPage - 1) * jobsPerPage || index >= jobsPage * jobsPerPage;
    item.hidden = hidden;
    const gap = jobsHost.querySelector(`[data-job-gap="${CSS.escape(item.dataset.jobKey)}"]`);
    if (gap) gap.hidden = hidden;
  });
  const pagination = jobsHost.querySelector('[data-job-pagination]');
  if (!pagination) return;
  pagination.hidden = items.length <= jobsPerPage;
  pagination.querySelector('[data-page-status]').textContent = `Page ${jobsPage} of ${pages}`;
  pagination.querySelector('[data-page-action="previous"]').disabled = jobsPage === 1;
  pagination.querySelector('[data-page-action="next"]').disabled = jobsPage === pages;
}

function compactDuration(totalSeconds) {
  const seconds = Math.abs(Math.round(totalSeconds));
  if (seconds < 60) return `${seconds}s`;
  if (seconds < 3600) return `${Math.round(seconds / 60)}m`;
  if (seconds < 86400) return `${Math.round(seconds / 3600)}h`;
  return `${Math.round(seconds / 86400)}d`;
}

function updateSmartTimes() {
  document.querySelectorAll('[data-relative-time]').forEach(element => {
    if (!element.dataset.relativeTime) return;
    const elapsed = Math.max(0, (Date.now() - Date.parse(element.dataset.relativeTime)) / 1000);
    element.querySelector('span').textContent = elapsed < 5 ? 'just now' : `${compactDuration(elapsed)} ago`;
  });
  document.querySelectorAll('[data-gap-seconds]').forEach(element => {
    const seconds = Number(element.dataset.gapSeconds);
    element.querySelector('[data-gap-label]').textContent = seconds >= 0
      ? `${compactDuration(seconds)} after the previous group ended`
      : `${compactDuration(seconds)} before the previous group ended`;
  });
  document.querySelectorAll('[data-next-run-time]').forEach(element => {
    const remaining = (Date.parse(element.dataset.nextRunTime) - Date.now()) / 1000;
    element.textContent = remaining <= 1 ? 'as soon as the worker is free' : `in ${compactDuration(remaining)}`;
  });
}

document.addEventListener('click', event => {
  const button = event.target.closest('[data-page-action]');
  if (!button) return;
  jobsPage += button.dataset.pageAction === 'next' ? 1 : -1;
  updateJobsPage();
  jobsHost?.scrollIntoView({ behavior: 'smooth', block: 'start' });
});

async function refreshJobs() {
  if (!jobsHost || jobsRefreshRunning || document.hidden) return;
  jobsRefreshRunning = true;
  try {
    const response = await fetch(jobsHost.dataset.jobsUrl, { credentials: 'same-origin', cache: 'no-store' });
    if (!response.ok || response.redirected) return;
    const html = await response.text();
    const nextDocument = new DOMParser().parseFromString(html, 'text/html');
    const nextRegion = nextDocument.querySelector('[data-jobs-version]');
    const currentRegion = jobsHost.querySelector('[data-jobs-version]');
    if (nextRegion?.dataset.jobsVersion === currentRegion?.dataset.jobsVersion) return;
    const openIds = new Set([...jobsHost.querySelectorAll('details[open][data-disclosure-id]')].map(x => x.dataset.disclosureId));
    const previousStatuses = new Map([...jobsHost.querySelectorAll('.job-item')].map(x => [x.dataset.jobKey, x.dataset.jobStatus]));
    const previousActivity = jobsHost.querySelector('[data-job-activity]')?.dataset.activityVersion;
    jobsHost.innerHTML = html;
    jobsHost.querySelectorAll('details[data-disclosure-id]').forEach(details => { details.open = openIds.has(details.dataset.disclosureId); });
    let hasNewJob = false;
    jobsHost.querySelectorAll('.job-item').forEach(item => {
      const previousStatus = previousStatuses.get(item.dataset.jobKey);
      if (previousStatus === undefined) { item.classList.add('job-added'); hasNewJob = true; }
      else if (previousStatus !== item.dataset.jobStatus) item.classList.add('job-updated');
    });
    const activity = jobsHost.querySelector('[data-job-activity]');
    if (activity && previousActivity !== activity.dataset.activityVersion) activity.classList.add('activity-changed');
    if (hasNewJob) jobsPage = 1;
    updateJobsPage();
    updateSmartTimes();
  } catch {
    // Keep the current list visible and try again on the next interval.
  } finally {
    jobsRefreshRunning = false;
  }
}

document.querySelector('.tab[data-tab="jobs"]')?.addEventListener('click', refreshJobs);
if (jobsHost) { updateJobsPage(); updateSmartTimes(); setInterval(refreshJobs, 3000); setInterval(updateSmartTimes, 10000); }

const fileManager = document.querySelector('[data-file-manager]');
if (fileManager) {
  const browser = fileManager.querySelector('[data-file-browser]');
  const items = [...fileManager.querySelectorAll('[data-file-item]')];
  const checkboxes = [...fileManager.querySelectorAll('[data-file-checkbox]')];
  const search = fileManager.querySelector('[data-file-search]');
  const viewToggle = fileManager.querySelector('[data-file-view-toggle]');
  const deleteButton = fileManager.querySelector('[data-delete-selected]');
  const selectedCount = fileManager.querySelector('[data-selected-count]');
  const emptySearch = fileManager.querySelector('[data-search-empty]');

  const updateSelection = () => {
    const count = checkboxes.filter(checkbox => checkbox.checked).length;
    selectedCount.textContent = count;
    deleteButton.disabled = count === 0;
    items.forEach(item => item.classList.toggle('selected', item.querySelector('[data-file-checkbox]').checked));
  };
  const setView = view => {
    browser.dataset.view = view;
    const nextView = view === 'grid' ? 'list' : 'grid';
    viewToggle.textContent = nextView === 'list' ? '☷ List view' : '▦ Grid view';
    viewToggle.setAttribute('aria-label', `Switch to ${nextView} view`);
    localStorage.setItem('dan-loop-file-view', view);
  };
  const filterFiles = () => {
    const query = search.value.trim().toLocaleLowerCase();
    let visible = 0;
    items.forEach(item => {
      item.hidden = query.length > 0 && !item.dataset.fileName.includes(query) && !item.dataset.fileKind.includes(query);
      if (!item.hidden) visible++;
    });
    emptySearch.hidden = visible > 0 || query.length === 0;
  };

  fileManager.addEventListener('click', event => {
    const viewButton = event.target.closest('[data-file-view-toggle]');
    if (viewButton) setView(browser.dataset.view === 'grid' ? 'list' : 'grid');
    const selectionButton = event.target.closest('[data-select-files]');
    if (selectionButton) {
      checkboxes.forEach(checkbox => { checkbox.checked = selectionButton.dataset.selectFiles === 'all' && !checkbox.closest('[data-file-item]').hidden; });
      updateSelection();
    }
  });
  checkboxes.forEach(checkbox => checkbox.addEventListener('change', updateSelection));
  search.addEventListener('input', filterFiles);
  setView(localStorage.getItem('dan-loop-file-view') === 'list' ? 'list' : 'grid');
  updateSelection();
}
