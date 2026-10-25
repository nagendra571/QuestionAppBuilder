(() => {
  const form = document.querySelector("#draft-workspace");
  if (!form) return;
  const outline = document.querySelector(".aw-outline");
  const compactViewport = window.matchMedia("(max-width: 800px)");
  const adaptOutline = () => { if (outline) outline.open = !compactViewport.matches; };
  adaptOutline();
  compactViewport.addEventListener("change", adaptOutline);
  const status = document.querySelector("[data-save-status]");
  const previewResult = document.querySelector("[data-preview-result]");
  const publishForm = document.querySelector("#publish-draft");
  const readiness = document.querySelector("#readiness");
  const readinessContent = readiness && readiness.querySelector("[data-readiness-content]");
  const readinessStatus = readiness && readiness.querySelector("[data-readiness-status]");
  const publishButton = publishForm && publishForm.querySelector("button[type='submit']");
  let timer = 0, inFlight = false, inFlightPromise = null, dirtyVersion = 0, savedVersion = 0;
  let readinessCurrent = true;
  let markScenarioRunsStale = () => {};
  let hasPendingScenarioEdits = () => false;
  let invalidateWorkspaceSearch = () => {};
  const lastControlStates = new WeakMap();
  let previewRequestId = 0, previewHasRendered = false, previewInFlight = false;
  const invalidatePreviewExplanations = (cause = "draft") => {
    const result = previewResult;
    if (!result) return;
    const wasInFlight = previewInFlight;
    if (wasInFlight) { previewRequestId++; previewInFlight = false; }
    const reports = result.querySelectorAll("[data-hidden-preview-inspector], [data-preview-validation-inspector], [data-preview-scoring-inspector]");
    const hadResult = previewHasRendered || reports.length > 0;
    reports.forEach(report => report.remove());
    if (!hadResult && !wasInFlight && !result.querySelector(".qab-runtime-preview")) return;
    const note = document.createElement("p");
    note.className = "aw-preview-hidden-stale";
    note.dataset.hiddenPreviewStale = "true";
    note.setAttribute("role", "status");
    note.textContent = cause === "answers"
      ? "Preview answers changed. Evaluate again to refresh preview explanations."
      : wasInFlight && !hadResult
      ? "The draft changed while preview was loading. Save and evaluate again."
      : "This preview is out of date. Save the draft and evaluate again to refresh preview explanations.";
    const host = result.querySelector(".qab-runtime-preview");
    if (host) {
      host.querySelector("[data-hidden-preview-stale]")?.remove();
      host.append(note);
    } else result.replaceChildren(note);
  };
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
    if (!/^(Version\.|Categories\[|Elements\[|Questions\[|Options\[|Rules\[|Conditions\[)/.test(control.name || '')) return;
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
    invalidateDependencyReports();
    invalidatePreviewExplanations();
    markScenarioRunsStale();
    invalidateWorkspaceSearch("The draft changed. Save it and search again to refresh these results.");
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
    const followUpRevision = document.querySelector("#add-follow-up-form [name='expectedRevision']");
    if (followUpRevision) followUpRevision.value = revision;
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
  const searchSection = document.querySelector("[data-workspace-search]");
  if (searchSection) {
    const searchForm = searchSection.querySelector("[data-definition-search]");
    const queryInput = searchSection.querySelector("[data-search-query]");
    const status = searchSection.querySelector("[data-search-status]");
    const results = searchSection.querySelector("[data-search-results]");
    const resultsContent = searchSection.querySelector("[data-search-results-content]");
    const resultsSummary = searchSection.querySelector("[data-search-summary]");
    const clearButton = searchSection.querySelector("[data-search-clear]");
    let requestId = 0, queryVersion = 0, pending = false, hasSearchResult = false;
    const setStatus = (message, state = "") => {
      if (!status) return;
      status.textContent = message;
      status.dataset.state = state;
    };
    const showStale = message => {
      results.dataset.stale = "true";
      results.hidden = false;
      results.open = true;
      let note = resultsContent.querySelector("[data-search-stale]");
      if (!note) { note = document.createElement("p"); note.dataset.searchStale = "true"; note.setAttribute("role", "status"); resultsContent.append(note); }
      note.textContent = message;
    };
    invalidateWorkspaceSearch = message => {
      requestId++;
      const wasPending = pending;
      pending = false;
      if (!hasSearchResult && !wasPending) return;
      showStale(message);
      setStatus(message, "stale");
    };
    const render = report => {
      resultsContent.replaceChildren();
      results.dataset.stale = "false";
      results.hidden = false;
      results.open = true;
      hasSearchResult = true;
      const summary = document.createElement("p");
      summary.textContent = report.totalCount === 1 ? "1 result" : `${report.totalCount} results`;
      if ((report.items || []).length < report.totalCount) summary.textContent += ` · showing ${(report.items || []).length}`;
      resultsContent.append(summary);
      resultsSummary.textContent = summary.textContent;
      for (const warning of report.warnings || []) {
        const note = document.createElement("p"); note.setAttribute("role", "status"); note.textContent = warning; resultsContent.append(note);
      }
      if (!report.items?.length) {
        const empty = document.createElement("p"); empty.textContent = report.complete ? "No matches in this saved draft." : "No matches could be confirmed. Review the search warnings.";
        resultsContent.append(empty); return;
      }
      const list = document.createElement("ul");
      for (const item of report.items) {
        const row = document.createElement("li");
        const kindLabels = { QuestionScoring:"Question scoring", CombinedRule:"Combined rule", DynamicVariable:"Dynamic value", DynamicTemplate:"Text template" };
        const link = document.createElement("a"); link.href = item.url; link.textContent = `${kindLabels[item.kind] || item.kind}: ${item.name || "Unnamed item"}`;
        row.append(link);
        const match = document.createElement("p");
        match.textContent = item.matchKind === "Reference"
          ? `Matches reference: ${item.referenceLabel || item.matchedText || "a matching value"}${item.context ? ` · ${item.context}` : ""}`
          : `Matched ${item.matchedText || "text"}${item.context ? ` · ${item.context}` : ""}`;
        row.append(match); list.append(row);
      }
      resultsContent.append(list);
    };
    searchForm.addEventListener("submit", async event => {
      event.preventDefault();
      const query = queryInput.value.trim();
      if (query.length < 1 || query.length > 200) { setStatus("Enter between 1 and 200 characters to search.", "error"); return; }
      const thisRequest = ++requestId;
      const thisQueryVersion = queryVersion;
      pending = true;
      const stillCurrent = () => thisRequest === requestId && thisQueryVersion === queryVersion && queryInput.value.trim() === query;
      setStatus("Saving the current draft before searching…", "pending");
      clearTimeout(timer);
      try {
        if (await saveCurrentDraft() !== "saved") {
          if (stillCurrent()) setStatus("The current draft could not be saved. Search is unavailable until the save succeeds.", "error");
          return;
        }
        if (!stillCurrent()) return;
        const revisionInput = form.querySelector("[name='ExpectedRevision']");
        const revision = revisionInput.value;
        const draftVersion = dirtyVersion;
        const url = new URL(searchSection.dataset.searchUrl, document.baseURI);
        url.searchParams.set("q", query);
        url.searchParams.set("expectedRevision", revision);
        setStatus("Searching the saved draft…", "pending");
        const response = await fetch(url, { credentials:"same-origin", headers:{ "Accept":"application/json" } });
        const report = await response.json().catch(() => ({}));
        if (!stillCurrent()) return;
        if (!response.ok) throw new Error(report.message || "The saved draft could not be searched.");
        if (dirtyVersion !== draftVersion || dirtyVersion !== savedVersion || inFlight
            || revisionInput.value !== revision || report.revision !== revision) {
          showStale("The draft or search text changed while searching. Save and search again.");
          setStatus("Search results are out of date.", "stale");
          return;
        }
        render(report);
        setStatus(report.complete ? "Search complete." : "Search complete with warnings.", report.complete ? "saved" : "stale");
      } catch (error) {
        if (stillCurrent()) setStatus(error.message || "The saved draft could not be searched.", "error");
      } finally {
        if (thisRequest === requestId) pending = false;
      }
    });
    queryInput.addEventListener("input", () => {
      queryVersion++;
      invalidateWorkspaceSearch("Search text changed. Search again to refresh these results.");
      if (!queryInput.value.trim()) setStatus("");
    });
    clearButton.addEventListener("click", () => {
      requestId++; queryVersion++; pending = false; hasSearchResult = false;
      queryInput.value = ""; resultsContent.replaceChildren(); results.hidden = true; results.open = false;
      results.dataset.stale = "false"; resultsSummary.textContent = "Search results"; setStatus(""); queryInput.focus();
    });
    results.addEventListener("click", event => {
      if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
      const link = event.target instanceof Element ? event.target.closest("a[href]") : null;
      if (!link || link.hasAttribute("download") || (link.target && link.target.toLowerCase() !== "_self")) return;
      if (results.dataset.stale === "true") {
        event.preventDefault(); setStatus("These results are out of date. Search again before opening a result.", "stale"); return;
      }
      let targetUrl;
      try { targetUrl = new URL(link.href, window.location.href); } catch { return; }
      if (targetUrl.origin !== window.location.origin || targetUrl.pathname !== location.pathname || !targetUrl.hash) return;
      setTimeout(() => requestAnimationFrame(() => {
        const target = document.getElementById(decodeURIComponent(targetUrl.hash.slice(1)));
        if (!target) return;
        if (!target.hasAttribute("tabindex")) {
          target.setAttribute("tabindex", "-1");
          target.addEventListener("blur", () => target.removeAttribute("tabindex"), { once:true });
        }
        target.focus({ preventScroll:true });
      }), 0);
    });
  }
  const dependencyPanels = [...document.querySelectorAll("[data-dependency-panel]")];
  const invalidateDependencyReports = () => {
    dependencyPanels.forEach(panel => {
      panel.dataset.requestId = String((Number(panel.dataset.requestId) || 0) + 1);
      panel.dataset.loadedRevision = "";
      const host = panel.querySelector("[data-dependency-results]");
      if (host) { const message = document.createElement("p"); message.className = "aw-muted"; message.textContent = "The draft changed. Save before checking these references again."; host.replaceChildren(message); }
    });
  };
  const renderDependencyReport = (panel, report) => {
    const host = panel.querySelector("[data-dependency-results]");
    if (!host) return;
    const nodes = [];
    if (!report.complete) {
      for (const warning of report.warnings || []) { const p = document.createElement("p"); p.setAttribute("role", "status"); p.textContent = warning; nodes.push(p); }
    }
    const items = report.items || [];
    if (!items.length && report.complete) { const empty = document.createElement("p"); empty.textContent = "No configured references were found in this saved draft."; nodes.push(empty); }
    const labels = { UsedBy:"Used by", Owned:"Owned configuration", DependsOn:"Depends on", PotentialTarget:"Possible target" };
    const sectionLinks = { Groups:"Groups", Calculations:"Calculations", Rules:"Rules", DynamicText:"DynamicText" };
    for (const relation of ["UsedBy", "Owned", "DependsOn", "PotentialTarget"]) {
      const group = items.filter(item => item.relation === relation);
      if (!group.length) continue;
      const heading = document.createElement("h4"); heading.textContent = labels[relation]; nodes.push(heading);
      const list = document.createElement("ul");
      for (const item of group) {
        const row = document.createElement("li");
        const title = document.createElement("strong"); title.textContent = `${item.name || "Unnamed item"} — ${item.kind || "Reference"}`;
        row.append(title, document.createTextNode(`: ${item.detail || "Configured reference."}`));
        let href = null;
        if (item.compositionSection && sectionLinks[item.compositionSection]) href = `${panel.dataset.compositionUrl}#${sectionLinks[item.compositionSection]}`;
        else if (item.categoryId > 0) href = `${panel.dataset.workspaceUrl}#category-${item.categoryId}`;
        else if (item.compositionSection === "visibility") href = `${panel.dataset.workspaceUrl}#visibility`;
        else if (Number.isInteger(item.elementId) && item.elementId > 0) href = `${panel.dataset.workspaceUrl}#element-${item.elementId}`;
        if (href) { const link = document.createElement("a"); link.href = href; link.textContent = "Open related configuration"; row.append(document.createTextNode(" "), link); }
        list.append(row);
      }
      nodes.push(list);
    }
    host.replaceChildren(...nodes);
  };
  const loadDependencyReport = async panel => {
    const host = panel.querySelector("[data-dependency-results]");
    const revisionInput = form.querySelector("[name='ExpectedRevision']");
    if (!host || !revisionInput) return;
    const requestId = (Number(panel.dataset.requestId) || 0) + 1;
    panel.dataset.requestId = String(requestId);
    const message = document.createElement("p"); message.textContent = "Saving the current draft before checking references…"; host.replaceChildren(message);
    clearTimeout(timer);
    const outcome = await saveCurrentDraft();
    if (outcome !== "saved") { const error = document.createElement("p"); error.setAttribute("role", "alert"); error.textContent = "The current draft could not be saved. References are unavailable until the save succeeds."; host.replaceChildren(error); return; }
    const revision = revisionInput.value;
    const url = new URL(panel.dataset.dependencyUrl, document.baseURI);
    url.searchParams.set("expectedRevision", revision);
    try {
      const response = await fetch(url, { credentials:"same-origin", headers:{ "Accept":"application/json" } });
      if (!response.ok) { const result = await response.json().catch(() => ({})); throw new Error(result.message || "References could not be loaded."); }
      const result = await response.json();
      if (Number(panel.dataset.requestId) !== requestId) return;
      if (dirtyVersion !== savedVersion || inFlight || result.revision !== revision || revisionInput.value !== revision) {
        const stale = document.createElement("p"); stale.className = "aw-muted"; stale.textContent = "The draft changed while references were loading. Close and reopen this section after the save."; host.replaceChildren(stale); return;
      }
      renderDependencyReport(panel, result.report);
      panel.dataset.loadedRevision = revision;
    } catch (error) {
      if (Number(panel.dataset.requestId) !== requestId) return;
      const p = document.createElement("p"); p.setAttribute("role", "alert"); p.textContent = error.message || "References could not be loaded."; host.replaceChildren(p);
    }
  };
  dependencyPanels.forEach(panel => panel.addEventListener("toggle", () => {
    if (panel.open) void loadDependencyReport(panel);
  }));
  form.addEventListener("input", recordEdit);
  // Some controls (and older browsers) report edits only through `change`. When
  // both events fire for one edit, the state comparison prevents blur from
  // marking a freshly saved value dirty a second time.
  form.addEventListener("change", recordEdit);
  form.addEventListener("submit", async event => {
    const action = event.submitter?.getAttribute("formaction");
    if (!action) {
      event.preventDefault(); clearTimeout(timer);
      await saveCurrentDraft();
      return;
    }
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
  let pendingNavigation = null;
  const authoringSurface = form.closest("[data-authoring-context]");
  document.addEventListener("click", event => {
    if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
    const link = event.target instanceof Element ? event.target.closest("a[href]") : null;
    if (!link || !authoringSurface?.contains(link) || link.hasAttribute("download") || (link.target && link.target.toLowerCase() !== "_self")) return;
    let destination;
    try { destination = new URL(link.href, window.location.href); }
    catch { return; }
    if (destination.origin !== window.location.origin) return;
    const sameDocumentNavigation = destination.pathname === window.location.pathname && destination.search === window.location.search;
    if (!sameDocumentNavigation && hasPendingScenarioEdits()) {
      event.preventDefault();
      document.querySelector("[data-scenario-status]")?.replaceChildren(Object.assign(document.createElement("span"), {
        textContent:"Save or clear your preview scenario edits before leaving this workspace."
      }));
      return;
    }
    if (dirtyVersion === savedVersion && !inFlight) return;

    event.preventDefault();
    clearTimeout(timer);
    if (pendingNavigation) { pendingNavigation.href = destination.href; return; }
    const intent = { href: destination.href };
    pendingNavigation = intent;
    void saveCurrentDraft().then(outcome => {
      if (outcome === "saved" && dirtyVersion === savedVersion && !inFlight && readinessCurrent && pendingNavigation === intent)
        window.location.assign(intent.href);
    }).finally(() => { if (pendingNavigation === intent) pendingNavigation = null; });
  });
  window.addEventListener("beforeunload", event => {
    if (dirtyVersion !== savedVersion || hasPendingScenarioEdits()) { event.preventDefault(); event.returnValue = ""; }
  });
  const previewButton = document.querySelector("[data-preview-button]");
  let previewAnswers = {};
  let previewCategory = 0;
  let previewAnswerVersion = 0;
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
  const appendPreviewInspector = (host, score, scoring) => {
    const inspector = document.createElement("section");
    inspector.className = "aw-preview-score";
    inspector.dataset.previewScoringInspector = "true";
    const heading = document.createElement("h3"); heading.textContent = "Scoring"; inspector.append(heading);
    const total = document.createElement("p"); total.className = "aw-preview-score-total";
    total.textContent = `Total score: ${score.total}`; inspector.append(total);
    const explanations = document.createElement("details");
    const summary = document.createElement("summary"); summary.textContent = "Scoring explanations";
    explanations.append(summary);
    if (scoring === null) {
      const note = document.createElement("p"); note.textContent = "Scoring explanations are unavailable from this evaluator.";
      explanations.append(note);
    } else if (scoring.unavailableReason) {
      const note = document.createElement("p"); note.textContent = scoring.unavailableReason;
      explanations.append(note);
    } else {
      if ((scoring.combinedDefinitionErrors || []).length) {
        const errors = document.createElement("ul");
        for (const errorText of scoring.combinedDefinitionErrors) { const item = document.createElement("li"); item.textContent = errorText; errors.append(item); }
        explanations.append(errors);
      }
      const safeLink = (url, label) => {
        if (typeof url !== "string") return null;
        try {
          const parsed = new URL(url, window.location.href);
          if (parsed.origin !== window.location.origin) return null;
          const link = document.createElement("a"); link.href = parsed.href; link.textContent = label; return link;
        } catch { return null; }
      };
      const appendReads = (parent, label, checks, skippedCount, skippedText) => {
        if (!checks?.length && !skippedCount) return;
        const section = document.createElement("details");
        const title = document.createElement("summary"); title.textContent = label; section.append(title);
        const list = document.createElement("ul");
        for (const check of checks || []) {
          const item = document.createElement("li");
          const outcome = document.createElement("p");
          outcome.textContent = `${check.matches ? "Passed" : "Failed"}: ${check.requirement || "Configured numeric check"}. Read ${check.leftDescription || "left value"} as ${check.leftValueText || "unavailable"}; read ${check.rightDescription || "right value"} as ${check.rightValueText || "unavailable"}.`;
          item.append(outcome);
          for (const effectText of [...(check.leftEffects || []), ...(check.rightEffects || [])]) {
            const effect = document.createElement("p"); effect.textContent = effectText; item.append(effect);
          }
          if (check.leftValue === null || check.rightValue === null) {
            const unavailable = document.createElement("p"); unavailable.textContent = "An unavailable numeric value makes this comparison fail, including “does not equal”.";
            item.append(unavailable);
          }
          list.append(item);
        }
        section.append(list);
        if (skippedCount > 0) { const skipped = document.createElement("p"); skipped.textContent = skippedText; section.append(skipped); }
        parent.append(section);
      };
      if (scoring.questions?.length) {
        const questionList = document.createElement("ul");
        for (const question of scoring.questions) {
          const item = document.createElement("li");
          const detail = document.createElement("details");
          const title = document.createElement("summary");
          title.textContent = `${question.name}: ${question.applicable ? `${question.points} points` : "not applicable"}`;
          detail.append(title);
          const questionEdit = safeLink(question.editUrl, "Open question settings"); if (questionEdit) detail.append(questionEdit);
          const category = document.createElement("p"); category.textContent = `Category: ${question.categoryName}.`; detail.append(category);
          if (!question.applicable) {
            const hidden = document.createElement("p"); hidden.textContent = "This question was hidden and did not contribute to the preview score."; detail.append(hidden);
          } else {
            const answer = document.createElement("p");
            answer.textContent = question.answerType === "Number"
              ? `Numeric answer: ${question.answerNumberText ?? "not entered"}.`
              : `Answer present: ${question.answered ? "yes" : "no"}${question.selectedOptions?.length ? `; selected: ${question.selectedOptions.join(", ")}` : ""}.`;
            detail.append(answer);
            const mode = document.createElement("p");
            mode.textContent = question.scoringMode === "FirstMatch"
              ? "All rules are evaluated in order; only the first matching rule contributes."
              : question.scoringMode === "Additive" ? "Points from all matching rules are added." : "This question has an unsupported scoring mode.";
            detail.append(mode);
            for (const rule of question.rules || []) {
              const ruleDetail = document.createElement("details");
              const ruleTitle = document.createElement("summary"); ruleTitle.textContent = rule.name;
              const requirement = document.createElement("p"); requirement.textContent = rule.requirement;
              const outcome = document.createElement("p"); outcome.textContent = rule.outcome;
              ruleDetail.append(ruleTitle, requirement, outcome);
              const edit = safeLink(rule.editUrl, "Edit this scoring rule"); if (edit) ruleDetail.append(edit);
              ruleDetail.dataset.scoringRuleName = rule.name;
              detail.append(ruleDetail);
            }
            if (!(question.rules || []).length) { const none = document.createElement("p"); none.textContent = "No question scoring rules are configured."; detail.append(none); }
          }
          item.prepend(detail);
          questionList.append(item);
        }
        if (scoring.categories?.length) {
          const categoryTotals = document.createElement("ul");
          for (const category of scoring.categories) { const row = document.createElement("li"); row.textContent = `${category.name}: ${category.points} points`; categoryTotals.append(row); }
          explanations.append(categoryTotals);
        }
        const group = document.createElement("details");
        const groupTitle = document.createElement("summary"); groupTitle.textContent = "Question scoring"; group.append(groupTitle, questionList);
        explanations.append(group);
      }
      if (scoring.combinedRules === null) {
        const note = document.createElement("p"); note.textContent = scoring.combinedDefinitionErrors?.length
          ? "Combined scoring details are unavailable because the saved configuration has errors."
          : "Combined scoring explanations are unavailable from this scorer.";
        explanations.append(note);
      } else if (scoring.combinedRules?.length) {
        const combined = document.createElement("details");
        const combinedTitle = document.createElement("summary"); combinedTitle.textContent = `Combined scoring rules (${scoring.combinedRules.length})`;
        combined.append(combinedTitle);
        const list = document.createElement("ul");
        for (const rule of scoring.combinedRules) {
          const item = document.createElement("li");
          const detail = document.createElement("details");
          const title = document.createElement("summary"); title.textContent = rule.name || "Unnamed combined rule"; detail.append(title);
          const category = document.createElement("p"); category.textContent = `Category: ${rule.categoryName}. Target: ${rule.targetName}.`; detail.append(category);
          const outcome = document.createElement("p"); outcome.textContent = rule.outcome; detail.append(outcome);
          if (rule.bandName) {
            const band = document.createElement("p"); band.textContent = rule.supersedingRuleName
              ? `Scoring band “${rule.bandName}” was already awarded by ${rule.supersedingRuleName}.`
              : `Scoring band: ${rule.bandName}.`; detail.append(band);
          }
          const conditionSkipReason = rule.outcomeCode === "no-visible-target" || rule.outcomeCode === "target-hidden"
            ? "The conditions were not evaluated because the target question was unavailable."
            : "Remaining conditions were not evaluated after an earlier condition failed.";
          appendReads(detail, "Configured conditions (all must pass)", rule.conditions, rule.skippedConditionCount, conditionSkipReason);
          const checkSkipReason = rule.outcomeCode === "no-visible-target" || rule.outcomeCode === "target-hidden"
            ? "The checks were not evaluated because the target question was unavailable."
            : rule.outcomeCode === "condition-not-matched"
              ? "The checks were not evaluated because a gating condition failed."
              : rule.anyCheck ? "Remaining checks were not evaluated because an ANY check already passed."
                : "Remaining checks were not evaluated because an ALL check already failed.";
          appendReads(detail, rule.anyCheck ? "Required checks (any one may pass)" : "Required checks (all must pass)", rule.checks,
            rule.skippedCheckCount, checkSkipReason);
          const edit = safeLink(rule.configurationUrl, "Edit this combined rule"); if (edit) detail.append(edit);
          const target = safeLink(rule.targetUrl, "Open target question"); if (target) detail.append(target);
          item.append(detail); list.append(item);
        }
        combined.append(list); explanations.append(combined);
      }
      if (!scoring.questions?.some(question => (question.rules || []).length) && Array.isArray(scoring.combinedRules)
          && scoring.combinedRules.length === 0 && !scoring.unavailableReason && !(scoring.combinedDefinitionErrors || []).length) {
        const note = document.createElement("p"); note.textContent = "No scoring rules are configured."; explanations.append(note);
      }
    }
    inspector.append(explanations);
    host.append(inspector);
  };
  const appendHiddenItemInspector = (host, items) => {
    const inspector = document.createElement("details");
    inspector.className = "aw-preview-hidden";
    inspector.dataset.hiddenPreviewInspector = "true";
    const summary = document.createElement("summary");
    summary.textContent = items === null ? "Hidden-item explanations" : `Hidden questions and content (${items.length})`;
    inspector.append(summary);
    if (items === null) {
      const note = document.createElement("p");
      note.textContent = "Hidden-item explanations are unavailable from this preview evaluator.";
      inspector.append(note);
    } else if (items.length === 0) {
      const note = document.createElement("p");
      note.textContent = "No questions or content are hidden for these preview answers.";
      inspector.append(note);
    } else {
      const list = document.createElement("ul");
      const makeLink = (url, text) => {
        if (typeof url !== "string") return null;
        try {
          const parsed = new URL(url, window.location.href);
          if (parsed.origin !== window.location.origin) return null;
          const link = document.createElement("a"); link.href = parsed.href; link.textContent = text; return link;
        } catch { return null; }
      };
      for (const item of items) {
        const row = document.createElement("li");
        const title = document.createElement("strong"); title.textContent = item.name || "Unnamed item";
        const kind = document.createElement("span"); kind.textContent = ` (${item.kind || "Item"})`;
        const message = document.createElement("p"); message.textContent = item.message || "This item is hidden in the current preview.";
        row.append(title, kind, message);
        const itemLink = makeLink(item.itemUrl, "Open item in the workspace");
        const configurationLabel = item.configurationUrl === "#visibility" ? "Open visibility settings"
          : item.configurationUrl?.startsWith("#element-") ? "Open parent question" : "Open dynamic text settings";
        const configurationLink = makeLink(item.configurationUrl, configurationLabel);
        if (itemLink) row.append(itemLink);
        if (configurationLink) { configurationLink.classList.add("aw-preview-hidden-config-link"); row.append(configurationLink); }
        list.append(row);
      }
      inspector.append(list);
    }
    host.append(inspector);
  };
  const appendFailedValidationInspector = (host, rules) => {
    const inspector = document.createElement("details");
    inspector.className = "aw-preview-validation";
    inspector.dataset.previewValidationInspector = "true";
    const summary = document.createElement("summary");
    summary.textContent = rules === null ? "Combined-validation explanations" : `Failed combined validation rules (${rules.length})`;
    inspector.append(summary);
    if (rules === null) {
      const note = document.createElement("p");
      note.textContent = "Combined-validation explanations are unavailable for this evaluator or configuration.";
      inspector.append(note);
    } else if (rules.length === 0) {
      const note = document.createElement("p");
      note.textContent = "No combined validation rules failed for these preview answers.";
      inspector.append(note);
    } else {
      const note = document.createElement("p");
      note.textContent = "Preview lists evaluated failures regardless of the live-feedback setting. The question field shows only the first issue for that question, which may be a required-answer or another rule message.";
      inspector.append(note);
      const list = document.createElement("ul");
      const makeLink = (url, label) => {
        if (typeof url !== "string") return null;
        try {
          const parsed = new URL(url, window.location.href);
          if (parsed.origin !== window.location.origin) return null;
          const link = document.createElement("a"); link.href = parsed.href; link.textContent = label; return link;
        } catch { return null; }
      };
      const formatValue = (value, text) => typeof text === "string" ? text : value === null || value === undefined ? "unavailable" : String(value);
      for (const rule of rules) {
        const row = document.createElement("li");
        const title = document.createElement("strong"); title.textContent = rule.name || "Unnamed validation rule";
        const target = document.createElement("p"); target.textContent = `Targets ${rule.targetName || "an unknown question"}.`;
        const message = document.createElement("p"); message.textContent = `Configured message: ${rule.message || "No message configured."}`;
        const required = document.createElement("p");
        required.textContent = rule.anyCheck ? "This rule requires at least one check to pass." : "This rule requires all checks to pass.";
        const live = document.createElement("p");
        live.textContent = rule.showWhileAnswering
          ? "Live feedback is enabled while answering in the target category; navigation, review, and submission also check this rule."
          : "Live feedback is disabled; navigation, review, and submission still check this rule.";
        row.append(title, target, message, required, live);
        const visited = document.createElement("ul");
        for (const check of rule.checks || []) {
          const detail = document.createElement("li");
          const condition = document.createElement("p"); condition.textContent = `Required: ${check.requirement || "Incomplete check"}`;
          const outcome = document.createElement("p");
          outcome.textContent = `${check.matches ? "Passed" : "Failed"}: ${check.leftDescription || "Left value"} used ${formatValue(check.leftValue, check.leftValueText)}; ${check.rightDescription || "right value"} used ${formatValue(check.rightValue, check.rightValueText)}.`;
          detail.append(condition, outcome);
          for (const effectText of [...(check.leftEffects || []), ...(check.rightEffects || [])]) {
            const effect = document.createElement("p"); effect.textContent = effectText; detail.append(effect);
          }
          if (check.leftValue === null || check.rightValue === null) {
            const unavailable = document.createElement("p");
            unavailable.textContent = "An unavailable numeric value makes this comparison fail, including “does not equal”.";
            detail.append(unavailable);
          }
          visited.append(detail);
        }
        row.append(visited);
        if (rule.skippedCheckCount > 0) {
          const skipped = document.createElement("p");
          skipped.textContent = `${rule.skippedCheckCount} remaining check${rule.skippedCheckCount === 1 ? " was" : "s were"} not evaluated because an ALL check already failed.`;
          row.append(skipped);
        }
        const itemLink = makeLink(rule.itemUrl, "Open target question");
        const configurationLink = makeLink(rule.configurationUrl, "Edit this rule");
        if (itemLink) row.append(itemLink);
        if (configurationLink) { configurationLink.classList.add("aw-preview-validation-config-link"); row.append(configurationLink); }
        list.append(row);
      }
      inspector.append(list);
    }
    host.append(inspector);
  };
  const renderPreview = payload => {
    const result = document.querySelector("[data-preview-result]");
    previewCategory = payload.categoryIndex;
    result.innerHTML = payload.html;
    previewHasRendered = true;
    previewInFlight = false;
    const runtime = result.querySelector(".qab-runtime-preview");
    if (runtime) {
      appendFailedValidationInspector(runtime, payload.failedValidationRules ?? null);
      appendHiddenItemInspector(runtime, payload.hiddenItems ?? null);
      appendPreviewInspector(runtime, payload.score, payload.scoring ?? null);
    }
  };
  const evaluatePreview = async options => {
    const result = document.querySelector("[data-preview-result]");
    previewRequestId++;
    previewInFlight = false;
    const intentId = previewRequestId;
    result.textContent = "Saving the current draft before evaluating preview…";
    const saveOutcome = await saveCurrentDraft();
    if (intentId !== previewRequestId) return;
    if (saveOutcome !== "saved") {
      result.textContent = "The draft could not be saved, so this preview was not updated. Your edits are still in the workspace.";
      return;
    }
    const requestId = ++previewRequestId;
    previewInFlight = true;
    const revision = form.querySelector("[name='ExpectedRevision']").value;
    const requestVersion = dirtyVersion;
    const token = form.querySelector("[name='__RequestVerificationToken']").value;
    result.textContent = "Evaluating preview…";
    try {
      const response = await fetch(previewButton.dataset.previewUrl, { method:"POST", credentials:"same-origin",
        headers:{ "Content-Type":"application/json", "Accept":"application/json", "RequestVerificationToken":token },
        body:JSON.stringify({ answers:previewAnswers, categoryIndex:previewCategory, expectedRevision:revision, ...options }) });
      const payload = await response.json();
      if (!response.ok) throw new Error(payload.message || "Preview could not be evaluated.");
      if (requestId !== previewRequestId) return;
      if (requestVersion !== dirtyVersion || requestVersion !== savedVersion || revision !== form.querySelector("[name='ExpectedRevision']").value) {
        invalidatePreviewExplanations();
        return;
      }
      if (payload.revision !== revision) throw new Error("The draft changed during preview evaluation. Reload the workspace before updating this preview.");
      renderPreview(payload);
    } catch (error) { if (requestId === previewRequestId) { previewInFlight = false; result.textContent = error.message; } }
  };
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
  previewResult?.addEventListener("input", event => {
    if (event.target.closest("[data-preview-runtime-form]")) {
      previewAnswerVersion++;
      collectPreviewAnswers();
      invalidatePreviewExplanations("answers");
    }
  });
  previewButton && previewButton.addEventListener("click", async () => {
    if (previewResult.querySelector("[data-preview-runtime-form]")) collectPreviewAnswers();
    await evaluatePreview();
  });
  const scenarioPanel = document.querySelector("[data-preview-scenarios]");
  if (scenarioPanel) {
    const scenarioList = scenarioPanel.querySelector("[data-scenario-list]");
    const scenarioResults = scenarioPanel.querySelector("[data-scenario-results]");
    const scenarioNotice = scenarioPanel.querySelector("[data-scenario-status]");
    const scenarioName = scenarioPanel.querySelector("[data-scenario-name]");
    const expectedValidity = scenarioPanel.querySelector("[data-scenario-validity]");
    const expectedTotal = scenarioPanel.querySelector("[data-scenario-total]");
    const saveScenarioButton = scenarioPanel.querySelector("[data-scenario-save]");
    const cancelScenarioButton = scenarioPanel.querySelector("[data-scenario-cancel]");
    const runScenariosButton = scenarioPanel.querySelector("[data-scenario-run]");
    const token = form.querySelector("[name='__RequestVerificationToken']").value;
    let scenarioRows = [], editingScenario = null, editingAnswers = null, scenarioListRequest = 0, scenarioOperation = 0,
      scenarioFieldVersion = 0, scenarioMutationInFlight = false, scenarioRunsCurrent = false;
    let scenarioBaseline = { name:"", validity:"ignore", total:"" };
    const scenarioFieldsChanged = () => scenarioName.value !== scenarioBaseline.name
      || expectedValidity.value !== scenarioBaseline.validity || expectedTotal.value !== scenarioBaseline.total;
    hasPendingScenarioEdits = () => scenarioFieldsChanged() || scenarioMutationInFlight;
    const scenarioDraft = () => ({ applicationId:Number(scenarioPanel.dataset.applicationId),
      expectedVersionId:Number(scenarioPanel.dataset.versionId), expectedRevision:form.querySelector("[name='ExpectedRevision']").value });
    const scenarioStatus = (message, state = "") => {
      if (scenarioNotice) { scenarioNotice.dataset.state = state; scenarioNotice.textContent = message; }
    };
    const scenarioPost = async (url, body) => {
      const response = await fetch(url, { method:"POST", credentials:"same-origin", headers:{
        "Content-Type":"application/json", "Accept":"application/json", "RequestVerificationToken":token }, body:JSON.stringify(body) });
      const value = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(value.message || "The preview scenario request could not be completed.");
      return value;
    };
    const expectationText = scenario => {
      const parts = [];
      if (scenario.expectedValid !== null && scenario.expectedValid !== undefined) parts.push(`expects ${scenario.expectedValid ? "valid" : "invalid"} answers`);
      if (scenario.expectedTotal !== null && scenario.expectedTotal !== undefined) parts.push(`expects ${scenario.expectedTotal} points`);
      return parts.join("; ");
    };
    const draftIsSaved = async () => {
      clearTimeout(timer);
      if (await saveCurrentDraft() === "saved") return true;
      scenarioStatus("Save the current draft successfully before using preview scenarios.", "error");
      return false;
    };
    const renderScenarioRows = () => {
      scenarioList.replaceChildren();
      if (!scenarioRows.length) {
        const empty = document.createElement("p"); empty.textContent = "No preview scenarios saved."; scenarioList.append(empty); return;
      }
      const list = document.createElement("ul");
      for (const scenario of scenarioRows) {
        const row = document.createElement("li"); row.dataset.scenarioId = scenario.id;
        const name = document.createElement("strong"); name.textContent = scenario.name;
        const expectation = document.createElement("span"); expectation.textContent = expectationText(scenario);
        row.append(name, document.createTextNode(expectation.textContent ? ` — ${expectation.textContent}` : ""));
        const actions = document.createElement("div"); actions.className = "aw-preview-scenario-actions";
        const makeButton = (label, handler) => { const button = document.createElement("button"); button.type = "button"; button.className = "aw-secondary"; button.textContent = label; button.addEventListener("click", handler); return button; };
        actions.append(
          makeButton("Load answers", () => void loadScenario(scenario)),
          makeButton("Run", () => void runScenarios(scenario.id)),
          makeButton("Edit name/expectation", () => void loadScenarioForEdit(scenario)),
          makeButton("Delete", () => void deleteScenario(scenario)));
        row.append(actions); list.append(row);
      }
      scenarioList.append(list);
    };
    const refreshScenarioList = async () => {
      const requestId = ++scenarioListRequest;
      try {
        const response = await fetch(scenarioPanel.dataset.listUrl, { credentials:"same-origin", headers:{ "Accept":"application/json" } });
        if (!response.ok) throw new Error("Saved preview scenarios could not be loaded.");
        const rows = await response.json();
        if (requestId !== scenarioListRequest) return;
        scenarioRows = Array.isArray(rows) ? rows : []; renderScenarioRows();
      } catch (error) { if (requestId === scenarioListRequest) scenarioStatus(error.message || "Saved preview scenarios could not be loaded.", "error"); }
    };
    const readExpectation = () => {
      const validity = expectedValidity.value === "valid" ? true : expectedValidity.value === "invalid" ? false : null;
      const totalText = expectedTotal.value.trim();
      const total = totalText === "" ? null : Number(totalText);
      if (total !== null && (!Number.isSafeInteger(total) || total < 0 || total > 2147483647)) throw new Error("Expected total points must be a whole number from 0 to 2,147,483,647.");
      if (validity === null && total === null) throw new Error("Set an expected validity or expected total before saving this scenario.");
      return { expectedValid:validity, expectedTotal:total };
    };
    const copyScenarioAnswers = answers => JSON.parse(JSON.stringify(answers || {}));
    const resetScenarioForm = () => {
      scenarioOperation++;
      editingScenario = null; editingAnswers = null;
      scenarioName.value = ""; expectedValidity.value = "ignore"; expectedTotal.value = "";
      scenarioBaseline = { name:"", validity:"ignore", total:"" };
      scenarioFieldVersion++; saveScenarioButton.textContent = "Save current answers as a scenario";
      scenarioStatus("");
    };
    const loadScenarioForEdit = async scenario => {
      if (scenarioMutationInFlight) { scenarioStatus("Wait for the scenario change to finish before editing another scenario.", "pending"); return; }
      if (scenarioFieldsChanged()) { scenarioStatus("Save or clear your current scenario edits before editing another scenario.", "error"); return; }
      const operation = ++scenarioOperation;
      const fieldVersion = scenarioFieldVersion;
      if (!await draftIsSaved() || operation !== scenarioOperation) return;
      if (fieldVersion !== scenarioFieldVersion) { scenarioStatus("Scenario fields changed while settings were loading. Review them and try again.", "error"); return; }
      const draft = scenarioDraft(), revision = draft.expectedRevision, versionId = draft.expectedVersionId;
      try {
        scenarioStatus(`Loading saved settings for “${scenario.name}”…`, "pending");
        const loaded = await scenarioPost(scenarioPanel.dataset.loadUrl, { draft, scenarioId:scenario.id });
        if (operation !== scenarioOperation) return;
        if (fieldVersion !== scenarioFieldVersion) { scenarioStatus("Scenario fields changed while settings were loading. Review them and try again.", "error"); return; }
        if (loaded.versionId !== versionId || loaded.draftRevision !== revision
            || revision !== form.querySelector("[name='ExpectedRevision']").value || dirtyVersion !== savedVersion) {
          scenarioStatus("The draft changed while scenario settings were loading. Save and try again.", "error"); return;
        }
        if (loaded.repairIssues?.length) { scenarioStatus(`This scenario needs repair before it can be edited: ${loaded.repairIssues.join(" ")} Save a replacement using the current preview answers, then delete this scenario.`, "error"); return; }
        editingScenario = { ...scenario, revision:loaded.scenario.revision };
        editingAnswers = loaded.answers || {};
        scenarioName.value = loaded.scenario.name;
        expectedValidity.value = loaded.scenario.expectedValid === null || loaded.scenario.expectedValid === undefined ? "ignore" : loaded.scenario.expectedValid ? "valid" : "invalid";
        expectedTotal.value = loaded.scenario.expectedTotal === null || loaded.scenario.expectedTotal === undefined ? "" : String(loaded.scenario.expectedTotal);
        scenarioBaseline = { name:scenarioName.value, validity:expectedValidity.value, total:expectedTotal.value };
        saveScenarioButton.textContent = "Update name/expectation";
        scenarioStatus("Editing the saved name and expectations. The saved answers will be preserved.");
        scenarioName.focus();
      } catch (error) { if (operation === scenarioOperation) scenarioStatus(error.message || "Scenario settings could not be loaded.", "error"); }
    };
    [scenarioName, expectedValidity, expectedTotal].forEach(control => control.addEventListener("input", () => {
      scenarioFieldVersion++;
      if (scenarioFieldsChanged()) scenarioStatus("Preview scenario changes are not saved yet.", "pending");
      else scenarioStatus("");
    }));
    cancelScenarioButton.addEventListener("click", () => {
      if (scenarioMutationInFlight) { scenarioStatus("Wait for the scenario change to finish before clearing these fields.", "pending"); return; }
      if (scenarioFieldsChanged() && !window.confirm("Clear the unsaved scenario name and expectations?")) return;
      resetScenarioForm();
    });
    saveScenarioButton.addEventListener("click", async () => {
      if (scenarioMutationInFlight) return;
      scenarioMutationInFlight = true;
      saveScenarioButton.disabled = true;
      const operation = ++scenarioOperation;
      const submittedFieldVersion = scenarioFieldVersion;
      try {
        const name = scenarioName.value.trim();
        if (!name) throw new Error("Enter a name for this preview scenario.");
        const expectation = readExpectation();
        if (!await draftIsSaved()) return;
        if (operation !== scenarioOperation) return;
        if (scenarioFieldVersion !== submittedFieldVersion) {
          scenarioStatus("Scenario fields changed while the draft was saving. Review them and click Save again.", "error"); return;
        }
        if (!editingScenario && previewResult.querySelector("[data-preview-runtime-form]")) collectPreviewAnswers();
        const submittedName = name;
        const submittedValidity = expectation.expectedValid === null ? "ignore" : expectation.expectedValid ? "valid" : "invalid";
        const submittedTotal = expectation.expectedTotal === null ? "" : String(expectation.expectedTotal);
        const submittedAnswers = copyScenarioAnswers(editingScenario ? editingAnswers : previewAnswers);
        scenarioStatus("Saving preview scenario…", "pending");
        const saved = await scenarioPost(scenarioPanel.dataset.saveUrl, { draft:scenarioDraft(),
          scenarioId:editingScenario?.id ?? null, expectedScenarioRevision:editingScenario?.revision ?? null,
          name, answers:submittedAnswers, ...expectation });
        if (operation !== scenarioOperation) return;
        editingScenario = saved; editingAnswers = submittedAnswers;
        saveScenarioButton.textContent = "Update name/expectation";
        scenarioBaseline = { name:saved.name, validity:submittedValidity, total:submittedTotal };
        if (scenarioFieldVersion === submittedFieldVersion) {
          scenarioName.value = scenarioBaseline.name;
          expectedValidity.value = scenarioBaseline.validity;
          expectedTotal.value = scenarioBaseline.total;
        }
        await refreshScenarioList();
        scenarioStatus(scenarioFieldVersion === submittedFieldVersion
          ? `Saved “${saved.name}”.` : `Saved “${saved.name}”. Newer scenario field edits remain unsaved.`,
          scenarioFieldVersion === submittedFieldVersion ? "saved" : "pending");
        invalidateScenarioRuns("Scenario definitions changed. Run scenarios again to refresh these results.");
      } catch (error) { if (operation === scenarioOperation) scenarioStatus(error.message || "The preview scenario could not be saved.", "error"); }
      finally { scenarioMutationInFlight = false; saveScenarioButton.disabled = false; }
    });
    const loadScenario = async scenario => {
      let operation = 0;
      try {
        if (scenarioMutationInFlight) { scenarioStatus("Wait for the scenario change to finish before loading answers.", "pending"); return; }
        if (scenarioFieldsChanged()) { scenarioStatus("Save or clear your scenario edits before loading another scenario.", "error"); return; }
        operation = ++scenarioOperation;
        const answerVersion = previewAnswerVersion;
        const fieldVersion = scenarioFieldVersion;
        const previewIntent = ++previewRequestId; previewInFlight = false;
        if (!await draftIsSaved() || operation !== scenarioOperation) return;
        if (previewAnswerVersion !== answerVersion || previewRequestId !== previewIntent || scenarioFieldVersion !== fieldVersion) {
          scenarioStatus("Preview answers changed before loading began. Try loading again.", "error"); return;
        }
        const draft = scenarioDraft(), revision = draft.expectedRevision, versionId = draft.expectedVersionId;
        scenarioStatus(`Loading answers from “${scenario.name}”…`, "pending");
        const loaded = await scenarioPost(scenarioPanel.dataset.loadUrl, { draft, scenarioId:scenario.id });
        if (operation !== scenarioOperation) return;
        if (loaded.versionId !== versionId || loaded.draftRevision !== revision
            || previewRequestId !== previewIntent || previewAnswerVersion !== answerVersion || scenarioFieldVersion !== fieldVersion
            || dirtyVersion !== savedVersion || revision !== form.querySelector("[name='ExpectedRevision']").value) {
          scenarioStatus("The draft or preview answers changed while the scenario was loading. Load it again.", "error"); return;
        }
        if (loaded.repairIssues?.length) {
          scenarioStatus(`This scenario needs repair and cannot be loaded: ${loaded.repairIssues.join(" ")} Delete it and save a replacement using the current preview answers.`, "error"); return;
        }
        previewAnswers = loaded.answers || {};
        previewCategory = 0;
        previewAnswerVersion++;
        await evaluatePreview();
      } catch (error) {
        if (operation === scenarioOperation) scenarioStatus(error.message || "The scenario answers could not be loaded.", "error");
      }
    };
    const renderScenarioRuns = report => {
      scenarioRunsCurrent = true;
      scenarioResults.dataset.stale = "false";
      if (scenarioNotice) scenarioNotice.textContent = "";
      scenarioResults.replaceChildren();
      if (!report.results?.length) { const empty = document.createElement("p"); empty.textContent = "No scenarios saved to run."; scenarioResults.append(empty); return; }
      const list = document.createElement("ul");
      for (const result of report.results) {
        const row = document.createElement("li"); row.dataset.runStatus = result.status;
        const visibleStatus = result.status === "NeedsRepair" ? "Needs repair" : result.status;
        const heading = document.createElement("strong"); heading.textContent = `${visibleStatus}: ${result.name}`; row.append(heading);
        const expected = document.createElement("p");
        const expectedParts = [];
        if (result.expectedValid !== null && result.expectedValid !== undefined) expectedParts.push(`validity ${result.expectedValid ? "valid" : "invalid"}`);
        if (result.expectedTotal !== null && result.expectedTotal !== undefined) expectedParts.push(`total ${result.expectedTotal} points`);
        expected.textContent = expectedParts.length ? `Expected: ${expectedParts.join("; ")}.` : "No expected result configured.";
        row.append(expected);
        const actual = document.createElement("p"); actual.textContent = `Actual: ${result.actualValid === null ? "not evaluated" : result.actualValid ? "valid" : "invalid"}${result.actualTotal === null ? "" : `; ${result.actualTotal} points`}.`;
        row.append(actual);
        for (const message of result.messages || []) { const note = document.createElement("p"); note.textContent = message; row.append(note); }
        list.append(row);
      }
      scenarioResults.append(list);
    };
    const invalidateScenarioRuns = message => {
      if (!scenarioRunsCurrent) return;
      scenarioRunsCurrent = false;
      scenarioResults.dataset.stale = "true";
      const stale = document.createElement("p"); stale.dataset.scenarioStale = "true"; stale.textContent = message;
      scenarioResults.querySelector("[data-scenario-stale]")?.remove(); scenarioResults.append(stale);
      scenarioStatus(message, "stale");
    };
    markScenarioRunsStale = () => {
      invalidateScenarioRuns("The draft changed after these results. Save and run the scenarios again.");
    };
    const runScenarios = async scenarioId => {
      let operation = 0;
      try {
        if (scenarioMutationInFlight) { scenarioStatus("Wait for the scenario change to finish before running scenarios.", "pending"); return; }
        operation = ++scenarioOperation;
        if (!await draftIsSaved() || operation !== scenarioOperation) return;
        const runVersion = dirtyVersion, revision = form.querySelector("[name='ExpectedRevision']").value;
        scenarioStatus(scenarioId ? "Running preview scenario…" : "Running all preview scenarios…", "pending");
        const report = await scenarioPost(scenarioPanel.dataset.runUrl, { draft:scenarioDraft(), scenarioId:scenarioId ?? null });
        if (operation !== scenarioOperation) return;
        if (runVersion !== dirtyVersion || runVersion !== savedVersion || revision !== form.querySelector("[name='ExpectedRevision']").value
            || report.draftRevision !== revision) {
          scenarioStatus("The draft changed while scenarios were running. Save and run them again.", "error"); return;
        }
        renderScenarioRuns(report);
      } catch (error) { if (operation === scenarioOperation) scenarioStatus(error.message || "The preview scenarios could not be run.", "error"); }
    };
    const deleteScenario = async scenario => {
      if (scenarioMutationInFlight) { scenarioStatus("Wait for the scenario change to finish before deleting a scenario.", "pending"); return; }
      if (!window.confirm(`Delete preview scenario “${scenario.name}"?${editingScenario?.id === scenario.id && scenarioFieldsChanged() ? " Unsaved scenario edits will also be discarded." : ""}`)) return;
      if (scenarioMutationInFlight) { scenarioStatus("Wait for the scenario change to finish before deleting a scenario.", "pending"); return; }
      scenarioMutationInFlight = true;
      const operation = ++scenarioOperation;
      try {
        scenarioStatus(`Deleting “${scenario.name}”…`, "pending");
        await scenarioPost(scenarioPanel.dataset.deleteUrl, { scenarioId:scenario.id, expectedScenarioRevision:scenario.revision });
        if (operation !== scenarioOperation) return;
        if (editingScenario?.id === scenario.id) {
          resetScenarioForm();
        }
        invalidateScenarioRuns("A scenario was deleted. Run scenarios again to refresh these results.");
        await refreshScenarioList();
      } catch (error) { if (operation === scenarioOperation) scenarioStatus(error.message || "The preview scenario could not be deleted.", "error"); }
      finally { scenarioMutationInFlight = false; }
    };
    runScenariosButton.addEventListener("click", () => void runScenarios(null));
    void refreshScenarioList();
  }
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
      window.qabAuthoringContext?.remember();
      window.location.reload();
    });
  }
  const followUpForm = document.querySelector('[data-follow-up-form]');
  if (followUpForm) {
    followUpForm.addEventListener('submit', async event => {
      event.preventDefault(); clearTimeout(timer);
      const status = followUpForm.querySelector('[data-follow-up-status]');
      const submit = followUpForm.querySelector('button[type="submit"]');
      if (submit.disabled) return;
      submit.disabled = true;
      try {
        if (await saveCurrentDraft() !== 'saved') return;
        status.textContent = 'Adding follow-up question…';
        followUpForm.querySelector('[name="expectedRevision"]').value = form.querySelector('[name="ExpectedRevision"]').value;
        const response = await fetch(followUpForm.action, { method: 'POST', credentials: 'same-origin', body: new FormData(followUpForm),
          headers: { 'Accept': 'application/json', 'X-Requested-With': 'QuestionAppBuilder' } });
        const result = await response.json().catch(() => ({}));
        if (!response.ok) throw new Error(result.message || 'The follow-up could not be added. Your entries are still here.');
        window.qabAuthoringContext?.clear();
        const destination = new URL(location.href); destination.searchParams.delete('followUpOption'); destination.hash = `element-${result.elementId}`;
        history.replaceState(null, "", destination.href); location.reload();
      } catch (error) { status.textContent = error.message; }
      finally { submit.disabled = false; }
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
      window.qabAuthoringContext?.remember();
      window.location.reload();
    } catch (error) { announce(error.message || "The structure change could not be saved.", "error"); }
  }));
})();
