(() => {
  const root = document.querySelector("[data-response-id][data-save-url]");
  if (!root) return;
  const form = root.querySelector("form[data-runtime-form]");
  const status = root.querySelector(".qab-save-status");
  if (!form || !status) return;

  let timer = 0;
  let inFlight = false;
  let dirtyVersion = 0;
  let savedVersion = 0;
  let allowSubmit = false;
  let failed = false;

  const announce = message => { status.textContent = message; };
  const save = async () => {
    if (inFlight) return false;
    if (dirtyVersion === savedVersion) return true;
    inFlight = true;
    const version = dirtyVersion;
    announce("Saving your answers…");
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
      savedVersion = version;
      failed = false;
      if (dirtyVersion === version) announce("Saved just now.");
      else announce("A newer change is waiting to save.");
      return dirtyVersion === version;
    } catch {
      failed = true;
      announce("Your answers could not be saved. They are still here; retry before leaving.");
      return false;
    } finally {
      inFlight = false;
      if (dirtyVersion > savedVersion) queueSave(0);
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
    window.clearTimeout(timer);
    const saved = await save();
    if (saved) { allowSubmit = true; form.requestSubmit(event.submitter); }
  });
  root.querySelectorAll(".qab-tabs a").forEach(link => link.addEventListener("click", async event => {
    if (dirtyVersion === savedVersion && !inFlight) return;
    event.preventDefault();
    window.clearTimeout(timer);
    if (await save()) window.location.assign(link.href);
  }));
})();
