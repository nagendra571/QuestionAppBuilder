(() => {
  const form = document.querySelector("#draft-workspace");
  if (!form) return;
  const outline = form.querySelector(".aw-outline");
  const compactViewport = window.matchMedia("(max-width: 800px)");
  const adaptOutline = () => { if (outline) outline.open = !compactViewport.matches; };
  adaptOutline();
  compactViewport.addEventListener("change", adaptOutline);
  const status = document.querySelector("[data-save-status]");
  const publishForm = document.querySelector("#publish-draft");
  const readiness = document.querySelector("#readiness");
  const readinessContent = readiness && readiness.querySelector("[data-readiness-content]");
  const readinessStatus = readiness && readiness.querySelector("[data-readiness-status]");
  const publishButton = publishForm && publishForm.querySelector("button[type='submit']");
  let timer = 0, inFlight = false, inFlightPromise = null, dirtyVersion = 0, savedVersion = 0;
  let readinessCurrent = true;
  const lastControlStates = new WeakMap();
  const controlState = control => {
    if (control instanceof HTMLSelectElement && control.multiple)
      return JSON.stringify([...control.selectedOptions].map(option => option.value));
    if (control instanceof HTMLInputElement && (control.type === "checkbox" || control.type === "radio"))
      return `${control.value}\u0000${control.checked}`;
    return control.value ?? control.textContent;
  };
  form.querySelectorAll("input, select, textarea, [contenteditable='true']").forEach(control => lastControlStates.set(control, controlState(control)));
  const recordEdit = event => {
    const control = event.target;
    const state = controlState(control);
    if (lastControlStates.get(control) === state) return;
    if (control instanceof HTMLInputElement && control.type === "radio" && control.name) {
      form.querySelectorAll("input[type='radio']").forEach(radio => {
        if (radio.form === form && radio.name === control.name) lastControlStates.set(radio, controlState(radio));
      });
    } else lastControlStates.set(control, state);
    dirtyVersion++;
    markReadinessStale();
    announce("Unsaved changes", "pending");
    queueSave(Number(form.dataset.saveDelay) || 600);
  };
  const announce = (message, state) => { status.textContent = message; status.dataset.state = state || ""; };
  const syncRevision = revision => {
    if (!revision) return;
    form.querySelector("[name='ExpectedRevision']").value = revision;
    const publishRevision = publishForm && publishForm.querySelector("[name='ExpectedRevision']");
    if (publishRevision) publishRevision.value = revision;
    const conditionRevision = document.querySelector("#add-condition-form [name='ExpectedRevision']");
    if (conditionRevision) conditionRevision.value = revision;
  };
  const markReadinessStale = () => {
    readinessCurrent = false;
    if (publishButton) publishButton.disabled = true;
    publishForm?.querySelectorAll("[name='AcknowledgedWarningCodes']").forEach(input => { input.checked = false; });
    if (readinessStatus) readinessStatus.textContent = "The draft changed. Readiness is refreshing after the save; review and acknowledge warnings for this revision.";
  };
  const addIssue = (list, issue) => {
    const item = document.createElement("li");
    if (issue.elementId != null) {
      const link = document.createElement("a");
      link.href = `#element-${issue.elementId}`;
      link.textContent = issue.message;
      item.append(link);
    } else item.textContent = issue.message;
    list.append(item);
  };
  const refreshReadiness = async expectedRevision => {
    if (!readiness || !readinessContent || !readinessStatus) return true;
    const response = await fetch(readiness.dataset.readinessUrl, { credentials:"same-origin", headers:{ "Accept":"application/json" } });
    if (!response.ok) throw new Error("The draft was saved, but readiness could not be refreshed. Retry before publishing.");
    const result = await response.json();
    if (result.revision !== expectedRevision) throw new Error("The draft changed again while readiness was loading. Reload before publishing.");
    if (savedVersion !== dirtyVersion) {
      readinessCurrent = false;
      if (publishButton) publishButton.disabled = true;
      readinessStatus.textContent = "Newer edits are still being saved. Readiness will refresh again before publishing.";
      return false;
    }
    const errors = result.errors || [], warnings = result.warnings || [];
    readinessContent.replaceChildren();
    if (errors.length) {
      const alert = document.createElement("div"); alert.className = "aw-alert"; alert.setAttribute("role", "alert");
      const heading = document.createElement("h3"); heading.textContent = "Resolve these issues before publishing";
      const list = document.createElement("ul"); errors.forEach(issue => addIssue(list, issue)); alert.append(heading, list); readinessContent.append(alert);
    } else {
      const success = document.createElement("p"); success.className = "aw-success"; success.textContent = "This draft meets the current publication checks."; readinessContent.append(success);
    }
    if (warnings.length) {
      const heading = document.createElement("h3"); heading.textContent = "Warnings requiring acknowledgment";
      const list = document.createElement("ul"); warnings.forEach(issue => addIssue(list, issue)); readinessContent.append(heading, list);
    }
    const warningContainer = publishForm && publishForm.querySelector("[data-publication-warnings]");
    if (warningContainer) {
      warningContainer.replaceChildren();
      warnings.forEach(warning => {
        const label = document.createElement("label"); label.className = "aw-field aw-check";
        const input = document.createElement("input"); input.type = "checkbox"; input.name = "AcknowledgedWarningCodes"; input.value = warning.code;
        label.append(input, document.createTextNode(` Acknowledge ${warning.code}: ${warning.message}`)); warningContainer.append(label);
      });
    }
    readinessCurrent = true;
    if (publishButton) publishButton.disabled = errors.length > 0;
    readinessStatus.textContent = "Readiness checked against the saved revision. Review and acknowledge any warnings before publishing.";
    return true;
  };
  const save = async () => {
    if (inFlight) {
      const outcome = await inFlightPromise;
      if (outcome === "failed") return "failed";
      return dirtyVersion !== savedVersion || !readinessCurrent ? "changed" : "saved";
    }
    if (dirtyVersion === savedVersion) {
      if (readinessCurrent) return "saved";
      try { return await refreshReadiness(form.querySelector("[name='ExpectedRevision']").value) ? "saved" : "changed"; }
      catch (error) { announce(error.message, "error"); return "failed"; }
    }
    inFlight = true;
    const saveVersion = dirtyVersion;
    let failed = false;
    announce("Saving draft…", "pending");
    inFlightPromise = (async () => {
      try {
        const response = await fetch(form.dataset.saveUrl, { method:"POST", body:new FormData(form), credentials:"same-origin",
          headers:{ "Accept":"application/json", "X-Requested-With":"QuestionAppBuilder" } });
        if (!response.ok) { const result = await response.json().catch(() => ({})); throw new Error(result.message || "The draft could not be saved."); }
        const result = await response.json();
        syncRevision(result.revision);
        savedVersion = saveVersion;
        if (!await refreshReadiness(result.revision)) return "changed";
        announce(dirtyVersion === saveVersion ? "All changes saved" : "Saving newer changes…", "saved");
        return dirtyVersion === saveVersion ? "saved" : "changed";
      } catch (error) { failed = true; announce(error.message || "The draft could not be saved. Your edits are still here.", "error"); return "failed"; }
      finally { inFlight = false; inFlightPromise = null; if (!failed && dirtyVersion > savedVersion) queueSave(0); }
    })();
    return await inFlightPromise;
  };
  const queueSave = delay => { clearTimeout(timer); timer = setTimeout(() => { void save(); }, delay); };
  const saveCurrentDraft = async () => {
    while (true) {
      const saved = await save();
      if (saved === "saved" && dirtyVersion === savedVersion && !inFlight && readinessCurrent) return "saved";
      if (saved === "failed") return "failed";
    }
  };
  form.addEventListener("input", recordEdit);
  // Some controls (and older browsers) report edits only through `change`. When
  // both events fire for one edit, the state comparison prevents blur from
  // marking a freshly saved value dirty a second time.
  form.addEventListener("change", recordEdit);
  form.addEventListener("submit", async event => {
    if (dirtyVersion === savedVersion && !inFlight && readinessCurrent) return;
    event.preventDefault(); clearTimeout(timer);
    const submitter = event.submitter;
    const outcome = await saveCurrentDraft();
    if (outcome === "saved") {
      setTimeout(() => {
        const liveSubmitter = submitter?.isConnected && submitter.form === form ? submitter :
          submitter && [...form.querySelectorAll("button[type='submit'], input[type='submit']")].find(button => button.formAction === submitter.formAction);
        if (submitter && !liveSubmitter) { announce("The requested action is no longer available. Review the workspace and try again.", "error"); return; }
        if (liveSubmitter) form.requestSubmit(liveSubmitter);
        else form.requestSubmit();
      }, 0);
    }
  });
  document.querySelectorAll("a[href^='#']").forEach(link => link.addEventListener("click", async event => {
    if (dirtyVersion === savedVersion && !inFlight) return;
    event.preventDefault(); clearTimeout(timer);
    if (await saveCurrentDraft() === "saved") window.location.assign(link.href);
  }));
  const previewButton = document.querySelector("[data-preview-button]");
  let previewAnswers = {};
  let previewCategory = 0;
  const collectPreviewAnswers = () => {
    document.querySelectorAll("[data-preview-runtime-form] [data-question-id]").forEach(group => {
      const id = Number(group.dataset.questionId);
      const text = group.querySelector(`[name="Answers[${id}].Text"]`);
      const number = group.querySelector(`[name="Answers[${id}].Number"]`);
      const selected = [...group.querySelectorAll(`[name='Answers[${id}].SelectedOptionIds']:checked, select[name='Answers[${id}].SelectedOptionIds']`)]
        .map(control => Number(control.value)).filter(value => Number.isInteger(value) && value > 0);
      const hasChoiceInput = group.querySelector(`[name='Answers[${id}].SelectedOptionIds']`) !== null;
      previewAnswers[id] = {
        text: text ? text.value : null,
        number: number ? number.value : null,
        selectedOptionIds: hasChoiceInput ? selected : null
      };
    });
  };
  const appendPreviewInspector = (host, score) => {
    const inspector = document.createElement("section"); inspector.className = "aw-preview-score";
    const heading = document.createElement("h3"); heading.textContent = "Designer scoring inspector"; inspector.append(heading);
    const total = document.createElement("p"); total.textContent = `Total score: ${score.total}`; inspector.append(total);
    for (const category of score.categories || []) {
      const row = document.createElement("p"); row.textContent = `Category ${category.categoryId}: ${category.score} points`; inspector.append(row);
    }
    for (const question of score.questions || []) {
      const details = document.createElement("details");
      const summary = document.createElement("summary");
      summary.textContent = `Question ${question.questionId}: ${question.isApplicable ? `${question.points} points` : "not applicable"}`;
      details.append(summary);
      for (const rule of question.rules || []) {
        const line = document.createElement("p");
        line.textContent = `Rule ${rule.ruleId}: ${rule.matches ? "matches" : "does not match"}; ${rule.contributes ? `contributes ${rule.points} points` : "does not contribute"}.`;
        details.append(line);
      }
      inspector.append(details);
    }
    host.append(inspector);
  };
  const renderPreview = payload => {
    const result = document.querySelector("[data-preview-result]");
    previewCategory = payload.categoryIndex;
    result.innerHTML = payload.html;
    const runtime = result.querySelector(".qab-runtime-preview");
    if (runtime) appendPreviewInspector(runtime, payload.score);
  };
  const evaluatePreview = async options => {
    const token = form.querySelector("[name='__RequestVerificationToken']").value;
    const result = document.querySelector("[data-preview-result]");
    result.textContent = "Evaluating preview…";
    try {
      const response = await fetch(previewButton.dataset.previewUrl, { method:"POST", credentials:"same-origin",
        headers:{ "Content-Type":"application/json", "Accept":"application/json", "RequestVerificationToken":token },
        body:JSON.stringify({ answers:previewAnswers, categoryIndex:previewCategory, ...options }) });
      const payload = await response.json();
      if (!response.ok) throw new Error(payload.message || "Preview could not be evaluated.");
      renderPreview(payload);
    } catch (error) { result.textContent = error.message; }
  };
  const previewResult = document.querySelector("[data-preview-result]");
  previewResult?.addEventListener("click", async event => {
    const target = event.target.closest("[data-preview-target]");
    const evaluate = event.target.closest("[data-preview-evaluate]");
    const review = event.target.closest("[data-preview-review]");
    const submit = event.target.closest("[data-preview-submit]");
    if (!target && !evaluate && !review && !submit) return;
    event.preventDefault(); collectPreviewAnswers();
    if (target) await evaluatePreview({ targetCategoryIndex:Number(target.dataset.previewTarget) });
    else if (evaluate) await evaluatePreview();
    else if (review) await evaluatePreview({ review:true });
    else if (submit) await evaluatePreview({ submit:true });
  });
  previewButton && previewButton.addEventListener("click", async () => {
    if (previewResult.querySelector("[data-preview-runtime-form]")) collectPreviewAnswers();
    await evaluatePreview();
  });
  publishForm && publishForm.addEventListener("submit", async event => {
    if (dirtyVersion === savedVersion && !inFlight && readinessCurrent) return;
    event.preventDefault(); clearTimeout(timer);
    const submitter = event.submitter;
    if (await saveCurrentDraft() === "saved") {
      setTimeout(() => {
        const liveSubmitter = submitter?.isConnected && submitter.form === publishForm ? submitter :
          submitter && [...publishForm.querySelectorAll("button[type='submit'], input[type='submit']")].find(button => button.formAction === submitter.formAction);
        if (submitter && !liveSubmitter) { announce("The requested publication action is no longer available. Review the workspace and try again.", "error"); return; }
        if (liveSubmitter) publishForm.requestSubmit(liveSubmitter);
        else publishForm.requestSubmit();
      }, 0);
    }
  });
  const conditionForm = document.querySelector("#add-condition-form");
  if (conditionForm) {
    const source = conditionForm.querySelector("[name='sourceQuestionId']");
    const updateOptions = () => conditionForm.querySelectorAll("[data-option-group]").forEach(group => { group.hidden = group.dataset.questionId !== source.value; });
    source.addEventListener("change", updateOptions);
    updateOptions();
    conditionForm.addEventListener("submit", async event => {
      event.preventDefault();
      clearTimeout(timer);
      if (dirtyVersion !== savedVersion && await saveCurrentDraft() !== "saved") return;
      const response = await fetch(conditionForm.dataset.actionUrl, { method:"POST", credentials:"same-origin",
        body:new URLSearchParams(new FormData(conditionForm)), headers:{ "Accept":"application/json", "X-Requested-With":"QuestionAppBuilder" } });
      const result = await response.json().catch(() => ({}));
      if (!response.ok) { announce(result.message || "The visibility rule could not be saved.", "error"); return; }
      syncRevision(result.revision);
      window.location.reload();
    });
  }
  const structureQuestion = document.querySelector("#structure-question");
  if (structureQuestion) {
    const updateScoringOptions = () => document.querySelectorAll("[data-scoring-options]").forEach(group => {
      const active = group.dataset.scoringOptions === structureQuestion.value;
      group.hidden = !active;
      group.querySelectorAll("input").forEach(input => { input.disabled = !active; });
    });
    structureQuestion.addEventListener("change", updateScoringOptions);
    updateScoringOptions();
  }
  document.querySelectorAll("[data-structure-action]").forEach(button => button.addEventListener("click", async () => {
    clearTimeout(timer);
    if (dirtyVersion !== savedVersion && await saveCurrentDraft() !== "saved") return;
    const token = form.querySelector("[name='__RequestVerificationToken']").value;
    const revision = form.querySelector("[name='ExpectedRevision']").value;
    announce("Updating questionnaire structure…", "pending");
    try {
      const body = new URLSearchParams({ __RequestVerificationToken:token, expectedRevision:revision });
      const response = await fetch(button.dataset.structureAction, { method:"POST", credentials:"same-origin",
        headers:{ "Accept":"application/json", "X-Requested-With":"QuestionAppBuilder" }, body });
      if (!response.ok) { const result = await response.json().catch(() => ({})); throw new Error(result.message || "The structure change could not be saved."); }
      window.location.reload();
    } catch (error) { announce(error.message || "The structure change could not be saved.", "error"); }
  }));
})();
