chrome.sidePanel
  .setPanelBehavior({ openPanelOnActionClick: true })
  .catch(console.error);

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (!message || typeof message.type !== "string") return undefined;
  if (message.type === "OPEN_MEETING_LIVE_TAB") {
    const sid = (message.sessionId || "").trim();
    const q = sid ? `?sessionId=${encodeURIComponent(sid)}` : "";
    const url = chrome.runtime.getURL("meeting-live.html") + q;
    chrome.tabs
      .create({ url })
      .then(() => sendResponse({ ok: true }))
      .catch((e) => sendResponse({ ok: false, error: String(e && e.message ? e.message : e) }));
    return true;
  }
  if (message.type === "REQUEST_MEETING_SUMMARY") {
    const sid = (message.sessionId || "").trim();
    if (!sid) {
      sendResponse({ ok: false, error: "missing sessionId" });
      return true;
    }
    chrome.storage.local.set(
      { meetingSummaryPending: { sessionId: sid, nonce: Date.now() } },
      () => {
        const wid = message.windowId;
        if (wid != null && typeof chrome.sidePanel?.open === "function") {
          chrome.sidePanel.open({ windowId: wid }).catch(() => {});
        }
        sendResponse({ ok: true });
      }
    );
    return true;
  }
  return undefined;
});
