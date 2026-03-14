const API_URL = "http://localhost:8765/api/config";
const SKILLS_API_URL = "http://localhost:8765/api/skills";
const BUILTIN_TOOLS_URL = "http://localhost:8765/api/tools/builtin";

const els = {
  allowedPageScriptIds: document.getElementById('allowedPageScriptIds'),
  saveBtn: document.getElementById('saveBtn'),
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
let testConnectionFields = { endpoint: '', modelId: '', apiKey: '', provider: 'OpenAI', deploymentName: '' };

// ───── Tabs ─────
els.tabs.forEach(tab => {
  tab.addEventListener('click', () => {
    els.tabs.forEach(t => t.classList.remove('active'));
    els.tabContents.forEach(c => c.classList.remove('active'));
    
    tab.classList.add('active');
    document.getElementById(tab.dataset.target).classList.add('active');
    
    if (tab.dataset.target === 'tab-skills') {
      loadSkills();
    }
    if (tab.dataset.target === 'tab-mcp') {
      loadBuiltinTools();
    }
    if (tab.dataset.target === 'tab-memory') {
      loadMemoryList();
    }
    if (tab.dataset.target === 'tab-scheduled-tasks') {
      loadScheduledTasks();
    }
    if (tab.dataset.target === 'tab-plans-accurate') {
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
      '<button type="button" class="btn-secondary edit-ai-btn" data-id="' + escapeAttr(id) + '">编辑</button>' +
      '<button type="button" class="btn-danger delete-ai-btn" data-id="' + escapeAttr(id) + '">删除</button>' +
      '</div></div>';
  }).join('');
  els.aiModelsList.querySelectorAll('.enable-ai-btn').forEach(function (btn) {
    btn.addEventListener('click', function () { setActiveAiModel(btn.dataset.id); });
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

function toggleEmbeddingSections() {
  var srcEl = document.getElementById('embeddingSource');
  var src = srcEl ? srcEl.value : '';
  var remoteSec = document.getElementById('embeddingRemoteSection');
  if (remoteSec) remoteSec.style.display = (src === 'Remote') ? 'block' : 'none';
}

function updateEmbeddingModelSummary() {
  var sum = document.getElementById('embeddingModelSummary');
  if (!sum) return;
  var srcEl = document.getElementById('embeddingSource');
  var src = (srcEl && srcEl.value) || (fullConfig && (fullConfig.embeddingSource ?? fullConfig.EmbeddingSource)) || '';
  var modelId = (document.getElementById('embeddingModelId') && document.getElementById('embeddingModelId').value) || (fullConfig && (fullConfig.embeddingModelId ?? fullConfig.EmbeddingModelId)) || '';
  var endpoint = (document.getElementById('embeddingEndpoint') && document.getElementById('embeddingEndpoint').value) || (fullConfig && (fullConfig.embeddingEndpoint ?? fullConfig.EmbeddingEndpoint)) || '';
  if (src === 'Remote' && (modelId || endpoint)) {
    sum.textContent = 'Embedding 模型（当前：远程 ' + (modelId || endpoint) + '）';
  } else {
    sum.textContent = 'Embedding 模型（未配置）';
  }
}

var MEMORY_SCOPE_SHARED = '__shared__';

function loadMemoryList() {
  var scopeEl = document.getElementById('memoryScopeFilter');
  var scope = (scopeEl && scopeEl.value) || 'all';
  var sessionId = (document.getElementById('memorySessionFilter') && document.getElementById('memorySessionFilter').value.trim()) || undefined;
  if (scope === 'session' && !sessionId) sessionId = '';
  var baseUrl = API_URL.replace('/api/config', '');
  var url = baseUrl + '/api/memory?skip=0&take=50&scope=' + encodeURIComponent(scope);
  if (scope === 'session' && sessionId !== undefined) url += '&sessionId=' + encodeURIComponent(sessionId);
  fetch(url).then(function (r) { return r.json(); }).then(function (data) {
    var list = data.items || [];
    var html = list.length ? list.map(function (item) {
      var text = (item.text || '').substring(0, 120);
      if ((item.text || '').length > 120) text += '…';
      var createdAt = item.createdAt ? new Date(item.createdAt).toLocaleString() : '';
      var isShared = item.sessionId === MEMORY_SCOPE_SHARED;
      var scopeLabel = isShared ? ' <span class="help-text" style="color:var(--accent);">共享</span>' : '';
      var sessionLabel = (item.sessionId && !isShared) ? '会话: ' + escapeHtml(item.sessionId) + ' | ' : '';
      return '<div class="skill-card" data-memory-id="' + escapeAttr(item.id) + '">' +
        '<div class="skill-header"><span class="skill-title">' + escapeHtml(item.id) + '</span>' + scopeLabel +
        '<div><button type="button" class="btn-secondary edit-memory-btn" data-id="' + escapeAttr(item.id) + '">编辑</button> ' +
        '<button type="button" class="btn-danger delete-memory-btn" data-id="' + escapeAttr(item.id) + '">删除</button></div></div>' +
        '<div class="skill-desc">' + escapeHtml(text) + '</div>' +
        '<div class="help-text">' + sessionLabel + createdAt + '</div></div>';
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
    fetch(baseUrl + '/api/memory/' + encodeURIComponent(id)).then(function (r) { return r.json(); }).then(function (item) {
      textEl.value = item.text || '';
      tagsEl.value = (item.metadata && item.metadata.tags) ? item.metadata.tags : '';
      var scopeCb = document.getElementById('memoryEditScopeShared');
      if (scopeCb) scopeCb.checked = item.sessionId === MEMORY_SCOPE_SHARED;
      editor.style.display = 'block';
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
  fetch(url, { method: method, headers: { 'Content-Type': 'application/json' }, body: body }).then(function (r) {
    if (!r.ok) throw new Error('保存失败');
    closeMemoryEditor();
    loadMemoryList();
  }).catch(function (e) { alert('保存失败: ' + (e.message || e)); });
}

function deleteMemory(id) {
  if (!id || !confirm('确定删除这条记忆？')) return;
  var baseUrl = API_URL.replace('/api/config', '');
  fetch(baseUrl + '/api/memory/' + encodeURIComponent(id), { method: 'DELETE' }).then(function (r) {
    if (!r.ok) throw new Error('删除失败');
    loadMemoryList();
  }).catch(function (e) { alert('删除失败: ' + (e.message || e)); });
}

var embeddingSourceEl = document.getElementById('embeddingSource');
if (embeddingSourceEl) embeddingSourceEl.addEventListener('change', function () {
  toggleEmbeddingSections();
  updateEmbeddingModelSummary();
});

var ragStorageTypeEl = document.getElementById('ragStorageType');
if (ragStorageTypeEl) ragStorageTypeEl.addEventListener('change', function () {
  var g = document.getElementById('ragStoragePathGroup');
  if (g) g.style.display = (this.value === 'Sqlite') ? 'block' : 'none';
});

if (document.getElementById('addMemoryBtn')) document.getElementById('addMemoryBtn').addEventListener('click', function () { openMemoryEditor(null); });
if (document.getElementById('refreshMemoryListBtn')) document.getElementById('refreshMemoryListBtn').addEventListener('click', loadMemoryList);
if (document.getElementById('saveMemoryBtn')) document.getElementById('saveMemoryBtn').addEventListener('click', saveMemoryFromEditor);
if (document.getElementById('cancelMemoryEditBtn')) document.getElementById('cancelMemoryEditBtn').addEventListener('click', closeMemoryEditor);
var memoryScopeFilterEl = document.getElementById('memoryScopeFilter');
var memorySessionFilterEl = document.getElementById('memorySessionFilter');
if (memoryScopeFilterEl && memorySessionFilterEl) {
  function toggleMemorySessionFilter() {
    memorySessionFilterEl.style.display = (memoryScopeFilterEl.value === 'session') ? 'inline-block' : 'none';
  }
  memoryScopeFilterEl.addEventListener('change', toggleMemorySessionFilter);
  toggleMemorySessionFilter();
}

function openAiModelEditor(existingId) {
  editingAiModelId = existingId || null;
  els.aiModelEditorTitle.textContent = existingId ? '编辑 AI 模型' : '添加新 AI';
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
      testConnectionFields = {
        endpoint: String(m.endpoint || m.Endpoint || '').trim(),
        modelId: String(m.modelId || m.ModelId || '').trim(),
        apiKey: m.apiKey || m.ApiKey || '',
        provider: m.provider || m.Provider || 'OpenAI',
        deploymentName: String(m.deploymentName || m.DeploymentName || '').trim()
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
    testConnectionFields = { endpoint: '', modelId: '', apiKey: '', provider: 'OpenAI', deploymentName: '' };
  }
  toggleAzureFields();
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
    if (e && e.value != null) testConnectionFields.endpoint = String(e.value).trim();
    if (m && m.value != null) testConnectionFields.modelId = String(m.value).trim();
    if (p && p.value) testConnectionFields.provider = p.value;
    if (d && d.value != null) testConnectionFields.deploymentName = String(d.value).trim();
    if (k && k.value != null) testConnectionFields.apiKey = k.value;
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
    enabled: els.aiModelEnabled ? els.aiModelEnabled.checked : true
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
}

async function loadSkillEnvSection() {
  const listEl = document.getElementById('skillEnvList');
  const sectionEl = document.getElementById('skillEnvSection');
  if (!listEl || !sectionEl) return;
  const skillEnv = fullConfig && (fullConfig.skillEnv || fullConfig.SkillEnv);
  const existing = skillEnv && typeof skillEnv === 'object' ? Object.keys(skillEnv) : [];
  let keysSet = new Set(existing);
  try {
    const res = await fetch(SKILLS_API_URL);
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

async function loadConfig() {
  try {
    els.saveBtn.disabled = true;
    els.saveBtn.textContent = '加载中...';
    
    const response = await fetch(API_URL);
    if (!response.ok) throw new Error('Failed to load');
    
    fullConfig = await response.json();
    const data = fullConfig;
    renderAiModelsList();
    const ids = data.allowedPageScriptIds ?? data.AllowedPageScriptIds;
    if (els.allowedPageScriptIds) {
      els.allowedPageScriptIds.value = Array.isArray(ids) ? ids.join('\n') : '';
    }
    const runEverythingEl = document.getElementById('runEverythingMode');
    if (runEverythingEl) {
      runEverythingEl.checked = !!(data.runEverythingMode ?? data.RunEverythingMode);
    }
    const mcps = data.mcpServers ?? data.McpServers;
    renderMcpList(mcps || []);
    await loadSkillEnvSection();
    await loadBuiltinTools();
    var embSrc = (data.embeddingSource ?? data.EmbeddingSource) || '';
    var embEndpoint = (data.embeddingEndpoint ?? data.EmbeddingEndpoint) || '';
    var embApiKey = (data.embeddingApiKey ?? data.EmbeddingApiKey) || '';
    var embModelId = (data.embeddingModelId ?? data.EmbeddingModelId) || '';
    var embSourceEl = document.getElementById('embeddingSource');
    var embEndpointEl = document.getElementById('embeddingEndpoint');
    var embApiKeyEl = document.getElementById('embeddingApiKey');
    var embModelIdEl = document.getElementById('embeddingModelId');
    if (embSourceEl) embSourceEl.value = embSrc || '';
    if (embEndpointEl) embEndpointEl.value = embEndpoint || '';
    if (embApiKeyEl) embApiKeyEl.value = embApiKey || '';
    if (embModelIdEl) embModelIdEl.value = embModelId || '';
    toggleEmbeddingSections();
    updateEmbeddingModelSummary();
    var ragType = data.ragStorageType ?? data.RagStorageType ?? 'Memory';
    var ragPath = data.ragStoragePath ?? data.RagStoragePath ?? '';
    var rtEl = document.getElementById('ragStorageType');
    var rpEl = document.getElementById('ragStoragePath');
    var rpGroup = document.getElementById('ragStoragePathGroup');
    if (rtEl) rtEl.value = ragType || 'Memory';
    if (rpEl) rpEl.value = ragPath || '';
    if (rpGroup) rpGroup.style.display = (ragType === 'Sqlite') ? 'block' : 'none';
    var pc = data.planConfirmation ?? data.PlanConfirmation;
    if (pc) {
      var aeEl = document.getElementById('autoExecuteMaxSteps');
      var rcEl = document.getElementById('requireConfirmForSensitiveTools');
      var stEl = document.getElementById('sensitiveToolIds');
      if (aeEl) aeEl.value = (pc.autoExecuteMaxSteps ?? pc.AutoExecuteMaxSteps ?? 3);
      if (rcEl) rcEl.checked = !!(pc.requireConfirmForSensitiveTools ?? pc.RequireConfirmForSensitiveTools);
      if (stEl) stEl.value = Array.isArray(pc.sensitiveToolIds ?? pc.SensitiveToolIds) ? (pc.sensitiveToolIds || pc.SensitiveToolIds).join('\n') : '';
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
  } catch (err) {
    console.error(err);
    alert('无法连接到本地服务，请确保已启动 OfficeCopilot.Server.exe');
  } finally {
    els.saveBtn.disabled = false;
    els.saveBtn.textContent = '保存配置';
  }
}

async function saveConfig() {
  try {
    els.saveBtn.disabled = true;
    els.saveBtn.textContent = '保存中...';
    const allowedPageScriptIds = (els.allowedPageScriptIds && els.allowedPageScriptIds.value)
      ? els.allowedPageScriptIds.value.split('\n').map(function (s) { return s.trim(); }).filter(Boolean)
      : [];
    var allowedCliCommands = [];
    var cliTa = document.getElementById('allowedCliCommands');
    if (cliTa && cliTa.value) allowedCliCommands = cliTa.value.split('\n').map(function (s) { return s.trim(); }).filter(Boolean);
    else allowedCliCommands = getAllowedCliCommands();
    const runEverythingEl = document.getElementById('runEverythingMode');
    const runEverythingMode = runEverythingEl ? runEverythingEl.checked : !!(fullConfig && (fullConfig.runEverythingMode ?? fullConfig.RunEverythingMode));
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
    var embSourceEl = document.getElementById('embeddingSource');
    var embEndpointEl = document.getElementById('embeddingEndpoint');
    var embApiKeyEl = document.getElementById('embeddingApiKey');
    var embModelIdEl = document.getElementById('embeddingModelId');
    var embSrc = embSourceEl ? embSourceEl.value : ((fullConfig && (fullConfig.embeddingSource ?? fullConfig.EmbeddingSource)) || '');
    var embEndpoint = embEndpointEl ? embEndpointEl.value.trim() : ((fullConfig && (fullConfig.embeddingEndpoint ?? fullConfig.EmbeddingEndpoint)) || '');
    var embApiKey = embApiKeyEl ? embApiKeyEl.value : ((fullConfig && (fullConfig.embeddingApiKey ?? fullConfig.EmbeddingApiKey)) || '');
    var embModelId = embModelIdEl ? embModelIdEl.value.trim() : ((fullConfig && (fullConfig.embeddingModelId ?? fullConfig.EmbeddingModelId)) || '');
    var ragTypeEl = document.getElementById('ragStorageType');
    var ragPathEl = document.getElementById('ragStoragePath');
    var ragType = ragTypeEl ? ragTypeEl.value : 'Memory';
    var ragPath = ragPathEl ? ragPathEl.value : '';
    var aeEl = document.getElementById('autoExecuteMaxSteps');
    var rcEl = document.getElementById('requireConfirmForSensitiveTools');
    var stEl = document.getElementById('sensitiveToolIds');
    var autoExecuteMaxSteps = (aeEl && aeEl.value !== '') ? parseInt(aeEl.value, 10) : (fullConfig && fullConfig.planConfirmation && (fullConfig.planConfirmation.autoExecuteMaxSteps ?? fullConfig.planConfirmation.AutoExecuteMaxSteps)) || 3;
    if (isNaN(autoExecuteMaxSteps) || autoExecuteMaxSteps < 1) autoExecuteMaxSteps = 3;
    var requireConfirmForSensitiveTools = rcEl ? rcEl.checked : !!(fullConfig && fullConfig.planConfirmation && (fullConfig.planConfirmation.requireConfirmForSensitiveTools ?? fullConfig.planConfirmation.RequireConfirmForSensitiveTools));
    var sensitiveToolIds = (stEl && stEl.value) ? stEl.value.split('\n').map(function (s) { return s.trim(); }).filter(Boolean) : [];
    var presetSelectEl = document.getElementById('activeContextPreset');
    var activeContextPresetId = (presetSelectEl && presetSelectEl.value) ? presetSelectEl.value : (fullConfig && (fullConfig.activeContextPresetId ?? fullConfig.ActiveContextPresetId));
    var contextOptimizationPresets = (fullConfig && (fullConfig.contextOptimizationPresets ?? fullConfig.ContextOptimizationPresets)) || [];
    var planConfirmationPayload = { autoExecuteMaxSteps: autoExecuteMaxSteps, requireConfirmForSensitiveTools: requireConfirmForSensitiveTools, sensitiveToolIds: sensitiveToolIds };
    if (activeContextPresetId && contextOptimizationPresets.length > 0) {
      var activePreset = contextOptimizationPresets.find(function (p) { return (p.id || p.Id) === activeContextPresetId; });
      if (activePreset) {
        activePreset.planConfirmation = activePreset.PlanConfirmation = planConfirmationPayload;
      }
    }
    const payload = {
      ai: aiPayload,
      aiModels: aiModelsToSave,
      activeModelId: activeId,
      tavilyApiKey: (function () { var se = collectSkillEnv(); return (se && se.TAVILY_API_KEY) || (fullConfig && (fullConfig.tavilyApiKey ?? fullConfig.TavilyApiKey)) || ''; })(),
      skillEnv: collectSkillEnv(),
      mcpServers: (fullConfig && fullConfig.mcpServers) || (fullConfig && fullConfig.McpServers) || [],
      allowedPageScriptIds: allowedPageScriptIds,
      allowedCliCommands: allowedCliCommands,
      disabledBuiltInPlugins: getDisabledBuiltIn(),
      runEverythingMode: runEverythingMode,
      embeddingSource: embSrc || undefined,
      embeddingEndpoint: (embSrc === 'Remote') ? (embEndpoint || undefined) : undefined,
      embeddingApiKey: (embSrc === 'Remote') ? (embApiKey || undefined) : undefined,
      embeddingModelId: (embSrc === 'Remote' && embModelId) ? embModelId : undefined,
      ragStorageType: ragType,
      ragStoragePath: (ragType === 'Sqlite' && ragPath) ? ragPath : undefined,
      planConfirmation: planConfirmationPayload,
      activeContextPresetId: activeContextPresetId || undefined,
      contextOptimizationPresets: contextOptimizationPresets.length > 0 ? contextOptimizationPresets : undefined
    };
    const response = await fetch(API_URL, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });
    if (!response.ok) throw new Error('Failed to save');
    fullConfig = Object.assign({}, fullConfig || {}, { ai: payload.ai, aiModels: payload.aiModels, activeModelId: payload.activeModelId, tavilyApiKey: payload.tavilyApiKey, skillEnv: payload.skillEnv, mcpServers: payload.mcpServers, allowedPageScriptIds: payload.allowedPageScriptIds, allowedCliCommands: payload.allowedCliCommands, disabledBuiltInPlugins: payload.disabledBuiltInPlugins, runEverythingMode: payload.runEverythingMode, embeddingSource: payload.embeddingSource, embeddingEndpoint: payload.embeddingEndpoint, embeddingApiKey: payload.embeddingApiKey, embeddingModelId: payload.embeddingModelId, ragStorageType: payload.ragStorageType, ragStoragePath: payload.ragStoragePath, planConfirmation: payload.planConfirmation, activeContextPresetId: payload.activeContextPresetId, contextOptimizationPresets: payload.contextOptimizationPresets });
    els.statusMessage.style.opacity = '1';
    setTimeout(function () { els.statusMessage.style.opacity = '0'; }, 2000);
    await loadConfig();
  } catch (err) {
    console.error(err);
    alert('保存失败，请检查服务状态。');
  } finally {
    els.saveBtn.disabled = false;
    els.saveBtn.textContent = '保存配置';
  }
}

els.saveBtn.addEventListener('click', saveConfig);

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

if (els.addAiModelBtn) els.addAiModelBtn.addEventListener('click', function () { openAiModelEditor(null); });
if (els.closeAiModelEditorBtn) els.closeAiModelEditorBtn.addEventListener('click', closeAiModelEditor);
if (els.saveAiModelBtn) els.saveAiModelBtn.addEventListener('click', saveAiModelFromEditor);
if (els.aiModelProvider) els.aiModelProvider.addEventListener('change', toggleAzureFields);
if (els.testAiConnectionBtn) els.testAiConnectionBtn.addEventListener('click', testAiConnection);

async function testAiConnection() {
  var statusEl = document.getElementById('testAiStatus');
  if (!statusEl) return;
  var endpoint = testConnectionFields.endpoint || '';
  var modelId = testConnectionFields.modelId || '';
  var provider = testConnectionFields.provider || 'OpenAI';
  var deploymentName = testConnectionFields.deploymentName || '';
  var apiKey = testConnectionFields.apiKey || '';
  if (!endpoint || !modelId) {
    statusEl.textContent = '请先填写接口地址和模型 ID';
    statusEl.style.color = 'var(--danger)';
    return;
  }
  statusEl.textContent = '测试中…';
  statusEl.style.color = 'var(--text-secondary)';
  const testModelId = (provider === 'Azure' && deploymentName) ? deploymentName : modelId;
  try {
    const baseUrl = API_URL.replace('/api/config', '');
    const res = await fetch(baseUrl + '/api/config/test-ai', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        endpoint: endpoint,
        apiKey: apiKey,
        modelId: testModelId,
        provider: provider,
        deploymentName: deploymentName
      })
    });
    const data = await res.json().catch(function () { return { ok: false, message: '响应解析失败' }; });
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

// ───── Skills ─────
let currentSkills = [];

async function loadSkills() {
  try {
    const res = await fetch(SKILLS_API_URL);
    if (res.ok) {
      currentSkills = await res.json();
      renderSkills();
    } else {
      currentSkills = [];
      renderSkills();
      alert('无法加载技能列表，请确保后端已启动。');
    }
  } catch (err) {
    console.error('Failed to load skills', err);
    currentSkills = [];
    renderSkills();
    alert('无法加载技能列表，请确保后端已启动。');
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
    const res = await fetch(`${SKILLS_API_URL}/${id}`, { method: 'DELETE' });
    if (res.ok) await loadSkills();
  } catch (err) {
    alert('删除失败');
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
    const res = await fetch(SKILLS_API_URL, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });
    
    if (res.ok) {
      els.skillEditor.style.display = 'none';
      els.skillsList.style.display = 'block';
      els.newSkillBtn.style.display = 'block';
      await loadSkills();
    }
  } catch (err) {
    alert('保存失败');
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
    const res = await fetch(SKILLS_API_URL, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });
    if (res.ok) await loadSkills();
    else alert('操作失败');
  } catch (err) {
    alert('操作失败');
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

// ───── Boot ─────
document.addEventListener('DOMContentLoaded', function () {
  loadConfig();
  var mcpTab = document.getElementById('tab-mcp');
  if (mcpTab) {
    mcpTab.addEventListener('click', function (e) {
      if (e.target.closest('.save-scripts-btn')) {
        var ta = document.getElementById('allowedPageScriptIds');
        if (!ta || !fullConfig) return;
        var ids = (ta.value || '').split('\n').map(function (s) { return s.trim(); }).filter(Boolean);
        fullConfig.allowedPageScriptIds = ids;
        fullConfig.AllowedPageScriptIds = ids;
        _saveFullConfig().then(function () {
          var card = e.target.closest('.builtin-card');
          var statusEl = card && card.querySelector('.status-scripts');
          if (statusEl) {
            statusEl.style.opacity = '1';
            statusEl.textContent = '保存成功！';
            setTimeout(function () { statusEl.style.opacity = '0'; }, 2000);
          }
        });
        return;
      }
      if (e.target.closest('.save-cli-whitelist-btn')) {
        var cliTa = document.getElementById('allowedCliCommands');
        if (!cliTa || !fullConfig) return;
        var cmds = (cliTa.value || '').split('\n').map(function (s) { return s.trim(); }).filter(Boolean);
        fullConfig.allowedCliCommands = cmds;
        fullConfig.AllowedCliCommands = cmds;
        _saveFullConfig().then(function () {
          var card = e.target.closest('.builtin-card');
          var statusEl = card && card.querySelector('.status-cli-whitelist');
          if (statusEl) {
            statusEl.style.opacity = '1';
            statusEl.textContent = '保存成功！';
            setTimeout(function () { statusEl.style.opacity = '0'; }, 2000);
          }
          loadBuiltinTools();
        });
      }
    });
  }
});

// ───── MCP ─────
async function loadBuiltinTools() {
  const el = document.getElementById('builtinToolsList');
  if (!el) return;
  var disabledSet = (getDisabledBuiltIn() || []).map(function (s) { return (s || '').toLowerCase(); }).filter(Boolean);
  try {
    const res = await fetch(BUILTIN_TOOLS_URL);
    if (!res.ok) {
      el.innerHTML = '<div style="padding:12px;color:#94a3b8;font-size:13px;">无法加载内置插件列表（' + res.status + '）。请确认后端已启动且已更新至最新版本。</div>';
      return;
    }
    const list = await res.json();
    if (!Array.isArray(list) || list.length === 0) {
      el.innerHTML = '<div style="padding:12px;color:#94a3b8;font-size:13px;">暂无内置插件</div>';
      return;
    }
    var scriptIds = (fullConfig && (fullConfig.allowedPageScriptIds || fullConfig.AllowedPageScriptIds)) || [];
    var scriptIdsText = Array.isArray(scriptIds) ? scriptIds.join('\n') : '';
    var scriptIdsEscaped = escapeHtml(scriptIdsText);
    var cliCommands = (fullConfig && (fullConfig.allowedCliCommands || fullConfig.AllowedCliCommands)) || [];
    var cliCommandsText = Array.isArray(cliCommands) ? cliCommands.join('\n') : '';
    var cliCommandsEscaped = escapeHtml(cliCommandsText);
    el.innerHTML = list.map(p => {
      const id = (p.id || p.Id || p.name || p.Name || '').toLowerCase();
      const name = p.name || p.Name || p.id || p.Id || '';
      const desc = p.description || p.Description || '';
      const disabled = disabledSet.indexOf(id) >= 0;
      const idAttr = escapeAttr(id);
      var cardBody = `<div class="skill-header"><div class="skill-title">${escapeHtml(name)}${disabled ? ' <span style="color:#94a3b8;font-weight:normal;">(已停用)</span>' : ''}</div><div><button type="button" class="btn-secondary builtin-toggle-btn" style="padding:4px 12px;font-size:12px;">${disabled ? '启用' : '停用'}</button></div></div><div class="skill-desc">${escapeHtml(desc)}</div>`;
      if (id === 'browser') {
        cardBody += `<div class="form-group" style="margin-top:12px;padding-top:12px;border-top:1px solid var(--border);"><label for="allowedPageScriptIds">页面脚本白名单 (run_page_script)</label><textarea id="allowedPageScriptIds" placeholder="scroll_to_top&#10;scroll_to_bottom&#10;get_visible_text&#10;get_page_title" style="min-height:72px;">${scriptIdsEscaped}</textarea><div class="help-text" style="margin-top:4px;">每行一个 scriptId，仅允许列表内的脚本被 AI 执行。留空则使用默认列表。</div><div class="actions" style="margin-top:8px;padding-top:8px;border-top:none;"><span class="status-scripts status" style="opacity:0;">保存成功！</span><button type="button" class="btn-secondary save-scripts-btn" style="padding:4px 12px;font-size:12px;">保存白名单</button></div></div>`;
      }
      if (id === 'cli') {
        cardBody += `<div class="form-group" style="margin-top:12px;padding-top:12px;border-top:1px solid var(--border);"><label for="allowedCliCommands">命令白名单 (run_command)</label><textarea id="allowedCliCommands" placeholder="dir&#10;echo&#10;type&#10;ping&#10;systeminfo&#10;ipconfig" style="min-height:72px;">${cliCommandsEscaped}</textarea><div class="help-text" style="margin-top:4px;">每行一个命令名（如 dir、echo、type），仅允许列表内的命令被 AI 执行。留空则使用默认列表。</div><div class="actions" style="margin-top:8px;padding-top:8px;border-top:none;"><span class="status-cli-whitelist status" style="opacity:0;">保存成功！</span><button type="button" class="btn-secondary save-cli-whitelist-btn" style="padding:4px 12px;font-size:12px;">保存白名单</button></div></div>`;
      }
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
    console.warn('loadBuiltinTools failed', e);
    el.innerHTML = '<div style="padding:12px;color:#94a3b8;font-size:13px;">无法加载内置插件列表（请确认后端已启动且已更新至最新版本）。</div>';
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

const SALES_DB_MCP_PRESET = {
  id: 'sales-db-mcp',
  name: 'SalesDbMcp',
  command: 'dotnet',
  args: ['run', '--project', 'sales-db-mcp/SalesDbMcp.csproj']
};

const ACCURATE_DATA_MCP_PRESET = {
  id: 'accurate-data-mcp',
  name: 'AccurateDataMcp',
  command: 'dotnet',
  args: ['run', '--project', 'accurate-data-mcp/AccurateDataMcp.csproj']
};

document.getElementById('addSalesDbMcpBtn').addEventListener('click', async () => {
  if (!fullConfig) {
    alert('请先等待配置加载完成。');
    return;
  }
  const list = getMcpServers();
  const exists = list.some(s => (s.id || s.Id) === SALES_DB_MCP_PRESET.id);
  if (exists) {
    window.editMcp(SALES_DB_MCP_PRESET.id);
    return;
  }
  if (!fullConfig.mcpServers) fullConfig.mcpServers = list.slice();
  fullConfig.mcpServers.push({ ...SALES_DB_MCP_PRESET });
  await _saveFullConfig();
  renderMcpList(getMcpServers());
});

document.getElementById('addAccurateDataMcpBtn').addEventListener('click', async () => {
  if (!fullConfig) {
    alert('请先等待配置加载完成。');
    return;
  }
  const list = getMcpServers();
  const exists = list.some(s => (s.id || s.Id) === ACCURATE_DATA_MCP_PRESET.id);
  if (exists) {
    window.editMcp(ACCURATE_DATA_MCP_PRESET.id);
    return;
  }
  if (!fullConfig.mcpServers) fullConfig.mcpServers = list.slice();
  fullConfig.mcpServers.push({ ...ACCURATE_DATA_MCP_PRESET });
  await _saveFullConfig();
  renderMcpList(getMcpServers());
});

const SCHEDULED_TASK_MCP_PRESET = {
  id: 'scheduled-task-mcp',
  name: 'ScheduledTaskMcp',
  command: 'dotnet',
  args: ['run', '--project', 'scheduled-task-mcp/ScheduledTaskMcp.csproj']
};

document.getElementById('addScheduledTaskMcpBtn').addEventListener('click', async () => {
  if (!fullConfig) {
    alert('请先等待配置加载完成。');
    return;
  }
  const list = getMcpServers();
  const exists = list.some(s => (s.id || s.Id) === SCHEDULED_TASK_MCP_PRESET.id);
  if (exists) {
    window.editMcp(SCHEDULED_TASK_MCP_PRESET.id);
    return;
  }
  if (!fullConfig.mcpServers) fullConfig.mcpServers = list.slice();
  fullConfig.mcpServers.push({ ...SCHEDULED_TASK_MCP_PRESET });
  await _saveFullConfig();
  renderMcpList(getMcpServers());
});

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
    const res = await fetch(BASE_URL() + '/api/scheduled-tasks');
    if (!res.ok) { listEl.innerHTML = '<p class="help-text">加载失败或后端未就绪。</p>'; return; }
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
    const res = await fetch(BASE_URL() + '/api/scheduled-tasks/' + encodeURIComponent(id));
    if (!res.ok) throw new Error('获取失败');
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
      const res = await fetch(BASE_URL() + '/api/scheduled-tasks/' + encodeURIComponent(id), {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          title, content, scheduleType,
          cronExpression: scheduleType === 'cron' ? cronExpression : null,
          intervalMinutes: scheduleType === 'interval' && !isNaN(intervalMinutes) ? intervalMinutes : null,
          enabled, deleteAfterRun
        })
      });
      if (!res.ok) throw new Error((await res.json()).message || '更新失败');
    } else {
      const res = await fetch(BASE_URL() + '/api/scheduled-tasks', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          title, content, scheduleType,
          cronExpression: scheduleType === 'cron' ? cronExpression : null,
          intervalMinutes: scheduleType === 'interval' && !isNaN(intervalMinutes) ? intervalMinutes : null,
          deleteAfterRun
        })
      });
      if (!res.ok) throw new Error((await res.json()).message || '创建失败');
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
    const res = await fetch(BASE_URL() + '/api/scheduled-tasks/' + encodeURIComponent(id), { method: 'DELETE' });
    if (!res.ok) throw new Error('删除失败');
    loadScheduledTasks();
  } catch (err) {
    alert('删除失败: ' + err.message);
  }
}

// ───── 计划与准确数据 Tab（仅列表 + 删除）─────
async function loadPlansList() {
  const listEl = document.getElementById('plansList');
  if (!listEl) return;
  try {
    const res = await fetch(BASE_URL() + '/api/plans');
    if (!res.ok) { listEl.innerHTML = '<p class="help-text">加载失败或后端未就绪。</p>'; return; }
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
      return `<div class="mcp-server-row" data-plan-id="${escapeHtml(id)}">
        <div class="mcp-info">
          <div class="mcp-name">${escapeHtml(title)}</div>
          <div class="mcp-desc">ID: ${escapeHtml(id)} · 更新: ${escapeHtml(updatedAt)} · 状态: ${escapeHtml(status)}</div>
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
    const res = await fetch(BASE_URL() + '/api/plans/' + encodeURIComponent(id), { method: 'DELETE' });
    if (!res.ok) throw new Error('删除失败');
    loadPlansList();
  } catch (err) {
    alert('删除失败: ' + err.message);
  }
}

async function loadAccurateDataList() {
  const listEl = document.getElementById('accurateDataList');
  if (!listEl) return;
  try {
    const res = await fetch(BASE_URL() + '/api/accurate-data');
    if (!res.ok) { listEl.innerHTML = '<p class="help-text">加载失败或后端未就绪。</p>'; return; }
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
    const res = await fetch(BASE_URL() + '/api/accurate-data/' + encodeURIComponent(id), { method: 'DELETE' });
    if (!res.ok) throw new Error('删除失败');
    loadAccurateDataList();
  } catch (err) {
    alert('删除失败: ' + err.message);
  }
}

async function _saveFullConfig() {
  try {
    var scriptIdsEl = els.allowedPageScriptIds || document.getElementById('allowedPageScriptIds');
    var allowedPageScriptIds = (scriptIdsEl && scriptIdsEl.value)
      ? scriptIdsEl.value.split('\n').map(function (s) { return s.trim(); }).filter(Boolean)
      : [];
    const runEverythingEl = document.getElementById('runEverythingMode');
    const runEverythingMode = runEverythingEl ? runEverythingEl.checked : !!(fullConfig && (fullConfig.runEverythingMode ?? fullConfig.RunEverythingMode));
    const payload = {
      ai: {
        provider: els.provider.value.trim(),
        endpoint: els.endpoint.value.trim(),
        apiKey: els.apiKey.value.trim(),
        modelId: els.modelId.value.trim(),
        systemPrompt: els.systemPrompt.value.trim()
      },
      tavilyApiKey: (function () { var se = collectSkillEnv(); return (se && se.TAVILY_API_KEY) || (fullConfig && (fullConfig.tavilyApiKey ?? fullConfig.TavilyApiKey)) || ''; })(),
      skillEnv: collectSkillEnv(),
      mcpServers: (fullConfig && (fullConfig.mcpServers || fullConfig.McpServers)) || [],
      allowedPageScriptIds: allowedPageScriptIds,
      allowedCliCommands: (function () {
        var ta = document.getElementById('allowedCliCommands');
        if (ta && ta.value) return ta.value.split('\n').map(function (s) { return s.trim(); }).filter(Boolean);
        return getAllowedCliCommands();
      })(),
      disabledBuiltInPlugins: getDisabledBuiltIn(),
      runEverythingMode: runEverythingMode
    };
    const response = await fetch(API_URL, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });
    if (!response.ok) throw new Error('Failed to save');
    await loadConfig();
  } catch (err) {
    console.error(err);
    alert('保存失败');
  }
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
