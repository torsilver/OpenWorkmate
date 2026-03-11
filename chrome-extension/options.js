const API_URL = "http://localhost:8765/api/config";
const SKILLS_API_URL = "http://localhost:8765/api/skills";
const BUILTIN_TOOLS_URL = "http://localhost:8765/api/tools/builtin";

const els = {
  provider: document.getElementById('provider'),
  endpoint: document.getElementById('endpoint'),
  apiKey: document.getElementById('apiKey'),
  modelId: document.getElementById('modelId'),
  systemPrompt: document.getElementById('systemPrompt'),
  allowedPageScriptIds: document.getElementById('allowedPageScriptIds'),
  saveBtn: document.getElementById('saveBtn'),
  statusMessage: document.getElementById('statusMessage'),
  
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
  });
});

// ───── AI Config ─────
let fullConfig = null;

async function loadConfig() {
  try {
    els.saveBtn.disabled = true;
    els.saveBtn.textContent = '加载中...';
    
    const response = await fetch(API_URL);
    if (!response.ok) throw new Error('Failed to load');
    
    fullConfig = await response.json();
    const data = fullConfig;
    // 兼容后端 camelCase 与 PascalCase
    const ai = data && (data.ai || data.AI);
    if (ai) {
      els.provider.value = ai.provider ?? ai.Provider ?? '';
      els.endpoint.value = ai.endpoint ?? ai.Endpoint ?? '';
      els.apiKey.value = ai.apiKey ?? ai.ApiKey ?? '';
      els.modelId.value = ai.modelId ?? ai.ModelId ?? '';
      els.systemPrompt.value = ai.systemPrompt ?? ai.SystemPrompt ?? '';
    }
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
    await loadBuiltinTools();
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
    const payload = {
      ai: {
        provider: els.provider.value.trim(),
        endpoint: els.endpoint.value.trim(),
        apiKey: els.apiKey.value.trim(),
        modelId: els.modelId.value.trim(),
        systemPrompt: els.systemPrompt.value.trim()
      },
      mcpServers: (fullConfig && fullConfig.mcpServers) || (fullConfig && fullConfig.McpServers) || [],
      allowedPageScriptIds: allowedPageScriptIds,
      allowedCliCommands: allowedCliCommands,
      disabledBuiltInPlugins: getDisabledBuiltIn(),
      runEverythingMode: runEverythingMode
    };
    const response = await fetch(API_URL, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });
    if (!response.ok) throw new Error('Failed to save');
    fullConfig = { ai: payload.ai, mcpServers: payload.mcpServers, allowedPageScriptIds: payload.allowedPageScriptIds, allowedCliCommands: payload.allowedCliCommands, disabledBuiltInPlugins: payload.disabledBuiltInPlugins, runEverythingMode: payload.runEverythingMode };
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
