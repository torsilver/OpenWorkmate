var API_URL = "http://127.0.0.1:8765/api/config";
var SKILLS_API_URL = "http://127.0.0.1:8765/api/skills";
var BUILTIN_TOOLS_URL = "http://127.0.0.1:8765/api/tools/builtin";
/** 与后端有效访问密钥一致；保存配置会写入 user-config，并同步到 chrome.storage.local 供侧栏/Workspace 使用 */
var COPILOT_TOKEN_STORAGE_KEY = 'localServiceAuthToken';

function tasklySetOptionsApiUrls(apiBase) {
  var b = TasklyLocalService.normalizeBase(apiBase);
  API_URL = b + "/api/config";
  SKILLS_API_URL = b + "/api/skills";
  BUILTIN_TOOLS_URL = b + "/api/tools/builtin";
}

var tasklyOptionsApiReady = null;
function tasklyEnsureOptionsApiBase() {
  if (tasklyOptionsApiReady) return tasklyOptionsApiReady;
  tasklyOptionsApiReady = TasklyLocalService.tasklyResolveLocalServiceBase(chrome.storage.local).then(function (r) {
    tasklySetOptionsApiUrls(r.baseUrl);
  });
  return tasklyOptionsApiReady;
}

function tasklyMergeAuthHeaders(init) {
  init = init ? Object.assign({}, init) : {};
  return new Promise(function (resolve) {
    try {
      chrome.storage.local.get([COPILOT_TOKEN_STORAGE_KEY], function (r) {
        if (chrome.runtime && chrome.runtime.lastError) {
          resolve(init);
          return;
        }
        var t = (r && r[COPILOT_TOKEN_STORAGE_KEY] || '').trim();
        var headers = Object.assign({}, init.headers || {});
        if (t) headers['X-OfficeCopilot-Token'] = t;
        init.headers = headers;
        resolve(init);
      });
    } catch (e) {
      resolve(init);
    }
  });
}

function tasklyFetch(url, init) {
  return tasklyMergeAuthHeaders(init).then(function (merged) { return fetch(url, merged); });
}

/** 本机 loopback 从后台拉取密钥并写入 chrome.storage（仅当本地尚未保存时），便于 WPS/侧栏与选项页共用同一配置源 */
function ensureLocalServiceTokenFromBootstrap() {
  return tasklyEnsureOptionsApiBase().then(function () {
  var base = API_URL.replace('/api/config', '');
  return fetch(base + '/api/bootstrap/local-service-auth')
    .then(function (r) { return r.ok ? r.json() : null; })
    .then(function (j) {
      if (!j || !j.ok) return;
      var t = (j.webSocketAuthToken || '').trim();
      if (!t) return;
      return new Promise(function (resolve) {
        chrome.storage.local.get([COPILOT_TOKEN_STORAGE_KEY], function (cur) {
          var existing = (cur && cur[COPILOT_TOKEN_STORAGE_KEY] || '').trim();
          if (existing) { resolve(); return; }
          var o = {};
          o[COPILOT_TOKEN_STORAGE_KEY] = t;
          chrome.storage.local.set(o, function () { resolve(); });
        });
      });
    })
    .catch(function () {});
  });
}

/** User Scripts（自定义页面脚本）是否可用：需 Chrome 135+ 且在扩展详情页开启 Allow User Scripts。 */
function isUserScriptsAvailable() {
  try {
    chrome.userScripts.getScripts();
    return true;
  } catch {
    return false;
  }
}

/** 更新选项页「自定义页面脚本」区块的状态与扩展详情页链接。 */
function updateUserScriptsSection() {
  const statusEl = document.getElementById('userScriptsStatusText');
  const linkEl = document.getElementById('userScriptsExtensionLink');
  if (!statusEl) return;
  const available = isUserScriptsAvailable();
  statusEl.textContent = available ? '已开启' : '未开启';
  statusEl.style.color = available ? 'var(--success)' : 'var(--danger)';
  if (linkEl && chrome.runtime && chrome.runtime.id) {
    linkEl.href = 'chrome://extensions?id=' + chrome.runtime.id;
  }
}

/** 判断是否为「无法连接后端」类错误，返回可展示给用户的文案；否则返回 null。 */
function messageForBackendUnreachable(err) {
  if (!err) return null;
  var msg = (err && err.message) ? String(err.message) : '';
  if (msg === 'Failed to fetch' || (err.name === 'TypeError' && msg && msg.indexOf('fetch') !== -1))
    return '无法连接到本地服务（默认扫描 127.0.0.1:8765 起连续端口），请确保已启动 OfficeCopilot.Server。';
  return null;
}

(function () {
  function openDebugStatsFromOptions() {
    if (!chrome.runtime || !chrome.runtime.getURL || !chrome.tabs || !chrome.tabs.create) {
      alert('当前环境无法打开扩展页。');
      return;
    }
    var url = chrome.runtime.getURL('debug-stats.html');
    chrome.tabs.create({ url: url }, function () {
      if (chrome.runtime.lastError)
        alert('打开失败：' + (chrome.runtime.lastError.message || String(chrome.runtime.lastError)));
    });
  }
  function openDebugLogsPageFromOptions() {
    if (!chrome.tabs || !chrome.tabs.create) {
      alert('当前环境无法打开标签页。');
      return;
    }
    tasklyEnsureOptionsApiBase()
      .then(function () {
        var base = API_URL.replace('/api/config', '');
        if (!base) {
          alert('未能解析本机服务地址，请确认 Office Copilot 后台已启动。');
          return;
        }
        var url = base + '/debug/logs.html';
        chrome.tabs.create({ url: url }, function () {
          if (chrome.runtime.lastError)
            alert('打开失败：' + (chrome.runtime.lastError.message || String(chrome.runtime.lastError)));
        });
      })
      .catch(function (err) {
        var m = messageForBackendUnreachable(err);
        alert(m || (err && err.message) || '未能解析本机服务地址，请确认 Office Copilot 后台已启动。');
      });
  }
  var sBtn = document.getElementById('openDebugStatsFromOptionsBtn');
  var lBtn = document.getElementById('openDebugLogsPageBtn');
  if (sBtn) sBtn.addEventListener('click', openDebugStatsFromOptions);
  if (lBtn) lBtn.addEventListener('click', openDebugLogsPageFromOptions);
})();

const els = {
  statusMessage: document.getElementById('statusMessage'),
  aiModelsList: document.getElementById('aiModelsList'),
  addAiModelBtn: document.getElementById('addAiModelBtn'),
  aiModelEditor: document.getElementById('aiModelEditor'),
  aiModelEditorTitle: document.getElementById('aiModelEditorTitle'),
  closeAiModelEditorBtn: document.getElementById('closeAiModelEditorBtn'),
  aiModelDisplayName: document.getElementById('aiModelDisplayName'),
  aiModelProvider: document.getElementById('aiModelProvider'),
  aiModelEndpoint: document.getElementById('aiModelEndpoint'),
  aiModelApiKey: document.getElementById('aiModelApiKey'),
  aiModelDeploymentName: document.getElementById('aiModelDeploymentName'),
  aiModelApiVersion: document.getElementById('aiModelApiVersion'),
  aiModelModelId: document.getElementById('aiModelModelId'),
  aiModelSystemPrompt: document.getElementById('aiModelSystemPrompt'),
  aiModelEnabled: document.getElementById('aiModelEnabled'),
  saveAiModelBtn: document.getElementById('saveAiModelBtn'),
  testAiConnectionBtn: document.getElementById('testAiConnectionBtn'),
  testAiStatus: document.getElementById('testAiStatus'),
  
  // Tabs
  tabs: document.querySelectorAll('.tab'),
  tabContents: document.querySelectorAll('.tab-content'),
  
  // Skills
  skillsList: document.getElementById('skillsList'),
  newSkillBtn: document.getElementById('newSkillBtn'),
  skillEditor: document.getElementById('skillEditor'),
  skillEditorTitle: document.getElementById('skillEditorTitle'),
  skillId: document.getElementById('skillId'),
  skillName: document.getElementById('skillName'),
  skillDesc: document.getElementById('skillDesc'),
  skillPrompt: document.getElementById('skillPrompt'),
  saveSkillBtn: document.getElementById('saveSkillBtn'),
  cancelSkillBtn: document.getElementById('cancelSkillBtn')
};

/** 内存中的 AI 模型列表，与 fullConfig.aiModels 同步，编辑时直接修改 */
let aiModelsCache = [];
/** 当前编辑的模型 Id，空表示新增 */
let editingAiModelId = null;
/** 测试连接时使用的字段缓存，打开编辑时写入、输入时同步，点击测试时只读此对象避免 DOM 读取异常 */
let testConnectionFields = { endpoint: '', modelId: '', apiKey: '', provider: 'OpenAI', deploymentName: '', vendorId: '' };

function getVendorPresets() {
  return window.OFFICE_COPILOT_VENDOR_PRESETS || { chat: [], ocr: [] };
}

function findChatPreset(id) {
  var list = getVendorPresets().chat;
  for (var i = 0; i < list.length; i++) if (list[i].id === id) return list[i];
  return null;
}

var CHAT_ALL_PROVIDER_VALUES = ['OpenAI', 'Azure', 'Ollama', 'Anthropic'];

var CHAT_PROVIDER_LABELS = {
  OpenAI: 'OpenAI 形态 API',
  Azure: 'Azure OpenAI',
  Ollama: 'Ollama',
  Anthropic: 'Anthropic（需网关为 OpenAI 形态时）'
};

function getChatPresetProviderChoices(preset) {
  if (!preset) return CHAT_ALL_PROVIDER_VALUES.slice();
  if (Array.isArray(preset.providerChoices) && preset.providerChoices.length > 0)
    return preset.providerChoices.slice();
  return [preset.provider || 'OpenAI'];
}

function populateAiProviderSelect(allowedValues) {
  var sel = document.getElementById('aiModelProvider');
  if (!sel || !allowedValues || !allowedValues.length) return;
  var prev = sel.value;
  sel.innerHTML = '';
  allowedValues.forEach(function (v) {
    var opt = document.createElement('option');
    opt.value = v;
    opt.textContent = CHAT_PROVIDER_LABELS[v] || v;
    sel.appendChild(opt);
  });
  if (allowedValues.indexOf(prev) >= 0) sel.value = prev;
  else sel.value = allowedValues[0];
}

function mountChatProviderSelect(mode) {
  var sel = document.getElementById('aiModelProvider');
  var hostP = document.getElementById('aiModelProviderSelectHostPrimary');
  var hostA = document.getElementById('aiModelProviderSelectHostAdvanced');
  if (!sel || !hostP || !hostA) return;
  var host = mode === 'primary' ? hostP : hostA;
  if (sel.parentNode !== host) host.appendChild(sel);
}

/** @param {boolean} resetProviderToPreset true 时按供应商预设覆盖对接类型（切换供应商）；false 时尽量保留当前已选值 */
function updateChatProviderUi(resetProviderToPreset) {
  var vendorEl = document.getElementById('aiModelVendor');
  var vid = (vendorEl && vendorEl.value) || 'other_auto';
  var preset = findChatPreset(vid);
  var choices = getChatPresetProviderChoices(preset);
  var multi = choices.length > 1;
  var primaryRow = document.getElementById('aiModelProviderPrimaryRow');
  var advancedWrap = document.getElementById('aiModelProviderAdvancedWrap');
  var sel = document.getElementById('aiModelProvider');
  if (multi) {
    if (primaryRow) primaryRow.style.display = '';
    if (advancedWrap) advancedWrap.style.display = 'none';
    mountChatProviderSelect('primary');
    populateAiProviderSelect(choices);
  } else {
    if (primaryRow) primaryRow.style.display = 'none';
    if (advancedWrap) advancedWrap.style.display = '';
    mountChatProviderSelect('advanced');
    populateAiProviderSelect(CHAT_ALL_PROVIDER_VALUES);
  }
  if (resetProviderToPreset && preset && sel && sel.options.length) {
    var d = preset.provider || choices[0];
    var ok = false;
    for (var i = 0; i < sel.options.length; i++) if (sel.options[i].value === d) { ok = true; break; }
    if (ok) sel.value = d;
    else sel.value = sel.options[0].value;
  } else if (!resetProviderToPreset && sel && sel.options.length) {
    var cur = sel.value;
    var has = false;
    for (var j = 0; j < sel.options.length; j++) if (sel.options[j].value === cur) { has = true; break; }
    if (!has) {
      var d2 = preset ? (preset.provider || sel.options[0].value) : sel.options[0].value;
      var ok2 = false;
      for (var k = 0; k < sel.options.length; k++) if (sel.options[k].value === d2) { ok2 = true; break; }
      sel.value = ok2 ? d2 : sel.options[0].value;
    }
  }
  toggleAzureFields();
}

function findOcrPreset(id) {
  var list = getVendorPresets().ocr;
  for (var i = 0; i < list.length; i++) if (list[i].id === id) return list[i];
  return null;
}

function fillSelectFromPresets(selectId, items) {
  var el = document.getElementById(selectId);
  if (!el || !items || !items.length) return;
  while (el.firstChild) el.removeChild(el.firstChild);
  items.forEach(function (p) {
    var opt = document.createElement('option');
    opt.value = p.id;
    opt.textContent = p.label;
    el.appendChild(opt);
  });
}

function initVendorPresetSelects() {
  var p = getVendorPresets();
  fillSelectFromPresets('aiModelVendor', p.chat);
  fillSelectFromPresets('embeddingEditorVendor', p.chat);
  fillSelectFromPresets('ocrEditorVendor', p.ocr);
}

function resolveChatVendorId(entry) {
  if (!entry) return 'other_auto';
  var vid = String(entry.vendorId || entry.VendorId || '').trim();
  if (vid) return vid;
  var ep = String(entry.endpoint || entry.Endpoint || '').trim().toLowerCase().replace(/\/$/, '');
  var prov = String(entry.provider || entry.Provider || 'OpenAI');
  var list = getVendorPresets().chat;
  for (var i = 0; i < list.length; i++) {
    var preset = list[i];
    var de = String(preset.defaultEndpoint || '').trim().toLowerCase().replace(/\/$/, '');
    if (de && ep && (ep === de || ep.indexOf(de + '/') === 0) && (!preset.provider || preset.provider === prov)) return preset.id;
  }
  if (prov === 'Azure') return 'azure_openai';
  if (prov === 'Ollama') return 'ollama';
  return 'other_auto';
}

function resolveOcrVendorId(entry) {
  if (!entry) return 'other_auto';
  var vid = String(entry.vendorId || entry.VendorId || '').trim();
  if (vid) return vid;
  var ck = String(entry.connectionKind || entry.ConnectionKind || '').trim();
  var ep = String(entry.endpoint || entry.Endpoint || '').trim().toLowerCase();
  var list = getVendorPresets().ocr;
  var i;
  for (i = 0; i < list.length; i++) {
    if (ck && String(list[i].connectionKind || '') === ck) return list[i].id;
  }
  for (i = 0; i < list.length; i++) {
    var de = String(list[i].defaultEndpoint || '').trim().toLowerCase().replace(/\/$/, '');
    if (de && ep.indexOf(de) >= 0) return list[i].id;
  }
  if (ep.indexOf('dashscope') >= 0) return 'aliyun_bailian';
  if (ep.indexOf('openai.com') >= 0) return 'openai';
  return 'other_auto';
}

function onAiVendorChange() {
  var el = document.getElementById('aiModelVendor');
  if (!el) return;
  var preset = findChatPreset(el.value);
  if (preset) {
    if (preset.defaultEndpoint && els.aiModelEndpoint) els.aiModelEndpoint.value = preset.defaultEndpoint;
    if (preset.defaultModelId !== undefined && preset.defaultModelId !== null && preset.defaultModelId !== '' && els.aiModelModelId) els.aiModelModelId.value = preset.defaultModelId;
  }
  updateChatProviderUi(!!preset);
}

function onOcrVendorChange() {
  var el = document.getElementById('ocrEditorVendor');
  if (!el) return;
  var preset = findOcrPreset(el.value);
  if (!preset) return;
  var endEl = document.getElementById('ocrEditorEndpoint');
  var modelEl = document.getElementById('ocrEditorModelId');
  if (preset.defaultEndpoint && endEl) endEl.value = preset.defaultEndpoint;
  if (modelEl && preset.defaultModelId !== undefined && preset.defaultModelId !== null && preset.defaultModelId !== '') modelEl.value = preset.defaultModelId;
}

function onEmbeddingVendorChange() {
  var el = document.getElementById('embeddingEditorVendor');
  if (!el) return;
  var preset = findChatPreset(el.value);
  if (!preset) return;
  var endEl = document.getElementById('embeddingEditorEndpoint');
  var modelEl = document.getElementById('embeddingEditorModelId');
  if (preset.defaultEndpoint && endEl) endEl.value = preset.defaultEndpoint;
  if (preset.defaultModelId !== undefined && preset.defaultModelId !== null && preset.defaultModelId !== '' && modelEl) modelEl.value = preset.defaultModelId;
}

function wireVendorSelectListeners() {
  var a = document.getElementById('aiModelVendor');
  if (a) a.addEventListener('change', onAiVendorChange);
  var o = document.getElementById('ocrEditorVendor');
  if (o) o.addEventListener('change', onOcrVendorChange);
  var e = document.getElementById('embeddingEditorVendor');
  if (e) e.addEventListener('change', onEmbeddingVendorChange);
}

// ───── Tabs ─────
els.tabs.forEach(tab => {
  tab.addEventListener('click', () => {
    els.tabs.forEach(t => t.classList.remove('active'));
    els.tabContents.forEach(c => c.classList.remove('active'));
    
    tab.classList.add('active');
    document.getElementById(tab.dataset.target).classList.add('active');
    
    if (tab.dataset.target === 'tab-skills-mcp') {
      loadSkills();
      loadBuiltinTools();
      updateUserScriptsSection();
    }
    if (tab.dataset.target === 'tab-memory-storage') {
      loadMemoryList();
    }
    if (tab.dataset.target === 'tab-plans-automation') {
      loadScheduledTasks();
      loadPlansList();
      loadAccurateDataList();
    }
  });
});

// ───── AI Config ─────
let fullConfig = null;
/** 当前渲染的「技能环境变量」键名列表，保存时据此收集表单值 */
let skillEnvKeys = [];

function getAiModels() {
  const raw = fullConfig && (fullConfig.aiModels || fullConfig.AiModels);
  return Array.isArray(raw) ? raw : [];
}

function renderAiModelsList() {
  if (!els.aiModelsList) return;
  aiModelsCache = getAiModels();
  const activeId = (fullConfig && (fullConfig.activeModelId || fullConfig.ActiveModelId)) || '';
  els.aiModelsList.innerHTML = aiModelsCache.map(function (m) {
    const id = m.id || m.Id || '';
    const name = m.displayName || m.DisplayName || id || '(未命名)';
    const provider = m.provider || m.Provider || 'OpenAI';
    const enabled = m.enabled !== false && m.Enabled !== false;
    const isActive = (activeId && id && activeId === id);
    const enableBtn = isActive ? '' : ('<button type="button" class="btn-secondary enable-ai-btn" data-id="' + escapeAttr(id) + '">启用</button>');
    return '<div class="mcp-server-row" data-ai-id="' + escapeAttr(id) + '">' +
      '<div class="mcp-icon">' + (provider.substring(0, 1).toUpperCase()) + '</div>' +
      '<div class="mcp-info"><div class="mcp-name">' + escapeHtml(name) + ' <span style="color:var(--text-secondary);font-weight:400;">(' + escapeHtml(provider) + ')</span>' + (isActive ? ' <span style="color:var(--success);font-size:12px;">当前</span>' : '') + '</div>' +
      '<div class="mcp-desc">' + escapeHtml(m.modelId || m.ModelId || '') + '</div></div>' +
      '<div class="mcp-actions">' + enableBtn +
      '<button type="button" class="btn-secondary test-ai-btn" data-id="' + escapeAttr(id) + '">测试</button>' +
      '<button type="button" class="btn-secondary edit-ai-btn" data-id="' + escapeAttr(id) + '">编辑</button>' +
      '<button type="button" class="btn-danger delete-ai-btn" data-id="' + escapeAttr(id) + '">删除</button>' +
      '<span class="ai-row-test-status help-text" style="margin-left:8px;"></span></div></div>';
  }).join('');
  els.aiModelsList.querySelectorAll('.enable-ai-btn').forEach(function (btn) {
    btn.addEventListener('click', function () { setActiveAiModel(btn.dataset.id); });
  });
  els.aiModelsList.querySelectorAll('.test-ai-btn').forEach(function (btn) {
    btn.addEventListener('click', function () { testAiConnectionById(btn.dataset.id); });
  });
  els.aiModelsList.querySelectorAll('.edit-ai-btn').forEach(function (btn) {
    btn.addEventListener('click', function () { openAiModelEditor(btn.dataset.id); });
  });
  els.aiModelsList.querySelectorAll('.delete-ai-btn').forEach(function (btn) {
    btn.addEventListener('click', function () { deleteAiModel(btn.dataset.id); });
  });
}

function setActiveAiModel(id) {
  if (!id || !fullConfig) return;
  fullConfig.activeModelId = id;
  fullConfig.ActiveModelId = id;
  renderAiModelsList();
  saveConfig();
}

function normalizePath(p) {
  return (p || '').replace(/\\/g, '/').toLowerCase();
}

function getEmbeddingModels() {
  var raw = fullConfig && (fullConfig.embeddingModels || fullConfig.EmbeddingModels);
  return Array.isArray(raw) ? raw : [];
}

function renderEmbeddingModelsList() {
  var listEl = document.getElementById('embeddingModelsList');
  if (!listEl) return;
  var list = getEmbeddingModels();
  var activeId = (fullConfig && (fullConfig.activeEmbeddingModelId || fullConfig.ActiveEmbeddingModelId)) || '';
  listEl.innerHTML = list.map(function (m) {
    var id = m.id || m.Id || '';
    var name = m.displayName || m.DisplayName || id || '(未命名)';
    var modelId = m.modelId || m.ModelId || '';
    var isActive = (activeId && id && activeId === id);
    var setActiveBtn = isActive ? '' : ('<button type="button" class="btn-secondary set-active-emb-btn" data-id="' + escapeAttr(id) + '">设为当前</button>');
    return '<div class="mcp-server-row" data-emb-id="' + escapeAttr(id) + '">' +
      '<div class="mcp-icon">E</div>' +
      '<div class="mcp-info"><div class="mcp-name">' + escapeHtml(name) + (isActive ? ' <span style="color:var(--success);font-size:12px;">当前</span>' : '') + '</div>' +
      '<div class="mcp-desc">' + escapeHtml(modelId || '') + '</div></div>' +
      '<div class="mcp-actions">' + setActiveBtn +
      '<button type="button" class="btn-secondary test-emb-btn" data-id="' + escapeAttr(id) + '">测试</button>' +
      '<button type="button" class="btn-secondary edit-emb-btn" data-id="' + escapeAttr(id) + '">编辑</button>' +
      '<button type="button" class="btn-danger delete-emb-btn" data-id="' + escapeAttr(id) + '">删除</button>' +
      '<span class="emb-row-test-status help-text" style="margin-left:8px;"></span></div></div>';
  }).join('');
  listEl.querySelectorAll('.set-active-emb-btn').forEach(function (btn) {
    btn.addEventListener('click', function () { setActiveEmbeddingModel(btn.dataset.id); });
  });
  listEl.querySelectorAll('.test-emb-btn').forEach(function (btn) {
    btn.addEventListener('click', function () { testEmbeddingById(btn.dataset.id); });
  });
  listEl.querySelectorAll('.edit-emb-btn').forEach(function (btn) {
    btn.addEventListener('click', function () { openEmbeddingEditor(btn.dataset.id); });
  });
  listEl.querySelectorAll('.delete-emb-btn').forEach(function (btn) {
    btn.addEventListener('click', function () { deleteEmbeddingModel(btn.dataset.id); });
  });
  updateEmbeddingModelSummary();
}

function updateEmbeddingModelSummary() {
  var sum = document.getElementById('embeddingModelSummary');
  if (!sum) return;
  var list = getEmbeddingModels();
  var activeId = (fullConfig && (fullConfig.activeEmbeddingModelId || fullConfig.ActiveEmbeddingModelId)) || '';
  var active = list.find(function (m) { return (m.id || m.Id) === activeId; });
  if (active) {
    var name = active.displayName || active.DisplayName || active.modelId || active.ModelId || activeId;
    sum.textContent = 'Embedding 模型（当前：' + name + '）';
  } else {
    sum.textContent = 'Embedding 模型（未配置）';
  }
}

function getOcrModels() {
  var raw = fullConfig && (fullConfig.ocrModels || fullConfig.OcrModels);
  return Array.isArray(raw) ? raw : [];
}

function renderOcrModelsList() {
  var listEl = document.getElementById('ocrModelsList');
  if (!listEl) return;
  var list = getOcrModels();
  var activeId = (fullConfig && (fullConfig.activeOcrModelId || fullConfig.ActiveOcrModelId)) || '';
  listEl.innerHTML = list.map(function (m) {
    var id = m.id || m.Id || '';
    var name = m.displayName || m.DisplayName || id || '(未命名)';
    var isActive = (activeId && id && activeId === id);
    var setActiveBtn = isActive ? '' : ('<button type="button" class="btn-secondary set-active-ocr-btn" data-id="' + escapeAttr(id) + '">设为当前</button>');
    return '<div class="mcp-server-row" data-ocr-id="' + escapeAttr(id) + '">' +
      '<div class="mcp-icon">O</div>' +
      '<div class="mcp-info"><div class="mcp-name">' + escapeHtml(name) + (isActive ? ' <span style="color:var(--success);font-size:12px;">当前</span>' : '') + '</div>' +
      '<div class="mcp-desc">' + escapeHtml((m.endpoint || m.Endpoint || '').substring(0, 50)) + '</div></div>' +
      '<div class="mcp-actions">' + setActiveBtn +
      '<button type="button" class="btn-secondary test-ocr-btn" data-id="' + escapeAttr(id) + '">测试</button>' +
      '<button type="button" class="btn-secondary edit-ocr-btn" data-id="' + escapeAttr(id) + '">编辑</button>' +
      '<button type="button" class="btn-danger delete-ocr-btn" data-id="' + escapeAttr(id) + '">删除</button>' +
      '<span class="ocr-row-test-status help-text" style="margin-left:8px;"></span></div></div>';
  }).join('');
  listEl.querySelectorAll('.set-active-ocr-btn').forEach(function (btn) { btn.addEventListener('click', function () { setActiveOcrModel(btn.dataset.id); }); });
  listEl.querySelectorAll('.test-ocr-btn').forEach(function (btn) { btn.addEventListener('click', function () { testOcrById(btn.dataset.id); }); });
  listEl.querySelectorAll('.edit-ocr-btn').forEach(function (btn) { btn.addEventListener('click', function () { openOcrEditor(btn.dataset.id); }); });
  listEl.querySelectorAll('.delete-ocr-btn').forEach(function (btn) { btn.addEventListener('click', function () { deleteOcrModel(btn.dataset.id); }); });
  updateOcrModelSummary();
}

function updateOcrModelSummary() {
  var sum = document.getElementById('ocrModelSummary');
  if (!sum) return;
  var list = getOcrModels();
  var activeId = (fullConfig && (fullConfig.activeOcrModelId || fullConfig.ActiveOcrModelId)) || '';
  var active = list.find(function (m) { return (m.id || m.Id) === activeId; });
  if (active) {
    var name = active.displayName || active.DisplayName || activeId;
    sum.textContent = 'OCR 模型（当前：' + name + '）';
  } else {
    sum.textContent = 'OCR 模型';
  }
}

var editingOcrId = null;

function openOcrEditor(id) {
  editingOcrId = id || null;
  var titleEl = document.getElementById('ocrModelEditorTitle');
  var editorEl = document.getElementById('ocrModelEditor');
  if (titleEl) titleEl.textContent = id ? '编辑 OCR 模型' : '添加 OCR 模型';
  var list = getOcrModels();
  var entry = id ? list.find(function (m) { return (m.id || m.Id) === id; }) : null;
  var dispEl = document.getElementById('ocrEditorDisplayName');
  var endEl = document.getElementById('ocrEditorEndpoint');
  var keyEl = document.getElementById('ocrEditorApiKey');
  var langEl = document.getElementById('ocrEditorLanguage');
  if (dispEl) dispEl.value = entry ? (entry.displayName || entry.DisplayName || '') : '';
  if (endEl) endEl.value = entry ? (entry.endpoint || entry.Endpoint || '') : '';
  if (keyEl) keyEl.value = entry ? (entry.apiKey || entry.ApiKey || '') : '';
  if (langEl) langEl.value = entry ? (entry.language || entry.Language || '') : '';
  var ovEl = document.getElementById('ocrEditorVendor');
  if (ovEl) ovEl.value = entry ? resolveOcrVendorId(entry) : 'other_auto';
  var midEl = document.getElementById('ocrEditorModelId');
  if (midEl) midEl.value = entry ? (entry.modelId || entry.ModelId || '') : '';
  var statusEl = document.getElementById('testOcrStatus');
  if (statusEl) statusEl.textContent = '';
  if (editorEl) editorEl.style.display = 'block';
}

function closeOcrEditor() {
  editingOcrId = null;
  var editorEl = document.getElementById('ocrModelEditor');
  if (editorEl) editorEl.style.display = 'none';
}

function saveOcrFromEditor() {
  var dispEl = document.getElementById('ocrEditorDisplayName');
  var endEl = document.getElementById('ocrEditorEndpoint');
  var keyEl = document.getElementById('ocrEditorApiKey');
  var langEl = document.getElementById('ocrEditorLanguage');
  var modelIdEl = document.getElementById('ocrEditorModelId');
  var vendorEl = document.getElementById('ocrEditorVendor');
  var displayName = (dispEl && dispEl.value && dispEl.value.trim()) || '';
  var endpoint = (endEl && endEl.value && endEl.value.trim()) || '';
  var apiKey = (keyEl && keyEl.value) || '';
  var language = (langEl && langEl.value && langEl.value.trim()) || null;
  var modelId = (modelIdEl && modelIdEl.value && modelIdEl.value.trim()) || '';
  var vendorId = (vendorEl && vendorEl.value) ? vendorEl.value : 'other_auto';
  var ocrPreset = findOcrPreset(vendorId);
  var ocrConnectionKind = ocrPreset && typeof ocrPreset.connectionKind === 'string' ? ocrPreset.connectionKind : '';
  if (!endpoint) {
    alert('请填写接口地址');
    return;
  }
  if (!apiKey) {
    alert('请填写 API Key');
    return;
  }
  var list = getOcrModels();
  var id = editingOcrId || ('ocr-' + Date.now());
  var entry = { id: id, displayName: displayName || id, endpoint: endpoint, apiKey: apiKey, language: language || undefined, vendorId: vendorId, connectionKind: ocrConnectionKind, modelId: modelId };
  var idx = list.findIndex(function (m) { return (m.id || m.Id) === editingOcrId; });
  if (idx >= 0) list[idx] = entry;
  else list.push(entry);
  if (!fullConfig) fullConfig = {};
  fullConfig.ocrModels = fullConfig.OcrModels = list;
  if (list.length === 1 && !(fullConfig.activeOcrModelId || fullConfig.ActiveOcrModelId)) {
    fullConfig.activeOcrModelId = fullConfig.ActiveOcrModelId = id;
  }
  closeOcrEditor();
  renderOcrModelsList();
  saveConfig();
}

function setActiveOcrModel(id) {
  if (!id || !fullConfig) return;
  fullConfig.activeOcrModelId = fullConfig.ActiveOcrModelId = id;
  renderOcrModelsList();
  saveConfig();
}

function deleteOcrModel(id) {
  if (!id || !fullConfig) return;
  var list = getOcrModels().filter(function (m) { return (m.id || m.Id) !== id; });
  fullConfig.ocrModels = fullConfig.OcrModels = list;
  if ((fullConfig.activeOcrModelId || fullConfig.ActiveOcrModelId) === id) {
    fullConfig.activeOcrModelId = fullConfig.ActiveOcrModelId = list.length ? (list[0].id || list[0].Id) : '';
  }
  if (editingOcrId === id) closeOcrEditor();
  renderOcrModelsList();
  saveConfig();
}

function testOcrById(id) {
  var list = getOcrModels();
  var entry = list.find(function (m) { return (m.id || m.Id) === id; });
  if (!entry) return;
  var endpoint = (entry.endpoint || entry.Endpoint || '').trim();
  var apiKey = (entry.apiKey || entry.ApiKey || '') || '';
  var listEl = document.getElementById('ocrModelsList');
  var statusEl = null;
  if (listEl) {
    listEl.querySelectorAll('.mcp-server-row').forEach(function (row) {
      if (row.getAttribute('data-ocr-id') === id) statusEl = row.querySelector('.ocr-row-test-status');
    });
  }
  var connectionKind = (entry.connectionKind || entry.ConnectionKind || '').trim();
  var vendorId = (entry.vendorId || entry.VendorId || '').trim();
  var modelId = (entry.modelId || entry.ModelId || '').trim();
  var language = (entry.language || entry.Language || '').trim();
  doTestOcrConnection(endpoint, apiKey, statusEl || undefined, {
    language: language || undefined,
    modelId: modelId || undefined,
    connectionKind: connectionKind || undefined,
    vendorId: vendorId || undefined
  });
}

var editingEmbeddingId = null;

function openEmbeddingEditor(id) {
  editingEmbeddingId = id || null;
  var titleEl = document.getElementById('embeddingModelEditorTitle');
  var editorEl = document.getElementById('embeddingModelEditor');
  if (titleEl) titleEl.textContent = id ? '编辑 Embedding 模型' : '添加 Embedding 模型';
  var list = getEmbeddingModels();
  var entry = id ? list.find(function (m) { return (m.id || m.Id) === id; }) : null;
  var dispEl = document.getElementById('embeddingEditorDisplayName');
  var endEl = document.getElementById('embeddingEditorEndpoint');
  var keyEl = document.getElementById('embeddingEditorApiKey');
  var modelEl = document.getElementById('embeddingEditorModelId');
  if (dispEl) dispEl.value = entry ? (entry.displayName || entry.DisplayName || '') : '';
  if (endEl) endEl.value = entry ? (entry.endpoint || entry.Endpoint || '') : '';
  if (keyEl) keyEl.value = entry ? (entry.apiKey || entry.ApiKey || '') : '';
  if (modelEl) modelEl.value = entry ? (entry.modelId || entry.ModelId || '') : '';
  var evEl = document.getElementById('embeddingEditorVendor');
  if (evEl) evEl.value = entry ? resolveChatVendorId(entry) : 'other_auto';
  var statusEl = document.getElementById('testEmbeddingStatus');
  if (statusEl) statusEl.textContent = '';
  if (editorEl) editorEl.style.display = 'block';
}

function closeEmbeddingEditor() {
  editingEmbeddingId = null;
  var editorEl = document.getElementById('embeddingModelEditor');
  if (editorEl) editorEl.style.display = 'none';
}

function saveEmbeddingFromEditor() {
  var dispEl = document.getElementById('embeddingEditorDisplayName');
  var endEl = document.getElementById('embeddingEditorEndpoint');
  var keyEl = document.getElementById('embeddingEditorApiKey');
  var modelEl = document.getElementById('embeddingEditorModelId');
  var vendorEl = document.getElementById('embeddingEditorVendor');
  var displayName = (dispEl && dispEl.value && dispEl.value.trim()) || '';
  var endpoint = (endEl && endEl.value && endEl.value.trim()) || '';
  var apiKey = (keyEl && keyEl.value) || '';
  var modelId = (modelEl && modelEl.value && modelEl.value.trim()) || '';
  var embVendorId = (vendorEl && vendorEl.value) ? vendorEl.value : 'other_auto';
  if (!modelId) {
    alert('请填写模型 ID');
    return;
  }
  var list = getEmbeddingModels();
  var id = editingEmbeddingId || ('emb-' + Date.now());
  var entry = { id: id, displayName: displayName || modelId, source: 'Remote', endpoint: endpoint, apiKey: apiKey, modelId: modelId, vendorId: embVendorId };
  var idx = list.findIndex(function (m) { return (m.id || m.Id) === editingEmbeddingId; });
  if (idx >= 0) list[idx] = entry;
  else list.push(entry);
  if (!fullConfig) fullConfig = {};
  fullConfig.embeddingModels = fullConfig.EmbeddingModels = list;
  if (list.length === 1 && !(fullConfig.activeEmbeddingModelId || fullConfig.ActiveEmbeddingModelId)) {
    fullConfig.activeEmbeddingModelId = fullConfig.ActiveEmbeddingModelId = id;
  }
  closeEmbeddingEditor();
  renderEmbeddingModelsList();
  saveConfig();
}

function setActiveEmbeddingModel(id) {
  if (!id || !fullConfig) return;
  fullConfig.activeEmbeddingModelId = fullConfig.ActiveEmbeddingModelId = id;
  renderEmbeddingModelsList();
  saveConfig();
}

function deleteEmbeddingModel(id) {
  if (!id || !fullConfig) return;
  var list = getEmbeddingModels().filter(function (m) { return (m.id || m.Id) !== id; });
  fullConfig.embeddingModels = fullConfig.EmbeddingModels = list;
  if ((fullConfig.activeEmbeddingModelId || fullConfig.ActiveEmbeddingModelId) === id) {
    fullConfig.activeEmbeddingModelId = fullConfig.ActiveEmbeddingModelId = list.length ? (list[0].id || list[0].Id) : '';
  }
  if (editingEmbeddingId === id) closeEmbeddingEditor();
  renderEmbeddingModelsList();
  saveConfig();
}

function testEmbeddingById(id) {
  var list = getEmbeddingModels();
  var entry = list.find(function (m) { return (m.id || m.Id) === id; });
  if (!entry) return;
  var endpoint = (entry.endpoint || entry.Endpoint || '').trim();
  var apiKey = (entry.apiKey || entry.ApiKey || '') || '';
  var modelId = (entry.modelId || entry.ModelId || '').trim();
  var listEl = document.getElementById('embeddingModelsList');
  var statusEl = null;
  if (listEl) {
    listEl.querySelectorAll('.mcp-server-row').forEach(function (row) {
      if (row.getAttribute('data-emb-id') === id) statusEl = row.querySelector('.emb-row-test-status');
    });
  }
  if (statusEl) statusEl.textContent = '';
  var vendorId = (entry.vendorId || entry.VendorId || '').trim();
  doTestEmbeddingConnection(endpoint, apiKey, modelId, statusEl || undefined, vendorId || undefined);
}

var MEMORY_SCOPE_SHARED = '__shared__';

function loadMemoryList() {
  var scopeEl = document.getElementById('memoryScopeFilter');
  var scope = (scopeEl && scopeEl.value) || 'all';
  var sessionId = (document.getElementById('memorySessionFilter') && document.getElementById('memorySessionFilter').value.trim()) || undefined;
  var agentName = (document.getElementById('memoryAgentFilter') && document.getElementById('memoryAgentFilter').value.trim()) || undefined;
  if (scope === 'session' && !sessionId) sessionId = '';
  var baseUrl = API_URL.replace('/api/config', '');
  var urlScope = scope === 'agent' ? 'all' : scope;
  var url = baseUrl + '/api/memory?skip=0&take=50&scope=' + encodeURIComponent(urlScope);
  if (scope === 'session' && sessionId !== undefined) url += '&sessionId=' + encodeURIComponent(sessionId);
  if (scope === 'agent' && agentName) url += '&agentName=' + encodeURIComponent(agentName);
  tasklyFetch(url).then(async function (r) {
    var data = await r.json().catch(function () { return {}; });
    if (!r.ok) {
      var el = document.getElementById('memoryList');
      if (el) el.innerHTML = '<p class="help-text">' + escapeHtml((data && data.message) || ('请求失败 ' + r.status)) + '</p>';
      return;
    }
    var list = (data && data.items) || [];
    var html = list.length ? list.map(function (item) {
      var text = (item.text || '').substring(0, 120);
      if ((item.text || '').length > 120) text += '…';
      var createdAt = item.createdAt ? new Date(item.createdAt).toLocaleString() : '';
      var isShared = item.sessionId === MEMORY_SCOPE_SHARED;
      var scopeLabel = isShared ? ' <span class="help-text" style="color:var(--accent);">共享</span>' : '';
      var agentLabel = (!isShared && (item.agentName || item.sessionId)) ? 'Agent: ' + escapeHtml(item.agentName || item.sessionId) + ' | ' : '';
      return '<div class="skill-card" data-memory-id="' + escapeAttr(item.id) + '">' +
        '<div class="skill-header"><span class="skill-title">' + escapeHtml(item.id) + '</span>' + scopeLabel +
        '<div><button type="button" class="btn-secondary edit-memory-btn" data-id="' + escapeAttr(item.id) + '">编辑</button> ' +
        '<button type="button" class="btn-danger delete-memory-btn" data-id="' + escapeAttr(item.id) + '">删除</button></div></div>' +
        '<div class="skill-desc">' + escapeHtml(text) + '</div>' +
        '<div class="help-text">' + agentLabel + createdAt + '</div></div>';
    }).join('') : '<p class="help-text">暂无记忆，或未配置 Embedding 模型。</p>';
    var el = document.getElementById('memoryList');
    if (el) el.innerHTML = html;
    document.querySelectorAll('.edit-memory-btn').forEach(function (btn) {
      btn.addEventListener('click', function () { openMemoryEditor(btn.getAttribute('data-id')); });
    });
    document.querySelectorAll('.delete-memory-btn').forEach(function (btn) {
      btn.addEventListener('click', function () { deleteMemory(btn.getAttribute('data-id')); });
    });
  }).catch(function (e) {
    var el = document.getElementById('memoryList');
    if (el) el.innerHTML = '<p class="help-text">加载失败: ' + escapeHtml(String(e.message || e)) + '</p>';
  });
}

function openMemoryEditor(id) {
  var title = document.getElementById('memoryEditorTitle');
  var editId = document.getElementById('memoryEditId');
  var textEl = document.getElementById('memoryEditText');
  var tagsEl = document.getElementById('memoryEditTags');
  var editor = document.getElementById('memoryEditor');
  if (!editor || !textEl) return;
  if (id) {
    if (title) title.textContent = '编辑记忆';
    if (editId) editId.value = id;
    var baseUrl = API_URL.replace('/api/config', '');
    tasklyFetch(baseUrl + '/api/memory/' + encodeURIComponent(id)).then(async function (r) {
      var data = await r.json().catch(function () { return {}; });
      if (!r.ok) {
        alert('加载失败：' + (data && data.message ? data.message : r.status));
        return;
      }
      var item = data;
      textEl.value = item.text || '';
      tagsEl.value = (item.metadata && item.metadata.tags) ? item.metadata.tags : '';
      var scopeCb = document.getElementById('memoryEditScopeShared');
      if (scopeCb) scopeCb.checked = item.sessionId === MEMORY_SCOPE_SHARED;
      editor.style.display = 'block';
    }).catch(function (e) {
      alert('加载失败：' + (e && e.message ? e.message : String(e)));
    });
  } else {
    if (title) title.textContent = '新增记忆';
    if (editId) editId.value = '';
    textEl.value = '';
    tagsEl.value = '';
    var scopeCb = document.getElementById('memoryEditScopeShared');
    if (scopeCb) scopeCb.checked = false;
    editor.style.display = 'block';
  }
}

function closeMemoryEditor() {
  var editor = document.getElementById('memoryEditor');
  if (editor) editor.style.display = 'none';
}

function saveMemoryFromEditor() {
  var editId = document.getElementById('memoryEditId');
  var textEl = document.getElementById('memoryEditText');
  var tagsEl = document.getElementById('memoryEditTags');
  var scopeCb = document.getElementById('memoryEditScopeShared');
  var id = editId && editId.value ? editId.value.trim() : '';
  var text = textEl && textEl.value ? textEl.value.trim() : '';
  if (!text) { alert('请输入内容'); return; }
  var scopeShared = scopeCb && scopeCb.checked;
  var baseUrl = API_URL.replace('/api/config', '');
  var url = id ? baseUrl + '/api/memory/' + encodeURIComponent(id) : baseUrl + '/api/memory';
  var method = id ? 'PUT' : 'POST';
  var body = id
    ? JSON.stringify({ text: text, tags: tagsEl && tagsEl.value ? tagsEl.value.trim() : '', scopeShared: scopeShared })
    : JSON.stringify({ text: text, tags: tagsEl && tagsEl.value ? tagsEl.value.trim() : '', scopeShared: scopeShared });
  tasklyFetch(url, { method: method, headers: { 'Content-Type': 'application/json' }, body: body }).then(function (r) {
    if (!r.ok) return r.json().catch(function () { return {}; }).then(function (data) { throw new Error(data.message || '保存失败'); });
    closeMemoryEditor();
    loadMemoryList();
  }).catch(function (e) { alert('保存失败: ' + (e.message || e)); });
}

function deleteMemory(id) {
  if (!id || !confirm('确定删除这条记忆？')) return;
  var baseUrl = API_URL.replace('/api/config', '');
  tasklyFetch(baseUrl + '/api/memory/' + encodeURIComponent(id), { method: 'DELETE' }).then(function (r) {
    if (!r.ok) return r.json().catch(function () { return {}; }).then(function (data) { throw new Error(data.message || '删除失败'); });
    loadMemoryList();
  }).catch(function (e) { alert('删除失败: ' + (e.message || e)); });
}


function testEmbeddingConnection() {
  var endEl = document.getElementById('embeddingEditorEndpoint');
  var keyEl = document.getElementById('embeddingEditorApiKey');
  var modelEl = document.getElementById('embeddingEditorModelId');
  var vendorEl = document.getElementById('embeddingEditorVendor');
  var endpoint = (endEl && endEl.value && endEl.value.trim()) || '';
  var apiKey = (keyEl && keyEl.value) || '';
  var modelId = (modelEl && modelEl.value && modelEl.value.trim()) || '';
  var vendorId = (vendorEl && vendorEl.value) ? vendorEl.value : undefined;
  doTestEmbeddingConnection(endpoint, apiKey, modelId, undefined, vendorId);
}

async function doTestEmbeddingConnection(endpoint, apiKey, modelId, statusEl, vendorId) {
  statusEl = statusEl || document.getElementById('testEmbeddingStatus');
  if (!statusEl) return;
  if (!endpoint || !modelId) {
    statusEl.textContent = '请先填写接口地址和模型 ID';
    statusEl.style.color = 'var(--danger, #ef4444)';
    return;
  }
  statusEl.textContent = '测试中…';
  statusEl.style.color = 'var(--text-secondary, #94a3b8)';
  try {
    var baseUrl = API_URL.replace('/api/config', '');
    var body = { endpoint: endpoint, apiKey: apiKey, modelId: modelId };
    if (vendorId) body.vendorId = vendorId;
    var res = await tasklyFetch(baseUrl + '/api/config/test-embedding', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    });
    var data = await res.json().catch(function () { return { ok: false, message: '响应解析失败' }; });
    if (res.ok && data.ok) {
      statusEl.textContent = '连接成功';
      statusEl.style.color = 'var(--success, #22c55e)';
    } else {
      statusEl.textContent = data.message || '测试失败';
      statusEl.style.color = 'var(--danger, #ef4444)';
    }
  } catch (e) {
    statusEl.textContent = '请求失败: ' + (e.message || e);
    statusEl.style.color = 'var(--danger, #ef4444)';
  }
}
if (document.getElementById('testEmbeddingBtn')) document.getElementById('testEmbeddingBtn').addEventListener('click', testEmbeddingConnection);
if (document.getElementById('addEmbeddingModelBtn')) document.getElementById('addEmbeddingModelBtn').addEventListener('click', function () { openEmbeddingEditor(null); });
if (document.getElementById('closeEmbeddingEditorBtn')) document.getElementById('closeEmbeddingEditorBtn').addEventListener('click', closeEmbeddingEditor);
if (document.getElementById('saveEmbeddingModelBtn')) document.getElementById('saveEmbeddingModelBtn').addEventListener('click', saveEmbeddingFromEditor);

(function wireMicExtOriginHelp() {
  var disp = document.getElementById('micExtOriginDisplay');
  var btn = document.getElementById('openMicSiteSettingsBtn');
  if (!disp && !btn) return;
  var id = (typeof chrome !== 'undefined' && chrome.runtime && chrome.runtime.id) ? chrome.runtime.id : '';
  var origin = id ? ('chrome-extension://' + id + '/') : '';
  if (disp) {
    disp.textContent = id
      ? ('当前扩展站点：' + origin)
      : '无法读取扩展 ID（非扩展环境）';
  }
  function openMicSiteSettings() {
    function fail(msg) {
      alert(
        msg ||
          '无法自动打开设置页。请手动打开 Chrome「设置 → 隐私与安全 → 网站设置 → 麦克风」，找到 ' +
            (origin || '本扩展') +
            ' 并允许麦克风。'
      );
    }
    if (!id || !origin) {
      fail('扩展 ID 不可用。');
      return;
    }
    if (!chrome.tabs || !chrome.tabs.create) {
      fail('tabs API 不可用。');
      return;
    }
    var siteParam = encodeURIComponent(origin);
    var urls = [
      'chrome://settings/content/siteDetails?site=' + siteParam,
      'chrome://extensions/?id=' + encodeURIComponent(id)
    ];
    function tryUrl(index) {
      if (index >= urls.length) {
        fail();
        return;
      }
      chrome.tabs.create({ url: urls[index] }, function () {
        if (chrome.runtime.lastError) {
          tryUrl(index + 1);
        }
      });
    }
    tryUrl(0);
  }
  if (btn) btn.addEventListener('click', openMicSiteSettings);
})();

function fillRealtimeAsrInForm(data) {
  var ra = data && (data.realtimeAsr || data.RealtimeAsr);
  if (!ra) ra = {};
  var apiEl = document.getElementById('realtimeAsrApiKey');
  var wsEl = document.getElementById('realtimeAsrWsUrl');
  var modelEl = document.getElementById('realtimeAsrModelId');
  var hintsEl = document.getElementById('realtimeAsrLanguageHints');
  var wsIdEl = document.getElementById('realtimeAsrWorkspaceId');
  var hbEl = document.getElementById('realtimeAsrHeartbeat');
  var semEl = document.getElementById('realtimeAsrSemanticPunctuation');
  var disEl = document.getElementById('realtimeAsrDisfluencyRemoval');
  if (apiEl) apiEl.value = ra.apiKey || ra.ApiKey || '';
  if (wsEl) wsEl.value = ra.webSocketBaseUrl || ra.WebSocketBaseUrl || 'wss://dashscope.aliyuncs.com/api-ws/v1/inference';
  if (modelEl) modelEl.value = ra.modelId || ra.ModelId || 'fun-asr-realtime';
  var hints = ra.languageHints || ra.LanguageHints;
  if (hintsEl) hintsEl.value = Array.isArray(hints) ? hints.join(', ') : '';
  if (wsIdEl) wsIdEl.value = ra.workspaceId || ra.WorkspaceId || '';
  if (hbEl) hbEl.checked = ra.heartbeat !== false && ra.Heartbeat !== false;
  if (semEl) semEl.checked = ra.semanticPunctuationEnabled !== false && ra.SemanticPunctuationEnabled !== false;
  if (disEl) disEl.checked = !!(ra.disfluencyRemovalEnabled || ra.DisfluencyRemovalEnabled);
}

function collectRealtimeAsrPayload() {
  var apiEl = document.getElementById('realtimeAsrApiKey');
  var wsEl = document.getElementById('realtimeAsrWsUrl');
  var modelEl = document.getElementById('realtimeAsrModelId');
  var hintsEl = document.getElementById('realtimeAsrLanguageHints');
  var wsIdEl = document.getElementById('realtimeAsrWorkspaceId');
  var hbEl = document.getElementById('realtimeAsrHeartbeat');
  var semEl = document.getElementById('realtimeAsrSemanticPunctuation');
  var disEl = document.getElementById('realtimeAsrDisfluencyRemoval');
  var hintsRaw = (hintsEl && hintsEl.value) || '';
  var hints = hintsRaw.split(/[\n,]+/).map(function (s) { return s.trim(); }).filter(Boolean);
  var wsUrl = (wsEl && wsEl.value.trim()) || 'wss://dashscope.aliyuncs.com/api-ws/v1/inference';
  var o = {
    apiKey: (apiEl && apiEl.value.trim()) || '',
    webSocketBaseUrl: wsUrl,
    modelId: (modelEl && modelEl.value.trim()) || 'fun-asr-realtime',
    heartbeat: hbEl ? hbEl.checked : true,
    semanticPunctuationEnabled: semEl ? semEl.checked : true,
    disfluencyRemovalEnabled: disEl ? disEl.checked : false
  };
  if (hints.length) o.languageHints = hints;
  var wid = wsIdEl && wsIdEl.value.trim();
  if (wid) o.workspaceId = wid;
  return o;
}

async function doTestRealtimeAsrConnection() {
  var statusEl = document.getElementById('testRealtimeAsrStatus');
  var apiEl = document.getElementById('realtimeAsrApiKey');
  var wsEl = document.getElementById('realtimeAsrWsUrl');
  var modelEl = document.getElementById('realtimeAsrModelId');
  if (!statusEl) return;
  var apiKey = (apiEl && apiEl.value.trim()) || '';
  if (!apiKey) {
    statusEl.textContent = '请先填写 API Key';
    statusEl.style.color = 'var(--danger, #ef4444)';
    return;
  }
  statusEl.textContent = '测试中…';
  statusEl.style.color = 'var(--text-secondary, #94a3b8)';
  try {
    var baseUrl = API_URL.replace('/api/config', '');
    var payload = {
      apiKey: apiKey,
      webSocketBaseUrl: (wsEl && wsEl.value.trim()) || undefined,
      modelId: (modelEl && modelEl.value.trim()) || undefined
    };
    var res = await tasklyFetch(baseUrl + '/api/config/test-realtime-asr', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });
    var data = await res.json().catch(function () { return { ok: false, message: '响应解析失败' }; });
    if (res.ok && data.ok) {
      statusEl.textContent = '连接成功' + (data.preview ? '（识别预览：' + String(data.preview).slice(0, 40) + '）' : '');
      statusEl.style.color = 'var(--success, #22c55e)';
    } else {
      statusEl.textContent = data.message || '测试失败';
      statusEl.style.color = 'var(--danger, #ef4444)';
    }
  } catch (e) {
    statusEl.textContent = '请求失败: ' + (e.message || e);
    statusEl.style.color = 'var(--danger, #ef4444)';
  }
}

function testOcrConnection() {
  var endEl = document.getElementById('ocrEditorEndpoint');
  var keyEl = document.getElementById('ocrEditorApiKey');
  var langEl = document.getElementById('ocrEditorLanguage');
  var modelEl = document.getElementById('ocrEditorModelId');
  var vendorEl = document.getElementById('ocrEditorVendor');
  var endpoint = (endEl && endEl.value && endEl.value.trim()) || '';
  var apiKey = (keyEl && keyEl.value) || '';
  var ocrPreset = findOcrPreset(vendorEl && vendorEl.value);
  var ck = ocrPreset && typeof ocrPreset.connectionKind === 'string' ? ocrPreset.connectionKind : '';
  doTestOcrConnection(endpoint, apiKey, undefined, {
    language: (langEl && langEl.value && langEl.value.trim()) || undefined,
    modelId: (modelEl && modelEl.value && modelEl.value.trim()) || undefined,
    connectionKind: ck || undefined,
    vendorId: (vendorEl && vendorEl.value) || undefined
  });
}

async function doTestOcrConnection(endpoint, apiKey, statusEl, opts) {
  opts = opts || {};
  statusEl = statusEl || document.getElementById('testOcrStatus');
  if (!statusEl) return;
  if (!endpoint) {
    statusEl.textContent = '请先填写接口地址';
    statusEl.style.color = 'var(--danger, #ef4444)';
    return;
  }
  if (!apiKey) {
    statusEl.textContent = '请先填写 API Key';
    statusEl.style.color = 'var(--danger, #ef4444)';
    return;
  }
  statusEl.textContent = '测试中…';
  statusEl.style.color = 'var(--text-secondary, #94a3b8)';
  try {
    var baseUrl = API_URL.replace('/api/config', '');
    var payload = { endpoint: endpoint, apiKey: apiKey };
    if (opts.language) payload.language = opts.language;
    if (opts.modelId) payload.modelId = opts.modelId;
    if (opts.connectionKind) payload.connectionKind = opts.connectionKind;
    if (opts.vendorId) payload.vendorId = opts.vendorId;
    var res = await tasklyFetch(baseUrl + '/api/config/test-ocr', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });
    var data = await res.json().catch(function () { return { ok: false, message: '响应解析失败' }; });
    if (res.ok && data.ok) {
      statusEl.textContent = '连接成功';
      statusEl.style.color = 'var(--success, #22c55e)';
    } else {
      statusEl.textContent = data.message || '测试失败';
      statusEl.style.color = 'var(--danger, #ef4444)';
    }
  } catch (e) {
    statusEl.textContent = '请求失败: ' + (e.message || e);
    statusEl.style.color = 'var(--danger, #ef4444)';
  }
}

if (document.getElementById('testRealtimeAsrBtn')) document.getElementById('testRealtimeAsrBtn').addEventListener('click', doTestRealtimeAsrConnection);
['realtimeAsrApiKey', 'realtimeAsrWsUrl', 'realtimeAsrModelId', 'realtimeAsrLanguageHints', 'realtimeAsrWorkspaceId'].forEach(function (id) {
  var el = document.getElementById(id);
  if (el) el.addEventListener('input', debouncedSaveConfig);
});
['realtimeAsrHeartbeat', 'realtimeAsrSemanticPunctuation', 'realtimeAsrDisfluencyRemoval'].forEach(function (id) {
  var el = document.getElementById(id);
  if (el) el.addEventListener('change', debouncedSaveConfig);
});

if (document.getElementById('addOcrModelBtn')) document.getElementById('addOcrModelBtn').addEventListener('click', function () { openOcrEditor(null); });
if (document.getElementById('closeOcrEditorBtn')) document.getElementById('closeOcrEditorBtn').addEventListener('click', closeOcrEditor);
if (document.getElementById('saveOcrModelBtn')) document.getElementById('saveOcrModelBtn').addEventListener('click', saveOcrFromEditor);
if (document.getElementById('testOcrBtn')) document.getElementById('testOcrBtn').addEventListener('click', testOcrConnection);

var ragStorageTypeEl = document.getElementById('ragStorageType');
if (ragStorageTypeEl) {
  ragStorageTypeEl.addEventListener('change', function () {
    var g = document.getElementById('ragStoragePathGroup');
    if (g) g.style.display = (this.value === 'Sqlite') ? 'block' : 'none';
    saveConfig();
  });
}
var ragStoragePathEl = document.getElementById('ragStoragePath');
if (ragStoragePathEl) ragStoragePathEl.addEventListener('change', debouncedSaveConfig);

var CLI_SCRIPT_END_KEYS = ['chrome', 'backend', 'office', 'wps'];
var CLI_SCRIPT_END_LABELS = { chrome: 'Chrome', backend: '后台', office: 'Office', wps: 'WPS' };
var DEFAULT_CLI_COMMANDS_BACKEND = ['dir', 'echo', 'type', 'ping', 'systeminfo', 'ipconfig'];
var DEFAULT_PAGE_SCRIPTS = ['scroll_to_top', 'scroll_to_bottom', 'get_visible_text', 'get_page_title'];
/** 与 Office 任务窗格 DOCUMENT_SCRIPTS 对齐（current_run_document_script） */
var DEFAULT_OFFICE_DOCUMENT_SCRIPTS = ['word_read_selection', 'office_doc_meta', 'office_word_body_preview', 'office_host_quick_glance'];
/** 与 WPS 任务窗格 DOCUMENT_SCRIPTS 对齐 */
var DEFAULT_WPS_DOCUMENT_SCRIPTS = ['word_read_selection', 'wps_doc_meta', 'wps_word_body_preview', 'wps_ppt_slide_glance'];

function getBuiltinCliCommandsForEnd(endKey) {
  if (endKey === 'backend') return DEFAULT_CLI_COMMANDS_BACKEND;
  return [];
}

function getBuiltinDocumentScriptIdsForEnd(endKey) {
  if (endKey === 'office') return DEFAULT_OFFICE_DOCUMENT_SCRIPTS;
  if (endKey === 'wps') return DEFAULT_WPS_DOCUMENT_SCRIPTS;
  return [];
}
var CLI_RUN_MODES = [
  { value: 'RunEverything', label: 'RunEverything（不校验、不弹确认）' },
  { value: 'AskEverytime', label: 'AskEverytime（每次执行前确认）' },
  { value: 'UseAllowList', label: 'UseAllowList（白名单内不弹，名单外确认）' }
];

function getGlobalCliRunModeFromConfig() {
  var g = fullConfig && (fullConfig.cliRunMode != null ? fullConfig.cliRunMode : fullConfig.CliRunMode);
  if (g != null && String(g).trim()) return String(g).trim();
  var legacy = fullConfig && (fullConfig.cliRunModeByClient || fullConfig.CliRunModeByClient);
  if (legacy && typeof legacy === 'object') {
    var order = ['chrome', 'backend', 'office', 'wps'];
    for (var i = 0; i < order.length; i++) {
      var v = legacy[order[i]];
      if (v != null && String(v).trim()) return String(v).trim();
    }
  }
  return 'UseAllowList';
}

if (document.getElementById('addMemoryBtn')) document.getElementById('addMemoryBtn').addEventListener('click', function () { openMemoryEditor(null); });
if (document.getElementById('refreshMemoryListBtn')) document.getElementById('refreshMemoryListBtn').addEventListener('click', loadMemoryList);
if (document.getElementById('refreshPlansListBtn')) document.getElementById('refreshPlansListBtn').addEventListener('click', loadPlansList);
if (document.getElementById('saveMemoryBtn')) document.getElementById('saveMemoryBtn').addEventListener('click', saveMemoryFromEditor);
if (document.getElementById('cancelMemoryEditBtn')) document.getElementById('cancelMemoryEditBtn').addEventListener('click', closeMemoryEditor);
var memoryScopeFilterEl = document.getElementById('memoryScopeFilter');
var memorySessionFilterEl = document.getElementById('memorySessionFilter');
var memoryAgentFilterEl = document.getElementById('memoryAgentFilter');
if (memoryScopeFilterEl) {
  function toggleMemoryFilters() {
    var scope = memoryScopeFilterEl.value;
    if (memorySessionFilterEl) memorySessionFilterEl.style.display = (scope === 'session') ? 'inline-block' : 'none';
    if (memoryAgentFilterEl) memoryAgentFilterEl.style.display = (scope === 'agent') ? 'inline-block' : 'none';
  }
  memoryScopeFilterEl.addEventListener('change', toggleMemoryFilters);
  toggleMemoryFilters();
}

function openAiModelEditor(existingId) {
  editingAiModelId = existingId || null;
  els.aiModelEditorTitle.textContent = existingId ? '编辑 AI 模型' : '添加新 AI';
  var vendorSelect = document.getElementById('aiModelVendor');
  if (existingId) {
    const m = aiModelsCache.find(function (x) { return (x.id || x.Id) === existingId; });
    if (m) {
      els.aiModelDisplayName.value = m.displayName || m.DisplayName || '';
      els.aiModelProvider.value = m.provider || m.Provider || 'OpenAI';
      els.aiModelEndpoint.value = m.endpoint || m.Endpoint || '';
      els.aiModelApiKey.value = m.apiKey || m.ApiKey || '';
      els.aiModelDeploymentName.value = m.deploymentName || m.DeploymentName || '';
      els.aiModelApiVersion.value = m.apiVersion || m.ApiVersion || '2024-02-01';
      els.aiModelModelId.value = m.modelId || m.ModelId || '';
      els.aiModelSystemPrompt.value = m.systemPrompt || m.SystemPrompt || '';
      els.aiModelEnabled.checked = m.enabled !== false && m.Enabled !== false;
      if (vendorSelect) vendorSelect.value = resolveChatVendorId(m);
      testConnectionFields = {
        endpoint: String(m.endpoint || m.Endpoint || '').trim(),
        modelId: String(m.modelId || m.ModelId || '').trim(),
        apiKey: m.apiKey || m.ApiKey || '',
        provider: m.provider || m.Provider || 'OpenAI',
        deploymentName: String(m.deploymentName || m.DeploymentName || '').trim(),
        vendorId: resolveChatVendorId(m)
      };
    }
  } else {
    els.aiModelDisplayName.value = '';
    els.aiModelProvider.value = 'OpenAI';
    els.aiModelEndpoint.value = '';
    els.aiModelApiKey.value = '';
    els.aiModelDeploymentName.value = '';
    els.aiModelApiVersion.value = '2024-02-01';
    els.aiModelModelId.value = '';
    els.aiModelSystemPrompt.value = '';
    els.aiModelEnabled.checked = true;
    if (vendorSelect) vendorSelect.value = 'other_auto';
    testConnectionFields = { endpoint: '', modelId: '', apiKey: '', provider: 'OpenAI', deploymentName: '', vendorId: 'other_auto' };
  }
  updateChatProviderUi(!existingId);
  if (els.aiModelEditor) els.aiModelEditor.style.display = 'block';
  bindTestConnectionFieldsSync();
}

function bindTestConnectionFieldsSync() {
  var form = document.getElementById('aiModelForm');
  if (!form) return;
  function update() {
    var e = form.elements['endpoint'];
    var m = form.elements['modelId'];
    var p = form.elements['provider'];
    var d = form.elements['deploymentName'];
    var k = form.elements['apiKey'];
    var ve = document.getElementById('aiModelVendor');
    if (e && e.value != null) testConnectionFields.endpoint = String(e.value).trim();
    if (m && m.value != null) testConnectionFields.modelId = String(m.value).trim();
    if (p && p.value) testConnectionFields.provider = p.value;
    if (d && d.value != null) testConnectionFields.deploymentName = String(d.value).trim();
    if (k && k.value != null) testConnectionFields.apiKey = k.value;
    if (ve && ve.value) testConnectionFields.vendorId = ve.value;
  }
  ['endpoint', 'modelId', 'apiKey', 'provider', 'deploymentName'].forEach(function (name) {
    var el = form.elements[name];
    if (el) {
      el.removeEventListener('input', update);
      el.removeEventListener('change', update);
      el.addEventListener('input', update);
      el.addEventListener('change', update);
    }
  });
  var ve = document.getElementById('aiModelVendor');
  if (ve) {
    ve.removeEventListener('change', update);
    ve.addEventListener('change', update);
  }
}

function closeAiModelEditor() {
  editingAiModelId = null;
  if (els.aiModelEditor) els.aiModelEditor.style.display = 'none';
}

function toggleAzureFields() {
  var providerEl = document.getElementById('aiModelProvider');
  const provider = (providerEl && providerEl.value) ? providerEl.value : 'OpenAI';
  const isAzure = provider === 'Azure';
  const isOllama = provider === 'Ollama';
  const deployGroup = document.getElementById('aiModelDeploymentGroup');
  const apiVerGroup = document.getElementById('aiModelApiVersionGroup');
  const apiKeyGroup = document.getElementById('aiModelApiKeyGroup');
  if (deployGroup) deployGroup.style.display = isAzure ? 'block' : 'none';
  if (apiVerGroup) apiVerGroup.style.display = isAzure ? 'block' : 'none';
  if (apiKeyGroup) apiKeyGroup.style.display = isOllama ? 'none' : 'block';
}

function saveAiModelFromEditor() {
  const id = editingAiModelId || ('ai_' + Date.now());
  var vendorEl = document.getElementById('aiModelVendor');
  const entry = {
    id: id,
    displayName: (els.aiModelDisplayName && els.aiModelDisplayName.value.trim()) || id,
    provider: (els.aiModelProvider && els.aiModelProvider.value) || 'OpenAI',
    endpoint: (els.aiModelEndpoint && els.aiModelEndpoint.value.trim()) || '',
    apiKey: (els.aiModelApiKey && els.aiModelApiKey.value) || '',
    deploymentName: (els.aiModelDeploymentName && els.aiModelDeploymentName.value.trim()) || '',
    apiVersion: (els.aiModelApiVersion && els.aiModelApiVersion.value.trim()) || '2024-02-01',
    modelId: (els.aiModelModelId && els.aiModelModelId.value.trim()) || '',
    systemPrompt: (els.aiModelSystemPrompt && els.aiModelSystemPrompt.value.trim()) || '',
    enabled: els.aiModelEnabled ? els.aiModelEnabled.checked : true,
    vendorId: (vendorEl && vendorEl.value) ? vendorEl.value : 'other_auto'
  };
  if (editingAiModelId) {
    const idx = aiModelsCache.findIndex(function (x) { return (x.id || x.Id) === editingAiModelId; });
    if (idx >= 0) aiModelsCache[idx] = entry;
  } else {
    aiModelsCache.push(entry);
  }
  fullConfig = fullConfig || {};
  fullConfig.aiModels = aiModelsCache;
  fullConfig.AiModels = aiModelsCache;
  if (aiModelsCache.length === 1 && !(fullConfig.activeModelId || fullConfig.ActiveModelId)) {
    fullConfig.activeModelId = entry.id;
    fullConfig.ActiveModelId = entry.id;
  }
  closeAiModelEditor();
  renderAiModelsList();
  saveConfig();
}

function deleteAiModel(id) {
  if (!id || !confirm('确定删除该 AI 模型？')) return;
  aiModelsCache = aiModelsCache.filter(function (m) { return (m.id || m.Id) !== id; });
  fullConfig = fullConfig || {};
  fullConfig.aiModels = aiModelsCache;
  fullConfig.AiModels = aiModelsCache;
  if ((fullConfig.activeModelId || fullConfig.ActiveModelId) === id) {
    fullConfig.activeModelId = aiModelsCache.length ? (aiModelsCache[0].id || aiModelsCache[0].Id) : '';
    fullConfig.ActiveModelId = fullConfig.activeModelId;
  }
  renderAiModelsList();
  saveConfig();
}

async function loadSkillEnvSection() {
  const listEl = document.getElementById('skillEnvList');
  const sectionEl = document.getElementById('skillEnvSection');
  if (!listEl || !sectionEl) return;
  const skillEnv = fullConfig && (fullConfig.skillEnv || fullConfig.SkillEnv);
  const existing = skillEnv && typeof skillEnv === 'object' ? Object.keys(skillEnv) : [];
  let keysSet = new Set(existing);
  try {
    const res = await tasklyFetch(SKILLS_API_URL);
    if (res.ok) {
      const skills = await res.json();
      if (Array.isArray(skills)) {
        skills.forEach(function (s) {
          const env = s.requiresEnv || s.RequiresEnv;
          if (Array.isArray(env)) env.forEach(function (k) { if (k) keysSet.add(k); });
          if (s.primaryEnv || s.PrimaryEnv) keysSet.add(s.primaryEnv || s.PrimaryEnv);
        });
      }
    }
  } catch (e) { /* 忽略，仅用已有 key 渲染 */ }
  skillEnvKeys = Array.from(keysSet).sort();
  if (skillEnvKeys.length === 0) {
    listEl.innerHTML = '<p class="help-text" style="margin:0;">当前没有需要配置环境变量的技能。安装带 requires.env 的 Clawhub 技能后刷新本页即可出现对应输入项。</p>';
    return;
  }
  listEl.innerHTML = skillEnvKeys.map(function (key) {
    const val = (skillEnv && skillEnv[key]) || '';
    const id = 'skillEnv_' + key.replace(/[^a-zA-Z0-9_]/g, '_');
    return '<div class="form-group" style="margin-bottom:12px;"><label for="' + escapeAttr(id) + '">' + escapeHtml(key) + '</label><input type="password" id="' + escapeAttr(id) + '" placeholder="可选，也可用系统环境变量" value="' + escapeAttr(String(val)) + '"></div>';
  }).join('');
  skillEnvKeys.forEach(function (key) {
    const id = 'skillEnv_' + key.replace(/[^a-zA-Z0-9_]/g, '_');
    const inputEl = document.getElementById(id);
    if (inputEl) inputEl.addEventListener('input', debouncedSaveConfig);
  });
}

function escapeAttr(s) {
  if (!s) return '';
  return String(s)
    .replace(/&/g, '&amp;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}

function collectSkillEnv() {
  const base = fullConfig && (fullConfig.skillEnv || fullConfig.SkillEnv);
  const out = base && typeof base === 'object' ? { ...base } : {};
  skillEnvKeys.forEach(function (key) {
    const id = 'skillEnv_' + key.replace(/[^a-zA-Z0-9_]/g, '_');
    const el = document.getElementById(id);
    if (el) {
      const v = (el.value && el.value.trim()) || '';
      if (v) out[key] = v; else delete out[key];
    }
  });
  return out;
}

function setSaveConfigButtonsState(showBusy, text) {
  document.querySelectorAll('.save-config-status').forEach(function (el) {
    el.textContent = text || '已自动保存';
    el.style.opacity = text ? '1' : '0';
  });
}

var saveConfigDebounceTimer = null;
var SAVE_DEBOUNCE_MS = 600;

function debouncedSaveConfig() {
  if (saveConfigDebounceTimer) clearTimeout(saveConfigDebounceTimer);
  saveConfigDebounceTimer = setTimeout(function () {
    saveConfigDebounceTimer = null;
    saveConfig();
  }, SAVE_DEBOUNCE_MS);
}

async function loadConfig() {
  try {
    setSaveConfigButtonsState(true, '加载中...');
    
    const response = await tasklyFetch(API_URL);
    if (!response.ok) {
      var errData = await response.json().catch(function () { return {}; });
      throw new Error(errData.message || '加载配置失败');
    }
    fullConfig = await response.json();
    const data = fullConfig;
    var apEl = document.getElementById('allowPrivateEndpointTests');
    if (apEl) apEl.checked = !!(data.allowPrivateEndpointTests ?? data.AllowPrivateEndpointTests);
    var srvTok = String((data.webSocketAuthToken ?? data.WebSocketAuthToken) ?? '').trim();
    var tokInp = document.getElementById('localServiceAuthToken');
    if (tokInp) {
      if (srvTok) tokInp.value = srvTok;
      else chrome.storage.local.get([COPILOT_TOKEN_STORAGE_KEY], function (sr) {
        if (sr && sr[COPILOT_TOKEN_STORAGE_KEY] != null && !tokInp.value) tokInp.value = String(sr[COPILOT_TOKEN_STORAGE_KEY]);
      });
    }
    if (srvTok) {
      var o = {};
      o[COPILOT_TOKEN_STORAGE_KEY] = srvTok;
      chrome.storage.local.set(o);
    }
    renderAiModelsList();
    renderCliScriptUnifiedConfig();
    const mcps = data.mcpServers ?? data.McpServers;
    renderMcpList(mcps || []);
    await loadSkillEnvSection();
    await loadBuiltinTools();
    renderEmbeddingModelsList();
    fillRealtimeAsrInForm(data);
    renderOcrModelsList();
    var ragType = data.ragStorageType ?? data.RagStorageType ?? 'Sqlite';
    var ragPath = data.ragStoragePath ?? data.RagStoragePath ?? '';
    var rtEl = document.getElementById('ragStorageType');
    var rpEl = document.getElementById('ragStoragePath');
    var rpGroup = document.getElementById('ragStoragePathGroup');
    if (rtEl) rtEl.value = ragType || 'Sqlite';
    if (rpEl) rpEl.value = ragPath || '';
    if (rpGroup) rpGroup.style.display = (ragType === 'Sqlite') ? 'block' : 'none';
    var pc = data.planConfirmation ?? data.PlanConfirmation;
    if (pc) {
      var aeEl = document.getElementById('autoExecuteMaxSteps');
      if (aeEl) aeEl.value = (pc.autoExecuteMaxSteps ?? pc.AutoExecuteMaxSteps ?? 3);
    }
    var presets = data.contextOptimizationPresets ?? data.ContextOptimizationPresets;
    var activePresetId = data.activeContextPresetId ?? data.ActiveContextPresetId ?? '';
    var presetEl = document.getElementById('activeContextPreset');
    if (presetEl) {
      presetEl.innerHTML = '<option value="">-- 请选择 --</option>';
      if (Array.isArray(presets) && presets.length > 0) {
        presets.forEach(function (p) {
          var id = p.id ?? p.Id ?? '';
          var name = p.displayName ?? p.DisplayName ?? id;
          var opt = document.createElement('option');
          opt.value = id;
          opt.textContent = name;
          presetEl.appendChild(opt);
        });
      }
      presetEl.value = activePresetId || '';
    }
    fillContextWindowForm(activePresetId, presets);
    fillSessionForm(activePresetId, presets);
    toggleContextWindowSection(!!activePresetId);
    toggleSessionSection(!!activePresetId);
    updatePresetRenameDeleteVisibility(activePresetId, presets);
    var themeEl = document.getElementById('uiThemeId');
    if (themeEl && typeof TasklyTheme !== 'undefined') {
      var serverTheme = data.uiThemeId ?? data.UiThemeId;
      if (serverTheme != null && String(serverTheme).trim() !== '') {
        TasklyTheme.setTheme(String(serverTheme).trim());
      } else {
        TasklyTheme.applyFromStorage();
      }
      themeEl.value = TasklyTheme.normalize(localStorage.getItem(TasklyTheme.KEY) || 'dark');
    } else if (themeEl) {
      themeEl.value = 'dark';
    }
  } catch (err) {
    var friendly = messageForBackendUnreachable(err);
    if (friendly) console.warn(friendly);
    else console.error(err);
    var msg = friendly || (err && err.message) || '无法连接到本地服务，请确保已启动 OfficeCopilot.Server。';
    alert(msg);
  } finally {
    setSaveConfigButtonsState(false, '');
  }
}

function getPresets() {
  var raw = fullConfig && (fullConfig.contextOptimizationPresets || fullConfig.ContextOptimizationPresets);
  return Array.isArray(raw) ? raw : [];
}

function getActivePresetId() {
  var el = document.getElementById('activeContextPreset');
  return (el && el.value) ? el.value : '';
}

function fillContextWindowForm(activePresetId, presets) {
  if (!activePresetId || !Array.isArray(presets) || presets.length === 0) return;
  var preset = presets.find(function (p) { return (p.id || p.Id) === activePresetId; });
  if (!preset) return;
  var cw = preset.contextWindow || preset.ContextWindow || {};
  function num(val, def) { var n = parseInt(val, 10); return isNaN(n) ? def : n; }
  function floatVal(val, def) { var n = parseFloat(val); return isNaN(n) ? def : n; }
  var passThroughEl = document.getElementById('ctxPassThroughContext');
  if (passThroughEl) passThroughEl.checked = !!(cw.passThroughContext ?? cw.PassThroughContext);
  toggleContextWindowDetailsDisabled(!!(cw.passThroughContext ?? cw.PassThroughContext));
  var dirEl = document.getElementById('ctxConversationHistoryDirectory');
  var sumEnEl = document.getElementById('ctxSummarizationEnabled');
  var sumRatioEl = document.getElementById('ctxSummarizationTriggerRatio');
  var sumCharsEl = document.getElementById('ctxSummarizationMaxSummaryChars');
  var truncRatioEl = document.getElementById('ctxTruncateToolArgsThresholdRatio');
  var truncKeepEl = document.getElementById('ctxTruncateToolArgsKeepMessages');
  var truncMaxEl = document.getElementById('ctxTruncateToolArgsMaxChars');
  var maxCtxEl = document.getElementById('ctxMaxContextTokens');
  var resSysEl = document.getElementById('ctxReservedSystemTokens');
  var resToolsEl = document.getElementById('ctxReservedToolsTokens');
  var resOutEl = document.getElementById('ctxReservedOutputTokens');
  var planCharsEl = document.getElementById('ctxPlanContentMaxChars');
  var memInjEl = document.getElementById('ctxMemoryInjectionMaxChars');
  var memSessEl = document.getElementById('ctxMemorySessionTopK');
  var memSharedEl = document.getElementById('ctxMemorySharedTopK');
  var tokenEstEl = document.getElementById('ctxTokenEstimation');
  var charsPerEl = document.getElementById('ctxCharsPerToken');
  var retryEnEl = document.getElementById('ctxContextLengthRetryEnabled');
  var retryTurnsEl = document.getElementById('ctxContextLengthRetryMaxTurns');
  if (maxCtxEl) maxCtxEl.value = (cw.maxContextTokens ?? cw.MaxContextTokens ?? 64000);
  if (resSysEl) resSysEl.value = (cw.reservedSystemTokens ?? cw.ReservedSystemTokens ?? 12000);
  if (resToolsEl) resToolsEl.value = (cw.reservedToolsTokens ?? cw.ReservedToolsTokens ?? 12000);
  if (resOutEl) resOutEl.value = (cw.reservedOutputTokens ?? cw.ReservedOutputTokens ?? 4096);
  if (planCharsEl) planCharsEl.value = (cw.planContentMaxChars ?? cw.PlanContentMaxChars ?? 16000);
  if (memInjEl) memInjEl.value = (cw.memoryInjectionMaxChars ?? cw.MemoryInjectionMaxChars ?? 4000);
  if (memSessEl) memSessEl.value = (cw.memorySessionTopK ?? cw.MemorySessionTopK ?? 5);
  if (memSharedEl) memSharedEl.value = (cw.memorySharedTopK ?? cw.MemorySharedTopK ?? 3);
  if (tokenEstEl) tokenEstEl.value = (cw.tokenEstimation ?? cw.TokenEstimation ?? 'CharsRatio');
  if (charsPerEl) charsPerEl.value = (cw.charsPerToken ?? cw.CharsPerToken ?? 2);
  if (retryEnEl) retryEnEl.checked = (cw.contextLengthRetryEnabled ?? cw.ContextLengthRetryEnabled ?? true);
  if (retryTurnsEl) retryTurnsEl.value = (cw.contextLengthRetryMaxTurns ?? cw.ContextLengthRetryMaxTurns ?? 10);
  if (dirEl) dirEl.value = (cw.conversationHistoryDirectory ?? cw.ConversationHistoryDirectory) ?? '';
  if (sumEnEl) sumEnEl.checked = !!(cw.summarizationEnabled ?? cw.SummarizationEnabled);
  if (sumRatioEl) sumRatioEl.value = (cw.summarizationTriggerRatio ?? cw.SummarizationTriggerRatio) ?? 0.9;
  if (sumCharsEl) sumCharsEl.value = (cw.summarizationMaxSummaryChars ?? cw.SummarizationMaxSummaryChars) ?? 500;
  if (truncRatioEl) truncRatioEl.value = (cw.truncateToolArgsThresholdRatio ?? cw.TruncateToolArgsThresholdRatio) ?? 0;
  if (truncKeepEl) truncKeepEl.value = (cw.truncateToolArgsKeepMessages ?? cw.TruncateToolArgsKeepMessages) ?? 10;
  if (truncMaxEl) truncMaxEl.value = (cw.truncateToolArgsMaxChars ?? cw.TruncateToolArgsMaxChars) ?? 2000;
}

function fillSessionForm(activePresetId, presets) {
  if (!activePresetId || !Array.isArray(presets) || presets.length === 0) return;
  var preset = presets.find(function (p) { return (p.id || p.Id) === activePresetId; });
  if (!preset) return;
  var sess = preset.session || preset.Session || {};
  function num(val, def) { var n = parseInt(val, 10); return isNaN(n) ? def : n; }
  var maxTurnsEl = document.getElementById('sessionMaxHistoryTurns');
  var minTurnsEl = document.getElementById('sessionMinTurnsToKeep');
  var timeoutEl = document.getElementById('sessionTimeoutMinutes');
  var cleanupEl = document.getElementById('sessionCleanupIntervalMinutes');
  if (maxTurnsEl) maxTurnsEl.value = (sess.maxHistoryTurns ?? sess.MaxHistoryTurns ?? 80);
  if (minTurnsEl) minTurnsEl.value = (sess.minTurnsToKeep ?? sess.MinTurnsToKeep ?? 8);
  if (timeoutEl) timeoutEl.value = (sess.timeoutMinutes ?? sess.TimeoutMinutes ?? 30);
  if (cleanupEl) cleanupEl.value = (sess.cleanupIntervalMinutes ?? sess.CleanupIntervalMinutes ?? 5);
}

function collectSessionFromForm() {
  var maxTurnsEl = document.getElementById('sessionMaxHistoryTurns');
  var minTurnsEl = document.getElementById('sessionMinTurnsToKeep');
  var timeoutEl = document.getElementById('sessionTimeoutMinutes');
  var cleanupEl = document.getElementById('sessionCleanupIntervalMinutes');
  function num(val, def) { var n = parseInt(val, 10); return isNaN(n) ? def : n; }
  return {
    maxHistoryTurns: num(maxTurnsEl && maxTurnsEl.value ? maxTurnsEl.value : '', 80),
    minTurnsToKeep: num(minTurnsEl && minTurnsEl.value ? minTurnsEl.value : '', 8),
    timeoutMinutes: num(timeoutEl && timeoutEl.value ? timeoutEl.value : '', 30),
    cleanupIntervalMinutes: num(cleanupEl && cleanupEl.value ? cleanupEl.value : '', 5)
  };
}

function collectContextWindowFromForm() {
  var dirEl = document.getElementById('ctxConversationHistoryDirectory');
  var sumEnEl = document.getElementById('ctxSummarizationEnabled');
  var sumRatioEl = document.getElementById('ctxSummarizationTriggerRatio');
  var sumCharsEl = document.getElementById('ctxSummarizationMaxSummaryChars');
  var truncRatioEl = document.getElementById('ctxTruncateToolArgsThresholdRatio');
  var truncKeepEl = document.getElementById('ctxTruncateToolArgsKeepMessages');
  var truncMaxEl = document.getElementById('ctxTruncateToolArgsMaxChars');
  function num(val, def) { var n = parseInt(val, 10); return isNaN(n) ? def : n; }
  function floatVal(val, def) { var n = parseFloat(val); return isNaN(n) ? def : n; }
  var maxCtxEl = document.getElementById('ctxMaxContextTokens');
  var resSysEl = document.getElementById('ctxReservedSystemTokens');
  var resToolsEl = document.getElementById('ctxReservedToolsTokens');
  var resOutEl = document.getElementById('ctxReservedOutputTokens');
  var planCharsEl = document.getElementById('ctxPlanContentMaxChars');
  var memInjEl = document.getElementById('ctxMemoryInjectionMaxChars');
  var memSessEl = document.getElementById('ctxMemorySessionTopK');
  var memSharedEl = document.getElementById('ctxMemorySharedTopK');
  var tokenEstEl = document.getElementById('ctxTokenEstimation');
  var charsPerEl = document.getElementById('ctxCharsPerToken');
  var retryEnEl = document.getElementById('ctxContextLengthRetryEnabled');
  var retryTurnsEl = document.getElementById('ctxContextLengthRetryMaxTurns');
  var passThroughEl = document.getElementById('ctxPassThroughContext');
  return {
    passThroughContext: !!(passThroughEl && passThroughEl.checked),
    maxContextTokens: num(maxCtxEl && maxCtxEl.value ? maxCtxEl.value : '', 64000),
    reservedSystemTokens: num(resSysEl && resSysEl.value ? resSysEl.value : '', 12000),
    reservedToolsTokens: num(resToolsEl && resToolsEl.value ? resToolsEl.value : '', 12000),
    reservedOutputTokens: num(resOutEl && resOutEl.value ? resOutEl.value : '', 4096),
    planContentMaxChars: num(planCharsEl && planCharsEl.value ? planCharsEl.value : '', 16000),
    memoryInjectionMaxChars: num(memInjEl && memInjEl.value ? memInjEl.value : '', 4000),
    memorySessionTopK: num(memSessEl && memSessEl.value ? memSessEl.value : '', 5),
    memorySharedTopK: num(memSharedEl && memSharedEl.value ? memSharedEl.value : '', 3),
    tokenEstimation: (tokenEstEl && tokenEstEl.value) ? tokenEstEl.value : 'CharsRatio',
    charsPerToken: num(charsPerEl && charsPerEl.value ? charsPerEl.value : '', 2),
    contextLengthRetryEnabled: !!(retryEnEl && retryEnEl.checked),
    contextLengthRetryMaxTurns: num(retryTurnsEl && retryTurnsEl.value ? retryTurnsEl.value : '', 10),
    conversationHistoryDirectory: (dirEl && dirEl.value.trim()) || null,
    summarizationEnabled: !!(sumEnEl && sumEnEl.checked),
    summarizationTriggerRatio: floatVal(sumRatioEl ? sumRatioEl.value : '', 0.9),
    summarizationMaxSummaryChars: num(sumCharsEl ? sumCharsEl.value : '', 500),
    truncateToolArgsThresholdRatio: floatVal(truncRatioEl ? truncRatioEl.value : '', 0),
    truncateToolArgsKeepMessages: num(truncKeepEl ? truncKeepEl.value : '', 10),
    truncateToolArgsMaxChars: num(truncMaxEl ? truncMaxEl.value : '', 2000)
  };
}

function toggleContextWindowSection(show) {
  var section = document.getElementById('contextWindowSection');
  if (section) section.style.display = show ? 'block' : 'none';
}

function toggleContextWindowDetailsDisabled(passThroughOn) {
  var details = document.getElementById('contextWindowDetails');
  if (!details) return;
  details.style.opacity = passThroughOn ? '0.6' : '1';
  details.style.pointerEvents = passThroughOn ? 'none' : '';
}

function setupPassThroughContextToggle() {
  var el = document.getElementById('ctxPassThroughContext');
  if (!el) return;
  el.removeEventListener('change', onPassThroughContextChange);
  el.addEventListener('change', onPassThroughContextChange);
}

function onPassThroughContextChange() {
  var el = document.getElementById('ctxPassThroughContext');
  toggleContextWindowDetailsDisabled(!!(el && el.checked));
}

function toggleSessionSection(show) {
  var section = document.getElementById('sessionSection');
  if (section) section.style.display = show ? 'block' : 'none';
}

function updatePresetRenameDeleteVisibility(activePresetId, presets) {
  var renameBtn = document.getElementById('renameContextPresetBtn');
  var deleteBtn = document.getElementById('deleteContextPresetBtn');
  if (!renameBtn || !deleteBtn) return;
  if (!activePresetId || !Array.isArray(presets) || presets.length === 0) {
    renameBtn.style.display = 'none';
    deleteBtn.style.display = 'none';
    return;
  }
  renameBtn.style.display = 'inline-block';
  var id = (activePresetId || '').toLowerCase();
  var isBuiltIn = id === 'internal-64k' || id === 'kimi-k25';
  deleteBtn.style.display = isBuiltIn ? 'none' : 'inline-block';
}

async function saveConfig() {
  try {
    setSaveConfigButtonsState(true, '保存中…');
    var perEnd = collectCliSecurityPayload();
    const activeId = (fullConfig && (fullConfig.activeModelId || fullConfig.ActiveModelId)) || '';
    var aiModelsToSave = (aiModelsCache && aiModelsCache.length) ? aiModelsCache : (fullConfig.aiModels || fullConfig.AiModels || []);
    var activeEntry = aiModelsToSave.find(function (m) { return (m.id || m.Id) === activeId; });
    var legacyAi = fullConfig && (fullConfig.ai || fullConfig.AI);
    if (activeEntry && legacyAi) {
      legacyAi = {
        provider: activeEntry.provider || activeEntry.Provider || 'OpenAI',
        endpoint: activeEntry.endpoint || activeEntry.Endpoint || '',
        apiKey: activeEntry.apiKey || activeEntry.ApiKey || '',
        modelId: activeEntry.modelId || activeEntry.ModelId || '',
        systemPrompt: (activeEntry.systemPrompt || activeEntry.SystemPrompt) || (legacyAi.systemPrompt || legacyAi.SystemPrompt || ''),
        alwaysIncludePlugins: legacyAi.alwaysIncludePlugins || legacyAi.AlwaysIncludePlugins || []
      };
    }
    var aiPayload = legacyAi || fullConfig.ai || fullConfig.AI || {};
    var embeddingModelsToSave = getEmbeddingModels();
    var activeEmbeddingModelId = (fullConfig && (fullConfig.activeEmbeddingModelId || fullConfig.ActiveEmbeddingModelId)) || '';
    var ragTypeEl = document.getElementById('ragStorageType');
    var ragPathEl = document.getElementById('ragStoragePath');
    var ragType = ragTypeEl ? ragTypeEl.value : 'Sqlite';
    var ragPath = ragPathEl ? ragPathEl.value : '';
    var aeEl = document.getElementById('autoExecuteMaxSteps');
    var autoExecuteMaxSteps = (aeEl && aeEl.value !== '') ? parseInt(aeEl.value, 10) : (fullConfig && fullConfig.planConfirmation && (fullConfig.planConfirmation.autoExecuteMaxSteps ?? fullConfig.planConfirmation.AutoExecuteMaxSteps)) || 3;
    if (isNaN(autoExecuteMaxSteps) || autoExecuteMaxSteps < 1) autoExecuteMaxSteps = 3;
    var presetSelectEl = document.getElementById('activeContextPreset');
    var activeContextPresetId = (presetSelectEl && presetSelectEl.value) ? presetSelectEl.value : (fullConfig && (fullConfig.activeContextPresetId ?? fullConfig.ActiveContextPresetId));
    var contextOptimizationPresets = (fullConfig && (fullConfig.contextOptimizationPresets ?? fullConfig.ContextOptimizationPresets)) || [];
    var planConfirmationPayload = { autoExecuteMaxSteps: autoExecuteMaxSteps };
    if (activeContextPresetId && contextOptimizationPresets.length > 0) {
      var activePreset = contextOptimizationPresets.find(function (p) { return (p.id || p.Id) === activeContextPresetId; });
      if (activePreset) {
        activePreset.planConfirmation = activePreset.PlanConfirmation = planConfirmationPayload;
        var sessPayload = collectSessionFromForm();
        activePreset.session = activePreset.Session = sessPayload;
        var ctxPayload = collectContextWindowFromForm();
        if (ctxPayload) {
          var existing = activePreset.contextWindow || activePreset.ContextWindow || {};
          activePreset.contextWindow = activePreset.ContextWindow = Object.assign({}, existing, ctxPayload);
        }
      }
    }
    var ocrModelsToSave = getOcrModels();
    var activeOcrModelId = (fullConfig && (fullConfig.activeOcrModelId || fullConfig.ActiveOcrModelId)) || '';
    var uiThemeEl = document.getElementById('uiThemeId');
    var uiThemeId = (uiThemeEl && uiThemeEl.value) ? uiThemeEl.value : ((fullConfig && (fullConfig.uiThemeId ?? fullConfig.UiThemeId)) || 'dark');
    if (typeof TasklyTheme !== 'undefined') uiThemeId = TasklyTheme.normalize(uiThemeId);
    const payload = {
      ai: aiPayload,
      aiModels: aiModelsToSave,
      activeModelId: activeId,
      tavilyApiKey: (function () { var se = collectSkillEnv(); return (se && se.TAVILY_API_KEY) || (fullConfig && (fullConfig.tavilyApiKey ?? fullConfig.TavilyApiKey)) || ''; })(),
      skillEnv: collectSkillEnv(),
      mcpServers: (fullConfig && fullConfig.mcpServers) || (fullConfig && fullConfig.McpServers) || [],
      cliRunMode: perEnd.cliRunMode,
      allowedCliCommandsByClient: perEnd.allowedCliCommandsByClient,
      allowedPageScriptIdsByClient: perEnd.allowedPageScriptIdsByClient,
      allowedDocumentScriptIdsByClient: perEnd.allowedDocumentScriptIdsByClient,
      disabledBuiltInPlugins: getDisabledBuiltIn(),
      embeddingModels: embeddingModelsToSave,
      activeEmbeddingModelId: activeEmbeddingModelId || undefined,
      realtimeAsr: collectRealtimeAsrPayload(),
      ocrModels: ocrModelsToSave,
      activeOcrModelId: activeOcrModelId || undefined,
      ragStorageType: ragType,
      ragStoragePath: (ragType === 'Sqlite' && ragPath) ? ragPath : undefined,
      planConfirmation: planConfirmationPayload,
      activeContextPresetId: activeContextPresetId || undefined,
      contextOptimizationPresets: contextOptimizationPresets.length > 0 ? contextOptimizationPresets : undefined,
      uiThemeId: uiThemeId,
      allowPrivateEndpointTests: !!(document.getElementById('allowPrivateEndpointTests') && document.getElementById('allowPrivateEndpointTests').checked),
      webSocketAuthToken: (function () {
        var el = document.getElementById('localServiceAuthToken');
        return el ? String(el.value || '').trim() : '';
      })()
    };
    const response = await tasklyFetch(API_URL, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });
    if (!response.ok) {
      var data = await response.json().catch(function () { return {}; });
      throw new Error(data.message || '保存配置失败');
    }
    fullConfig = Object.assign({}, fullConfig || {}, { ai: payload.ai, aiModels: payload.aiModels, activeModelId: payload.activeModelId, tavilyApiKey: payload.tavilyApiKey, skillEnv: payload.skillEnv, mcpServers: payload.mcpServers, cliRunMode: payload.cliRunMode, allowedCliCommandsByClient: payload.allowedCliCommandsByClient, allowedPageScriptIdsByClient: payload.allowedPageScriptIdsByClient, allowedDocumentScriptIdsByClient: payload.allowedDocumentScriptIdsByClient, disabledBuiltInPlugins: payload.disabledBuiltInPlugins, embeddingModels: payload.embeddingModels, activeEmbeddingModelId: payload.activeEmbeddingModelId, realtimeAsr: payload.realtimeAsr, ocrModels: payload.ocrModels, activeOcrModelId: payload.activeOcrModelId, ragStorageType: payload.ragStorageType, ragStoragePath: payload.ragStoragePath, planConfirmation: payload.planConfirmation, activeContextPresetId: payload.activeContextPresetId, contextOptimizationPresets: payload.contextOptimizationPresets, uiThemeId: payload.uiThemeId, allowPrivateEndpointTests: payload.allowPrivateEndpointTests, webSocketAuthToken: payload.webSocketAuthToken });
    document.querySelectorAll('.save-config-status').forEach(function (el) {
      el.textContent = '已自动保存';
      el.style.opacity = '1';
    });
    setTimeout(function () {
      document.querySelectorAll('.save-config-status').forEach(function (el) {
        el.style.opacity = '0';
      });
    }, 2000);
    await loadConfig();
  } catch (err) {
    var friendly = messageForBackendUnreachable(err);
    var saveErrMsg = friendly || (err && err.message) || '保存失败，请检查服务状态。';
    if (friendly) console.warn(friendly);
    else console.error(err);
    alert(saveErrMsg);
  } finally {
    setSaveConfigButtonsState(false, '');
  }
}

var newContextPresetBtn = document.getElementById('newContextPresetBtn');
if (newContextPresetBtn) {
  newContextPresetBtn.addEventListener('click', function () {
    var presets = fullConfig && (fullConfig.contextOptimizationPresets || fullConfig.ContextOptimizationPresets);
    if (!Array.isArray(presets)) presets = [];
    var ctx = fullConfig && (fullConfig.contextWindow || fullConfig.ContextWindow);
    var sess = fullConfig && (fullConfig.session || fullConfig.Session);
    var plan = fullConfig && (fullConfig.planConfirmation || fullConfig.PlanConfirmation);
    function cloneObj(o) { return o ? JSON.parse(JSON.stringify(o)) : {}; }
    var newId = 'custom-' + Date.now();
    var newPreset = {
      id: newId,
      displayName: '自定义 ' + (presets.length + 1),
      contextWindow: cloneObj(ctx),
      session: cloneObj(sess),
      planConfirmation: cloneObj(plan)
    };
    fullConfig = fullConfig || {};
    fullConfig.contextOptimizationPresets = presets.slice();
    fullConfig.contextOptimizationPresets.push(newPreset);
    fullConfig.activeContextPresetId = newId;
    var presetEl = document.getElementById('activeContextPreset');
    if (presetEl) {
      var opt = document.createElement('option');
      opt.value = newId;
      opt.textContent = newPreset.displayName;
      presetEl.appendChild(opt);
      presetEl.value = newId;
    }
    saveConfig();
  });
}

var activeContextPresetEl = document.getElementById('activeContextPreset');
if (activeContextPresetEl) {
  activeContextPresetEl.addEventListener('change', function () {
    var presets = getPresets();
    var activeId = getActivePresetId();
    fillContextWindowForm(activeId, presets);
    fillSessionForm(activeId, presets);
    toggleContextWindowSection(!!activeId);
    toggleSessionSection(!!activeId);
    updatePresetRenameDeleteVisibility(activeId, presets);
    saveConfig();
  });
}
['ctxConversationHistoryDirectory', 'ctxSummarizationTriggerRatio', 'ctxSummarizationMaxSummaryChars', 'ctxTruncateToolArgsThresholdRatio', 'ctxTruncateToolArgsKeepMessages', 'ctxTruncateToolArgsMaxChars', 'ctxMaxContextTokens', 'ctxReservedSystemTokens', 'ctxReservedToolsTokens', 'ctxReservedOutputTokens', 'ctxPlanContentMaxChars', 'ctxMemoryInjectionMaxChars', 'ctxMemorySessionTopK', 'ctxMemorySharedTopK', 'ctxTokenEstimation', 'ctxCharsPerToken', 'ctxContextLengthRetryMaxTurns', 'sessionMaxHistoryTurns', 'sessionMinTurnsToKeep', 'sessionTimeoutMinutes', 'sessionCleanupIntervalMinutes'].forEach(function (id) {
  var el = document.getElementById(id);
  if (el) el.addEventListener('input', debouncedSaveConfig);
});
var ctxSummarizationEnabledEl = document.getElementById('ctxSummarizationEnabled');
if (ctxSummarizationEnabledEl) ctxSummarizationEnabledEl.addEventListener('change', debouncedSaveConfig);
var ctxContextLengthRetryEnabledEl = document.getElementById('ctxContextLengthRetryEnabled');
if (ctxContextLengthRetryEnabledEl) ctxContextLengthRetryEnabledEl.addEventListener('change', debouncedSaveConfig);

var renameContextPresetBtn = document.getElementById('renameContextPresetBtn');
if (renameContextPresetBtn) {
  renameContextPresetBtn.addEventListener('click', function () {
    var activeId = getActivePresetId();
    var presets = getPresets();
    if (!activeId || !presets.length) return;
    var preset = presets.find(function (p) { return (p.id || p.Id) === activeId; });
    if (!preset) return;
    var currentName = preset.displayName || preset.DisplayName || activeId;
    var newName = prompt('重命名预设', currentName);
    if (newName == null || (newName = (newName || '').trim()) === '') return;
    preset.displayName = preset.DisplayName = newName;
    var presetEl = document.getElementById('activeContextPreset');
    if (presetEl) {
      var opts = presetEl.querySelectorAll('option');
      for (var i = 0; i < opts.length; i++) { if (opts[i].value === activeId) { opts[i].textContent = newName; break; } }
    }
    saveConfig();
  });
}
var deleteContextPresetBtn = document.getElementById('deleteContextPresetBtn');
if (deleteContextPresetBtn) {
  deleteContextPresetBtn.addEventListener('click', function () {
    var activeId = getActivePresetId();
    var presets = getPresets();
    if (!activeId || !presets.length) return;
    var id = (activeId || '').toLowerCase();
    if (id === 'internal-64k' || id === 'kimi-k25') return;
    if (!confirm('确定删除该预设？')) return;
    var next = presets.filter(function (p) { return (p.id || p.Id) !== activeId; });
    fullConfig = fullConfig || {};
    fullConfig.contextOptimizationPresets = fullConfig.ContextOptimizationPresets = next;
    if (next.length > 0) {
      var firstId = next[0].id || next[0].Id || '';
      fullConfig.activeContextPresetId = fullConfig.ActiveContextPresetId = firstId;
    } else {
      fullConfig.activeContextPresetId = fullConfig.ActiveContextPresetId = '';
    }
    var presetEl = document.getElementById('activeContextPreset');
    if (presetEl) {
      presetEl.innerHTML = '<option value="">-- 请选择 --</option>';
      next.forEach(function (p) {
        var oid = p.id ?? p.Id ?? '';
        var name = p.displayName ?? p.DisplayName ?? oid;
        var opt = document.createElement('option');
        opt.value = oid;
        opt.textContent = name;
        presetEl.appendChild(opt);
      });
      presetEl.value = fullConfig.activeContextPresetId || '';
    }
    fillContextWindowForm(fullConfig.activeContextPresetId || '', next);
    fillSessionForm(fullConfig.activeContextPresetId || '', next);
    toggleContextWindowSection(next.length > 0);
    toggleSessionSection(next.length > 0);
    updatePresetRenameDeleteVisibility(fullConfig.activeContextPresetId || '', next);
    saveConfig();
  });
}

var autoExecuteMaxStepsEl = document.getElementById('autoExecuteMaxSteps');
if (autoExecuteMaxStepsEl) autoExecuteMaxStepsEl.addEventListener('input', debouncedSaveConfig);

if (els.addAiModelBtn) els.addAiModelBtn.addEventListener('click', function () { openAiModelEditor(null); });
if (els.closeAiModelEditorBtn) els.closeAiModelEditorBtn.addEventListener('click', closeAiModelEditor);
if (els.saveAiModelBtn) els.saveAiModelBtn.addEventListener('click', saveAiModelFromEditor);
if (els.aiModelProvider) els.aiModelProvider.addEventListener('change', toggleAzureFields);
if (els.testAiConnectionBtn) els.testAiConnectionBtn.addEventListener('click', testAiConnection);

function testAiConnectionById(id) {
  var m = aiModelsCache.find(function (x) { return (x.id || x.Id) === id; });
  if (!m) return;
  var endpoint = (m.endpoint || m.Endpoint || '').trim();
  var modelId = (m.modelId || m.ModelId || '').trim();
  var provider = m.provider || m.Provider || 'OpenAI';
  var deploymentName = (m.deploymentName || m.DeploymentName || '').trim();
  var apiKey = m.apiKey || m.ApiKey || '';
  var listEl = document.getElementById('aiModelsList');
  var statusEl = null;
  if (listEl) {
    listEl.querySelectorAll('.mcp-server-row').forEach(function (row) {
      if (row.getAttribute('data-ai-id') === id) statusEl = row.querySelector('.ai-row-test-status');
    });
  }
  if (statusEl) statusEl.textContent = '';
  var vendorId = (m.vendorId || m.VendorId || '').trim();
  doTestAiConnection({ endpoint: endpoint, modelId: modelId, provider: provider, deploymentName: deploymentName, apiKey: apiKey, vendorId: vendorId }, statusEl || undefined);
}

async function doTestAiConnection(fields, statusEl) {
  statusEl = statusEl || document.getElementById('testAiStatus');
  if (!statusEl) return;
  var endpoint = (fields && fields.endpoint) || '';
  var modelId = (fields && fields.modelId) || '';
  var provider = (fields && fields.provider) || 'OpenAI';
  var deploymentName = (fields && fields.deploymentName) || '';
  var apiKey = (fields && fields.apiKey) || '';
  var vendorId = (fields && fields.vendorId) || '';
  if (!endpoint || !modelId) {
    statusEl.textContent = '请先填写接口地址和模型 ID';
    statusEl.style.color = 'var(--danger)';
    return;
  }
  statusEl.textContent = '测试中…';
  statusEl.style.color = 'var(--text-secondary)';
  var testModelId = (provider === 'Azure' && deploymentName) ? deploymentName : modelId;
  try {
    var baseUrl = API_URL.replace('/api/config', '');
    var testBody = {
      endpoint: endpoint,
      apiKey: apiKey,
      modelId: testModelId,
      provider: provider,
      deploymentName: deploymentName
    };
    if (vendorId) testBody.vendorId = vendorId;
    var res = await tasklyFetch(baseUrl + '/api/config/test-ai', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(testBody)
    });
    var data = await res.json().catch(function () { return { ok: false, message: '响应解析失败' }; });
    if (data.ok) {
      statusEl.textContent = '连接成功';
      statusEl.style.color = 'var(--success)';
    } else {
      statusEl.textContent = data.message || '连接失败';
      statusEl.style.color = 'var(--danger)';
    }
  } catch (err) {
    statusEl.textContent = '请求失败: ' + (err.message || '请确保后端已启动');
    statusEl.style.color = 'var(--danger)';
  }
}

async function testAiConnection() {
  doTestAiConnection(testConnectionFields, document.getElementById('testAiStatus'));
}

// ───── Skills ─────
let currentSkills = [];

async function loadSkills() {
  try {
    const res = await tasklyFetch(SKILLS_API_URL);
    if (res.ok) {
      currentSkills = await res.json();
      renderSkills();
    } else {
      currentSkills = [];
      renderSkills();
      alert('无法加载技能列表，请确保后端已启动。');
    }
  } catch (err) {
    var friendly = messageForBackendUnreachable(err);
    if (friendly) console.warn(friendly);
    else console.error('Failed to load skills', err);
    currentSkills = [];
    renderSkills();
    alert(friendly || '无法加载技能列表，请确保后端已启动。');
  }
}

function renderSkills() {
  if (currentSkills.length === 0) {
    els.skillsList.innerHTML = '<div style="text-align:center; padding: 20px; color:#999;">暂无自定义技能</div>';
    return;
  }
  
  els.skillsList.innerHTML = currentSkills.map(skill => {
    const id = skill.id || skill.Id || '';
    const name = skill.name || skill.Name || '';
    const desc = skill.description || skill.Description || '';
    const prompt = skill.promptTemplate || skill.PromptTemplate || '';
    const enabled = skill.enabled !== false && skill.Enabled !== false;
    const idAttr = escapeAttr(id);
    return `
    <div class="skill-card" data-skill-id="${idAttr}">
      <div class="skill-header">
        <div class="skill-title">${escapeHtml(name)}${enabled ? '' : ' <span style="color:#94a3b8;font-weight:normal;">(已停用)</span>'}</div>
        <div class="skill-actions">
          <button type="button" class="btn-secondary skill-btn-edit" style="padding: 4px 12px; font-size: 12px;">编辑</button>
          <button type="button" class="btn-secondary skill-btn-toggle" style="padding: 4px 12px; font-size: 12px;">${enabled ? '停用' : '启用'}</button>
          <button type="button" class="btn-danger skill-btn-delete" style="padding: 4px 12px; font-size: 12px;">删除</button>
        </div>
      </div>
      <div class="skill-desc">${escapeHtml(desc)}</div>
      <div class="skill-prompt">${escapeHtml(prompt.substring(0, 100))}${prompt.length > 100 ? '...' : ''}</div>
    </div>
  `;
  }).join('');

  // 事件委托：避免 onclick 中 id 含引号导致编辑/删除/停用失效
  els.skillsList.querySelectorAll('.skill-btn-edit').forEach(btn => {
    btn.addEventListener('click', function () {
      const card = this.closest('[data-skill-id]');
      if (card) editSkill(card.getAttribute('data-skill-id') || '');
    });
  });
  els.skillsList.querySelectorAll('.skill-btn-delete').forEach(btn => {
    btn.addEventListener('click', function () {
      const card = this.closest('[data-skill-id]');
      if (card) deleteSkill(card.getAttribute('data-skill-id') || '');
    });
  });
  els.skillsList.querySelectorAll('.skill-btn-toggle').forEach(btn => {
    btn.addEventListener('click', function () {
      const card = this.closest('[data-skill-id]');
      if (card) toggleSkillEnabled(card.getAttribute('data-skill-id') || '');
    });
  });
}

function escapeAttr(str) {
  if (str == null) return '';
  return String(str)
    .replace(/&/g, '&amp;')
    .replace(/"/g, '&quot;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}

window.editSkill = (id) => {
  const skill = currentSkills.find(s => (s.id || s.Id) === id);
  if (!skill) return;
  els.skillId.value = skill.id || skill.Id || '';
  els.skillName.value = skill.name || skill.Name || '';
  els.skillDesc.value = skill.description || skill.Description || '';
  els.skillPrompt.value = skill.promptTemplate || skill.PromptTemplate || '';
  
  els.skillEditorTitle.textContent = '编辑技能';
  els.skillEditor.style.display = 'block';
  els.skillsList.style.display = 'none';
  els.newSkillBtn.style.display = 'none';
};

window.deleteSkill = async (id) => {
  if (!confirm('确定要删除这个技能吗？')) return;
  try {
    const res = await tasklyFetch(`${SKILLS_API_URL}/${id}`, { method: 'DELETE' });
    if (res.ok) await loadSkills();
    else {
      var data = await res.json().catch(function () { return {}; });
      alert(data.message || '删除失败');
    }
  } catch (err) {
    alert(err.message || '删除失败');
  }
};

els.newSkillBtn.addEventListener('click', () => {
  els.skillId.value = '';
  els.skillName.value = '';
  els.skillDesc.value = '';
  els.skillPrompt.value = '';
  
  els.skillEditorTitle.textContent = '新增技能';
  els.skillEditor.style.display = 'block';
  els.skillsList.style.display = 'none';
  els.newSkillBtn.style.display = 'none';
});

els.cancelSkillBtn.addEventListener('click', () => {
  els.skillEditor.style.display = 'none';
  els.skillsList.style.display = 'block';
  els.newSkillBtn.style.display = 'block';
});

els.saveSkillBtn.addEventListener('click', async () => {
  const payload = {
    id: els.skillId.value,
    name: els.skillName.value.trim(),
    description: els.skillDesc.value.trim(),
    promptTemplate: els.skillPrompt.value.trim(),
    enabled: true
  };
  
  if (!payload.name || !payload.promptTemplate) {
    alert('技能名称和处理逻辑不能为空');
    return;
  }
  
  try {
    els.saveSkillBtn.disabled = true;
    const res = await tasklyFetch(SKILLS_API_URL, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });
    
    if (res.ok) {
      els.skillEditor.style.display = 'none';
      els.skillsList.style.display = 'block';
      els.newSkillBtn.style.display = 'block';
      await loadSkills();
    } else {
      var data = await res.json().catch(function () { return {}; });
      alert(data.message || '保存失败');
    }
  } catch (err) {
    alert(err.message || '保存失败');
  } finally {
    els.saveSkillBtn.disabled = false;
  }
});

window.toggleSkillEnabled = async function (id) {
  const skill = currentSkills.find(s => (s.id || s.Id) === id);
  if (!skill) return;
  const nextEnabled = (skill.enabled !== false && skill.Enabled !== false) ? false : true;
  const payload = {
    id: skill.id || skill.Id,
    name: skill.name || skill.Name || '',
    description: skill.description || skill.Description || '',
    promptTemplate: skill.promptTemplate || skill.PromptTemplate || '',
    enabled: nextEnabled
  };
  try {
    const res = await tasklyFetch(SKILLS_API_URL, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });
    if (res.ok) await loadSkills();
    else {
      var data = await res.json().catch(function () { return {}; });
      alert(data.message || '操作失败');
    }
  } catch (err) {
    alert(err.message || '操作失败');
  }
};

function escapeHtml(unsafe) {
    return unsafe
         .replace(/&/g, "&amp;")
         .replace(/</g, "&lt;")
         .replace(/>/g, "&gt;")
         .replace(/"/g, "&quot;")
         .replace(/'/g, "&#039;");
}

function renderCliScriptUnifiedConfig() {
  var tabsContainer = document.getElementById('cliScriptEndTabs');
  var contentContainer = document.getElementById('cliScriptPerEndConfig');
  if (!contentContainer) return;
  if (tabsContainer) tabsContainer.innerHTML = '';
  var cliByClient = fullConfig && (fullConfig.allowedCliCommandsByClient || fullConfig.AllowedCliCommandsByClient) ? (fullConfig.allowedCliCommandsByClient || fullConfig.AllowedCliCommandsByClient) : {};
  var scriptByClient = fullConfig && (fullConfig.allowedPageScriptIdsByClient || fullConfig.AllowedPageScriptIdsByClient) ? (fullConfig.allowedPageScriptIdsByClient || fullConfig.AllowedPageScriptIdsByClient) : {};
  var docScriptByClient = fullConfig && (fullConfig.allowedDocumentScriptIdsByClient || fullConfig.AllowedDocumentScriptIdsByClient) ? (fullConfig.allowedDocumentScriptIdsByClient || fullConfig.AllowedDocumentScriptIdsByClient) : {};
  var mode = getGlobalCliRunModeFromConfig();
  var showAllowlist = mode === 'UseAllowList';

  function renderCliRows(endKey, cliList, cliListEmpty, cliSet) {
    var builtins = getBuiltinCliCommandsForEnd(endKey);
    var userCliList = cliList.filter(function (c) {
      return builtins.indexOf((c || '').toLowerCase()) < 0;
    });
    var html = '<div class="cli-allowlist-list" style="margin-bottom:12px;">';
    builtins.forEach(function (cmd) {
      var checked = (cliListEmpty || cliSet.indexOf(cmd.toLowerCase()) >= 0) ? ' checked' : '';
      html += '<label class="cli-allowlist-row cli-builtin-row" style="display:flex;align-items:center;gap:8px;margin-bottom:4px;cursor:pointer;"><input type="checkbox" class="cli-default-cb" data-cmd="' + escapeAttr(cmd) + '" aria-label="' + escapeAttr('加入白名单：' + cmd) + '"' + checked + '><span>' + escapeHtml(cmd) + '</span></label>';
    });
    if (builtins.length > 0) {
      html += '<hr style="margin:12px 0;border:0;border-top:1px solid var(--border);">';
    }
    userCliList.forEach(function (cmd) {
      var checked = cliSet.indexOf((cmd || '').toLowerCase()) >= 0 ? ' checked' : '';
      html += '<div class="cli-allowlist-row cli-user-row" style="display:flex;align-items:center;gap:8px;margin-bottom:4px;"><label style="display:flex;align-items:center;gap:4px;cursor:pointer;flex:1;"><input type="checkbox" class="cli-user-cb" data-cmd="' + escapeAttr(cmd) + '" aria-label="' + escapeAttr('加入白名单：' + cmd) + '"' + checked + '><span class="cli-user-cmd">' + escapeHtml(cmd) + '</span></label><button type="button" class="btn-secondary cli-user-delete" data-cmd="' + escapeAttr(cmd) + '" style="padding:2px 8px;font-size:12px;">删除</button></div>';
    });
    html += '</div><div style="display:flex;gap:8px;margin-bottom:16px;"><input type="text" class="cli-add-input" placeholder="添加命令名" style="flex:1;max-width:200px;"><button type="button" class="btn-secondary cli-add-btn">添加命令</button></div>';
    return html;
  }

  function renderScriptRows(scriptList, scriptListEmpty, scriptSet) {
    var userScriptList = scriptList.filter(function (s) { return DEFAULT_PAGE_SCRIPTS.indexOf((s || '').toLowerCase()) < 0; });
    var html = '<div class="script-allowlist-list" style="margin-bottom:12px;">';
    DEFAULT_PAGE_SCRIPTS.forEach(function (sid) {
      var checked = (scriptListEmpty || scriptSet.indexOf(sid.toLowerCase()) >= 0) ? ' checked' : '';
      html += '<label class="script-allowlist-row script-builtin-row" style="display:flex;align-items:center;gap:8px;margin-bottom:4px;cursor:pointer;"><input type="checkbox" class="script-default-cb" data-script="' + escapeAttr(sid) + '" aria-label="' + escapeAttr('加入白名单：' + sid) + '"' + checked + '><span>' + escapeHtml(sid) + '</span></label>';
    });
    html += '<hr style="margin:12px 0;border:0;border-top:1px solid var(--border);">';
    userScriptList.forEach(function (sid) {
      var checked = scriptSet.indexOf((sid || '').toLowerCase()) >= 0 ? ' checked' : '';
      html += '<div class="script-allowlist-row script-user-row" style="display:flex;align-items:center;gap:8px;margin-bottom:4px;"><label style="display:flex;align-items:center;gap:4px;cursor:pointer;flex:1;"><input type="checkbox" class="script-user-cb" data-script="' + escapeAttr(sid) + '" aria-label="' + escapeAttr('加入白名单：' + sid) + '"' + checked + '><span class="script-user-id">' + escapeHtml(sid) + '</span></label><button type="button" class="btn-secondary script-user-delete" data-script="' + escapeAttr(sid) + '" style="padding:2px 8px;font-size:12px;">删除</button></div>';
    });
    html += '</div><div style="display:flex;gap:8px;"><input type="text" class="script-add-input" placeholder="添加 scriptId" style="flex:1;max-width:200px;"><button type="button" class="btn-secondary script-add-btn">添加脚本</button></div>';
    return html;
  }

  function renderDocumentScriptRows(endKey, scriptList, scriptListEmpty, scriptSet) {
    var builtins = getBuiltinDocumentScriptIdsForEnd(endKey);
    var userScriptList = scriptList.filter(function (s) {
      return builtins.indexOf((s || '').toLowerCase()) < 0;
    });
    var html = '<div class="doc-script-allowlist-list" style="margin-bottom:12px;">';
    builtins.forEach(function (sid) {
      var checked = (scriptListEmpty || scriptSet.indexOf(sid.toLowerCase()) >= 0) ? ' checked' : '';
      html += '<label class="doc-script-allowlist-row doc-script-builtin-row" style="display:flex;align-items:center;gap:8px;margin-bottom:4px;cursor:pointer;"><input type="checkbox" class="doc-script-default-cb" data-doc-script="' + escapeAttr(sid) + '" aria-label="' + escapeAttr('加入白名单：' + sid) + '"' + checked + '><span>' + escapeHtml(sid) + '</span></label>';
    });
    if (builtins.length > 0) {
      html += '<hr style="margin:12px 0;border:0;border-top:1px solid var(--border);">';
    }
    userScriptList.forEach(function (sid) {
      var checked = scriptSet.indexOf((sid || '').toLowerCase()) >= 0 ? ' checked' : '';
      html += '<div class="doc-script-allowlist-row doc-script-user-row" style="display:flex;align-items:center;gap:8px;margin-bottom:4px;"><label style="display:flex;align-items:center;gap:4px;cursor:pointer;flex:1;"><input type="checkbox" class="doc-script-user-cb" data-doc-script="' + escapeAttr(sid) + '" aria-label="' + escapeAttr('加入白名单：' + sid) + '"' + checked + '><span class="doc-script-user-id">' + escapeHtml(sid) + '</span></label><button type="button" class="btn-secondary doc-script-user-delete" data-doc-script="' + escapeAttr(sid) + '" style="padding:2px 8px;font-size:12px;">删除</button></div>';
    });
    html += '</div><div style="display:flex;gap:8px;"><input type="text" class="doc-script-add-input" placeholder="添加 scriptId" style="flex:1;max-width:200px;"><button type="button" class="btn-secondary doc-script-add-btn">添加脚本</button></div>';
    return html;
  }

  var chromeScripts = Array.isArray(scriptByClient.chrome) ? scriptByClient.chrome : [];
  var chromeScriptEmpty = chromeScripts.length === 0;
  var chromeScriptSet = chromeScripts.map(function (s) { return (s || '').toLowerCase(); }).filter(Boolean);

  var backendCli = Array.isArray(cliByClient.backend) ? cliByClient.backend : [];
  var backendCliEmpty = backendCli.length === 0;
  var backendCliSet = backendCli.map(function (s) { return (s || '').toLowerCase(); }).filter(Boolean);

  var officeDoc = Array.isArray(docScriptByClient.office) ? docScriptByClient.office : [];
  var officeDocEmpty = officeDoc.length === 0;
  var officeDocSet = officeDoc.map(function (s) { return (s || '').toLowerCase(); }).filter(Boolean);

  var wpsDoc = Array.isArray(docScriptByClient.wps) ? docScriptByClient.wps : [];
  var wpsDocEmpty = wpsDoc.length === 0;
  var wpsDocSet = wpsDoc.map(function (s) { return (s || '').toLowerCase(); }).filter(Boolean);

  var html = '<div class="skill-card" style="margin-bottom:16px;">';
  html += '<div class="form-group" style="margin-top:8px;"><label style="display:block;margin-bottom:6px;">运行模式（全端共用）</label><select class="global-cli-run-mode-select" style="min-width:280px;">';
  CLI_RUN_MODES.forEach(function (opt) {
    html += '<option value="' + escapeAttr(opt.value) + '"' + (mode === opt.value ? ' selected' : '') + '>' + escapeHtml(opt.label) + '</option>';
  });
  html += '</select></div>';
  html += '<p class="help-text" style="margin-top:8px;font-size:13px;">定时任务路径不弹确认；全局为 AskEverytime 时，后台会话在 <code>GetCliRunModeForEnd</code> 中仍按白名单策略执行。</p>';
  html += '<div class="cli-unified-allowlist" style="margin-top:12px;padding-top:12px;border-top:1px solid var(--border);' + (showAllowlist ? '' : ' display:none;') + '">';
  html += '<p class="help-text" style="margin-bottom:16px;font-size:13px;">按<strong>工具实际执行位置</strong>分区。<code>run_command</code> 始终在<strong>本机后端</strong>的 <code>cmd.exe</code> 中执行，与从 Chrome / Office / WPS 哪一端连入无关；下面「后台」一节维护 CLI 白名单，保存时各连接来源键与后台共用同一份列表。浏览器脚本、Office/WPS 文档脚本分别在对应分区配置。</p>';

  html += '<div class="cli-location-section" style="margin-top:4px;">';
  html += '<h3 style="margin:0 0 8px;font-size:16px;">后台（服务端 CMD · run_command）</h3>';
  html += '<p class="help-text" style="margin-bottom:8px;font-size:13px;">执行位置：运行 Office Copilot 服务端的本机命令行。四端会话发起的 <code>run_command</code> 均在此执行；白名单以本节为准。</p>';
  html += '<p class="help-text" style="margin-bottom:8px;font-size:12px;color:var(--muted);">内置行由产品预设，不提供删除按钮；仅自定义行可删除。</p>';
  html += '<div class="cli-end-block" data-end="backend">';
  html += '<p class="help-text" style="margin-bottom:8px;">命令白名单</p>';
  html += renderCliRows('backend', backendCli, backendCliEmpty, backendCliSet);
  html += '</div></div>';

  html += '<hr style="margin:24px 0;border:0;border-top:1px solid var(--border);">';
  html += '<div class="cli-location-section">';
  html += '<h3 style="margin:0 0 8px;font-size:16px;">Chrome（浏览器内 · run_page_script）</h3>';
  html += '<p class="help-text" style="margin-bottom:8px;font-size:13px;">执行位置：当前浏览器标签页（扩展注入）。仅页面脚本白名单，不包含 CMD。</p>';
  html += '<div class="cli-end-block" data-end="chrome" data-chrome-part="pageScripts">';
  html += '<p class="help-text" style="margin-bottom:8px;">页面脚本白名单（run_page_script）</p>';
  html += renderScriptRows(chromeScripts, chromeScriptEmpty, chromeScriptSet);
  html += '</div></div>';

  html += '<hr style="margin:24px 0;border:0;border-top:1px solid var(--border);">';
  html += '<div class="cli-location-section">';
  html += '<h3 style="margin:0 0 8px;font-size:16px;">Office 加载项（文档环境 · current_run_document_script）</h3>';
  html += '<p class="help-text" style="margin-bottom:8px;font-size:13px;">执行位置：Word / Excel / PowerPoint 任务窗格。预定义 scriptId 须与加载项 <code>DOCUMENT_SCRIPTS</code> 一致。</p>';
  html += '<div class="doc-script-end-block" data-end="office">';
  html += '<p class="help-text" style="margin-bottom:8px;">文档脚本白名单（office 键）</p>';
  html += renderDocumentScriptRows('office', officeDoc, officeDocEmpty, officeDocSet);
  html += '</div></div>';

  html += '<hr style="margin:24px 0;border:0;border-top:1px solid var(--border);">';
  html += '<div class="cli-location-section">';
  html += '<h3 style="margin:0 0 8px;font-size:16px;">WPS 加载项（文档环境 · current_run_document_script）</h3>';
  html += '<p class="help-text" style="margin-bottom:8px;font-size:13px;">执行位置：WPS 任务窗格。预定义 scriptId 须与加载项 <code>DOCUMENT_SCRIPTS</code> 一致。</p>';
  html += '<div class="doc-script-end-block" data-end="wps">';
  html += '<p class="help-text" style="margin-bottom:8px;">文档脚本白名单（wps 键）</p>';
  html += renderDocumentScriptRows('wps', wpsDoc, wpsDocEmpty, wpsDocSet);
  html += '</div></div>';

  html += '</div></div>';
  contentContainer.innerHTML = html;

  var modeSel = contentContainer.querySelector('.global-cli-run-mode-select');
  if (modeSel) {
    modeSel.addEventListener('change', function () {
      var al = contentContainer.querySelector('.cli-unified-allowlist');
      if (al) al.style.display = this.value === 'UseAllowList' ? '' : 'none';
      debouncedSaveConfig();
    });
  }
  contentContainer.querySelectorAll('.cli-default-cb, .script-default-cb, .cli-user-cb, .script-user-cb, .doc-script-default-cb, .doc-script-user-cb').forEach(function (el) { el.addEventListener('change', debouncedSaveConfig); });
  contentContainer.querySelectorAll('.cli-user-delete').forEach(function (btn) {
    btn.addEventListener('click', function () {
      var row = btn.closest('.cli-user-row');
      if (row) row.remove();
      debouncedSaveConfig();
    });
  });
  contentContainer.querySelectorAll('.script-user-delete').forEach(function (btn) {
    btn.addEventListener('click', function () {
      var row = btn.closest('.script-user-row');
      if (row) row.remove();
      debouncedSaveConfig();
    });
  });
  contentContainer.querySelectorAll('.doc-script-user-delete').forEach(function (btn) {
    btn.addEventListener('click', function () {
      var row = btn.closest('.doc-script-user-row');
      if (row) row.remove();
      debouncedSaveConfig();
    });
  });
  contentContainer.querySelectorAll('.cli-end-block').forEach(function (block) {
    var cliAddBtn = block.querySelector('.cli-add-btn');
    var cliAddInput = block.querySelector('.cli-add-input');
    var cliListEl = block.querySelector('.cli-allowlist-list');
    if (cliAddBtn && cliAddInput && cliListEl) {
      cliAddBtn.addEventListener('click', function () {
        var cmd = (cliAddInput.value || '').trim();
        if (!cmd) return;
        var cmdLower = cmd.toLowerCase();
        var already = false;
        block.querySelectorAll('.cli-user-row .cli-user-cmd').forEach(function (span) { if ((span.textContent || '').trim().toLowerCase() === cmdLower) already = true; });
        if (already) return;
        var div = document.createElement('div');
        div.className = 'cli-allowlist-row cli-user-row';
        div.setAttribute('style', 'display:flex;align-items:center;gap:8px;margin-bottom:4px;');
        div.innerHTML = '<label style="display:flex;align-items:center;gap:4px;cursor:pointer;flex:1;"><input type="checkbox" class="cli-user-cb" data-cmd="' + escapeAttr(cmd) + '" checked aria-label="' + escapeAttr('加入白名单：' + cmd) + '"><span class="cli-user-cmd">' + escapeHtml(cmd) + '</span></label><button type="button" class="btn-secondary cli-user-delete" data-cmd="' + escapeAttr(cmd) + '" style="padding:2px 8px;font-size:12px;">删除</button>';
        var hr = cliListEl.querySelector('hr');
        if (hr && hr.nextSibling) cliListEl.insertBefore(div, hr.nextSibling);
        else cliListEl.appendChild(div);
        div.querySelector('.cli-user-cb').addEventListener('change', debouncedSaveConfig);
        div.querySelector('.cli-user-delete').addEventListener('click', function () { div.remove(); debouncedSaveConfig(); });
        cliAddInput.value = '';
        debouncedSaveConfig();
      });
    }
    var scriptAddBtn = block.querySelector('.script-add-btn');
    var scriptAddInput = block.querySelector('.script-add-input');
    var scriptListEl = block.querySelector('.script-allowlist-list');
    if (scriptAddBtn && scriptAddInput && scriptListEl) {
      scriptAddBtn.addEventListener('click', function () {
        var sid = (scriptAddInput.value || '').trim();
        if (!sid) return;
        var sidLower = sid.toLowerCase();
        var already = false;
        block.querySelectorAll('.script-user-row .script-user-id').forEach(function (span) { if ((span.textContent || '').trim().toLowerCase() === sidLower) already = true; });
        if (already) return;
        var div = document.createElement('div');
        div.className = 'script-allowlist-row script-user-row';
        div.setAttribute('style', 'display:flex;align-items:center;gap:8px;margin-bottom:4px;');
        div.innerHTML = '<label style="display:flex;align-items:center;gap:4px;cursor:pointer;flex:1;"><input type="checkbox" class="script-user-cb" data-script="' + escapeAttr(sid) + '" checked aria-label="' + escapeAttr('加入白名单：' + sid) + '"><span class="script-user-id">' + escapeHtml(sid) + '</span></label><button type="button" class="btn-secondary script-user-delete" data-script="' + escapeAttr(sid) + '" style="padding:2px 8px;font-size:12px;">删除</button>';
        var hr = scriptListEl.querySelector('hr');
        if (hr && hr.nextSibling) scriptListEl.insertBefore(div, hr.nextSibling);
        else scriptListEl.appendChild(div);
        div.querySelector('.script-user-cb').addEventListener('change', debouncedSaveConfig);
        div.querySelector('.script-user-delete').addEventListener('click', function () { div.remove(); debouncedSaveConfig(); });
        scriptAddInput.value = '';
        debouncedSaveConfig();
      });
    }
  });
  contentContainer.querySelectorAll('.doc-script-end-block').forEach(function (block) {
    var docAddBtn = block.querySelector('.doc-script-add-btn');
    var docAddInput = block.querySelector('.doc-script-add-input');
    var docListEl = block.querySelector('.doc-script-allowlist-list');
    if (docAddBtn && docAddInput && docListEl) {
      docAddBtn.addEventListener('click', function () {
        var sid = (docAddInput.value || '').trim();
        if (!sid) return;
        var sidLower = sid.toLowerCase();
        var already = false;
        block.querySelectorAll('.doc-script-user-row .doc-script-user-id').forEach(function (span) { if ((span.textContent || '').trim().toLowerCase() === sidLower) already = true; });
        if (already) return;
        var div = document.createElement('div');
        div.className = 'doc-script-allowlist-row doc-script-user-row';
        div.setAttribute('style', 'display:flex;align-items:center;gap:8px;margin-bottom:4px;');
        div.innerHTML = '<label style="display:flex;align-items:center;gap:4px;cursor:pointer;flex:1;"><input type="checkbox" class="doc-script-user-cb" data-doc-script="' + escapeAttr(sid) + '" checked aria-label="' + escapeAttr('加入白名单：' + sid) + '"><span class="doc-script-user-id">' + escapeHtml(sid) + '</span></label><button type="button" class="btn-secondary doc-script-user-delete" data-doc-script="' + escapeAttr(sid) + '" style="padding:2px 8px;font-size:12px;">删除</button>';
        var hr = docListEl.querySelector('hr');
        if (hr && hr.nextSibling) docListEl.insertBefore(div, hr.nextSibling);
        else docListEl.appendChild(div);
        div.querySelector('.doc-script-user-cb').addEventListener('change', debouncedSaveConfig);
        div.querySelector('.doc-script-user-delete').addEventListener('click', function () { div.remove(); debouncedSaveConfig(); });
        docAddInput.value = '';
        debouncedSaveConfig();
      });
    }
  });
}

function collectCliSecurityPayload() {
  var container = document.getElementById('cliScriptPerEndConfig');
  var modeEl = container && container.querySelector('.global-cli-run-mode-select');
  var cliRunMode = (modeEl && modeEl.value) ? modeEl.value : getGlobalCliRunModeFromConfig();
  var allowedCliCommandsByClient = {};
  var allowedPageScriptIdsByClient = {};
  var allowedDocumentScriptIdsByClient = { office: [], wps: [] };
  var cliBy = fullConfig && (fullConfig.allowedCliCommandsByClient || fullConfig.AllowedCliCommandsByClient);
  var scriptBy = fullConfig && (fullConfig.allowedPageScriptIdsByClient || fullConfig.AllowedPageScriptIdsByClient);
  var docBy = fullConfig && (fullConfig.allowedDocumentScriptIdsByClient || fullConfig.AllowedDocumentScriptIdsByClient);
  if (!container || cliRunMode !== 'UseAllowList') {
    CLI_SCRIPT_END_KEYS.forEach(function (endKey) {
      allowedCliCommandsByClient[endKey] = Array.isArray(cliBy && cliBy[endKey]) ? cliBy[endKey].slice() : [];
      allowedPageScriptIdsByClient[endKey] = Array.isArray(scriptBy && scriptBy[endKey]) ? scriptBy[endKey].slice() : [];
    });
    allowedDocumentScriptIdsByClient.office = Array.isArray(docBy && docBy.office) ? docBy.office.slice() : [];
    allowedDocumentScriptIdsByClient.wps = Array.isArray(docBy && docBy.wps) ? docBy.wps.slice() : [];
    return { cliRunMode: cliRunMode, allowedCliCommandsByClient: allowedCliCommandsByClient, allowedPageScriptIdsByClient: allowedPageScriptIdsByClient, allowedDocumentScriptIdsByClient: allowedDocumentScriptIdsByClient };
  }

  function collectCliFromBlock(block) {
    var cliList = [];
    if (!block) return cliList;
    block.querySelectorAll('.cli-default-cb:checked').forEach(function (cb) {
      var cmd = cb.getAttribute('data-cmd');
      if (cmd) cliList.push(cmd);
    });
    block.querySelectorAll('.cli-user-row .cli-user-cb:checked').forEach(function (cb) {
      var cmd = (cb.getAttribute('data-cmd') || '').trim();
      if (cmd) cliList.push(cmd);
    });
    return cliList;
  }

  function collectScriptsFromBlock(block) {
    var scriptList = [];
    if (!block) return scriptList;
    block.querySelectorAll('.script-default-cb:checked').forEach(function (cb) {
      var script = cb.getAttribute('data-script');
      if (script) scriptList.push(script);
    });
    block.querySelectorAll('.script-user-row .script-user-cb:checked').forEach(function (cb) {
      var script = (cb.getAttribute('data-script') || '').trim();
      if (script) scriptList.push(script);
    });
    return scriptList;
  }

  function collectDocumentScriptsFromBlock(block) {
    var scriptList = [];
    if (!block) return scriptList;
    block.querySelectorAll('.doc-script-default-cb:checked').forEach(function (cb) {
      var script = cb.getAttribute('data-doc-script');
      if (script) scriptList.push(script);
    });
    block.querySelectorAll('.doc-script-user-row .doc-script-user-cb:checked').forEach(function (cb) {
      var script = (cb.getAttribute('data-doc-script') || '').trim();
      if (script) scriptList.push(script);
    });
    return scriptList;
  }

  var backendBlock = container.querySelector('.cli-end-block[data-end="backend"]');
  var backendList = collectCliFromBlock(backendBlock);
  allowedCliCommandsByClient.backend = backendList;
  allowedCliCommandsByClient.chrome = backendList.slice();
  allowedCliCommandsByClient.office = backendList.slice();
  allowedCliCommandsByClient.wps = backendList.slice();

  var chromePs = container.querySelector('.cli-end-block[data-end="chrome"][data-chrome-part="pageScripts"]');
  allowedPageScriptIdsByClient.chrome = collectScriptsFromBlock(chromePs);
  allowedPageScriptIdsByClient.backend = Array.isArray(scriptBy && scriptBy.backend) ? scriptBy.backend.slice() : [];
  allowedPageScriptIdsByClient.office = Array.isArray(scriptBy && scriptBy.office) ? scriptBy.office.slice() : [];
  allowedPageScriptIdsByClient.wps = Array.isArray(scriptBy && scriptBy.wps) ? scriptBy.wps.slice() : [];

  var officeDocBlock = container.querySelector('.doc-script-end-block[data-end="office"]');
  var wpsDocBlock = container.querySelector('.doc-script-end-block[data-end="wps"]');
  allowedDocumentScriptIdsByClient.office = collectDocumentScriptsFromBlock(officeDocBlock);
  allowedDocumentScriptIdsByClient.wps = collectDocumentScriptsFromBlock(wpsDocBlock);

  return { cliRunMode: cliRunMode, allowedCliCommandsByClient: allowedCliCommandsByClient, allowedPageScriptIdsByClient: allowedPageScriptIdsByClient, allowedDocumentScriptIdsByClient: allowedDocumentScriptIdsByClient };
}

// ───── Boot ─────
document.addEventListener('DOMContentLoaded', function () {
  var aiModelForm = document.getElementById('aiModelForm');
  if (aiModelForm) {
    aiModelForm.addEventListener('submit', function (e) { e.preventDefault(); });
  }
  initVendorPresetSelects();
  wireVendorSelectListeners();
  var uiThemeSelect = document.getElementById('uiThemeId');
  if (uiThemeSelect) {
    uiThemeSelect.addEventListener('change', function () {
      if (typeof TasklyTheme !== 'undefined') TasklyTheme.setTheme(uiThemeSelect.value);
      debouncedSaveConfig();
    });
  }
  var localTok = document.getElementById('localServiceAuthToken');
  if (localTok) {
    localTok.addEventListener('change', function () {
      var o = {};
      o[COPILOT_TOKEN_STORAGE_KEY] = (localTok.value || '').trim();
      chrome.storage.local.set(o);
    });
  }
  var allowPrivEl = document.getElementById('allowPrivateEndpointTests');
  if (allowPrivEl) allowPrivEl.addEventListener('change', function () { debouncedSaveConfig(); });
  tasklyEnsureOptionsApiBase()
    .then(function () { return ensureLocalServiceTokenFromBootstrap(); })
    .then(function () { loadConfig(); })
    .catch(function (err) {
      var msg = (err && err.message) ? String(err.message) : String(err);
      if (els.statusMessage) els.statusMessage.textContent = msg;
      console.warn(msg);
    });
  setupPassThroughContextToggle();
  updateUserScriptsSection();
});

// ───── MCP ─────
async function loadBuiltinTools() {
  const el = document.getElementById('builtinToolsList');
  if (!el) return;
  var disabledSet = (getDisabledBuiltIn() || []).map(function (s) { return (s || '').toLowerCase(); }).filter(Boolean);
  try {
    const res = await tasklyFetch(BUILTIN_TOOLS_URL);
    if (!res.ok) {
      var data = await res.json().catch(function () { return {}; });
      el.innerHTML = '<div style="padding:12px;color:#94a3b8;font-size:13px;">' + escapeHtml(data.message || ('无法加载内置插件列表（' + res.status + '）。请确认后端已启动且已更新至最新版本。')) + '</div>';
      return;
    }
    const list = await res.json();
    if (!Array.isArray(list) || list.length === 0) {
      el.innerHTML = '<div style="padding:12px;color:#94a3b8;font-size:13px;">暂无内置插件</div>';
      return;
    }
    el.innerHTML = list.map(p => {
      const id = (p.id || p.Id || p.name || p.Name || '').toLowerCase();
      const name = p.name || p.Name || p.id || p.Id || '';
      const desc = p.description || p.Description || '';
      const disabled = disabledSet.indexOf(id) >= 0;
      const idAttr = escapeAttr(id);
      var configHint = '';
      if (id === 'mcp_ocr') {
        configHint = '<p class="help-text" style="margin-top:8px;font-size:12px;">配置位置：<strong>模型与多模态 → OCR 模型</strong>。</p>';
      } else if (id === 'mcp_stt') {
        configHint = '<p class="help-text" style="margin-top:8px;font-size:12px;">配置位置：<strong>模型与多模态 → 百炼实时语音识别</strong>。</p>';
      }
      var cardBody = `<div class="skill-header"><div class="skill-title">${escapeHtml(name)}${disabled ? ' <span style="color:#94a3b8;font-weight:normal;">(已停用)</span>' : ''}</div><div><button type="button" class="btn-secondary builtin-toggle-btn" style="padding:4px 12px;font-size:12px;">${disabled ? '启用' : '停用'}</button></div></div><div class="skill-desc">${escapeHtml(desc)}</div>${configHint}`;
      return `<div class="skill-card builtin-card" style="opacity:0.95;" data-builtin-id="${idAttr}">${cardBody}</div>`;
    }).join('');
    el.querySelectorAll('.builtin-toggle-btn').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var card = this.closest('[data-builtin-id]');
        if (!card) return;
        var id = (card.getAttribute('data-builtin-id') || '').toLowerCase();
        if (!id) return;
        if (!fullConfig) fullConfig = {};
        var list = fullConfig.disabledBuiltInPlugins || fullConfig.DisabledBuiltInPlugins;
        if (!Array.isArray(list)) list = [];
        list = list.slice();
        var idx = list.map(function (s) { return (s || '').toLowerCase(); }).indexOf(id);
        if (idx >= 0) list.splice(idx, 1);
        else list.push(id);
        fullConfig.disabledBuiltInPlugins = list;
        _saveFullConfig().then(function () { loadBuiltinTools(); });
      });
    });
  } catch (e) {
    var friendly = messageForBackendUnreachable(e);
    if (friendly) console.warn(friendly);
    else console.warn('loadBuiltinTools failed', e);
    el.innerHTML = '<div style="padding:12px;color:#94a3b8;font-size:13px;">' + (friendly || '无法加载内置插件列表（请确认后端已启动且已更新至最新版本）。') + '</div>';
  }
}

function renderMcpList(mcps) {
  const listEl = document.getElementById('mcpList');
  if (!listEl) return;
  const list = mcps || [];

  const rows = list.map((mcp, index) => {
    const id = mcp.id || mcp.Id || '';
    const name = mcp.name || mcp.Name || '';
    const command = mcp.command || mcp.Command || '';
    const args = mcp.args || mcp.Args || [];
    const enabled = mcp.enabled !== undefined ? mcp.enabled : (mcp.Enabled !== undefined ? mcp.Enabled : true);
    const cmdText = command + (Array.isArray(args) ? ' ' + args.join(' ') : '');
    const initial = (name && name[0]) ? name[0].toUpperCase() : 'M';
    return `
    <div class="mcp-server-row" data-mcp-id="${escapeHtml(id)}" data-mcp-index="${index}">
      <div class="mcp-icon">${escapeHtml(initial)}</div>
      <div class="mcp-info">
        <div class="mcp-name">${escapeHtml(name)}</div>
        <div class="mcp-desc">${escapeHtml(cmdText)}</div>
      </div>
      <div class="mcp-actions">
        <button type="button" class="btn-secondary" onclick="editMcp('${escapeHtml(id)}')" style="padding: 4px 10px; font-size: 12px;">编辑</button>
        <button type="button" class="btn-danger" onclick="deleteMcp('${escapeHtml(id)}')" style="padding: 4px 10px; font-size: 12px;">删除</button>
        <div class="mcp-toggle ${enabled ? 'on' : ''}" role="switch" aria-checked="${enabled}" data-mcp-id="${escapeHtml(id)}" title="${enabled ? '已启用' : '已关闭'}"></div>
      </div>
    </div>
    `;
  }).join('');

  const addNewRow = `
    <div class="mcp-server-row add-new" id="addNewMcpRow">
      <div class="mcp-icon">+</div>
      <div class="mcp-info">
        <div class="mcp-name">添加自定义 MCP 服务器</div>
        <div class="mcp-desc">New MCP Server</div>
      </div>
    </div>
  `;

  listEl.innerHTML = list.length === 0
    ? '<div style="text-align:center; padding: 16px; color:#94a3b8; font-size: 13px;">暂无已安装的 MCP 服务器</div>' + addNewRow
    : rows + addNewRow;

  listEl.querySelectorAll('.mcp-toggle').forEach(el => {
    el.addEventListener('click', function () {
      const mcpId = this.getAttribute('data-mcp-id');
      const list = getMcpServers();
      const mcp = list.find(s => (s.id || s.Id) === mcpId);
      if (!mcp || !fullConfig || !fullConfig.mcpServers) return;
      const current = mcp.enabled !== undefined ? mcp.enabled : (mcp.Enabled !== undefined ? mcp.Enabled : true);
      mcp.enabled = !current;
      if (fullConfig.McpServers && !fullConfig.mcpServers) fullConfig.mcpServers = fullConfig.McpServers;
      _saveFullConfig();
    });
  });

  document.getElementById('addNewMcpRow')?.addEventListener('click', openNewMcpEditor);
}

function openNewMcpEditor() {
  document.getElementById('mcpId').value = '';
  document.getElementById('mcpName').value = '';
  document.getElementById('mcpCommand').value = '';
  document.getElementById('mcpArgs').value = '';
  document.getElementById('mcpEditorTitle').textContent = '新增 MCP 组件';
  document.getElementById('mcpEditor').style.display = 'block';
  document.getElementById('mcpList').style.display = 'none';
}

document.getElementById('cancelMcpBtn').addEventListener('click', () => {
  document.getElementById('mcpEditor').style.display = 'none';
  document.getElementById('mcpList').style.display = 'block';
});

function getMcpServers() {
  if (!fullConfig) return [];
  return fullConfig.mcpServers || fullConfig.McpServers || [];
}

window.editMcp = (id) => {
  const list = getMcpServers();
  if (!list.length) return;
  const mcp = list.find(s => (s.id || s.Id) === id);
  if (!mcp) return;
  
  document.getElementById('mcpId').value = mcp.id || mcp.Id || '';
  document.getElementById('mcpName').value = mcp.name || mcp.Name || '';
  document.getElementById('mcpCommand').value = mcp.command || mcp.Command || '';
  document.getElementById('mcpArgs').value = (mcp.args || mcp.Args || []).join('\n');
  
  document.getElementById('mcpEditorTitle').textContent = '编辑 MCP 组件';
  document.getElementById('mcpEditor').style.display = 'block';
  document.getElementById('mcpList').style.display = 'none';
};

window.deleteMcp = async (id) => {
  if (!confirm('确定要删除这个组件吗？')) return;
  const list = getMcpServers().filter(s => (s.id || s.Id) !== id);
  if (!fullConfig) fullConfig = {};
  fullConfig.mcpServers = list;
  await _saveFullConfig();
};

document.getElementById('saveMcpBtn').addEventListener('click', async () => {
  const id = document.getElementById('mcpId').value || crypto.randomUUID().replace(/-/g, '');
  const name = document.getElementById('mcpName').value.trim();
  const cmd = document.getElementById('mcpCommand').value.trim();
  const args = document.getElementById('mcpArgs').value.split('\n').map(a => a.trim()).filter(a => a);
  
  if (!name || !cmd) {
    alert('名称和启动命令不能为空');
    return;
  }
  
  if (!fullConfig) fullConfig = {};
  if (!fullConfig.mcpServers) fullConfig.mcpServers = getMcpServers().slice();
  const list = fullConfig.mcpServers;
  const existingIdx = list.findIndex(s => (s.id || s.Id) === id);
  const existing = existingIdx >= 0 ? list[existingIdx] : null;
  const newMcp = { id, name, command: cmd, args, enabled: existing ? (existing.enabled !== undefined ? existing.enabled : (existing.Enabled !== undefined ? existing.Enabled : true)) : true };
  if (existingIdx >= 0) {
    list[existingIdx] = newMcp;
  } else {
    list.push(newMcp);
  }
  
  await _saveFullConfig();
  
  document.getElementById('mcpEditor').style.display = 'none';
  document.getElementById('mcpList').style.display = 'block';
});

// ───── 定时任务 Tab ─────
const BASE_URL = () => API_URL.replace('/api/config', '');

async function loadScheduledTasks() {
  const listEl = document.getElementById('scheduledTasksList');
  if (!listEl) return;
  try {
    const res = await tasklyFetch(BASE_URL() + '/api/scheduled-tasks');
    if (!res.ok) {
      var data = await res.json().catch(function () { return {}; });
      listEl.innerHTML = '<p class="help-text">' + escapeHtml(data.message || '加载失败或后端未就绪。') + '</p>';
      return;
    }
    const list = await res.json();
    if (!Array.isArray(list)) { listEl.innerHTML = '<p class="help-text">数据格式异常。</p>'; return; }
    if (list.length === 0) {
      listEl.innerHTML = '<p class="help-text">暂无定时任务，点击「新建定时任务」添加。</p>';
      return;
    }
    listEl.innerHTML = list.map(t => {
      const next = t.nextRunAt ? new Date(t.nextRunAt).toLocaleString() : '-';
      const en = t.enabled !== false;
      return `<div class="mcp-server-row" data-scheduled-task-id="${escapeHtml(t.id)}">
        <div class="mcp-info">
          <div class="mcp-name">${escapeHtml(t.title || t.id)}</div>
          <div class="mcp-desc">下次运行: ${next} · ${t.scheduleType || 'cron'} · ${en ? '已启用' : '已禁用'}</div>
        </div>
        <div class="mcp-actions">
          <button type="button" class="btn-secondary scheduled-task-edit" data-id="${escapeHtml(t.id)}">编辑</button>
          <button type="button" class="btn-secondary scheduled-task-delete" data-id="${escapeHtml(t.id)}">删除</button>
        </div>
      </div>`;
    }).join('');
    listEl.querySelectorAll('.scheduled-task-edit').forEach(btn => {
      btn.addEventListener('click', () => editScheduledTask(btn.getAttribute('data-id')));
    });
    listEl.querySelectorAll('.scheduled-task-delete').forEach(btn => {
      btn.addEventListener('click', () => deleteScheduledTask(btn.getAttribute('data-id')));
    });
  } catch (err) {
    console.error(err);
    listEl.innerHTML = '<p class="help-text">加载失败: ' + escapeHtml(err.message) + '</p>';
  }
}

function escapeHtml(s) {
  if (s == null) return '';
  const div = document.createElement('div');
  div.textContent = s;
  return div.innerHTML;
}

function toggleScheduledTaskScheduleType() {
  const st = document.getElementById('scheduledTaskScheduleType').value;
  document.getElementById('scheduledTaskCronGroup').style.display = st === 'cron' ? 'block' : 'none';
  document.getElementById('scheduledTaskIntervalGroup').style.display = st === 'interval' ? 'block' : 'none';
}

document.getElementById('scheduledTaskScheduleType').addEventListener('change', toggleScheduledTaskScheduleType);

document.getElementById('scheduledTaskNewBtn').addEventListener('click', () => {
  document.getElementById('scheduledTaskId').value = '';
  document.getElementById('scheduledTaskTitle').value = '';
  document.getElementById('scheduledTaskContent').value = '';
  document.getElementById('scheduledTaskScheduleType').value = 'cron';
  document.getElementById('scheduledTaskCron').value = '';
  document.getElementById('scheduledTaskIntervalMinutes').value = '';
  document.getElementById('scheduledTaskEnabled').checked = true;
  document.getElementById('scheduledTaskDeleteAfterRun').checked = false;
  toggleScheduledTaskScheduleType();
  document.getElementById('scheduledTaskEditorTitle').textContent = '新建定时任务';
  document.getElementById('scheduledTaskEditor').style.display = 'block';
  document.getElementById('scheduledTasksList').style.display = 'none';
});

document.getElementById('scheduledTaskCancelBtn').addEventListener('click', () => {
  document.getElementById('scheduledTaskEditor').style.display = 'none';
  document.getElementById('scheduledTasksList').style.display = 'block';
  loadScheduledTasks();
});

async function editScheduledTask(id) {
  try {
    const res = await tasklyFetch(BASE_URL() + '/api/scheduled-tasks/' + encodeURIComponent(id));
    if (!res.ok) {
      var errData = await res.json().catch(function () { return {}; });
      throw new Error(errData.message || '获取失败');
    }
    const data = await res.json();
    document.getElementById('scheduledTaskId').value = id;
    document.getElementById('scheduledTaskTitle').value = data.meta?.title ?? '';
    document.getElementById('scheduledTaskContent').value = data.content ?? '';
    document.getElementById('scheduledTaskScheduleType').value = data.meta?.scheduleType || 'cron';
    document.getElementById('scheduledTaskCron').value = data.meta?.cronExpression ?? '';
    document.getElementById('scheduledTaskIntervalMinutes').value = data.meta?.intervalMinutes ?? '';
    document.getElementById('scheduledTaskEnabled').checked = data.meta?.enabled !== false;
    document.getElementById('scheduledTaskDeleteAfterRun').checked = !!data.meta?.deleteAfterRun;
    toggleScheduledTaskScheduleType();
    document.getElementById('scheduledTaskEditorTitle').textContent = '编辑定时任务';
    document.getElementById('scheduledTaskEditor').style.display = 'block';
    document.getElementById('scheduledTasksList').style.display = 'none';
  } catch (err) {
    alert('加载失败: ' + err.message);
  }
}

document.getElementById('scheduledTaskSaveBtn').addEventListener('click', async () => {
  const id = document.getElementById('scheduledTaskId').value.trim();
  const title = document.getElementById('scheduledTaskTitle').value.trim();
  const content = document.getElementById('scheduledTaskContent').value.trim();
  if (!title) { alert('请填写标题'); return; }
  if (!content) { alert('请填写任务内容'); return; }
  const scheduleType = document.getElementById('scheduledTaskScheduleType').value;
  const cronExpression = document.getElementById('scheduledTaskCron').value.trim();
  const intervalMinutes = parseInt(document.getElementById('scheduledTaskIntervalMinutes').value, 10);
  const enabled = document.getElementById('scheduledTaskEnabled').checked;
  const deleteAfterRun = document.getElementById('scheduledTaskDeleteAfterRun').checked;
  try {
    if (id) {
      const res = await tasklyFetch(BASE_URL() + '/api/scheduled-tasks/' + encodeURIComponent(id), {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          title, content, scheduleType,
          cronExpression: scheduleType === 'cron' ? cronExpression : null,
          intervalMinutes: scheduleType === 'interval' && !isNaN(intervalMinutes) ? intervalMinutes : null,
          enabled, deleteAfterRun
        })
      });
      if (!res.ok) throw new Error((await res.json().catch(function () { return {}; })).message || '更新失败');
    } else {
      const res = await tasklyFetch(BASE_URL() + '/api/scheduled-tasks', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          title, content, scheduleType,
          cronExpression: scheduleType === 'cron' ? cronExpression : null,
          intervalMinutes: scheduleType === 'interval' && !isNaN(intervalMinutes) ? intervalMinutes : null,
          deleteAfterRun
        })
      });
      if (!res.ok) throw new Error((await res.json().catch(function () { return {}; })).message || '创建失败');
    }
    document.getElementById('scheduledTaskEditor').style.display = 'none';
    document.getElementById('scheduledTasksList').style.display = 'block';
    loadScheduledTasks();
  } catch (err) {
    alert('保存失败: ' + err.message);
  }
});

async function deleteScheduledTask(id) {
  if (!confirm('确定要删除该定时任务吗？')) return;
  try {
    const res = await tasklyFetch(BASE_URL() + '/api/scheduled-tasks/' + encodeURIComponent(id), { method: 'DELETE' });
    if (!res.ok) {
      var data = await res.json().catch(function () { return {}; });
      throw new Error(data.message || '删除失败');
    }
    loadScheduledTasks();
  } catch (err) {
    alert('删除失败: ' + err.message);
  }
}

// ───── 计划与准确数据 Tab（仅列表 + 删除）─────
async function loadPlansList() {
  const listEl = document.getElementById('plansList');
  if (!listEl) return;
  const agentFilterEl = document.getElementById('plansAgentFilter');
  const agentName = (agentFilterEl && agentFilterEl.value.trim()) || '';
  try {
    let url = BASE_URL() + '/api/plans';
    if (agentName) url += '?agentName=' + encodeURIComponent(agentName);
    const res = await tasklyFetch(url);
    if (!res.ok) {
      var data = await res.json().catch(function () { return {}; });
      listEl.innerHTML = '<p class="help-text">' + escapeHtml(data.message || '加载失败或后端未就绪。') + '</p>';
      return;
    }
    const list = await res.json();
    if (!Array.isArray(list)) { listEl.innerHTML = '<p class="help-text">数据格式异常。</p>'; return; }
    if (list.length === 0) {
      listEl.innerHTML = '<p class="help-text">暂无计划。</p>';
      return;
    }
    listEl.innerHTML = list.map(p => {
      const id = p.id || p.Id || '';
      const title = p.title || p.Title || id;
      const updatedAt = (p.updatedAt || p.UpdatedAt) ? new Date(p.updatedAt || p.UpdatedAt).toLocaleString() : '-';
      const status = p.status || p.Status || 'draft';
      const agentName = p.createdByDisplayName || p.createdBy || p.CreatedBy || '';
      const agentLine = agentName ? 'Agent: ' + escapeHtml(agentName) + ' · ' : '';
      return `<div class="mcp-server-row" data-plan-id="${escapeHtml(id)}">
        <div class="mcp-info">
          <div class="mcp-name">${escapeHtml(title)}</div>
          <div class="mcp-desc">${agentLine}ID: ${escapeHtml(id)} · 更新: ${escapeHtml(updatedAt)} · 状态: ${escapeHtml(status)}</div>
        </div>
        <div>
          <a href="plans.html?id=${encodeURIComponent(id)}" target="_blank" class="btn-secondary" style="padding:4px 10px;font-size:12px;margin-right:6px;">查看</a>
          <button type="button" class="btn-danger plan-delete" data-id="${escapeHtml(id)}" style="padding:4px 10px;font-size:12px;">删除</button>
        </div>
      </div>`;
    }).join('');
    listEl.querySelectorAll('.plan-delete').forEach(btn => {
      btn.addEventListener('click', () => deletePlan(btn.dataset.id));
    });
  } catch (err) {
    listEl.innerHTML = '<p class="help-text">加载失败: ' + escapeHtml(err.message) + '</p>';
  }
}

async function deletePlan(id) {
  if (!id || !confirm('确定要删除该计划吗？')) return;
  try {
    const res = await tasklyFetch(BASE_URL() + '/api/plans/' + encodeURIComponent(id), { method: 'DELETE' });
    if (!res.ok) {
      var data = await res.json().catch(function () { return {}; });
      throw new Error(data.message || '删除失败');
    }
    loadPlansList();
  } catch (err) {
    alert('删除失败: ' + err.message);
  }
}

async function loadAccurateDataList() {
  const listEl = document.getElementById('accurateDataList');
  if (!listEl) return;
  try {
    const res = await tasklyFetch(BASE_URL() + '/api/accurate-data');
    if (!res.ok) {
      var data = await res.json().catch(function () { return {}; });
      listEl.innerHTML = '<p class="help-text">' + escapeHtml(data.message || '加载失败或后端未就绪。') + '</p>';
      return;
    }
    const list = await res.json();
    if (!Array.isArray(list)) { listEl.innerHTML = '<p class="help-text">数据格式异常。</p>'; return; }
    if (list.length === 0) {
      listEl.innerHTML = '<p class="help-text">暂无准确数据条目。</p>';
      return;
    }
    listEl.innerHTML = list.map(item => {
      const id = item.id || '';
      const format = item.format || 'md';
      return `<div class="mcp-server-row" data-accurate-id="${escapeHtml(id)}">
        <div class="mcp-info">
          <div class="mcp-name">${escapeHtml(id)}</div>
          <div class="mcp-desc">格式: ${escapeHtml(format)}</div>
        </div>
        <div>
          <button type="button" class="btn-danger accurate-data-delete" data-id="${escapeHtml(id)}" style="padding:4px 10px;font-size:12px;">删除</button>
        </div>
      </div>`;
    }).join('');
    listEl.querySelectorAll('.accurate-data-delete').forEach(btn => {
      btn.addEventListener('click', () => deleteAccurateData(btn.dataset.id));
    });
  } catch (err) {
    listEl.innerHTML = '<p class="help-text">加载失败: ' + escapeHtml(err.message) + '</p>';
  }
}

async function deleteAccurateData(id) {
  if (!id || !confirm('确定要删除该准确数据条目吗？')) return;
  try {
    const res = await tasklyFetch(BASE_URL() + '/api/accurate-data/' + encodeURIComponent(id), { method: 'DELETE' });
    if (!res.ok) {
      var data = await res.json().catch(function () { return {}; });
      throw new Error(data.message || '删除失败');
    }
    loadAccurateDataList();
  } catch (err) {
    alert('删除失败: ' + err.message);
  }
}

/** 保存整份 config（MCP 等 Tab 内调用）。复用主保存逻辑，不再使用已废弃的 els.provider 等。 */
async function _saveFullConfig() {
  await saveConfig();
}

function getDisabledBuiltIn() {
  if (!fullConfig) return [];
  var list = fullConfig.disabledBuiltInPlugins || fullConfig.DisabledBuiltInPlugins;
  return Array.isArray(list) ? list.slice() : [];
}

function getAllowedCliCommands() {
  if (!fullConfig) return [];
  var list = fullConfig.allowedCliCommands || fullConfig.AllowedCliCommands;
  return Array.isArray(list) ? list.slice() : [];
}
