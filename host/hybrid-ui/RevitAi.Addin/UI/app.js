const state = {
  host: null,
  agent: null,
  tools: [],
  attachments: [],
  toolFilter: "all",
  busy: false,
  runId: null,
  sessions: []
};

const dom = {
  messages: document.getElementById("messages"),
  welcome: document.getElementById("welcome"),
  composer: document.getElementById("composer"),
  prompt: document.getElementById("prompt"),
  send: document.getElementById("send"),
  sendLabel: document.getElementById("send-label"),
  connectionChip: document.getElementById("connection-chip"),
  connectionText: document.getElementById("connection-text"),
  modelLabel: document.getElementById("model-label"),
  apiStatus: document.getElementById("api-status"),
  toolsButton: document.getElementById("tools-button"),
  toolsDrawer: document.getElementById("tools-drawer"),
  toolsClose: document.getElementById("tools-close"),
  drawerScrim: document.getElementById("drawer-scrim"),
  toolCount: document.getElementById("tool-count"),
  toolSearch: document.getElementById("tool-search"),
  toolFilters: document.getElementById("tool-filters"),
  toolList: document.getElementById("tool-list"),
  settingsButton: document.getElementById("settings-button"),
  settingsModal: document.getElementById("settings-modal"),
  settingsClose: document.getElementById("settings-close"),
  settingsForm: document.getElementById("settings-form"),
  settingsStatus: document.getElementById("settings-status"),
  apiUrl: document.getElementById("api-url"),
  apiKey: document.getElementById("api-key"),
  apiModel: document.getElementById("api-model"),
  testApi: document.getElementById("test-api"),
  attachmentInput: document.getElementById("attachment-input"),
  attachButton: document.getElementById("attach-button"),
  attachmentList: document.getElementById("attachment-list"),
  buildLabel: document.getElementById("build-label"),
  skillList: document.getElementById("skill-list"),
  sessionsButton: document.getElementById("sessions-button"),
  sessionsDrawer: document.getElementById("sessions-drawer"),
  sessionsScrim: document.getElementById("sessions-scrim"),
  sessionsClose: document.getElementById("sessions-close"),
  sessionsCount: document.getElementById("sessions-count"),
  sessionsList: document.getElementById("sessions-list"),
  sessionsNew: document.getElementById("sessions-new"),
  sessionsSave: document.getElementById("sessions-save"),
  sessionsExport: document.getElementById("sessions-export")
};

const toolCards = new Map();
let activeAssistant = null;

function post(type, payload = {}) {
  if (window.chrome?.webview) {
    window.chrome.webview.postMessage({ type, ...payload });
  }
}

function escapeHtml(value) {
  return String(value ?? "").replace(/[&<>"']/g, char => ({
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    "\"": "&quot;",
    "'": "&#39;"
  })[char]);
}

function renderMarkdown(value) {
  const source = String(value ?? "");
  const codeBlocks = [];
  let text = source.replace(/```([A-Za-z0-9_+-]*)\n?([\s\S]*?)```/g, (_, language, code) => {
    const token = `@@CODE_BLOCK_${codeBlocks.length}@@`;
    codeBlocks.push(
      `<pre><code data-language="${escapeHtml(language)}">${escapeHtml(code.trim())}</code></pre>`
    );
    return token;
  });

  text = escapeHtml(text);
  text = text.replace(/`([^`\n]+)`/g, "<code>$1</code>");
  text = text.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
  text = text
    .replace(/^###\s+(.+)$/gm, "<strong>$1</strong>")
    .replace(/^##\s+(.+)$/gm, "<strong>$1</strong>")
    .replace(/^#\s+(.+)$/gm, "<strong>$1</strong>");
  text = text.replace(/^\s*[-*]\s+(.+)$/gm, "• $1");

  const paragraphs = text
    .split(/\n{2,}/)
    .map(paragraph => `<p>${paragraph.replace(/\n/g, "<br>")}</p>`)
    .join("");

  return paragraphs.replace(/@@CODE_BLOCK_(\d+)@@/g, (_, index) => codeBlocks[Number(index)] ?? "");
}

function hideWelcome() {
  dom.welcome?.classList.add("hidden");
}

function renderAttachments() {
  const items = state.attachments ?? [];
  dom.attachmentList.hidden = items.length === 0;
  dom.attachmentList.innerHTML = items.map(item => `
    <div class="attachment-chip">
      <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m9 12 6.5-6.5a3 3 0 0 1 4.2 4.2L11 18.4a5 5 0 0 1-7.1-7.1l8.2-8.2"/></svg>
      <span title="${escapeHtml(item.fileName)}">${escapeHtml(item.fileName)}</span>
    </div>
  `).join("");
}

function scrollToBottom() {
  dom.messages.scrollTop = dom.messages.scrollHeight;
}

function addMessage(role, text, css = "") {
  hideWelcome();
  const article = document.createElement("article");
  article.className = `message ${role} ${css}`.trim();
  article.innerHTML = `
    <div class="message-label">${role === "user" ? "你" : "Revit AI"}</div>
    <div class="message-body">${renderMarkdown(text)}</div>
  `;
  dom.messages.appendChild(article);
  scrollToBottom();
  return article;
}

function addAssistantPlaceholder() {
  hideWelcome();
  const article = document.createElement("article");
  article.className = "message assistant";
  article.innerHTML = `
    <div class="message-label">Revit AI</div>
    <div class="message-body">
      <span class="typing"><i></i><i></i><i></i></span>
    </div>
  `;
  dom.messages.appendChild(article);
  scrollToBottom();
  return article;
}

function finishAssistantPlaceholder() {
  if (!activeAssistant) return;
  activeAssistant.querySelector(".message-body").innerHTML = renderMarkdown("已取消本次生成。");
  activeAssistant = null;
}

function addProgress(data) {
  if (data.kind === "round_started") {
    const line = document.createElement("div");
    line.className = "progress-line";
    line.textContent = data.message;
    dom.messages.appendChild(line);
    scrollToBottom();
    return;
  }

  if (data.kind === "tool_started" && data.tool) {
    const card = document.createElement("details");
    card.className = "tool-card running";
    card.dataset.toolId = data.tool.id;
    card.open = true;
    card.innerHTML = `
      <summary>
        <span>${escapeHtml(data.tool.name)}</span>
        <span>执行中</span>
      </summary>
      <pre class="payload">${escapeHtml(JSON.stringify(data.tool.arguments ?? {}, null, 2))}</pre>
    `;
    dom.messages.appendChild(card);
    toolCards.set(data.tool.id, card);
    scrollToBottom();
    return;
  }

  if (data.kind === "tool_completed" && data.tool) {
    const card = toolCards.get(data.tool.id);
    if (!card) return;
    updateToolCard(card, {
      toolCallId: data.tool.id,
      toolName: data.tool.name,
      success: data.result?.success ?? false,
      durationMs: data.result?.durationMs ?? 0,
      payload: data.result?.payload ?? {},
      errorCode: data.result?.errorCode,
      errorMessage: data.result?.errorMessage
    });
  }
}

function updateToolCard(card, item) {
  card.classList.toggle("success", item.success === true);
  card.classList.toggle("failed", item.success === false);
  card.classList.remove("running");
  const duration = Math.round(item.durationMs ?? 0);
  card.querySelector("summary").innerHTML = `
    <span>${escapeHtml(item.toolName)}</span>
    <span>${item.success ? "完成" : "失败"} · ${duration} ms</span>
  `;
  card.querySelector(".payload").textContent = JSON.stringify(item, null, 2);
  card.open = item.success === false;
  scrollToBottom();
}

function setBusy(value) {
  state.busy = value;
  dom.send.classList.toggle("stop", value);
  dom.send.innerHTML = value
    ? '<span class="stop-square"></span><span id="send-label">停止</span>'
    : '<svg class="send-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="m5 12 14-7-4 14-3-6-7-1Z"/></svg><span id="send-label">发送</span>';
  dom.sendLabel = document.getElementById("send-label");
}

function resetChat() {
  toolCards.clear();
  activeAssistant = null;
  state.runId = null;
  setBusy(false);
  dom.messages.innerHTML = `
    <section id="welcome" class="welcome">
      <div class="welcome-mark">AI</div>
      <h1>今天想处理什么？</h1>
      <p>可以直接查询模型，也可以让我创建标高、墙体、房间、视图或执行参数修改。</p>
      <div class="quick-actions">
        <button type="button" data-prompt="查看当前 Revit 文档状态">查看文档状态</button>
        <button type="button" data-prompt="列出当前项目所有标高">列出全部标高</button>
        <button type="button" data-prompt="列出当前视图和可见构件信息">分析当前视图</button>
        <button type="button" data-prompt="读取当前选中构件的参数">读取选中构件参数</button>
      </div>
    </section>
  `;
  dom.welcome = document.getElementById("welcome");
  bindQuickActions();
}

function openDrawer() {
  dom.toolsDrawer.classList.add("open");
  dom.drawerScrim.classList.add("open");
  dom.toolsDrawer.setAttribute("aria-hidden", "false");
  dom.toolSearch.focus();
}

function closeDrawer() {
  dom.toolsDrawer.classList.remove("open");
  dom.drawerScrim.classList.remove("open");
  dom.toolsDrawer.setAttribute("aria-hidden", "true");
}

function openSettings() {
  dom.settingsModal.classList.add("open");
  dom.settingsModal.setAttribute("aria-hidden", "false");
  dom.apiUrl.focus();
}

function closeSettings() {
  dom.settingsModal.classList.remove("open");
  dom.settingsModal.setAttribute("aria-hidden", "true");
}

function renderKnowledge() {
  const configured = state.agent?.configured === true;
  const toolsReady = state.agent?.toolsReady === true;
  const model = state.agent?.model || "deepseek-flash";

  dom.connectionChip.classList.toggle("online", configured && toolsReady);
  dom.connectionChip.classList.toggle("error", Boolean(state.agent) && !configured);
  dom.connectionText.textContent = configured
    ? (toolsReady ? "已连接" : "待初始化")
    : "未配置";
  dom.modelLabel.textContent = model;
  dom.apiStatus.textContent = configured
    ? (toolsReady ? `${model} · 本地直连` : "模型已连接，等待 Revit 工具初始化")
    : "请先配置模型 API";

  if (state.agent) {
    dom.apiUrl.value = state.agent.apiUrl || "https://api.deepseek.com/v1";
    dom.apiModel.value = model;
    dom.apiKey.placeholder = state.agent.hasApiKey
      ? "已保存加密 Key；留空保持不变"
      : "请输入 API Key";
  }

  if (dom.buildLabel) {
    dom.buildLabel.textContent = state.agent?.build || "v9.2-external-rename";
  }
}

function renderTools() {
  state.tools = state.agent?.tools ?? [];
  dom.toolCount.textContent = `${state.tools.length} 个工具`;

  const sets = ["all", ...new Set(state.tools.map(item => item.toolSet).filter(Boolean))];
  dom.toolFilters.innerHTML = sets.map(set => `
    <button type="button" class="${state.toolFilter === set ? "active" : ""}" data-filter="${escapeHtml(set)}">
      ${set === "all" ? "全部" : escapeHtml(set)}
    </button>
  `).join("");

  dom.toolFilters.querySelectorAll("button").forEach(button => {
    button.addEventListener("click", () => {
      state.toolFilter = button.dataset.filter;
      renderTools();
    });
  });

  const query = dom.toolSearch.value.trim().toLowerCase();
  const visible = state.tools.filter(item => {
    const matchesSet = state.toolFilter === "all" || item.toolSet === state.toolFilter;
    const haystack = `${item.name} ${item.description} ${item.toolSet}`.toLowerCase();
    return matchesSet && (!query || haystack.includes(query));
  });

  dom.toolList.innerHTML = visible.map(item => `
    <article class="tool-item">
      <strong>
        ${escapeHtml(item.name)}
        <span class="tool-tag">${escapeHtml(item.toolSet)}</span>
        ${item.mutating ? '<span class="tool-tag">会修改模型</span>' : ""}
      </strong>
      <p>${escapeHtml(item.description)}</p>
    </article>
  `).join("") || '<p class="progress-line">没有匹配的工具。</p>';

  renderSkills();
}

function renderSkills() {
  if (!dom.skillList) return;
  const skills = state.agent?.skills ?? [];
  dom.skillList.innerHTML = skills.length
    ? skills.map(skill => {
        const variables = (skill.variables ?? []).join(", ");
        return `
    <article class="skill-item" data-skill="${escapeHtml(skill.name)}">
      <strong>${escapeHtml(skill.displayName || skill.name)}</strong>
      <span>${escapeHtml(skill.category || "技能")} · ${skill.steps ?? 0} 步</span>
      <p>${escapeHtml(skill.description || "")}</p>
      <code>${escapeHtml(skill.name)}${variables ? " · 变量: " + escapeHtml(variables) : ""}</code>
    </article>`;
      }).join("")
    : '<p class="progress-line">没有可用技能。</p>';

  dom.skillList.querySelectorAll(".skill-item").forEach(item => {
    item.addEventListener("click", () => {
      dom.prompt.value = `使用 execute_skill 工具执行技能 "${item.dataset.skill}"。`;
      closeDrawer();
      dom.prompt.focus();
    });
  });
}

function openSessions() {
  dom.sessionsDrawer.classList.add("open");
  dom.sessionsScrim.classList.add("open");
  dom.sessionsDrawer.setAttribute("aria-hidden", "false");
  post("sessions.list");
}

function closeSessions() {
  dom.sessionsDrawer.classList.remove("open");
  dom.sessionsScrim.classList.remove("open");
  dom.sessionsDrawer.setAttribute("aria-hidden", "true");
}

function renderSessions(items, agent) {
  if (Array.isArray(items)) state.sessions = items;
  if (agent) state.agent = agent;
  if (dom.sessionsCount) {
    dom.sessionsCount.textContent = `${state.sessions.length} 个会话`;
  }

  if (!dom.sessionsList) return;
  dom.sessionsList.innerHTML = state.sessions.length
    ? state.sessions.map(item => `
      <article class="session-item${item.active ? " active" : ""}" data-session="${escapeHtml(item.sessionId)}">
        <div class="session-item-head">
          <strong>${escapeHtml(item.title || "新对话")}</strong>
          <button type="button" class="session-delete" data-delete="${escapeHtml(item.sessionId)}" title="删除会话">×</button>
        </div>
        <span>${escapeHtml(item.updatedAt || "")} · ${item.messageCount ?? 0} 条消息 · ${item.roundCount ?? 0} 轮${item.hasSummary ? " · 含摘要" : ""}</span>
      </article>`).join("")
    : '<p class="progress-line">还没有保存的会话。</p>';

  dom.sessionsList.querySelectorAll(".session-item").forEach(node => {
    node.addEventListener("click", event => {
      if (event.target.closest(".session-delete")) return;
      post("sessions.open", { sessionId: node.dataset.session });
    });
  });
  dom.sessionsList.querySelectorAll(".session-delete").forEach(button => {
    button.addEventListener("click", event => {
      event.stopPropagation();
      post("sessions.delete", { sessionId: button.dataset.delete });
    });
  });
}

function bindQuickActions() {
  document.querySelectorAll(".quick-actions button").forEach(button => {
    button.addEventListener("click", () => {
      dom.prompt.value = button.dataset.prompt ?? "";
      dom.prompt.focus();
    });
  });
}

dom.composer.addEventListener("submit", event => {
  event.preventDefault();
  const text = dom.prompt.value.trim();
  if (!text) return;

  if (text === "/skills") {
    addMessage("user", text);
    dom.prompt.value = "";
    resizePrompt();
    const skills = state.agent?.skills ?? [];
    addMessage(
      "assistant",
      skills.length
        ? "可用技能：\n" + skills.map(skill =>
            `- ${skill.displayName || skill.name}（${skill.name}）`
            + `${skill.steps ? ` · ${skill.steps} 步` : ""}\n  ${skill.description || ""}`
          ).join("\n")
        : "当前没有可用技能。"
    );
    return;
  }

  const skillMatch = /^\/skill\s+(\S+)\s*(.*)$/i.exec(text);
  if (skillMatch) {
    const variables = skillMatch[2]?.trim();
    dom.prompt.value = `使用 execute_skill 工具执行技能 "${skillMatch[1]}"`
      + (variables ? `，变量：${variables}` : "")
      + "。";
    resizePrompt();
    dom.prompt.focus();
    return;
  }

  // Never swallow the prompt. When a task is still winding down the host
  // cancels it, finishes the current tool call and starts this one after it.
  if (state.busy) {
    addProgress({
      kind: "round_started",
      message: "上一个任务正在收尾，指令已排队，结束后自动开始…"
    });
  }

  addMessage("user", text);
  dom.prompt.value = "";
  resizePrompt();
  activeAssistant = addAssistantPlaceholder();
  setBusy(true);
  post("chat", { text });
});

dom.prompt.addEventListener("keydown", event => {
  if (event.key === "Enter" && !event.shiftKey) {
    event.preventDefault();
    dom.composer.requestSubmit();
  }
});

dom.prompt.addEventListener("input", resizePrompt);

dom.attachButton.addEventListener("click", () => {
  if (!state.busy) dom.attachmentInput.click();
});

dom.sessionsButton.addEventListener("click", openSessions);
dom.sessionsClose.addEventListener("click", closeSessions);
dom.sessionsScrim.addEventListener("click", closeSessions);
dom.sessionsNew.addEventListener("click", () => post("sessions.new"));
dom.sessionsSave.addEventListener("click", () => post("sessions.save"));
dom.sessionsExport.addEventListener("click", () => post("sessions.export"));

dom.attachmentInput.addEventListener("change", async () => {
  const files = Array.from(dom.attachmentInput.files ?? []);
  dom.attachmentInput.value = "";
  for (const file of files) {
    if (file.size > 50 * 1024 * 1024) {
      addMessage("assistant", `附件过大：${file.name}，单个文件最大 50 MB。`, "error");
      continue;
    }

    try {
      const dataUrl = await new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => resolve(String(reader.result ?? ""));
        reader.onerror = () => reject(reader.error ?? new Error("读取失败"));
        reader.readAsDataURL(file);
      });
      post("attachment.add", {
        fileName: file.name,
        contentType: file.type || "application/octet-stream",
        dataUrl,
        size: file.size
      });
    } catch (error) {
      addMessage("assistant", `读取附件失败：${file.name}。`, "error");
    }
  }
});

function resizePrompt() {
  dom.prompt.style.height = "auto";
  dom.prompt.style.height = `${Math.min(dom.prompt.scrollHeight, 150)}px`;
}

document.getElementById("new-chat").addEventListener("click", () => {
  // The host cancels (and waits for) the running task before clearing history.
  post("reset");
  resetChat();
});

dom.connectionChip.addEventListener("click", openSettings);
dom.settingsButton.addEventListener("click", openSettings);
dom.settingsClose.addEventListener("click", closeSettings);
dom.settingsModal.addEventListener("click", event => {
  if (event.target === dom.settingsModal) closeSettings();
});

dom.toolsButton.addEventListener("click", openDrawer);
dom.toolsClose.addEventListener("click", closeDrawer);
dom.drawerScrim.addEventListener("click", closeDrawer);
dom.toolSearch.addEventListener("input", renderTools);

dom.settingsForm.addEventListener("submit", event => {
  event.preventDefault();
  dom.settingsStatus.textContent = "正在保存...";
  post("settings.save", {
    apiUrl: dom.apiUrl.value.trim(),
    apiKey: dom.apiKey.value.trim(),
    model: dom.apiModel.value.trim()
  });
});

dom.testApi.addEventListener("click", () => {
  dom.settingsStatus.textContent = "正在连接模型 API...";
  post("settings.test");
});

window.chrome?.webview?.addEventListener("message", event => {
  const data = event.data;
  if (!data || typeof data !== "object") return;

  if (data.type === "chat.started") {
    state.runId = data.runId ?? state.runId;
    setBusy(true);
    if (!activeAssistant) activeAssistant = addAssistantPlaceholder();
    return;
  }

  // A superseded run must not write into the new conversation or clear the
  // busy flag of the run that replaced it.
  if (data.runId != null && data.runId !== state.runId) {
    return;
  }

  if (data.type === "host.status") {
    state.host = data.host ?? state.host;
    state.agent = data.agent;
    renderKnowledge();
    renderTools();
  }

  if (data.type === "chat.progress") {
    addProgress(data);
  }

  if (data.type === "chat.toolResults") {
    (data.items ?? []).forEach(item => {
      const existing = toolCards.get(item.toolCallId);
      if (existing) updateToolCard(existing, item);
    });
  }

  if (data.type === "chat.attachments") {
    state.attachments = data.items ?? [];
    renderAttachments();
  }

  if (data.type === "chat.response") {
    if (activeAssistant) {
      activeAssistant.querySelector(".message-body").innerHTML = renderMarkdown(data.message);
      activeAssistant = null;
      scrollToBottom();
    } else {
      addMessage("assistant", data.message);
    }
  }

  if (data.type === "chat.cancelled") {
    finishAssistantPlaceholder();
  }

  if (data.type === "chat.finished") {
    setBusy(false);
  }

  if (data.type === "chat.error") {
    if (activeAssistant) {
      activeAssistant.remove();
      activeAssistant = null;
    }
    addMessage("assistant", data.message, "error");
    setBusy(false);
  }

  if (data.type === "chat.reset") {
    resetChat();
  }

  if (data.type === "settings.saved") {
    state.agent = data.agent;
    dom.apiKey.value = "";
    dom.settingsStatus.textContent = "已保存，可以开始使用。";
    renderKnowledge();
    renderTools();
  }

  if (data.type === "settings.testing") {
    dom.settingsStatus.textContent = "正在连接模型 API...";
  }

  if (data.type === "settings.tested") {
    state.agent = data.agent;
    dom.settingsStatus.textContent = `连接成功：${data.result?.response ?? "OK"}`;
    renderKnowledge();
    renderTools();
  }

  if (data.type === "settings.error") {
    dom.settingsStatus.textContent = `连接失败：${data.message}`;
  }

  if (data.type === "sessions.list" || data.type === "sessions.updated") {
    renderSessions(data.items, data.agent);
    if (data.message === "已新建会话。" || data.message === "已打开会话。") {
      resetChat();
      const title = data.agent?.sessionTitle ?? "新对话";
      const count = data.agent?.historyMessages ?? 0;
      addMessage("assistant", `${data.message}当前会话：${title}（${count} 条消息）`);
    } else if (data.message) {
      addMessage("assistant", data.message);
    }
  }

  if (data.type === "sessions.notice") {
    addMessage("assistant", data.message ?? "", data.success ? "" : "error");
  }
});

bindQuickActions();
renderAttachments();
renderKnowledge();
post("get.status");
