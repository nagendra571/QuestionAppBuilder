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
  let documentInFlight = false;

  const announce = message => { status.textContent = message; };
  const applyLiveContent = html => {
    const target = root.querySelector("[data-live-content]");
    if (!target || !html) return;
    const previousBranches = new Set([...target.querySelectorAll(".qab-follow-up [data-question-id]")].map(item => item.dataset.questionId));
    const active = document.activeElement;
    const activeId = active && target.contains(active) ? active.id : "";
    const activeName = active && target.contains(active) ? active.getAttribute("name") : "";
    const selection = active && typeof active.selectionStart === "number"
      ? [active.selectionStart, active.selectionEnd, active.selectionDirection] : null;
    target.innerHTML = html;
    const currentBranches = new Set([...target.querySelectorAll(".qab-follow-up [data-question-id]")].map(item => item.dataset.questionId));
    const branchStatus = root.querySelector('[data-branch-status]');
    if (branchStatus) {
      if ([...currentBranches].some(id => !previousBranches.has(id))) branchStatus.textContent = 'Additional questions are now available below your selection.';
      else if ([...previousBranches].some(id => !currentBranches.has(id))) branchStatus.textContent = 'Follow-up questions no longer apply to your selection and have been hidden.';
    }
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
          // A validation response can leave this document at the Navigate POST URL.
          // After saving, refresh should read the saved response rather than replay that POST.
          if (root.dataset.editUrl) {
            const canonical = new URL(root.dataset.editUrl, location.href);
            canonical.searchParams.set('category', form.querySelector('[name="CurrentCategoryIndex"]')?.value || '0');
            if (location.pathname !== canonical.pathname) history.replaceState(history.state, '', canonical.href);
          }
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

  const change = event => {
    if (event.target.matches('[data-upload-document]')) return;
    dirtyVersion++; failed = false; announce("Unsaved changes."); queueSave(Number(root.dataset.saveDelay) || 600);
  };
  form.addEventListener("input", change);
  form.addEventListener("change", change);

  const changeDocument = async (questionId, file) => {
    if (documentInFlight) return;
    if (file && (file.size === 0 || file.size > 10 * 1024 * 1024)) {
      announce(file.size === 0 ? 'The document is empty. Choose a file with content.' : 'The document must be 10 MB or smaller.');
      return;
    }
    documentInFlight = true;
    form.inert = true;
    window.clearTimeout(timer);
    try {
      if (!await save()) return;
      announce(file ? 'Uploading document…' : 'Removing document…');
      const body = new FormData();
      body.set('__RequestVerificationToken', form.querySelector('[name="__RequestVerificationToken"]').value);
      body.set('expectedRevision', form.querySelector('[name="ExpectedRevision"]').value);
      body.set('questionId', questionId);
      body.set('currentCategoryIndex', form.querySelector('[name="CurrentCategoryIndex"]').value);
      if (file) body.set('document', file);
      const url = new URL(root.dataset.saveUrl, location.href);
      url.pathname = url.pathname.replace(/\/Save$/, file ? '/UploadDocument' : '/RemoveDocument');
      const response = await fetch(url, { method: 'POST', body, credentials: 'same-origin', headers: { Accept: 'application/json' } });
      const result = await response.json();
      if (!response.ok) throw new Error(result.message || 'The document could not be saved. Please try again.');
      form.querySelector('[name="ExpectedRevision"]').value = result.revision;
      applyLiveContent(result.liveContentHtml);
      if (root.dataset.editUrl) {
        const canonical = new URL(root.dataset.editUrl, location.href);
        canonical.searchParams.set('category', form.querySelector('[name="CurrentCategoryIndex"]')?.value || '0');
        if (location.pathname !== canonical.pathname) history.replaceState(history.state, '', canonical.href);
      }
      announce(file ? 'Document uploaded and saved.' : 'Document removed.');
    } catch (error) {
      announce(error.message || 'The document could not be saved. Please try again.');
    } finally {
      documentInFlight = false;
      form.inert = false;
      const input = form.querySelector(`[data-upload-document="${questionId}"]`);
      if (input) { input.value = ''; input.focus({ preventScroll: true }); }
    }
  };
  form.addEventListener('change', event => {
    const input = event.target.closest('[data-upload-document]');
    if (input?.files?.[0]) void changeDocument(input.dataset.uploadDocument, input.files[0]);
  });
  form.addEventListener('click', event => {
    const button = event.target.closest('[data-remove-document]');
    if (button) void changeDocument(button.dataset.removeDocument, null);
  });
  window.addEventListener('beforeunload', event => {
    if (documentInFlight) { event.preventDefault(); event.returnValue = ''; }
  });
  form.addEventListener("submit", async event => {
    if (documentInFlight) { event.preventDefault(); return; }
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
    if (documentInFlight) { event.preventDefault(); return; }
    if (dirtyVersion === savedVersion && !inFlight) return;
    event.preventDefault();
    window.clearTimeout(timer);
    if (await save()) window.location.assign(link.href);
  });
})();
