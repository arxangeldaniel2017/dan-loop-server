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

document.querySelectorAll('[data-auto-dismiss-notice]').forEach(notice => {
  const text = notice.querySelector('span')?.textContent?.trim() || notice.textContent.trim();
  const isImportant = /error|failed|cannot|invalid|denied|warning|blocked/i.test(text);
  const duration = Math.min(isImportant ? 20000 : 14000, Math.max(isImportant ? 9000 : 4000, 2800 + text.length * 55));
  const timerBar = notice.querySelector('.notice-timer');
  let remaining = duration;
  let startedAt = 0;
  let timerId;
  const dismiss = () => {
    clearTimeout(timerId);
    notice.classList.add('notice-leaving');
    setTimeout(() => notice.remove(), 260);
  };
  const start = () => {
    clearTimeout(timerId);
    startedAt = performance.now();
    timerId = setTimeout(dismiss, remaining);
    if (timerBar) {
      timerBar.style.transition = `transform ${remaining}ms linear`;
      requestAnimationFrame(() => { timerBar.style.transform = 'scaleX(0)'; });
    }
  };
  const pause = () => {
    clearTimeout(timerId);
    remaining = Math.max(0, remaining - (performance.now() - startedAt));
    if (timerBar) { timerBar.style.transition = 'none'; timerBar.style.transform = `scaleX(${remaining / duration})`; }
  };
  notice.addEventListener('mouseenter', pause);
  notice.addEventListener('mouseleave', start);
  notice.addEventListener('focusin', pause);
  notice.addEventListener('focusout', event => { if (!notice.contains(event.relatedTarget)) start(); });
  notice.querySelector('[data-notice-close]')?.addEventListener('click', dismiss);
  start();
});

const aiCatalogNode = document.getElementById('ai-provider-catalog');
let aiCatalog = [];
try { aiCatalog = aiCatalogNode ? JSON.parse(aiCatalogNode.textContent) : []; } catch { aiCatalog = []; }
const providerIconUrl = provider => {
  if (provider?.iconUrl) return provider.iconUrl;
  const candidate = provider?.apiKeyUrl || provider?.documentationUrl || provider?.pricingUrl || provider?.baseUrl;
  try { return `${new URL(candidate).origin}/favicon.ico`; } catch { return ''; }
};
const serviceProviderIcons = {
  'Amazon S3': 'https://aws.amazon.com/favicon.ico',
  'DigitalOcean Spaces': 'https://cdn.simpleicons.org/digitalocean',
  'Cloudflare R2': 'https://cdn.simpleicons.org/cloudflare',
  'Backblaze B2': 'https://cdn.simpleicons.org/backblaze',
  'Wasabi': 'https://cdn.simpleicons.org/wasabi',
  'Google Cloud Storage': 'https://cdn.simpleicons.org/googlecloud',
  'Azure Blob Storage': 'https://azure.microsoft.com/favicon.ico',
  'Oracle Object Storage': 'https://www.oracle.com/favicon.ico',
  'IBM Cloud Object Storage': 'https://www.ibm.com/favicon.ico',
  'Storj': 'https://www.storj.io/assets/brand/storj-logo-mark-color.svg',
  'IDrive e2': 'https://www.idrive.com/favicon.ico',
  'Vultr Object Storage': 'https://cdn.simpleicons.org/vultr',
  'Linode Object Storage': 'https://cdn.simpleicons.org/akamai',
  'Scaleway Object Storage': 'https://cdn.simpleicons.org/scaleway',
  'OVHcloud Object Storage': 'https://cdn.simpleicons.org/ovh',
  'Hetzner Object Storage': 'https://cdn.simpleicons.org/hetzner',
  'Tigris Data': 'https://www.tigrisdata.com/favicon.ico',
  'Bunny Storage': 'https://cdn.simpleicons.org/bunnydotnet',
  'S3 compatible': 'https://aws.amazon.com/favicon.ico',
  'GitHub': 'https://cdn.simpleicons.org/github',
  'GitLab': 'https://cdn.simpleicons.org/gitlab',
  'Bitbucket': 'https://cdn.simpleicons.org/bitbucket',
  'Codeberg': 'https://cdn.simpleicons.org/codeberg',
  'Gitea Cloud': 'https://cdn.simpleicons.org/gitea',
  'SourceHut': 'https://cdn.simpleicons.org/sourcehut',
  'AWS CodeCommit': 'https://aws.amazon.com/favicon.ico',
  'Azure DevOps Repos': 'https://azure.microsoft.com/favicon.ico',
  'Google Cloud Secure Source Manager': 'https://cdn.simpleicons.org/googlecloud',
  'Gitee': 'https://cdn.simpleicons.org/gitee'
};
const serviceProviderIconUrl = name => serviceProviderIcons[name] || '';
const gitProviderLinks = {
  'GitHub': ['https://github.com/settings/personal-access-tokens/new', 'https://docs.github.com/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens'],
  'GitLab': ['https://gitlab.com/-/user_settings/personal_access_tokens', 'https://docs.gitlab.com/user/profile/personal_access_tokens/'],
  'Bitbucket': ['https://id.atlassian.com/manage-profile/security/api-tokens', 'https://support.atlassian.com/bitbucket-cloud/docs/create-an-api-token/'],
  'Codeberg': ['https://codeberg.org/user/settings/applications', 'https://docs.codeberg.org/advanced/access-token/'],
  'Gitea Cloud': ['https://gitea.com/user/settings/applications', 'https://docs.gitea.com/development/api-usage'],
  'SourceHut': ['https://meta.sr.ht/oauth2/personal-token', 'https://man.sr.ht/meta.sr.ht/oauth.md'],
  'AWS CodeCommit': ['https://console.aws.amazon.com/iam/home#/security_credentials', 'https://docs.aws.amazon.com/codecommit/latest/userguide/setting-up-gc.html'],
  'Azure DevOps Repos': ['https://dev.azure.com/_usersSettings/tokens', 'https://learn.microsoft.com/azure/devops/organizations/accounts/use-personal-access-tokens-to-authenticate'],
  'Google Cloud Secure Source Manager': ['https://console.cloud.google.com/iam-admin/serviceaccounts', 'https://cloud.google.com/secure-source-manager/docs/authentication'],
  'Gitee': ['https://gitee.com/profile/personal_access_tokens', 'https://gitee.com/api/v5/swagger']
};
const createIconSelect = (select, describeOption) => {
  if (!select || select.dataset.iconSelectReady) return null;
  select.dataset.iconSelectReady = 'true';
  select.classList.add('icon-select-native');
  const root = document.createElement('div'); root.className = 'icon-select';
  const button = document.createElement('button'); button.type = 'button'; button.className = 'icon-select-button'; button.setAttribute('aria-haspopup', 'listbox'); button.setAttribute('aria-expanded', 'false');
  const menu = document.createElement('div'); menu.className = 'icon-select-menu'; menu.hidden = true; menu.setAttribute('role', 'listbox');
  root.append(button, menu); select.insertAdjacentElement('afterend', root);
  const close = () => { menu.hidden = true; button.setAttribute('aria-expanded', 'false'); root.classList.remove('open'); };
  const open = () => { menu.hidden = false; button.setAttribute('aria-expanded', 'true'); root.classList.add('open'); menu.querySelector('[aria-selected="true"]')?.focus(); };
  const renderIdentity = (target, description, includeArrow = false) => {
    target.replaceChildren();
    const icon = document.createElement('span'); icon.className = 'icon-select-logo';
    const fallback = () => { icon.replaceChildren(); icon.textContent = description.label.trim().slice(0, 1).toUpperCase() || '•'; };
    if (description.icon) { const image = document.createElement('img'); image.src = description.icon; image.alt = ''; image.loading = 'lazy'; image.addEventListener('error', fallback); icon.append(image); } else fallback();
    const copy = document.createElement('span'); copy.className = 'icon-select-copy';
    const label = document.createElement('strong'); label.textContent = description.label; copy.append(label);
    if (description.meta) { const meta = document.createElement('small'); meta.textContent = description.meta; copy.append(meta); }
    target.append(icon, copy);
    if (includeArrow) { const arrow = document.createElement('span'); arrow.className = 'icon-select-arrow'; arrow.textContent = '▾'; target.append(arrow); }
  };
  const refresh = () => {
    menu.replaceChildren();
    [...select.options].forEach((option, index) => {
      const description = describeOption(option, index);
      const item = document.createElement('button'); item.type = 'button'; item.className = 'icon-select-option'; item.setAttribute('role', 'option'); item.setAttribute('aria-selected', String(option.value === select.value)); item.disabled = option.disabled;
      renderIdentity(item, description);
      item.addEventListener('click', () => { select.value = option.value; select.dispatchEvent(new Event('change', { bubbles: true })); close(); });
      menu.append(item);
    });
    const selected = select.options[select.selectedIndex] || select.options[0];
    renderIdentity(button, selected ? describeOption(selected, select.selectedIndex) : { label: 'Select' }, true);
  };
  button.addEventListener('click', () => menu.hidden ? open() : close());
  button.addEventListener('keydown', event => { if (event.key === 'Escape') close(); if (event.key === 'ArrowDown' && menu.hidden) { event.preventDefault(); open(); } });
  select.addEventListener('change', refresh);
  document.addEventListener('click', event => { if (!root.contains(event.target)) close(); });
  refresh();
  return { refresh, close };
};
function initializeConnectionUi(scope = document) {
scope.querySelectorAll('[data-ai-connection-form]').forEach(form => {
  if (form.dataset.connectionUiReady) return;
  form.dataset.connectionUiReady = 'true';
  const providerSelect = form.querySelector('[data-ai-provider]');
  const baseUrl = form.querySelector('input[name="baseUrl"]');
  const modelInput = form.querySelector('[data-ai-model-value]');
  const modelSelect = form.querySelector('[data-ai-model-select]');
  const customModel = form.querySelector('[data-ai-custom-model]');
  const customModelInput = form.querySelector('[data-ai-custom-model-input]');
  const authentication = form.querySelector('[data-ai-authentication]');
  const authenticationSelect = form.querySelector('[data-ai-authentication-select]');
  const apiKey = form.querySelector('[data-ai-api-key]');
  const apiKeyInput = apiKey?.querySelector('input');
  const keyHeader = form.querySelector('[data-ai-key-header]');
  const keyHeaderInput = keyHeader?.querySelector('input');
  const links = form.querySelector('[data-provider-links]');
  const loadModels = form.querySelector('[data-ai-load-models]');
  const discoveryStatus = form.querySelector('[data-ai-discovery-status]');
  let providerPicker;
  let modelPicker;
  const updateCapabilities = (provider) => {
    const exact = (provider?.models || []).find(item => String(item.id).toLowerCase() === String(modelInput?.value || '').toLowerCase());
    const modelName = String(modelInput?.value || '').toLowerCase();
    const inferred = modelName.includes('coder') || modelName.includes('codestral') || modelName.includes('code')
      ? ['Coder', 'Tool calling', 'Generator', 'Files'] : null;
    const allowed = new Set((exact?.capabilities || inferred || (provider?.models || []).flatMap(item => item.capabilities || [])));
    form.querySelectorAll('[data-ai-capability]').forEach(label => {
      const checkbox = label.querySelector('input');
      const visible = !provider || allowed.size === 0 || allowed.has(label.dataset.aiCapability);
      label.hidden = !visible;
      if (!visible) checkbox.checked = false;
      else if (exact) checkbox.checked = true;
    });
  };
  const updateAuthentication = (provider) => {
    if (!authenticationSelect) return;
    const selectable = Boolean(provider?.allowsAuthenticationSelection);
    authentication.hidden = !selectable;
    authenticationSelect.value = selectable ? authenticationSelect.value : (provider?.authenticationType || 'Bearer token');
    authenticationSelect.disabled = !selectable;
    if (!selectable) {
      let hidden = form.querySelector('input[type="hidden"][data-ai-fixed-auth]');
      if (!hidden) { hidden = document.createElement('input'); hidden.type = 'hidden'; hidden.name = 'authenticationType'; hidden.dataset.aiFixedAuth = 'true'; form.append(hidden); }
      hidden.value = authenticationSelect.value;
    } else form.querySelector('[data-ai-fixed-auth]')?.remove();
    const none = authenticationSelect.value === 'No authentication';
    const header = authenticationSelect.value === 'API key header';
    if (apiKey) apiKey.hidden = none;
    if (apiKeyInput) apiKeyInput.required = Boolean(provider?.apiKeyRequired) && !none;
    if (keyHeader) keyHeader.hidden = !header || (!selectable && Boolean(provider?.apiKeyHeaderName));
    if (keyHeaderInput && provider?.apiKeyHeaderName) keyHeaderInput.value = provider.apiKeyHeaderName;
  };
  const updateProvider = () => {
    const provider = aiCatalog.find(item => item.id === providerSelect?.value);
    if (!provider) return;
    if (baseUrl) {
      baseUrl.value = provider.baseUrl || '';
      baseUrl.readOnly = !provider.requiresBaseUrl && Boolean(provider.baseUrl);
      baseUrl.closest('label')?.classList.toggle('connection-field-fixed', baseUrl.readOnly);
    }
    if (modelSelect) {
      modelSelect.replaceChildren();
      modelSelect.add(new Option('Select a model', ''));
      (provider.models || []).filter(model => model.id !== 'custom').forEach(model => modelSelect.add(new Option(model.name, model.id)));
      modelSelect.add(new Option('Other model ID…', '__custom__'));
      modelSelect.value = '';
      if (modelInput) modelInput.value = '';
      if (customModel) customModel.hidden = true;
      if (customModelInput) { customModelInput.required = false; customModelInput.value = ''; }
      modelPicker?.refresh();
    }
    if (links) {
      const items = [provider.apiKeyUrl && ['Get API key', provider.apiKeyUrl], provider.documentationUrl && ['Documentation', provider.documentationUrl], provider.pricingUrl && ['Pricing', provider.pricingUrl]].filter(Boolean);
      links.innerHTML = items.map(([label, url]) => `<a href="${url}" target="_blank" rel="noopener">${label} ↗</a>`).join('');
    }
    updateAuthentication(provider);
    updateCapabilities(provider);
    const discoveryAvailable = provider.modelDiscovery && provider.modelDiscovery !== 'none' && (!provider.requiresBaseUrl || Boolean(baseUrl?.value.trim())) && (!provider.modelDiscoveryRequiresCredential || Boolean(apiKeyInput?.value.trim()));
    loadModels?.closest('.model-discovery-actions')?.toggleAttribute('hidden', !discoveryAvailable);
    if (discoveryStatus) { discoveryStatus.textContent = ''; discoveryStatus.classList.remove('error'); }
  };
  const updateModel = () => {
    const custom = modelSelect?.value === '__custom__';
    if (customModel) customModel.hidden = !custom;
    if (customModelInput) customModelInput.required = custom;
    if (modelInput) modelInput.value = custom ? (customModelInput?.value.trim() || '') : (modelSelect?.value || '');
    updateCapabilities(aiCatalog.find(item => item.id === providerSelect?.value));
  };
  const discoverModels = async () => {
    const provider = aiCatalog.find(item => item.id === providerSelect?.value);
    if (!provider || !loadModels) return;
    loadModels.disabled = true;
    const originalLabel = loadModels.textContent;
    loadModels.textContent = 'Loading…';
    if (discoveryStatus) { discoveryStatus.textContent = 'Contacting provider…'; discoveryStatus.classList.remove('error'); }
    const body = new FormData();
    body.append('providerId', provider.id);
    body.append('baseUrl', baseUrl?.value || provider.baseUrl || '');
    body.append('apiKey', apiKeyInput?.value || '');
    body.append('authenticationType', authenticationSelect?.value || provider.authenticationType || 'Bearer token');
    body.append('apiKeyHeaderName', keyHeaderInput?.value || provider.apiKeyHeaderName || 'Authorization');
    body.append('forceRefresh', 'false');
    const token = form.querySelector('input[name="__RequestVerificationToken"]')?.value || document.querySelector('input[name="__RequestVerificationToken"]')?.value;
    if (token) body.append('__RequestVerificationToken', token);
    try {
      const response = await fetch('/Manage/DiscoverModels', { method: 'POST', body, credentials: 'same-origin', headers: { 'X-Requested-With': 'XMLHttpRequest' } });
      const result = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(result.message || 'Models could not be loaded.');
      provider.models = Array.isArray(result.models) ? result.models : provider.models;
      updateProvider();
      if (discoveryStatus) discoveryStatus.textContent = result.message || 'Live models loaded.';
    } catch (error) {
      if (discoveryStatus) { discoveryStatus.textContent = error.message; discoveryStatus.classList.add('error'); }
    } finally {
      loadModels.disabled = false;
      loadModels.textContent = originalLabel;
    }
  };
  providerSelect?.addEventListener('change', updateProvider);
  baseUrl?.addEventListener('input', () => {
    const provider = aiCatalog.find(item => item.id === providerSelect?.value);
    const available = provider?.modelDiscovery && provider.modelDiscovery !== 'none' && (!provider.requiresBaseUrl || Boolean(baseUrl.value.trim())) && (!provider.modelDiscoveryRequiresCredential || Boolean(apiKeyInput?.value.trim()));
    loadModels?.closest('.model-discovery-actions')?.toggleAttribute('hidden', !available);
  });
  modelSelect?.addEventListener('change', updateModel);
  customModelInput?.addEventListener('input', updateModel);
  apiKeyInput?.addEventListener('input', () => {
    const provider = aiCatalog.find(item => item.id === providerSelect?.value);
    const available = provider?.modelDiscovery && provider.modelDiscovery !== 'none' && (!provider.requiresBaseUrl || Boolean(baseUrl?.value.trim())) && (!provider.modelDiscoveryRequiresCredential || Boolean(apiKeyInput.value.trim()));
    loadModels?.closest('.model-discovery-actions')?.toggleAttribute('hidden', !available);
  });
  authenticationSelect?.addEventListener('change', () => updateAuthentication(aiCatalog.find(item => item.id === providerSelect?.value)));
  loadModels?.addEventListener('click', discoverModels);
  providerPicker = createIconSelect(providerSelect, option => {
    const provider = aiCatalog.find(item => item.id === option.value);
    return { label: option.textContent, meta: provider?.category || '', icon: providerIconUrl(provider) };
  });
  modelPicker = createIconSelect(modelSelect, option => {
    const provider = aiCatalog.find(item => item.id === providerSelect?.value);
    return { label: option.textContent, meta: option.value && option.value !== '__custom__' ? option.value : '', icon: providerIconUrl(provider) };
  });
  updateProvider();
});

scope.querySelectorAll('[data-ai-load-saved-models]').forEach(button => {
  if (button.dataset.connectionUiReady) return;
  button.dataset.connectionUiReady = 'true';
  button.addEventListener('click', async () => {
    const status = button.parentElement?.querySelector('[data-ai-saved-discovery-status]');
    const list = document.getElementById(button.dataset.targetList);
    const body = new FormData();
    body.append('connectionId', button.dataset.connectionId || '');
    body.append('providerId', button.dataset.providerId || '');
    body.append('baseUrl', button.dataset.baseUrl || '');
    body.append('forceRefresh', 'false');
    const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value;
    if (token) body.append('__RequestVerificationToken', token);
    button.disabled = true;
    if (status) { status.textContent = 'Loading…'; status.classList.remove('error'); }
    try {
      const response = await fetch('/Manage/DiscoverModels', { method: 'POST', body, credentials: 'same-origin', headers: { 'X-Requested-With': 'XMLHttpRequest' } });
      const result = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(result.message || 'Models could not be loaded.');
      if (list) {
        list.replaceChildren();
        (result.models || []).forEach(model => { const option = document.createElement('option'); option.value = model.id; option.textContent = model.name; list.append(option); });
      }
      if (status) status.textContent = `${result.discoveredCount || 0} live models loaded. Open Edit to choose one.`;
    } catch (error) {
      if (status) { status.textContent = error.message; status.classList.add('error'); }
    } finally { button.disabled = false; }
  });
});

scope.querySelectorAll('[data-storage-connection-form]').forEach(form => {
  if (form.dataset.connectionUiReady) return;
  form.dataset.connectionUiReady = 'true';
  const provider = form.querySelector('[data-storage-provider]');
  const endpoint = form.querySelector('[data-storage-endpoint]');
  const endpointInput = endpoint?.querySelector('input');
  const regionInput = form.querySelector('input[name="region"]');
  const session = form.querySelector('[data-storage-session]');
  const pathStyle = form.querySelector('[data-storage-path-style]');
  const pathStyleInput = pathStyle?.querySelector('input');
  const secret = form.querySelector('[data-storage-secret]');
  const secretInput = secret?.querySelector('input');
  const accessLabel = form.querySelector('[data-storage-access-label]');
  const secretLabel = form.querySelector('[data-storage-secret-label]');
  const containerLabel = form.querySelector('[data-storage-container-label]');
  const links = form.querySelector('[data-storage-links]');
  let initialized = false;
  const updateStorageProvider = () => {
    const value = provider?.value || 'Amazon S3';
    const option = provider?.selectedOptions?.[0];
    const kind = option?.dataset.kind || 's3';
    const isAws = value === 'Amazon S3';
    const isBunny = kind === 'bunny';
    const isAzure = kind === 'azure';
    if (initialized) {
      if (regionInput) regionInput.value = option?.dataset.defaultRegion || '';
      if (endpointInput) endpointInput.value = '';
      if (pathStyleInput) pathStyleInput.checked = option?.dataset.forcePath === 'true';
    }
    if (endpoint) endpoint.hidden = isAws;
    if (endpointInput) {
      endpointInput.required = option?.dataset.endpointRequired === 'true';
      endpointInput.placeholder = endpointInput.required ? 'Required provider endpoint URL' : 'Optional — inferred when possible';
    }
    if (session) session.hidden = option?.dataset.session !== 'true';
    if (pathStyle) pathStyle.hidden = value !== 'S3 compatible';
    if (secret) secret.hidden = isBunny;
    if (secretInput) secretInput.required = !isBunny && !form.querySelector('input[name="id"]');
    if (accessLabel) accessLabel.textContent = isAzure ? (form.querySelector('input[name="id"]') ? 'New account name' : 'Storage account name') : isBunny ? (form.querySelector('input[name="id"]') ? 'New API password' : 'Storage-zone API password') : (form.querySelector('input[name="id"]') ? 'New access key' : 'Access key');
    if (secretLabel) secretLabel.textContent = isAzure ? (form.querySelector('input[name="id"]') ? 'New account key' : 'Storage account key') : (form.querySelector('input[name="id"]') ? 'New secret key' : 'Secret key');
    if (containerLabel) containerLabel.textContent = isAzure ? 'Container' : isBunny ? 'Storage zone' : 'Bucket';
    if (links && option?.dataset.docs) links.innerHTML = `<a href="${option.dataset.docs}" target="_blank" rel="noopener">${value} documentation ↗</a>`;
    initialized = true;
  };
  provider?.addEventListener('change', updateStorageProvider);
  createIconSelect(provider, option => ({
    label: option.textContent,
    meta: option.dataset.kind === 'azure' ? 'Blob storage' : option.dataset.kind === 'bunny' ? 'Edge storage' : 'S3-compatible storage',
    icon: serviceProviderIconUrl(option.value)
  }));
  updateStorageProvider();
});

scope.querySelectorAll('[data-git-provider]').forEach(select => {
  if (select.dataset.connectionUiReady) return;
  select.dataset.connectionUiReady = 'true';
  const links = select.closest('form')?.querySelector('[data-git-provider-links]');
  const updateGitProviderLinks = () => {
    if (!links) return;
    const [credentialUrl, documentationUrl] = gitProviderLinks[select.value] || [];
    links.replaceChildren();
    if (credentialUrl) {
      const credentialLink = document.createElement('a');
      credentialLink.href = credentialUrl;
      credentialLink.target = '_blank';
      credentialLink.rel = 'noopener';
      credentialLink.textContent = 'Get access token / API key ↗';
      links.append(credentialLink);
    }
    if (documentationUrl) {
      const documentationLink = document.createElement('a');
      documentationLink.href = documentationUrl;
      documentationLink.target = '_blank';
      documentationLink.rel = 'noopener';
      documentationLink.textContent = 'Credential documentation ↗';
      links.append(documentationLink);
    }
  };
  select.addEventListener('change', updateGitProviderLinks);
  createIconSelect(select, option => ({
    label: option.textContent,
    meta: 'Git provider',
    icon: serviceProviderIconUrl(option.value)
  }));
  updateGitProviderLinks();
});

scope.querySelectorAll('[data-model-capability]').forEach(checkbox => {
  if (checkbox.dataset.connectionUiReady) return;
  checkbox.dataset.connectionUiReady = 'true';
  checkbox.addEventListener('change', async () => {
    checkbox.disabled = true;
    const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value;
    const body = new FormData(); body.append('id', checkbox.dataset.modelId); body.append('capability', checkbox.value); body.append('enabled', String(checkbox.checked));
    if (token) body.append('__RequestVerificationToken', token);
    try {
      const response = await fetch('/Manage/SetModelCapability', { method: 'POST', body, credentials: 'same-origin', headers: { 'X-Requested-With': 'XMLHttpRequest' } });
      if (!response.ok) throw new Error('Capability could not be saved.');
      checkbox.closest('.capability-chip')?.classList.add('saved');
      setTimeout(() => checkbox.closest('.capability-chip')?.classList.remove('saved'), 700);
      checkbox.closest('.connection-card')?.querySelector('.capability-default')?.remove();
    } catch (error) { checkbox.checked = !checkbox.checked; alert(error.message); }
    finally { checkbox.disabled = false; }
  });
});
}

initializeConnectionUi();

document.addEventListener('click', event => {
  const link = event.target.closest('[data-collapse-add-form]');
  if (!link) return;
  const section = link.closest('.wizard-connection-section');
  (section || link.closest('[data-connection-workspace]'))?.querySelectorAll('.wizard-add-form').forEach(details => details.removeAttribute('open'));
});

document.addEventListener('submit', async event => {
  const form = event.target.closest('[data-connection-workspace] form[method="post"]');
  if (!form || form.dataset.ajaxBypass === 'true' || event.defaultPrevented) return;
  event.preventDefault();
  const workspace = form.closest('[data-connection-workspace]');
  const workspaceName = workspace?.dataset.connectionWorkspace;
  const submitter = event.submitter;
  const button = submitter instanceof HTMLButtonElement ? submitter : form.querySelector('button[type="submit"], button:not([type])');
  const originalLabel = button?.textContent;
  if (button) { button.disabled = true; button.textContent = 'Saving…'; }
  workspace?.setAttribute('aria-busy', 'true');
  try {
    const body = new FormData(form);
    if (submitter?.name) body.set(submitter.name, submitter.value);
    const response = await fetch(form.action, { method: 'POST', body, credentials: 'same-origin', headers: { 'X-Requested-With': 'XMLHttpRequest' } });
    const html = await response.text();
    if (!response.ok) throw new Error(`The action could not be completed (${response.status}).`);
    let nextDocument = new DOMParser().parseFromString(html, 'text/html');
    let nextWorkspace = nextDocument.querySelector(`[data-connection-workspace="${CSS.escape(workspaceName)}"]`);
    const responseUrl = new URL(response.url, location.origin);
    if (!nextWorkspace && ['/account/setup', '/account/login'].includes(responseUrl.pathname.toLowerCase())) {
      location.assign(responseUrl.href);
      return;
    }
    if (!nextWorkspace) {
      const refreshUrl = workspaceName === 'wizard' ? '/Account/Connections' : '/';
      const refreshResponse = await fetch(refreshUrl, { credentials: 'same-origin', cache: 'no-store' });
      if (!refreshResponse.ok) throw new Error('The updated connection details could not be loaded.');
      nextDocument = new DOMParser().parseFromString(await refreshResponse.text(), 'text/html');
      nextWorkspace = nextDocument.querySelector(`[data-connection-workspace="${CSS.escape(workspaceName)}"]`);
      const refreshResponseUrl = new URL(refreshResponse.url, location.origin);
      if (!nextWorkspace && ['/account/setup', '/account/login'].includes(refreshResponseUrl.pathname.toLowerCase())) {
        location.assign(refreshResponseUrl.href);
        return;
      }
    }
    if (!nextWorkspace) throw new Error('The updated connection details could not be loaded.');
    const notice = nextDocument.querySelector('[data-auto-dismiss-notice] span')?.textContent?.trim();
    workspace.replaceChildren(...nextWorkspace.childNodes);
    initializeConnectionUi(workspace);
    if (notice) {
      const status = document.createElement('div');
      status.className = 'notice connection-ajax-notice'; status.setAttribute('role', 'status'); status.textContent = notice;
      workspace.prepend(status);
      setTimeout(() => status.remove(), 7000);
    }
  } catch (error) {
    const status = document.createElement('div');
    status.className = 'error connection-ajax-notice'; status.setAttribute('role', 'alert'); status.textContent = error.message || 'The action could not be completed.';
    workspace?.querySelector('.connection-ajax-notice')?.remove();
    workspace?.prepend(status);
  } finally {
    workspace?.removeAttribute('aria-busy');
    if (button?.isConnected) { button.disabled = false; button.textContent = originalLabel; }
  }
});

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

const liveHistoryLists = new Map();
document.querySelectorAll('[data-live-list]').forEach(host => {
  const state = { host, loading: false };
  liveHistoryLists.set(host.id, state);
  const bindScroll = () => {
    const scroll = host.querySelector('[data-live-scroll]');
    if (!scroll || scroll.dataset.bound === 'true') return;
    scroll.dataset.bound = 'true';
    scroll.addEventListener('scroll', () => {
      const region = host.querySelector('[data-live-version]');
      if (region?.dataset.hasMore === 'true' && scroll.scrollTop + scroll.clientHeight >= scroll.scrollHeight - 70)
        refreshLiveHistory(state, Number(region.dataset.visibleCount || 10) + 10, true);
    });
  };
  state.bindScroll = bindScroll;
  bindScroll();
});

async function refreshLiveHistory(state, requestedTake, force = false) {
  if (!state || state.loading || document.hidden) return;
  state.loading = true;
  const host = state.host;
  const currentRegion = host.querySelector('[data-live-version]');
  const take = Math.max(10, requestedTake || Number(currentRegion?.dataset.visibleCount || 10));
  try {
    const url = new URL(host.dataset.liveUrl, location.origin); url.searchParams.set('take', String(take));
    const response = await fetch(url, { credentials: 'same-origin', cache: 'no-store' });
    if (!response.ok || response.redirected) return;
    const html = await response.text();
    const next = document.createElement('div'); next.innerHTML = html;
    const nextRegion = next.querySelector('[data-live-version]');
    if (!force && currentRegion?.dataset.liveVersion === nextRegion?.dataset.liveVersion && Number(currentRegion?.dataset.visibleCount) === Number(nextRegion?.dataset.visibleCount)) return;
    const openKeys = new Set([...host.querySelectorAll('details[open][data-live-key]')].map(item => item.dataset.liveKey));
    const previousScroll = host.querySelector('[data-live-scroll]')?.scrollTop || 0;
    host.innerHTML = html;
    host.querySelectorAll('details[data-live-key]').forEach(item => { item.open = openKeys.has(item.dataset.liveKey); });
    const nextScroll = host.querySelector('[data-live-scroll]');
    if (nextScroll) nextScroll.scrollTop = previousScroll;
    state.bindScroll();
  } catch {
    // Keep the retained history visible and retry on the next refresh.
  } finally { state.loading = false; }
}

const refreshAllLiveHistory = () => liveHistoryLists.forEach(state => refreshLiveHistory(state));
document.querySelector('.tab[data-tab="chats"]')?.addEventListener('click', refreshAllLiveHistory);
if (liveHistoryLists.size) setInterval(refreshAllLiveHistory, 3000);

document.addEventListener('submit', async event => {
  const form = event.target.closest('[data-live-clear]');
  if (!form) return;
  event.preventDefault();
  if (form.dataset.confirm && !confirm(form.dataset.confirm)) return;
  const button = form.querySelector('button[type="submit"], button:not([type])');
  if (button) button.disabled = true;
  try {
    const response = await fetch(form.action, { method: 'POST', body: new FormData(form), credentials: 'same-origin', headers: { 'X-Requested-With': 'XMLHttpRequest' } });
    if (!response.ok) throw new Error('Could not clear this history.');
    const state = liveHistoryLists.get(form.dataset.clearTarget);
    if (state) await refreshLiveHistory(state, 10, true);
  } catch (error) { console.warn(error); }
  finally { if (button) button.disabled = false; }
});

const planStepsHost = document.getElementById('plan-steps-list');
let planStepsRefreshing = false;
async function refreshPlanSteps() {
  if (!planStepsHost || planStepsRefreshing || document.hidden) return;
  planStepsRefreshing = true;
  try {
    const response = await fetch(planStepsHost.dataset.planStepsUrl, { credentials: 'same-origin', cache: 'no-store' });
    if (!response.ok || response.redirected) return;
    const html = await response.text();
    const next = document.createElement('div'); next.innerHTML = html;
    const currentVersion = planStepsHost.querySelector('[data-plan-steps-version]')?.dataset.planStepsVersion;
    const nextVersion = next.querySelector('[data-plan-steps-version]')?.dataset.planStepsVersion;
    if (currentVersion === nextVersion) return;
    const openSteps = new Set([...planStepsHost.querySelectorAll('.plan-step[open]')].map(item => item.querySelector('input[name="stepId"]')?.value).filter(Boolean));
    const openPlans = new Set([...planStepsHost.querySelectorAll('.plan-board[open]')].map(item => item.dataset.planId));
    planStepsHost.innerHTML = html;
    planStepsHost.querySelectorAll('.plan-step').forEach(item => { const id = item.querySelector('input[name="stepId"]')?.value; if (id && openSteps.has(id)) item.open = true; });
    planStepsHost.querySelectorAll('.plan-board').forEach(item => { item.open = openPlans.has(item.dataset.planId); });
  } catch {
    // Keep the durable step log visible while the server is temporarily unavailable.
  } finally { planStepsRefreshing = false; }
}
document.querySelector('.tab[data-tab="steps"]')?.addEventListener('click', refreshPlanSteps);
if (planStepsHost) setInterval(refreshPlanSteps, 3000);

const toolAuditHost = document.getElementById('tool-audit-list');
if (toolAuditHost) {
  let toolAuditRefreshing = false;
  setInterval(async () => {
    if (toolAuditRefreshing || document.visibilityState !== 'visible') return;
    toolAuditRefreshing = true;
    try {
      const response = await fetch(toolAuditHost.dataset.toolAuditUrl, { credentials: 'same-origin', cache: 'no-store' });
      if (!response.ok || response.redirected) return;
      const html = await response.text();
      const next = document.createElement('div');
      next.innerHTML = html;
      const currentVersion = toolAuditHost.querySelector('[data-tool-audit-version]')?.dataset.toolAuditVersion;
      const nextVersion = next.querySelector('[data-tool-audit-version]')?.dataset.toolAuditVersion;
      if (currentVersion !== nextVersion) toolAuditHost.innerHTML = html;
    } catch {
      // Keep the current audit visible while the server restarts or is temporarily unavailable.
    } finally { toolAuditRefreshing = false; }
  }, 3000);
}

function initializeFileManager(fileManager) {
  if (!fileManager || fileManager.dataset.initialized === 'true') return;
  fileManager.dataset.initialized = 'true';
  const browser = fileManager.querySelector('[data-file-browser]');
  const items = [...fileManager.querySelectorAll('[data-file-item]')];
  const checkboxes = [...fileManager.querySelectorAll('[data-file-checkbox]')];
  const search = fileManager.querySelector('[data-file-search]');
  const viewToggle = fileManager.querySelector('[data-file-view-toggle]');
  const selectionActions = [...fileManager.querySelectorAll('[data-selection-action]')];
  const selectedCounts = [...fileManager.querySelectorAll('[data-selected-count]')];
  const emptySearch = fileManager.querySelector('[data-search-empty]');
  const moveForm = fileManager.querySelector('[data-move-files-form]');
  let draggedPaths = [], dragGhost = null, draggedAt = 0;

  const updateSelection = () => {
    const count = checkboxes.filter(checkbox => checkbox.checked).length;
    selectedCounts.forEach(label => { label.textContent = count; });
    selectionActions.forEach(button => { button.disabled = count === 0; });
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
  const clearDropTargets = () => fileManager.querySelectorAll('.drag-over').forEach(target => target.classList.remove('drag-over'));
  const pathForItem = item => item?.dataset.filePath || '';
  const buildDragGhost = (item, count) => {
    const ghost = document.createElement('div');
    ghost.className = 'file-drag-ghost';
    ghost.innerHTML = `<span>${item.dataset.fileDirectory === 'true' ? '📁' : '↗'}</span><strong>${item.querySelector('.explorer-main strong')?.textContent || 'Item'}</strong>${count > 1 ? `<b>+${count - 1}</b>` : ''}`;
    document.body.appendChild(ghost); return ghost;
  };
  const finishDrag = () => {
    clearDropTargets(); items.forEach(item => item.classList.remove('dragging'));
    dragGhost?.remove(); dragGhost = null; draggedPaths = []; draggedAt = Date.now();
  };

  items.forEach(item => {
    item.addEventListener('dragstart', event => {
      const ownPath = pathForItem(item);
      const ownCheckbox = item.querySelector('[data-file-checkbox]');
      draggedPaths = ownCheckbox.checked ? checkboxes.filter(box => box.checked).map(box => box.value) : [ownPath];
      if (!draggedPaths.length) { event.preventDefault(); return; }
      items.filter(candidate => draggedPaths.includes(pathForItem(candidate))).forEach(candidate => candidate.classList.add('dragging'));
      event.dataTransfer.effectAllowed = 'move';
      event.dataTransfer.setData('application/x-dan-loop-paths', JSON.stringify(draggedPaths));
      event.dataTransfer.setData('text/plain', draggedPaths.join('\n'));
      dragGhost = buildDragGhost(item, draggedPaths.length);
      event.dataTransfer.setDragImage(dragGhost, 24, 24);
    });
    item.addEventListener('dragend', finishDrag);
  });
  fileManager.querySelectorAll('[data-drop-path]').forEach(target => {
    target.addEventListener('dragenter', event => {
      if (!draggedPaths.length || draggedPaths.includes(target.dataset.dropPath)) return;
      event.preventDefault(); clearDropTargets(); target.classList.add('drag-over');
    });
    target.addEventListener('dragover', event => {
      if (!draggedPaths.length || draggedPaths.includes(target.dataset.dropPath)) return;
      event.preventDefault(); event.dataTransfer.dropEffect = 'move';
    });
    target.addEventListener('dragleave', event => { if (!target.contains(event.relatedTarget)) target.classList.remove('drag-over'); });
    target.addEventListener('drop', event => {
      event.preventDefault(); event.stopPropagation();
      const paths = draggedPaths.length ? [...draggedPaths] : JSON.parse(event.dataTransfer.getData('application/x-dan-loop-paths') || '[]');
      const destination = target.dataset.dropPath || '';
      if (!moveForm || !paths.length || paths.includes(destination)) { finishDrag(); return; }
      moveForm.querySelectorAll('input[data-move-source]').forEach(input => input.remove());
      paths.forEach(path => {
        const input = document.createElement('input'); input.type = 'hidden'; input.name = 'sourcePaths'; input.value = path; input.dataset.moveSource = 'true'; moveForm.appendChild(input);
      });
      moveForm.querySelector('[data-move-destination]').value = destination;
      items.filter(candidate => paths.includes(pathForItem(candidate))).forEach(candidate => candidate.classList.add('file-dropping'));
      clearDropTargets(); target.classList.add('drop-received');
      draggedAt = Date.now();
      setTimeout(() => { finishDrag(); moveForm.requestSubmit(); }, 360);
    });
  });
  fileManager.addEventListener('click', event => {
    if (Date.now() - draggedAt < 500) { event.preventDefault(); event.stopPropagation(); }
  }, true);

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
  fileManager.querySelector('[data-download-selected-form]')?.addEventListener('submit', event => {
    const form = event.currentTarget;
    form.querySelectorAll('input[data-generated-selection]').forEach(input => input.remove());
    checkboxes.filter(checkbox => checkbox.checked).forEach(checkbox => {
      const input = document.createElement('input');
      input.type = 'hidden'; input.name = 'selectedPaths'; input.value = checkbox.value; input.dataset.generatedSelection = 'true';
      form.appendChild(input);
    });
  });
  search?.addEventListener('input', filterFiles);
  setView(localStorage.getItem('dan-loop-file-view') === 'list' ? 'list' : 'grid');
  updateSelection();
}

const preserveScroll = async operation => {
  const x = window.scrollX, y = window.scrollY;
  const previousAnchor = document.documentElement.style.overflowAnchor;
  document.documentElement.style.overflowAnchor = 'none';
  try { return await operation(); }
  finally {
    const restore = () => window.scrollTo(x, y);
    restore(); requestAnimationFrame(restore); setTimeout(() => { restore(); document.documentElement.style.overflowAnchor = previousAnchor; }, 80);
  }
};

const escapeCode = value => value.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;');
const languageKeywords = {
  csharp: ['class','public','private','protected','internal','static','async','await','using','namespace','return','new','var','string','int','bool','null','true','false','if','else','foreach','while','try','catch'],
  javascript: ['const','let','function','async','await','return','class','new','import','export','if','else','for','while','try','catch','true','false','null','undefined'],
  typescript: ['const','let','function','interface','type','class','public','private','async','await','return','import','export','string','number','boolean','unknown','null'],
  python: ['def','class','import','from','return','async','await','if','elif','else','for','while','try','except','with','True','False','None'],
  json: ['true','false','null'], xml: [], html: ['div','section','article','header','footer','main','button','input','form','label','span'],
  css: ['display','position','color','background','border','padding','margin','width','height','grid','flex','var'],
  sql: ['SELECT','FROM','WHERE','JOIN','INSERT','UPDATE','DELETE','CREATE','TABLE','ORDER','GROUP','BY','AS','AND','OR','NULL']
};

function highlightedCode(value, language) {
  const keywords = new Set(languageKeywords[language] || []);
  const pattern = language === 'xml' || language === 'html'
    ? /<!--[\s\S]*?-->|<[^>]+>|"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|\b-?\d+(?:\.\d+)?\b|\b[A-Za-z_$][\w$-]*\b/g
    : /\/\/[^\n]*|#[^\n]*|"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|`(?:\\.|[^`\\])*`|\b-?\d+(?:\.\d+)?\b|\b[A-Za-z_$][\w$-]*\b/g;
  let output = '', last = 0;
  for (const match of value.matchAll(pattern)) {
    const token = match[0], offset = match.index;
    output += escapeCode(value.slice(last, offset));
    let className = '';
    if (token.startsWith('//') || token.startsWith('#') || token.startsWith('<!--')) className = 'syntax-comment';
    else if (token.startsWith('<')) className = 'syntax-key';
    else if (/^["'`]/.test(token)) className = language === 'json' && /^\s*:/.test(value.slice(offset + token.length)) ? 'syntax-key' : 'syntax-string';
    else if (/^(true|false|null|undefined|-?\d)/.test(token)) className = 'syntax-value';
    else if (keywords.has(token)) className = 'syntax-keyword';
    output += className ? `<span class="${className}">${escapeCode(token)}</span>` : escapeCode(token);
    last = offset + token.length;
  }
  return output + escapeCode(value.slice(last));
}

function initializeTextEditor(panel) {
  if (!panel || panel.dataset.initialized === 'true') return;
  panel.dataset.initialized = 'true';
  const form = panel.querySelector('[data-editor-form]');
  const editor = panel.querySelector('[data-source-editor]');
  const preview = panel.querySelector('[data-syntax-preview]');
  const plainEditor = panel.querySelector('[data-plain-editor]');
  const codeToggle = panel.querySelector('[data-editor-code]');
  const darkToggle = panel.querySelector('[data-editor-dark]');
  const state = panel.querySelector('[data-editor-state]');
  const position = panel.querySelector('[data-editor-position]');
  const suggestions = panel.querySelector('[data-completion-strip]');
  const language = panel.dataset.language;
  const projectId = form.querySelector('[name="projectId"]').value;
  const path = form.querySelector('[name="path"]').value;
  const draftKey = `dan-loop-editor-draft:${projectId}:${path}`;
  const autosaveDelay = 4000;
  let revision = 0, savedRevision = 0, saveRunning = false, saveAgain = false;
  let highlightTimer, autosaveTimer;
  const storedCode = localStorage.getItem('dan-loop-editor-code');
  const storedDark = localStorage.getItem('dan-loop-editor-dark');
  codeToggle.checked = storedCode === null ? true : storedCode === 'true';
  darkToggle.checked = storedDark === 'true';
  const applyDisplay = () => {
    panel.classList.toggle('editor-code-mode', codeToggle.checked);
    preview.hidden = !codeToggle.checked;
    panel.classList.toggle('editor-dark', darkToggle.checked);
    localStorage.setItem('dan-loop-editor-code', String(codeToggle.checked));
    localStorage.setItem('dan-loop-editor-dark', String(darkToggle.checked));
  };
  const updatePositionAndSuggestions = () => {
    const before = editor.value.slice(0, editor.selectionStart), lines = before.split('\n');
    position.textContent = `Line ${lines.length}, column ${lines.at(-1).length + 1}`;
    const prefix = before.match(/[A-Za-z_$][\w$-]*$/)?.[0] || '';
    const documentWords = editor.value.match(/[A-Za-z_$][\w$-]{2,}/g) || [];
    const candidates = [...new Set([...(languageKeywords[language] || []), ...documentWords])].filter(word => prefix && word !== prefix && word.toLowerCase().startsWith(prefix.toLowerCase())).slice(0, 7);
    suggestions.innerHTML = candidates.map(word => `<button type="button" data-completion="${word.replaceAll('"','&quot;')}">${word}</button>`).join('');
  };
  const updateHighlight = () => {
    preview.innerHTML = highlightedCode(editor.value + (editor.value.endsWith('\n') ? ' ' : ''), language);
    preview.scrollTop = editor.scrollTop; preview.scrollLeft = editor.scrollLeft;
  };
  const rememberDraft = () => {
    try { localStorage.setItem(draftKey, JSON.stringify({ content: editor.value, selectionStart: editor.selectionStart, selectionEnd: editor.selectionEnd, updatedAt: Date.now() })); return true; }
    catch { state.textContent = 'Unsaved changes · local draft is too large'; return false; }
  };
  const save = async (manual = false) => {
    clearTimeout(autosaveTimer);
    if (saveRunning) { saveAgain = true; return; }
    if (!manual && revision === savedRevision) return;
    saveRunning = true;
    const savingRevision = revision;
    state.textContent = manual ? 'Saving…' : 'Autosaving…';
    const saveButton = panel.querySelector('[data-editor-save]');
    if (saveButton) saveButton.disabled = true;
    try {
      const data = new FormData(form);
      data.set('content', editor.value);
      data.set('autosave', String(!manual));
      const response = await fetch(form.action, { method: 'POST', body: data, credentials: 'same-origin', headers: { 'X-Requested-With': 'XMLHttpRequest' } });
      if (!response.ok || response.redirected) throw new Error(await response.text() || 'Save failed.');
      if (revision === savingRevision) {
        savedRevision = savingRevision;
        try { localStorage.removeItem(draftKey); } catch { }
        state.textContent = manual ? 'Saved' : 'Autosaved';
      } else {
        state.textContent = 'New changes waiting to save';
        saveAgain = true;
      }
    } catch (error) {
      rememberDraft();
      state.textContent = 'Save failed · draft kept on this device';
      console.warn('Editor save failed. The local draft was retained.', error);
      autosaveTimer = setTimeout(() => save(false), 8000);
    } finally {
      saveRunning = false;
      if (saveButton) saveButton.disabled = false;
      if (saveAgain) { saveAgain = false; autosaveTimer = setTimeout(() => save(false), 500); }
    }
  };
  const changed = () => {
    revision += 1;
    rememberDraft();
    updatePositionAndSuggestions();
    state.textContent = 'Unsaved changes · autosaves in 4 seconds';
    clearTimeout(highlightTimer); clearTimeout(autosaveTimer);
    highlightTimer = setTimeout(updateHighlight, 350);
    autosaveTimer = setTimeout(() => save(false), autosaveDelay);
  };
  const insertCompletion = word => {
    const start = editor.selectionStart, before = editor.value.slice(0, start), prefix = before.match(/[A-Za-z_$][\w$-]*$/)?.[0] || '';
    editor.setRangeText(word, start - prefix.length, editor.selectionEnd, 'end'); editor.focus(); changed();
  };
  suggestions.addEventListener('click', event => { const button = event.target.closest('[data-completion]'); if (button) insertCompletion(button.dataset.completion); });
  editor.addEventListener('input', changed);
  editor.addEventListener('click', updatePositionAndSuggestions);
  editor.addEventListener('keyup', updatePositionAndSuggestions);
  editor.addEventListener('select', updatePositionAndSuggestions);
  editor.addEventListener('scroll', () => { preview.scrollTop = editor.scrollTop; preview.scrollLeft = editor.scrollLeft; });
  editor.addEventListener('keydown', event => {
    if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 's') { event.preventDefault(); save(true); return; }
    if (event.key === 'Tab') { event.preventDefault(); editor.setRangeText('  ', editor.selectionStart, editor.selectionEnd, 'end'); changed(); return; }
    const pairs = { '(': ')', '[': ']', '{': '}', '"': '"', "'": "'" };
    if (pairs[event.key] && editor.selectionStart === editor.selectionEnd) { event.preventDefault(); const start = editor.selectionStart; editor.setRangeText(event.key + pairs[event.key], start, start, 'end'); editor.setSelectionRange(start + 1, start + 1); changed(); }
  });
  form.addEventListener('submit', event => { event.preventDefault(); event.stopPropagation(); save(true); });
  codeToggle.addEventListener('change', () => { updateHighlight(); applyDisplay(); editor.focus(); });
  darkToggle.addEventListener('change', applyDisplay);
  let recovered = false;
  try {
    const draft = JSON.parse(localStorage.getItem(draftKey) || 'null');
    if (draft?.content !== undefined && draft.content !== editor.value) {
      editor.value = draft.content; revision = 1; recovered = true;
      requestAnimationFrame(() => editor.setSelectionRange(draft.selectionStart || 0, draft.selectionEnd || draft.selectionStart || 0));
    }
  } catch { try { localStorage.removeItem(draftKey); } catch { } }
  updateHighlight(); updatePositionAndSuggestions(); applyDisplay();
  if (recovered) { state.textContent = 'Recovered unsaved changes · autosaves in 4 seconds'; autosaveTimer = setTimeout(() => save(false), autosaveDelay); }
  else state.textContent = 'Ready · autosave on';
}

function initializeImageEditor(panel) {
  if (!panel || panel.dataset.initialized === 'true') return;
  panel.dataset.initialized = 'true';
  const image = panel.querySelector('[data-image-source]'), canvas = panel.querySelector('[data-image-canvas]'), context = canvas.getContext('2d');
  const brightness = panel.querySelector('[data-image-brightness]'), grayscale = panel.querySelector('[data-image-grayscale]'), state = panel.querySelector('[data-image-state]');
  let rotation = 0;
  const draw = () => {
    const sideways = Math.abs(rotation % 180) === 90;
    canvas.width = sideways ? image.naturalHeight : image.naturalWidth; canvas.height = sideways ? image.naturalWidth : image.naturalHeight;
    context.save(); context.translate(canvas.width / 2, canvas.height / 2); context.rotate(rotation * Math.PI / 180);
    context.filter = `brightness(${brightness.value}%) grayscale(${grayscale.checked ? 100 : 0}%)`;
    context.drawImage(image, -image.naturalWidth / 2, -image.naturalHeight / 2); context.restore(); state.textContent = 'Preview updated';
  };
  image.addEventListener('load', () => { draw(); state.textContent = 'Ready'; });
  if (image.complete) draw();
  panel.querySelector('.image-editor-tools').addEventListener('click', event => {
    const action = event.target.closest('[data-image-action]')?.dataset.imageAction; if (!action) return;
    if (action === 'rotate-left') rotation -= 90; if (action === 'rotate-right') rotation += 90;
    if (action === 'reset') { rotation = 0; brightness.value = 100; grayscale.checked = false; } draw();
  });
  brightness.addEventListener('input', draw); grayscale.addEventListener('change', draw);
  const form = panel.querySelector('[data-image-save-form]');
  if (form) form.addEventListener('submit', () => {
    const extension = form.querySelector('[name="path"]').value.split('.').pop().toLowerCase();
    const mime = extension === 'png' ? 'image/png' : extension === 'webp' ? 'image/webp' : 'image/jpeg';
    form.querySelector('[data-image-data]').value = canvas.toDataURL(mime, .92); state.textContent = 'Saving…';
  });
}

function initializeFileWorkspace(workspace) {
  if (!workspace || workspace.dataset.initialized === 'true') return;
  workspace.dataset.initialized = 'true';
  const browserHost = workspace.querySelector('[data-file-browser-host]');
  const previewHost = workspace.querySelector('[data-file-preview-host]');
  let busy = false;
  const setPath = path => { const label = workspace.querySelector('[data-workspace-path]'); if (label) label.textContent = `/${path || ''}`; };
  const loadFolder = async source => {
    if (busy) return; busy = true;
    await preserveScroll(async () => {
      try {
        const destination = new URL(source, location.href), path = destination.searchParams.get('path') || '';
        const partial = new URL(workspace.dataset.browserUrl, location.origin); partial.searchParams.set('projectId', workspace.dataset.projectId); if (path) partial.searchParams.set('path', path);
        const response = await fetch(partial, { credentials: 'same-origin', cache: 'no-store' }); if (!response.ok || response.redirected) throw new Error('Could not open this folder.');
        browserHost.innerHTML = await response.text(); browserHost.hidden = false; previewHost.hidden = true; previewHost.innerHTML = ''; setPath(path);
        initializeFileManager(browserHost.querySelector('[data-file-manager]'));
      } finally { busy = false; }
    });
  };
  const loadPreview = async source => {
    if (busy) return; busy = true;
    await preserveScroll(async () => {
      try {
        const destination = new URL(source, location.href), partial = new URL(workspace.dataset.previewUrl, location.origin);
        partial.searchParams.set('projectId', workspace.dataset.projectId); partial.searchParams.set('path', destination.searchParams.get('path') || '');
        const response = await fetch(partial, { credentials: 'same-origin', cache: 'no-store' }); if (!response.ok || response.redirected) throw new Error(await response.text() || 'Could not preview this file.');
        previewHost.innerHTML = await response.text(); browserHost.hidden = true; previewHost.hidden = false;
        initializeTextEditor(previewHost.querySelector('[data-text-editor]')); initializeImageEditor(previewHost.querySelector('[data-image-editor]'));
      } finally { busy = false; }
    });
  };
  const submitInline = async form => {
    if (busy || (form.dataset.confirm && !confirm(form.dataset.confirm))) return; busy = true;
    const button = form.querySelector('button[type="submit"], button:not([type])'); if (button) button.disabled = true;
    await preserveScroll(async () => {
      try {
        const formData = new FormData(form);
        if (form.matches('[data-selected-files-form]')) {
          formData.delete('selectedPaths');
          browserHost.querySelectorAll('[data-file-checkbox]:checked').forEach(checkbox => formData.append('selectedPaths', checkbox.value));
        }
        const response = await fetch(form.action, { method: form.method || 'POST', body: formData, credentials: 'same-origin', headers: { 'X-Requested-With': 'XMLHttpRequest' } });
        if (!response.ok || response.redirected) throw new Error(await response.text() || 'The file action failed.');
        const html = await response.text(), isPreview = form.matches('[data-editor-form], [data-image-save-form]');
        if (isPreview) { previewHost.innerHTML = html; initializeTextEditor(previewHost.querySelector('[data-text-editor]')); initializeImageEditor(previewHost.querySelector('[data-image-editor]')); }
        else { browserHost.innerHTML = html; initializeFileManager(browserHost.querySelector('[data-file-manager]')); setPath(browserHost.querySelector('[data-file-manager]')?.dataset.currentPath || ''); }
      } catch (error) {
        console.warn('File action failed. Open Messages for details.', error);
      } finally { busy = false; if (button) button.disabled = false; }
    });
  };
  workspace.addEventListener('click', event => {
    const back = event.target.closest('[data-file-back]'); if (back) { loadFolder(new URL(`?projectId=${workspace.dataset.projectId}&path=${encodeURIComponent(back.dataset.path || '')}`, location.origin + '/Files')); return; }
    if (event.target.closest('[data-file-checkbox], .file-select, button, input, textarea, select, label')) return;
    const item = event.target.closest('[data-file-item]');
    const link = event.target.closest('a[data-file-nav]');
    const source = item?.dataset.fileOpenUrl || link?.href; if (!source) return;
    if (link) event.preventDefault();
    (item?.dataset.fileDirectory === 'true' || link?.dataset.fileNav === 'true' ? loadFolder : loadPreview)(source);
  });
  workspace.addEventListener('keydown', event => { const item = event.target.closest('[data-file-item]'); if (item && (event.key === 'Enter' || event.key === ' ')) { event.preventDefault(); (item.dataset.fileDirectory === 'true' ? loadFolder : loadPreview)(item.dataset.fileOpenUrl); } });
  workspace.addEventListener('submit', event => {
    const form = event.target.closest('[data-file-ajax-form], [data-editor-form], [data-image-save-form]'); if (!form) return;
    event.preventDefault(); submitInline(form);
  });
  initializeFileManager(browserHost.querySelector('[data-file-manager]'));
  initializeTextEditor(previewHost.querySelector('[data-text-editor]')); initializeImageEditor(previewHost.querySelector('[data-image-editor]'));
}

document.querySelectorAll('[data-inline-file-workspace]').forEach(initializeFileWorkspace);
const projectSelector = document.querySelector('[data-file-project-select]');
const selectedWorkspaceHost = document.querySelector('[data-selected-workspace]');
if (projectSelector && selectedWorkspaceHost) {
  const storageKey = 'dan-loop-selected-file-project';
  const options = [...projectSelector.options];
  const savedProject = localStorage.getItem(storageKey);
  if (savedProject && options.some(option => option.value === savedProject)) projectSelector.value = savedProject;
  else if (options.length) projectSelector.value = options[0].value;

  let projectLoadRunning = false;
  const loadSelectedProject = async ({ preservePosition = true } = {}) => {
    const option = projectSelector.selectedOptions[0];
    if (!option || projectLoadRunning) return;
    const summary = document.querySelector('[data-file-project-summary]');
    if (summary) summary.textContent = option.textContent || 'Project files';
    if (selectedWorkspaceHost.dataset.projectId === option.value && selectedWorkspaceHost.querySelector('[data-inline-file-workspace]')) return;
    projectLoadRunning = true;
    localStorage.setItem(storageKey, option.value);
    const load = async () => {
      selectedWorkspaceHost.setAttribute('aria-busy', 'true');
      try {
        const response = await fetch(option.dataset.workspaceUrl, { credentials: 'same-origin', cache: 'no-store' });
        if (!response.ok || response.redirected) throw new Error('Could not open this project workspace.');
        selectedWorkspaceHost.innerHTML = await response.text();
        selectedWorkspaceHost.dataset.projectId = option.value;
        initializeFileWorkspace(selectedWorkspaceHost.querySelector('[data-inline-file-workspace]'));
      } catch (error) {
        selectedWorkspaceHost.innerHTML = `<div class="notice error">${escapeCode(error.message)}</div>`;
        delete selectedWorkspaceHost.dataset.projectId;
      } finally {
        selectedWorkspaceHost.removeAttribute('aria-busy'); projectLoadRunning = false;
      }
    };
    if (preservePosition) await preserveScroll(load); else await load();
  };
  projectSelector.addEventListener('change', () => loadSelectedProject());
  document.querySelector('.tab[data-tab="files"]')?.addEventListener('click', () => loadSelectedProject({ preservePosition: false }));
  if (location.hash === '#files') loadSelectedProject({ preservePosition: false });
}

const runtimePolicyForm = document.querySelector('[data-runtime-policy-form]');
const allowedToolsSection = document.querySelector('[data-allowed-tools]');
if (runtimePolicyForm && allowedToolsSection) {
  const capabilityEnabled = name => runtimePolicyForm.elements.namedItem(name)?.checked === true;
  const refreshAllowedTools = () => {
    let visibleCount = 0;
    allowedToolsSection.querySelectorAll('[data-tool-policy]').forEach(card => {
      const requirements = (card.dataset.requires || '').split(',').filter(Boolean);
      const visible = requirements.every(capabilityEnabled);
      card.hidden = !visible;
      if (visible) visibleCount += 1;
    });
    const count = allowedToolsSection.querySelector('[data-allowed-tool-count]');
    if (count) count.textContent = `${visibleCount} enabled`;
    const empty = allowedToolsSection.querySelector('[data-allowed-tools-empty]');
    if (empty) empty.hidden = visibleCount !== 0;
  };
  runtimePolicyForm.querySelectorAll('[data-runtime-capability]').forEach(input => input.addEventListener('change', refreshAllowedTools));
  refreshAllowedTools();
}
