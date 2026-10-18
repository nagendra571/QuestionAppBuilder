(() => {
  const root = document.querySelector("[data-response-id][data-save-url]");
  if (!root) return;
  const form = root.querySelector("form[data-runtime-form]");
  const status = root.querySelector(".qab-save-status");
  if (!form || !status) return;

  let timer = 0;
  let inFlight = false;
  let inFlightPromise = null;
  let dirtyVersion = 0;
  let savedVersion = 0;
  let allowSubmit = false;
  let failed = false;

  const announce = message => { status.textContent = message; };
  const applyLiveContent = html => {
    const target = root.querySelector("[data-live-content]");
    if (!target || !html) return;
    const active = document.activeElement;
    const activeId = active && target.contains(active) ? active.id : "";
    const activeName = active && target.contains(active) ? active.getAttribute("name") : "";
    const selection = active && typeof active.selectionStart === "number"
      ? [active.selectionStart, active.selectionEnd, active.selectionDirection] : null;
    target.innerHTML = html;
    let replacement = activeId ? target.querySelector(`#${CSS.escape(activeId)}`) : null;
    if (!replacement && activeName) replacement = Array.from(target.querySelectorAll("[name]")).find(item => item.getAttribute("name") === activeName);
    if (replacement) {
      replacement.focus({ preventScroll: true });
      if (selection && typeof replacement.setSelectionRange === "function") replacement.setSelectionRange(...selection);
    }
  };
  const save = async () => {
    while (true) {
      if (inFlight) {
        await inFlightPromise;
        if (failed) return false;
        continue;
      }
      if (dirtyVersion === savedVersion) return true;
      inFlight = true;
      const version = dirtyVersion;
      announce("Saving your answers…");
      inFlightPromise = (async () => {
        try {
          const body = new URLSearchParams(new FormData(form));
          const response = await fetch(root.dataset.saveUrl, {
            method: "POST", body, credentials: "same-origin",
            headers: { "Accept": "application/json", "X-Requested-With": "QuestionAppBuilder" }
          });
          if (!response.ok) throw new Error("save failed");
          const result = await response.json();
          const revision = form.querySelector("[name='ExpectedRevision']");
          if (revision && result.revision) revision.value = result.revision;
          if (dirtyVersion === version && result.liveContentHtml) applyLiveContent(result.liveContentHtml);
          savedVersion = version;
          failed = false;
          if (dirtyVersion === version) announce("Saved just now.");
          else announce("A newer change is waiting to save.");
        } catch {
          failed = true;
          announce("Your answers could not be saved. They are still here; retry before leaving.");
        } finally {
          inFlight = false;
          if (!failed && dirtyVersion > savedVersion) queueSave(0);
        }
      })();
      await inFlightPromise;
      if (failed) return false;
    }
  };
  const queueSave = delay => {
    window.clearTimeout(timer);
    timer = window.setTimeout(() => { void save(); }, delay);
  };

  form.addEventListener("input", () => { dirtyVersion++; failed = false; announce("Unsaved changes."); queueSave(Number(root.dataset.saveDelay) || 600); });
  form.addEventListener("change", () => { dirtyVersion++; failed = false; announce("Unsaved changes."); queueSave(Number(root.dataset.saveDelay) || 600); });
  form.addEventListener("submit", async event => {
    if (allowSubmit) { allowSubmit = false; return; }
    if (dirtyVersion === savedVersion && !inFlight) return;
    event.preventDefault();
    const requestedAction = event.submitter && event.submitter.dataset.qabAction;
    window.clearTimeout(timer);
    const saved = await save();
    if (saved) {
      const actions = { back: ["back"], continue: ["continue", "review"], review: ["review", "continue"] };
      const candidates = actions[requestedAction] || ["back", "continue", "review"];
      const submitter = candidates.map(action => form.querySelector(`[data-qab-action='${action}']`)).find(Boolean);
      if (submitter) { allowSubmit = true; form.requestSubmit(submitter); }
      else announce("Your answers were saved, but navigation changed. Choose an available action to continue.");
    }
  });
  root.addEventListener("click", async event => {
    const link = event.target.closest(".qab-tabs a");
    if (!link || !root.contains(link)) return;
    if (dirtyVersion === savedVersion && !inFlight) return;
    event.preventDefault();
    window.clearTimeout(timer);
    if (await save()) window.location.assign(link.href);
  });
})();
