(() => {
  const form = document.querySelector("#draft-workspace");
  if (!form) return;
  const status = document.querySelector("[data-save-status]");
  const publishForm = document.querySelector("#publish-draft");
  let timer = 0, inFlight = false, dirtyVersion = 0, savedVersion = 0, allowSubmit = false;
  const announce = (message, state) => { status.textContent = message; status.dataset.state = state || ""; };
  const syncRevision = revision => {
    if (!revision) return;
    form.querySelector("[name='ExpectedRevision']").value = revision;
    const publishRevision = publishForm && publishForm.querySelector("[name='ExpectedRevision']");
    if (publishRevision) publishRevision.value = revision;
    const conditionRevision = document.querySelector("#add-condition-form [name='ExpectedRevision']");
    if (conditionRevision) conditionRevision.value = revision;
  };
  const save = async () => {
    if (inFlight) return false;
    if (dirtyVersion === savedVersion) return true;
    inFlight = true;
    const saveVersion = dirtyVersion;
    let failed = false;
    announce("Saving draft…", "pending");
    try {
      const response = await fetch(form.dataset.saveUrl, { method:"POST", body:new FormData(form), credentials:"same-origin",
        headers:{ "Accept":"application/json", "X-Requested-With":"QuestionAppBuilder" } });
      if (!response.ok) { const result = await response.json().catch(() => ({})); throw new Error(result.message || "The draft could not be saved."); }
      const result = await response.json();
      syncRevision(result.revision);
      savedVersion = saveVersion;
      announce(dirtyVersion === saveVersion ? "All changes saved" : "Saving newer changes…", "saved");
      return dirtyVersion === saveVersion;
    } catch (error) { failed = true; announce(error.message || "The draft could not be saved. Your edits are still here.", "error"); return false; }
    finally { inFlight = false; if (!failed && dirtyVersion > savedVersion) queueSave(0); }
  };
  const queueSave = delay => { clearTimeout(timer); timer = setTimeout(() => { void save(); }, delay); };
  form.addEventListener("input", () => { dirtyVersion++; announce("Unsaved changes", "pending"); queueSave(Number(form.dataset.saveDelay) || 600); });
  form.addEventListener("change", () => { dirtyVersion++; announce("Unsaved changes", "pending"); queueSave(Number(form.dataset.saveDelay) || 600); });
  form.addEventListener("submit", async event => {
    if (allowSubmit) { allowSubmit = false; return; }
    event.preventDefault(); clearTimeout(timer);
    if (await save()) { allowSubmit = true; form.requestSubmit(event.submitter); }
  });
  document.querySelectorAll("a[href^='#']").forEach(link => link.addEventListener("click", async event => {
    if (dirtyVersion === savedVersion && !inFlight) return;
    event.preventDefault(); clearTimeout(timer);
    if (await save()) window.location.assign(link.href);
  }));
  const previewButton = document.querySelector("[data-preview-button]");
  previewButton && previewButton.addEventListener("click", async () => {
    const token = form.querySelector("[name='__RequestVerificationToken']").value;
    const answers = {};
    document.querySelectorAll("[data-preview-answer-group]").forEach(group => {
      const controls = [...group.querySelectorAll("[data-preview-question]")];
      if (controls.length === 0) return;
      const id = controls[0].dataset.previewQuestion, type = controls[0].dataset.answerType;
      if (type === "Number") answers[id] = { number:controls[0].value, selectedOptionIds:[] };
      else if (type === "Dropdown" || type === "RadioGroup") answers[id] = { text:null, selectedOptionIds:controls[0].value ? [Number(controls[0].value)] : [] };
      else if (type === "Checkboxes") answers[id] = { text:null, selectedOptionIds:controls.filter(control => control.checked).map(control => Number(control.value)) };
      else answers[id] = { text:controls[0].value, selectedOptionIds:[] };
    });
    const result = document.querySelector("[data-preview-result]");
    result.textContent = "Evaluating preview…";
    try {
      const response = await fetch(previewButton.dataset.previewUrl, { method:"POST", credentials:"same-origin",
        headers:{ "Content-Type":"application/json", "Accept":"application/json", "RequestVerificationToken":token },
        body:JSON.stringify({ answers }) });
      const payload = await response.json();
      if (!response.ok) throw new Error(payload.message || "Preview could not be evaluated.");
      result.textContent = "Total score: " + payload.score.total + "; visible questions: " + payload.questionnaire.categories.flatMap(x => x.elements).filter(x => x.question).length;
    } catch (error) { result.textContent = error.message; }
  });
  publishForm && publishForm.addEventListener("submit", async event => {
    if (dirtyVersion === savedVersion && !inFlight) return;
    event.preventDefault(); clearTimeout(timer);
    if (await save()) event.target.submit();
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
      if (dirtyVersion !== savedVersion && !(await save())) return;
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
    if (dirtyVersion !== savedVersion && !(await save())) return;
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
