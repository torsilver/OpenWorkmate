const API_URL = "http://localhost:8765/api/config";
const SKILLS_API_URL = "http://localhost:8765/api/skills";
const BUILTIN_TOOLS_URL = "http://localhost:8765/api/tools/builtin";

/** 判断是否为「无法连接后端」类错误，返回可展示给用户的文案；否则返回 null。 */
function messageForBackendUnreachable(err) {
  if (!err) return null;
  var msg = (err && err.message) ? String(err.message) : '';
  if (msg === 'Failed to fetch' || (err.name === 'TypeError' && msg && msg.indexOf('fetch') !== -1))
    return '无法连接到本地服务（localhost:8765），请确保已启动 OfficeCopilot.Server。';
  return null;
}

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

function getSttConfig() {
  var raw = fullConfig && (fullConfig.speechToText || fullConfig.SpeechToText);
  return raw && typeof raw === 'object' ? raw : null;
}

function fillSttForm(data) {
  var stt = (data && (data.speechToText || data.SpeechToText)) || getSttConfig();
  var endEl = document.getElementById('sttEditorEndpoint');
  var keyEl = document.getElementById('sttEditorApiKey');
  var langEl = document.getElementById('sttEditorLanguage');
  if (endEl) endEl.value = (stt && (stt.endpoint || stt.Endpoint)) ? (stt.endpoint || stt.Endpoint) : '';
  if (keyEl) keyEl.value = (stt && (stt.apiKey || stt.ApiKey)) ? (stt.apiKey || stt.ApiKey) : '';
  if (langEl) langEl.value = (stt && (stt.language || stt.Language)) ? (stt.language || stt.Language) : '';
  updateSttModelSummary();
}

function collectSttForm() {
  var endEl = document.getElementById('sttEditorEndpoint');
  var keyEl = document.getElementById('sttEditorApiKey');
  var langEl = document.getElementById('sttEditorLanguage');
  var endpoint = (endEl && endEl.value && endEl.value.trim()) || '';
  var apiKey = (keyEl && keyEl.value && keyEl.value.trim()) || '';
  var language = (langEl && langEl.value && langEl.value.trim()) || null;
  if (!endpoint && !apiKey && !language) return null;
  return { endpoint: endpoint || 'https://api.openai.com/v1', apiKey: apiKey, language: language || undefined };
}

function updateSttModelSummary() {
  var sum = document.getElementById('sttModelSummary');
  if (!sum) return;
  var stt = getSttConfig();
  var hasConfig = stt && (stt.endpoint || stt.Endpoint) && (stt.apiKey || stt.ApiKey);
  sum.textContent = hasConfig ? '语音转文字 (STT) 模型（已配置）' : '语音转文字 (STT) 模型';
}

function getOcrConfig() {
  var raw = fullConfig && (fullConfig.ocr || fullConfig.Ocr);
  return raw && typeof raw === 'object' ? raw : null;
}

function fillOcrForm(data) {
  var ocr = (data && (data.ocr || data.Ocr)) || getOcrConfig();
  var endEl = document.getElementById('ocrEditorEndpoint');
  var keyEl = document.getElementById('ocrEditorApiKey');
  var langEl = document.getElementById('ocrEditorLanguage');
  if (endEl) endEl.value = (ocr && (ocr.endpoint || ocr.Endpoint)) ? (ocr.endpoint || ocr.Endpoint) : '';
  if (keyEl) keyEl.value = (ocr && (ocr.apiKey || ocr.ApiKey)) ? (ocr.apiKey || ocr.ApiKey) : '';
  if (langEl) langEl.value = (ocr && (ocr.language || ocr.Language)) ? (ocr.language || ocr.Language) : '';
  updateOcrModelSummary();
}

function collectOcrForm() {
  var endEl = document.getElementById('ocrEditorEndpoint');
  var keyEl = document.getElementById('ocrEditorApiKey');
  var langEl = document.getElementById('ocrEditorLanguage');
  var endpoint = (endEl && endEl.value && endEl.value.trim()) || '';
  var apiKey = (keyEl && keyEl.value && keyEl.value.trim()) || '';
  var language = (langEl && langEl.value && langEl.value.trim()) || null;
  if (!endpoint && !apiKey && !language) return null;
  return { endpoint: endpoint, apiKey: apiKey, language: language || undefined };
}

function updateOcrModelSummary() {
  var sum = document.getElementById('ocrModelSummary');
  if (!sum) return;
  var ocr = getOcrConfig();
  var hasConfig = ocr && (ocr.endpoint || ocr.Endpoint) && (ocr.apiKey || ocr.ApiKey);
  sum.textContent = hasConfig ? 'OCR 模型（已配置）' : 'OCR 模型';
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
  var displayName = (dispEl && dispEl.value && dispEl.value.trim()) || '';
  var endpoint = (endEl && endEl.value && endEl.value.trim()) || '';
  var apiKey = (keyEl && keyEl.value) || '';
  var modelId = (modelEl && modelEl.value && modelEl.value.trim()) || '';
  if (!modelId) {
    alert('请填写模型 ID');
    return;
  }
  var list = getEmbeddingModels();
  var id = editingEmbeddingId || ('emb-' + Date.now());
  var entry = { id: id, displayName: displayName || modelId, source: 'Remote', endpoint: endpoint, apiKey: apiKey, modelId: modelId };
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
  doTestEmbeddingConnection(endpoint, apiKey, modelId, statusEl || undefined);
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
  fetch(url).then(async function (r) {
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
    fetch(baseUrl + '/api/memory/' + encodeURIComponent(id)).then(async function (r) {
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
  fetch(url, { method: method, headers: { 'Content-Type': 'application/json' }, body: body }).then(function (r) {
    if (!r.ok) return r.json().catch(function () { return {}; }).then(function (data) { throw new Error(data.message || '保存失败'); });
    closeMemoryEditor();
    loadMemoryList();
  }).catch(function (e) { alert('保存失败: ' + (e.message || e)); });
}

function deleteMemory(id) {
  if (!id || !confirm('确定删除这条记忆？')) return;
  var baseUrl = API_URL.replace('/api/config', '');
  fetch(baseUrl + '/api/memory/' + encodeURIComponent(id), { method: 'DELETE' }).then(function (r) {
    if (!r.ok) return r.json().catch(function () { return {}; }).then(function (data) { throw new Error(data.message || '删除失败'); });
    loadMemoryList();
  }).catch(function (e) { alert('删除失败: ' + (e.message || e)); });
}


function testEmbeddingConnection() {
  var endEl = document.getElementById('embeddingEditorEndpoint');
  var keyEl = document.getElementById('embeddingEditorApiKey');
  var modelEl = document.getElementById('embeddingEditorModelId');
  var endpoint = (endEl && endEl.value && endEl.value.trim()) || '';
  var apiKey = (keyEl && keyEl.value) || '';
  var modelId = (modelEl && modelEl.value && modelEl.value.trim()) || '';
  doTestEmbeddingConnection(endpoint, apiKey, modelId);
}

async function doTestEmbeddingConnection(endpoint, apiKey, modelId, statusEl) {
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
    var res = await fetch(baseUrl + '/api/config/test-embedding', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ endpoint: endpoint, apiKey: apiKey, modelId: modelId })
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

if (document.getElementById('saveSttModelBtn')) document.getElementById('saveSttModelBtn').addEventListener('click', function () { saveConfig(); });
if (document.getElementById('saveOcrModelBtn')) document.getElementById('saveOcrModelBtn').addEventListener('click', function () { saveConfig(); });

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
var currentCliScriptEnd = 'chrome';
var DEFAULT_CLI_COMMANDS = ['dir', 'echo', 'type', 'ping', 'systeminfo', 'ipconfig'];
var DEFAULT_PAGE_SCRIPTS = ['scroll_to_top', 'scroll_to_bottom', 'get_visible_text', 'get_page_title'];
var CLI_RUN_MODES = [
  { value: 'RunEverything', label: 'RunEverything（不校验、不弹确认）' },
  { value: 'AskEverytime', label: 'AskEverytime（每次执行前确认）' },
  { value: 'UseAllowList', label: 'UseAllowList（白名单内不弹，名单外确认）' }
];

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
    
    const response = await fetch(API_URL);
    if (!response.ok) {
      var errData = await response.json().catch(function () { return {}; });
      throw new Error(errData.message || '加载配置失败');
    }
    fullConfig = await response.json();
    const data = fullConfig;
    renderAiModelsList();
    renderCliScriptPerEndConfig();
    const mcps = data.mcpServers ?? data.McpServers;
    renderMcpList(mcps || []);
    await loadSkillEnvSection();
    await loadBuiltinTools();
    renderEmbeddingModelsList();
    fillSttForm(data);
    fillOcrForm(data);
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
    fillContextWindowForm(activePresetId, presets);
    fillSessionForm(activePresetId, presets);
    toggleContextWindowSection(!!activePresetId);
    toggleSessionSection(!!activePresetId);
    updatePresetRenameDeleteVisibility(activePresetId, presets);
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
  return {
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
    var perEnd = collectCliScriptPerEndPayload();
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
        var sessPayload = collectSessionFromForm();
        activePreset.session = activePreset.Session = sessPayload;
        var ctxPayload = collectContextWindowFromForm();
        if (ctxPayload) {
          var existing = activePreset.contextWindow || activePreset.ContextWindow || {};
          activePreset.contextWindow = activePreset.ContextWindow = Object.assign({}, existing, ctxPayload);
        }
      }
    }
    var sttToSave = collectSttForm();
    var ocrToSave = collectOcrForm();
    const payload = {
      ai: aiPayload,
      aiModels: aiModelsToSave,
      activeModelId: activeId,
      tavilyApiKey: (function () { var se = collectSkillEnv(); return (se && se.TAVILY_API_KEY) || (fullConfig && (fullConfig.tavilyApiKey ?? fullConfig.TavilyApiKey)) || ''; })(),
      skillEnv: collectSkillEnv(),
      mcpServers: (fullConfig && fullConfig.mcpServers) || (fullConfig && fullConfig.McpServers) || [],
      cliRunModeByClient: perEnd.cliRunModeByClient,
      allowedCliCommandsByClient: perEnd.allowedCliCommandsByClient,
      allowedPageScriptIdsByClient: perEnd.allowedPageScriptIdsByClient,
      disabledBuiltInPlugins: getDisabledBuiltIn(),
      embeddingModels: embeddingModelsToSave,
      activeEmbeddingModelId: activeEmbeddingModelId || undefined,
      speechToText: sttToSave || undefined,
      ocr: ocrToSave || undefined,
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
    if (!response.ok) {
      var data = await response.json().catch(function () { return {}; });
      throw new Error(data.message || '保存配置失败');
    }
    fullConfig = Object.assign({}, fullConfig || {}, { ai: payload.ai, aiModels: payload.aiModels, activeModelId: payload.activeModelId, tavilyApiKey: payload.tavilyApiKey, skillEnv: payload.skillEnv, mcpServers: payload.mcpServers, cliRunModeByClient: payload.cliRunModeByClient, allowedCliCommandsByClient: payload.allowedCliCommandsByClient, allowedPageScriptIdsByClient: payload.allowedPageScriptIdsByClient, disabledBuiltInPlugins: payload.disabledBuiltInPlugins, embeddingModels: payload.embeddingModels, activeEmbeddingModelId: payload.activeEmbeddingModelId, speechToText: payload.speechToText, ocr: payload.ocr, ragStorageType: payload.ragStorageType, ragStoragePath: payload.ragStoragePath, planConfirmation: payload.planConfirmation, activeContextPresetId: payload.activeContextPresetId, contextOptimizationPresets: payload.contextOptimizationPresets });
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
var requireConfirmForSensitiveToolsEl = document.getElementById('requireConfirmForSensitiveTools');
if (requireConfirmForSensitiveToolsEl) requireConfirmForSensitiveToolsEl.addEventListener('change', saveConfig);
var sensitiveToolIdsEl = document.getElementById('sensitiveToolIds');
if (sensitiveToolIdsEl) sensitiveToolIdsEl.addEventListener('input', debouncedSaveConfig);

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
  doTestAiConnection({ endpoint: endpoint, modelId: modelId, provider: provider, deploymentName: deploymentName, apiKey: apiKey }, statusEl || undefined);
}

async function doTestAiConnection(fields, statusEl) {
  statusEl = statusEl || document.getElementById('testAiStatus');
  if (!statusEl) return;
  var endpoint = (fields && fields.endpoint) || '';
  var modelId = (fields && fields.modelId) || '';
  var provider = (fields && fields.provider) || 'OpenAI';
  var deploymentName = (fields && fields.deploymentName) || '';
  var apiKey = (fields && fields.apiKey) || '';
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
    var res = await fetch(baseUrl + '/api/config/test-ai', {
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
    const res = await fetch(`${SKILLS_API_URL}/${id}`, { method: 'DELETE' });
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
    const res = await fetch(SKILLS_API_URL, {
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

function renderCliScriptPerEndConfig() {
  var tabsContainer = document.getElementById('cliScriptEndTabs');
  var contentContainer = document.getElementById('cliScriptPerEndConfig');
  if (!tabsContainer || !contentContainer) return;
  var modeByClient = fullConfig && (fullConfig.cliRunModeByClient || fullConfig.CliRunModeByClient) ? (fullConfig.cliRunModeByClient || fullConfig.CliRunModeByClient) : {};
  var cliByClient = fullConfig && (fullConfig.allowedCliCommandsByClient || fullConfig.AllowedCliCommandsByClient) ? (fullConfig.allowedCliCommandsByClient || fullConfig.AllowedCliCommandsByClient) : {};
  var scriptByClient = fullConfig && (fullConfig.allowedPageScriptIdsByClient || fullConfig.AllowedPageScriptIdsByClient) ? (fullConfig.allowedPageScriptIdsByClient || fullConfig.AllowedPageScriptIdsByClient) : {};
  var endKey = currentCliScriptEnd;
  var mode = (modeByClient[endKey] || 'UseAllowList').trim() || 'UseAllowList';
  var cliList = Array.isArray(cliByClient[endKey]) ? cliByClient[endKey] : [];
  var scriptList = Array.isArray(scriptByClient[endKey]) ? scriptByClient[endKey] : [];
  var cliSet = cliList.map(function (s) { return (s || '').toLowerCase(); }).filter(Boolean);
  var scriptSet = scriptList.map(function (s) { return (s || '').toLowerCase(); }).filter(Boolean);
  var userCliList = cliList.filter(function (c) { return DEFAULT_CLI_COMMANDS.indexOf((c || '').toLowerCase()) < 0; });
  var userScriptList = scriptList.filter(function (s) { return DEFAULT_PAGE_SCRIPTS.indexOf((s || '').toLowerCase()) < 0; });

  tabsContainer.innerHTML = CLI_SCRIPT_END_KEYS.map(function (k) {
    var label = CLI_SCRIPT_END_LABELS[k] || k;
    var active = k === endKey ? ' active' : '';
    return '<button type="button" class="cli-script-end-tab' + active + '" data-end="' + escapeAttr(k) + '">' + escapeHtml(label) + '</button>';
  }).join('');
  tabsContainer.querySelectorAll('.cli-script-end-tab').forEach(function (btn) {
    btn.addEventListener('click', function () {
      currentCliScriptEnd = this.getAttribute('data-end') || 'chrome';
      renderCliScriptPerEndConfig();
    });
  });

  var showAllowlist = mode === 'UseAllowList';
  var html = '<div class="skill-card" style="margin-bottom:16px;" data-cli-script-end="' + escapeAttr(endKey) + '">';
  html += '<div class="form-group" style="margin-top:8px;"><label style="display:block;margin-bottom:6px;">运行模式</label><select class="cli-run-mode-select" data-end="' + escapeAttr(endKey) + '" style="min-width:220px;">';
  CLI_RUN_MODES.forEach(function (opt) {
    html += '<option value="' + escapeAttr(opt.value) + '"' + (mode === opt.value ? ' selected' : '') + '>' + escapeHtml(opt.label) + '</option>';
  });
  html += '</select></div>';
  html += '<div class="cli-allowlist-section" data-end="' + escapeAttr(endKey) + '" style="margin-top:12px;padding-top:12px;border-top:1px solid var(--border);' + (showAllowlist ? '' : ' display:none;') + '">';
  html += '<p class="help-text" style="margin-bottom:8px;">命令白名单（run_command）</p>';
  html += '<div class="cli-allowlist-list" style="margin-bottom:12px;">';
  DEFAULT_CLI_COMMANDS.forEach(function (cmd) {
    var checked = cliSet.indexOf(cmd.toLowerCase()) >= 0 ? ' checked' : '';
    html += '<label class="cli-allowlist-row cli-builtin-row" style="display:flex;align-items:center;gap:8px;margin-bottom:4px;cursor:pointer;"><input type="checkbox" class="cli-default-cb" data-cmd="' + escapeAttr(cmd) + '"' + checked + '><span>' + escapeHtml(cmd) + '</span></label>';
  });
  html += '<hr style="margin:12px 0;border:0;border-top:1px solid var(--border);">';
  userCliList.forEach(function (cmd) {
    var checked = cliSet.indexOf((cmd || '').toLowerCase()) >= 0 ? ' checked' : '';
    html += '<div class="cli-allowlist-row cli-user-row" style="display:flex;align-items:center;gap:8px;margin-bottom:4px;"><label style="display:flex;align-items:center;gap:4px;cursor:pointer;flex:1;"><input type="checkbox" class="cli-user-cb" data-cmd="' + escapeAttr(cmd) + '"' + checked + '><span class="cli-user-cmd">' + escapeHtml(cmd) + '</span></label><button type="button" class="btn-secondary cli-user-delete" data-cmd="' + escapeAttr(cmd) + '" style="padding:2px 8px;font-size:12px;">删除</button></div>';
  });
  html += '</div><div style="display:flex;gap:8px;margin-bottom:16px;"><input type="text" class="cli-add-input" placeholder="添加命令名" style="flex:1;max-width:200px;"><button type="button" class="btn-secondary cli-add-btn">添加命令</button></div>';
  html += '<p class="help-text" style="margin-bottom:8px;">页面脚本白名单（run_page_script）</p>';
  html += '<div class="script-allowlist-list" style="margin-bottom:12px;">';
  DEFAULT_PAGE_SCRIPTS.forEach(function (sid) {
    var checked = scriptSet.indexOf(sid.toLowerCase()) >= 0 ? ' checked' : '';
    html += '<label class="script-allowlist-row script-builtin-row" style="display:flex;align-items:center;gap:8px;margin-bottom:4px;cursor:pointer;"><input type="checkbox" class="script-default-cb" data-script="' + escapeAttr(sid) + '"' + checked + '><span>' + escapeHtml(sid) + '</span></label>';
  });
  html += '<hr style="margin:12px 0;border:0;border-top:1px solid var(--border);">';
  userScriptList.forEach(function (sid) {
    var checked = scriptSet.indexOf((sid || '').toLowerCase()) >= 0 ? ' checked' : '';
    html += '<div class="script-allowlist-row script-user-row" style="display:flex;align-items:center;gap:8px;margin-bottom:4px;"><label style="display:flex;align-items:center;gap:4px;cursor:pointer;flex:1;"><input type="checkbox" class="script-user-cb" data-script="' + escapeAttr(sid) + '"' + checked + '><span class="script-user-id">' + escapeHtml(sid) + '</span></label><button type="button" class="btn-secondary script-user-delete" data-script="' + escapeAttr(sid) + '" style="padding:2px 8px;font-size:12px;">删除</button></div>';
  });
  html += '</div><div style="display:flex;gap:8px;"><input type="text" class="script-add-input" placeholder="添加 scriptId" style="flex:1;max-width:200px;"><button type="button" class="btn-secondary script-add-btn">添加脚本</button></div></div></div>';
  contentContainer.innerHTML = html;

  contentContainer.querySelector('.cli-run-mode-select').addEventListener('change', function () {
    var section = contentContainer.querySelector('.cli-allowlist-section');
    if (section) section.style.display = this.value === 'UseAllowList' ? '' : 'none';
    debouncedSaveConfig();
  });
  contentContainer.querySelectorAll('.cli-default-cb, .script-default-cb, .cli-user-cb, .script-user-cb').forEach(function (el) { el.addEventListener('change', debouncedSaveConfig); });
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
  var cliAddBtn = contentContainer.querySelector('.cli-add-btn');
  var cliAddInput = contentContainer.querySelector('.cli-add-input');
  var cliListEl = contentContainer.querySelector('.cli-allowlist-list');
  if (cliAddBtn && cliAddInput && cliListEl) {
    cliAddBtn.addEventListener('click', function () {
      var cmd = (cliAddInput.value || '').trim();
      if (!cmd) return;
      var cmdLower = cmd.toLowerCase();
      var already = false;
      contentContainer.querySelectorAll('.cli-user-row .cli-user-cmd').forEach(function (span) { if ((span.textContent || '').trim().toLowerCase() === cmdLower) already = true; });
      if (already) return;
      var div = document.createElement('div');
      div.className = 'cli-allowlist-row cli-user-row';
      div.setAttribute('style', 'display:flex;align-items:center;gap:8px;margin-bottom:4px;');
      div.innerHTML = '<label style="display:flex;align-items:center;gap:4px;cursor:pointer;flex:1;"><input type="checkbox" class="cli-user-cb" data-cmd="' + escapeAttr(cmd) + '" checked><span class="cli-user-cmd">' + escapeHtml(cmd) + '</span></label><button type="button" class="btn-secondary cli-user-delete" data-cmd="' + escapeAttr(cmd) + '" style="padding:2px 8px;font-size:12px;">删除</button>';
      var hr = cliListEl.querySelector('hr');
      if (hr && hr.nextSibling) cliListEl.insertBefore(div, hr.nextSibling);
      else cliListEl.appendChild(div);
      div.querySelector('.cli-user-cb').addEventListener('change', debouncedSaveConfig);
      div.querySelector('.cli-user-delete').addEventListener('click', function () { div.remove(); debouncedSaveConfig(); });
      cliAddInput.value = '';
      debouncedSaveConfig();
    });
  }
  var scriptAddBtn = contentContainer.querySelector('.script-add-btn');
  var scriptAddInput = contentContainer.querySelector('.script-add-input');
  var scriptListEl = contentContainer.querySelector('.script-allowlist-list');
  if (scriptAddBtn && scriptAddInput && scriptListEl) {
    scriptAddBtn.addEventListener('click', function () {
      var sid = (scriptAddInput.value || '').trim();
      if (!sid) return;
      var sidLower = sid.toLowerCase();
      var already = false;
      contentContainer.querySelectorAll('.script-user-row .script-user-id').forEach(function (span) { if ((span.textContent || '').trim().toLowerCase() === sidLower) already = true; });
      if (already) return;
      var div = document.createElement('div');
      div.className = 'script-allowlist-row script-user-row';
      div.setAttribute('style', 'display:flex;align-items:center;gap:8px;margin-bottom:4px;');
      div.innerHTML = '<label style="display:flex;align-items:center;gap:4px;cursor:pointer;flex:1;"><input type="checkbox" class="script-user-cb" data-script="' + escapeAttr(sid) + '" checked><span class="script-user-id">' + escapeHtml(sid) + '</span></label><button type="button" class="btn-secondary script-user-delete" data-script="' + escapeAttr(sid) + '" style="padding:2px 8px;font-size:12px;">删除</button>';
      var hr = scriptListEl.querySelector('hr');
      if (hr && hr.nextSibling) scriptListEl.insertBefore(div, hr.nextSibling);
      else scriptListEl.appendChild(div);
      div.querySelector('.script-user-cb').addEventListener('change', debouncedSaveConfig);
      div.querySelector('.script-user-delete').addEventListener('click', function () { div.remove(); debouncedSaveConfig(); });
      scriptAddInput.value = '';
      debouncedSaveConfig();
    });
  }
}

function collectCliScriptPerEndPayload() {
  var cliRunModeByClient = {};
  var allowedCliCommandsByClient = {};
  var allowedPageScriptIdsByClient = {};
  var container = document.getElementById('cliScriptPerEndConfig');
  var modeByClient = fullConfig && (fullConfig.cliRunModeByClient || fullConfig.CliRunModeByClient) ? (fullConfig.cliRunModeByClient || fullConfig.CliRunModeByClient) : {};
  var cliByClient = fullConfig && (fullConfig.allowedCliCommandsByClient || fullConfig.AllowedCliCommandsByClient) ? (fullConfig.allowedCliCommandsByClient || fullConfig.AllowedCliCommandsByClient) : {};
  var scriptByClient = fullConfig && (fullConfig.allowedPageScriptIdsByClient || fullConfig.AllowedPageScriptIdsByClient) ? (fullConfig.allowedPageScriptIdsByClient || fullConfig.AllowedPageScriptIdsByClient) : {};
  CLI_SCRIPT_END_KEYS.forEach(function (endKey) {
    if (endKey !== currentCliScriptEnd) {
      cliRunModeByClient[endKey] = (modeByClient[endKey] || 'UseAllowList').trim() || 'UseAllowList';
      allowedCliCommandsByClient[endKey] = Array.isArray(cliByClient[endKey]) ? cliByClient[endKey].slice() : [];
      allowedPageScriptIdsByClient[endKey] = Array.isArray(scriptByClient[endKey]) ? scriptByClient[endKey].slice() : [];
      return;
    }
    if (!container) {
      cliRunModeByClient[endKey] = (modeByClient[endKey] || 'UseAllowList').trim() || 'UseAllowList';
      allowedCliCommandsByClient[endKey] = Array.isArray(cliByClient[endKey]) ? cliByClient[endKey].slice() : [];
      allowedPageScriptIdsByClient[endKey] = Array.isArray(scriptByClient[endKey]) ? scriptByClient[endKey].slice() : [];
      return;
    }
    var modeEl = container.querySelector('.cli-run-mode-select[data-end="' + endKey + '"]');
    cliRunModeByClient[endKey] = (modeEl && modeEl.value) ? modeEl.value : 'UseAllowList';
    var cliList = [];
    container.querySelectorAll('.cli-default-cb:checked').forEach(function (cb) {
      var cmd = cb.getAttribute('data-cmd');
      if (cmd) cliList.push(cmd);
    });
    container.querySelectorAll('.cli-user-row .cli-user-cb:checked').forEach(function (cb) {
      var cmd = (cb.getAttribute('data-cmd') || '').trim();
      if (cmd) cliList.push(cmd);
    });
    allowedCliCommandsByClient[endKey] = cliList;
    var scriptList = [];
    container.querySelectorAll('.script-default-cb:checked').forEach(function (cb) {
      var script = cb.getAttribute('data-script');
      if (script) scriptList.push(script);
    });
    container.querySelectorAll('.script-user-row .script-user-cb:checked').forEach(function (cb) {
      var script = (cb.getAttribute('data-script') || '').trim();
      if (script) scriptList.push(script);
    });
    allowedPageScriptIdsByClient[endKey] = scriptList;
  });
  return { cliRunModeByClient: cliRunModeByClient, allowedCliCommandsByClient: allowedCliCommandsByClient, allowedPageScriptIdsByClient: allowedPageScriptIdsByClient };
}

// ───── Boot ─────
document.addEventListener('DOMContentLoaded', function () {
  loadConfig();
});

// ───── MCP ─────
async function loadBuiltinTools() {
  const el = document.getElementById('builtinToolsList');
  if (!el) return;
  var disabledSet = (getDisabledBuiltIn() || []).map(function (s) { return (s || '').toLowerCase(); }).filter(Boolean);
  try {
    const res = await fetch(BUILTIN_TOOLS_URL);
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
      var cardBody = `<div class="skill-header"><div class="skill-title">${escapeHtml(name)}${disabled ? ' <span style="color:#94a3b8;font-weight:normal;">(已停用)</span>' : ''}</div><div><button type="button" class="btn-secondary builtin-toggle-btn" style="padding:4px 12px;font-size:12px;">${disabled ? '启用' : '停用'}</button></div></div><div class="skill-desc">${escapeHtml(desc)}</div>`;
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

const SALES_DB_MCP_PRESET = {
  id: 'sales-db-mcp',
  name: 'SalesDbMcp',
  command: 'dotnet',
  args: ['run', '--project', 'sales-db-mcp/SalesDbMcp.csproj']
};

const OCR_MCP_PRESET = {
  id: 'ocr-mcp',
  name: 'OCR',
  command: 'uvx',
  args: ['mcp-ocr']
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

document.getElementById('addOcrMcpBtn').addEventListener('click', async () => {
  if (!fullConfig) {
    alert('请先等待配置加载完成。');
    return;
  }
  const list = getMcpServers();
  const exists = list.some(s => (s.id || s.Id) === OCR_MCP_PRESET.id);
  if (exists) {
    window.editMcp(OCR_MCP_PRESET.id);
    return;
  }
  if (!fullConfig.mcpServers) fullConfig.mcpServers = list.slice();
  fullConfig.mcpServers.push({ ...OCR_MCP_PRESET });
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
    const res = await fetch(BASE_URL() + '/api/scheduled-tasks/' + encodeURIComponent(id));
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
      if (!res.ok) throw new Error((await res.json().catch(function () { return {}; })).message || '更新失败');
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
    const res = await fetch(BASE_URL() + '/api/scheduled-tasks/' + encodeURIComponent(id), { method: 'DELETE' });
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
    const res = await fetch(url);
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
    const res = await fetch(BASE_URL() + '/api/plans/' + encodeURIComponent(id), { method: 'DELETE' });
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
    const res = await fetch(BASE_URL() + '/api/accurate-data');
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
    const res = await fetch(BASE_URL() + '/api/accurate-data/' + encodeURIComponent(id), { method: 'DELETE' });
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
