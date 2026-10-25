(() => {
  const form = document.querySelector('[data-composition-form]');
  if (!form) return;
  const catalog = JSON.parse(document.querySelector('[data-composition-catalog]').textContent);
  const state = JSON.parse(document.querySelector('[data-composition-definition]').textContent);
  for (const items of Object.values(state)) if (Array.isArray(items)) items.forEach((item, index) => { if (typeof item._open !== 'boolean') item._open = index === 0; });
  state.DynamicText ||= { TimeZoneId: 'UTC', Variables: [], Templates: [] };
  state.DynamicText.Variables ||= [];
  state.DynamicText.Templates ||= [];
  const editor = form.querySelector('[data-composition-editor]');
  const status = form.querySelector('[data-composition-status]');
  const serializeDefinition = () => JSON.stringify(state, (key, value) => key === '_open' ? undefined : value);
  let pane = 'Groups', dirty = false, saving = false, saveUncertain = false, submittedDefinition = null;
  let focusSequence = 0, pendingFocusKey = null, savedDefinition = serializeDefinition();
  let recipeDirty = false, recipeProposal = null, recipeFingerprint = null;
  const mark = () => {
    const currentDefinition = serializeDefinition();
    if (recipeProposal && recipeProposal.definitionFingerprint !== currentDefinition) {
      recipeProposal = null; recipeFingerprint = null;
      const preview = form.querySelector('[data-limit-preview-result]');
      const add = form.querySelector('[data-limit-add]');
      if (preview) { preview.replaceChildren(); preview.hidden = true; }
      if (add) add.disabled = true;
      const recipeStatus = form.querySelector('[data-limit-status]');
      if (recipeStatus) recipeStatus.textContent = 'Configuration changed. Preview this limit again before adding it.';
    }
    dirty = currentDefinition !== savedDefinition || saveUncertain || saving && currentDefinition !== submittedDefinition;
    status.textContent = saving
      ? currentDefinition === submittedDefinition ? 'Saving configuration…' : 'Saving configuration. Newer edits still need saving.'
      : dirty ? 'Unsaved configuration changes. Select Save configuration to keep them.'
        : recipeDirty ? 'Limit recipe is not added. Add it or discard it before saving.' : 'All changes saved.';
    status.classList.remove('is-error');
  };
  const node = (tag, text, cls) => { const n = document.createElement(tag); if (text) n.textContent = text; if (cls) n.className = cls; return n; };
  const makeFocusable = (element, key) => { element.dataset.focusKey = key || `focus-${focusSequence++}`; return element; };
  const action = (parent, text, run, options = {}) => { const b = makeFocusable(node('button', text), options.focusKey); b.type = 'button'; if (options.disabled) b.disabled = true; b.addEventListener('click', () => { run(); mark(); }); parent.append(b); return b; };
  const input = (parent, title, value, change, type = 'text', focusKey = null) => {
    const label = node('label', title, 'composition-field'), i = document.createElement('input');
    makeFocusable(i, focusKey); i.setAttribute('aria-label', title); i.type = type; i.value = value ?? ''; if (type === 'text') i.maxLength = 2000; if (type === 'number') i.step = 'any';
    i.addEventListener('input', () => { change(type === 'number' ? Number(i.value) : i.value); mark(); });
    label.append(i); parent.append(label); return i;
  };
  const select = (parent, title, value, choices, change, focusKey = null) => {
    const label = node('label', title, 'composition-field'), s = makeFocusable(document.createElement('select'), focusKey);
    s.setAttribute('aria-label', title);
    for (const [key, text] of choices) { const o = node('option', text); o.value = key; o.selected = String(key) === String(value); s.append(o); }
    s.addEventListener('change', () => { change(s.value); mark(); }); label.append(s); parent.append(label); return s;
  };
  const cellInput = (parent, title, value, change, focusKey) => {
    const i = makeFocusable(document.createElement('input'), focusKey);
    i.type = 'text'; i.maxLength = 2000; i.value = value ?? ''; i.className = 'composition-cell-input'; i.setAttribute('aria-label', title);
    i.addEventListener('input', () => { change(i.value); mark(); }); parent.append(i); return i;
  };
  const categories = () => catalog.Categories.map(c => [c.Key, c.Name]);
  const categoryName = key => catalog.Categories.find(category => category.Key === key)?.Name || 'Category not selected';
  const summaryMeta = (item, currentPane) => {
    if (currentPane === 'Groups') return `${categoryName(item.CategoryKey)} · ${item.Rows.length} question${item.Rows.length === 1 ? '' : 's'} · ${item.Columns.length} column${item.Columns.length === 1 ? '' : 's'}`;
    if (currentPane === 'Calculations') return `${categoryName(item.CategoryKey)} · ${item.Sources.length} source${item.Sources.length === 1 ? '' : 's'} · ${item.Operation}`;
    const checkCount = Array.isArray(item.Checks) ? item.Checks.length : 0;
    const purpose = item.Kind === 'Validation' ? 'Validation' : item.Kind === 'Score' ? 'Scoring' : 'Unknown purpose';
    return `${categoryName(item.CategoryKey)} · ${purpose} · ${checkCount} check${checkCount === 1 ? '' : 's'}`;
  };
  const numericValue = value => value === null || value === undefined || typeof value === 'string' && !value.trim()
    ? null : Number.isFinite(Number(value)) ? Number(value) : null;
  const formatRuleNumber = value => {
    const number = numericValue(value);
    if (number === null) return 'Missing number';
    if (number >= 1e21 || number <= -1e21 || Number(number.toFixed(20)) !== number) return String(number);
    return new Intl.NumberFormat(undefined, { maximumFractionDigits: 20 }).format(number);
  };
  const sourceDescription = source => {
    if (!source || typeof source !== 'object') return 'Missing source — choose a value';
    if (source.Kind === 'Constant') return formatRuleNumber(source.Value);
    if (source.Kind === 'Question') {
      const question = catalog.Questions.find(item => String(item.Key).toLowerCase() === String(source.Key).toLowerCase());
      return question ? question.Numeric ? question.Label : `Unsupported question — ${question.Label} is not numeric` : 'Missing question — choose another';
    }
    if (source.Kind === 'Calculation') {
      const calculation = state.Calculations.find(item => String(item.Id).toLowerCase() === String(source.Key).toLowerCase());
      return calculation ? `calculated ${calculation.Name || 'Untitled calculation'}` : 'Missing calculation — choose another';
    }
    if (source.Kind === 'OptionSelected') {
      const question = catalog.Questions.find(item => item.Options.some(option => String(option.Key).toLowerCase() === String(source.Key).toLowerCase()));
      const option = question?.Options.find(item => String(item.Key).toLowerCase() === String(source.Key).toLowerCase());
      return option ? `1 when ${question.Label} / ${option.Label} is selected and visible; otherwise 0` : 'Missing option — choose another';
    }
    return 'Unsupported source — choose a supported value';
  };
  const comparisonText = comparison => ({ Equal: 'equals', NotEqual: 'does not equal', GreaterThan: 'is greater than',
    GreaterThanOrEqual: 'is at least', LessThan: 'is less than', LessThanOrEqual: 'is at most' })[comparison] || null;
  const checkDescription = check => {
    const comparison = comparisonText(check?.Comparison);
    if (!check || !comparison) return 'Incomplete check — choose a supported comparison';
    return `${sourceDescription(check.Left)} ${comparison} ${sourceDescription(check.Right)}`;
  };
  const sourceIsComplete = source => {
    if (!source || typeof source !== 'object') return false;
    if (source.Kind === 'Constant') return numericValue(source.Value) !== null;
    if (source.Kind === 'Question') return catalog.Questions.some(question => question.Numeric && String(question.Key).toLowerCase() === String(source.Key).toLowerCase());
    if (source.Kind === 'Calculation') return state.Calculations.some(calculation => String(calculation.Id).toLowerCase() === String(source.Key).toLowerCase());
    if (source.Kind === 'OptionSelected') return catalog.Questions.some(question => question.Options.some(option => String(option.Key).toLowerCase() === String(source.Key).toLowerCase()));
    return false;
  };
  const checkIsComplete = check => !!comparisonText(check?.Comparison) && sourceIsComplete(check.Left) && sourceIsComplete(check.Right);
  const conditionsFor = item => Array.isArray(item.Conditions) ? item.Conditions : [];
  const checksFor = item => Array.isArray(item.Checks) ? item.Checks : [];
  const conditionDescription = item => !conditionsFor(item).length ? 'Always' : `all of: ${conditionsFor(item).map(checkDescription).join('; ')}`;
  const requiredChecksDescription = item => !checksFor(item).length ? 'Incomplete — add at least one check'
    : `${item.AnyCheck ? 'any of' : 'all of'}: ${checksFor(item).map(checkDescription).join('; ')}`;
  const requiredChecksCompact = item => {
    const checks = checksFor(item);
    if (!checks.length) return 'Incomplete — add at least one check';
    if (checks.length === 1) return `Require: ${checkDescription(checks[0])}`;
    return `Require ${item.AnyCheck ? 'any of' : 'all'} ${checks.length} checks`;
  };
  const conditionSummary = conditions => conditions.length === 0 ? ''
    : ` · applies when ${conditions.length === 1 ? 'the condition matches' : `all ${conditions.length} conditions match`}`;
  const ruleTarget = item => {
    const category = catalog.Categories.find(entry => String(entry.Key).toLowerCase() === String(item.CategoryKey).toLowerCase());
    if (!category) return { text: 'Missing category — choose a category', complete: false };
    if (!item.TargetQuestionKey || String(item.TargetQuestionKey).toLowerCase() === emptyGuid)
      return { text: `first visible question in ${category.Name}; skipped if none is visible`, complete: true };
    const question = catalog.Questions.find(entry => String(entry.Key).toLowerCase() === String(item.TargetQuestionKey).toLowerCase());
    if (!question) return { text: 'Missing target question — choose another', complete: false };
    if (String(question.CategoryKey).toLowerCase() !== String(item.CategoryKey).toLowerCase())
      return { text: `Target is in another category — choose a question in ${category.Name}`, complete: false };
    return { text: `${question.Label} in ${category.Name} (runs only while this target is visible)`, complete: true };
  };
  const ruleOutcome = item => {
    const checks = checksFor(item), conditions = conditionsFor(item), target = ruleTarget(item);
    if (!Array.isArray(item.Checks) || !Array.isArray(item.Conditions) || !['Validation', 'Score'].includes(item.Kind) || !target.complete || !checks.length || !checks.every(checkIsComplete)
        || !conditions.every(checkIsComplete) || typeof item.AnyCheck !== 'boolean') return 'Incomplete — resolve target, checks, or sources';
    if (item.Kind === 'Validation') {
      if (!String(item.Message || '').trim()) return 'Incomplete — add a validation message';
      return `${requiredChecksCompact(item)}; show message when unmet${conditionSummary(conditions)}`;
    }
    const points = numericValue(item.Points);
    if (points === null || !Number.isInteger(points) || points < 0 || points > 1000000) return 'Incomplete — points must be a whole number from 0 to 1,000,000';
    const band = String(item.BandGroup || '').trim();
    const checksText = checks.length === 1 ? checkDescription(checks[0]) : `${item.AnyCheck ? 'any of' : 'all of'} ${checks.length} checks`;
    return `Award ${formatRuleNumber(points)} points when ${checksText} ${checks.length === 1 ? 'matches' : 'match'}${band ? ` · first in “${band}” band` : ' · independent award'}${conditionSummary(conditions)}`;
  };
  const updateRuleDescription = (description, item) => {
    if (!description) return;
    description.replaceChildren();
    const addLine = (label, text) => {
      const line = node('p', '', 'composition-rule-description-line');
      line.append(node('strong', `${label}: `), document.createTextNode(text));
      description.append(line);
    };
    addLine('Target', ruleTarget(item).text);
    addLine('Applies when', conditionDescription(item));
    addLine(item.Kind === 'Validation' ? 'Must satisfy' : 'Award when', requiredChecksDescription(item));
    if (item.Kind === 'Validation') {
      addLine('Message when unmet', String(item.Message || '').trim() || 'Incomplete — add a validation message');
      addLine('Timing', item.ShowWhileAnswering === true
        ? 'Also shown after a short typing pause; navigation and submission checks still apply.'
        : 'Checked during navigation and submission; it must pass before submission.');
    } else if (item.Kind === 'Score') {
      const band = String(item.BandGroup || '').trim();
      const points = numericValue(item.Points);
      addLine('Award', points !== null && Number.isInteger(points) && points >= 0 && points <= 1000000
        ? `${formatRuleNumber(points)} points when the required checks pass.`
        : 'Incomplete — points must be a whole number from 0 to 1,000,000.');
      addLine('Scoring band', band
        ? `“${band}” awards only the first matching rule in this category, in rule order (case-insensitive; surrounding spaces are ignored).`
        : 'Blank band; this award is independent of other rules.');
    } else addLine('Configuration', 'Incomplete — choose Validation or Scoring.');
    addLine('Numeric sources', 'Blank or hidden numeric answers read as 0. If a calculation is unavailable, its comparisons are false.');
  };
  const updateSummary = (summary, item, currentPane) => {
    summary.replaceChildren(node('span', item.Name || 'Untitled', 'composition-summary-name'), node('span', summaryMeta(item, currentPane), 'composition-summary-meta'));
    if (currentPane === 'Rules') summary.append(node('span', ruleOutcome(item), 'composition-summary-outcome'));
  };
  const iconAction = (parent, label, iconName, run, focusKey = null, focusAfter = null) => {
    const button = makeFocusable(node('button', '', 'composition-icon-button'), focusKey);
    button.type = 'button'; button.setAttribute('aria-label', label); button.title = label;
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('viewBox', '0 0 16 16'); svg.setAttribute('aria-hidden', 'true');
    const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    path.setAttribute('d', iconName === 'up' ? 'M8 13V3 M4 7l4-4 4 4' : iconName === 'down' ? 'M8 3v10 M4 9l4 4 4-4' : 'M4 4l8 8 M12 4l-8 8');
    path.setAttribute('fill', 'none'); path.setAttribute('stroke', 'currentColor'); path.setAttribute('stroke-width', '1.6'); path.setAttribute('stroke-linecap', 'round'); path.setAttribute('stroke-linejoin', 'round');
    svg.append(path); button.append(svg); button.addEventListener('click', () => { pendingFocusKey = focusAfter; run(); mark(); }); parent.append(button); return button;
  };
  const emptyGuid = '00000000-0000-0000-0000-000000000000';
  const setupLimitRecipe = () => {
    const panel = form.querySelector('[data-limit-recipe]');
    if (!panel) return;
    const body = panel.querySelector('[data-limit-recipe-body]');
    const toggle = panel.querySelector('[data-limit-recipe-toggle]');
    const sourceHost = panel.querySelector('[data-limit-sources]');
    const target = panel.querySelector('[data-limit-target]');
    const nameInput = panel.querySelector('[data-limit-name]');
    const maximumInput = panel.querySelector('[data-limit-maximum]');
    maximumInput.maxLength = 128;
    const messageInput = panel.querySelector('[data-limit-message]');
    const liveInput = panel.querySelector('[data-limit-live]');
    const previewButton = panel.querySelector('[data-limit-preview]');
    const addButton = panel.querySelector('[data-limit-add]');
    const discardButton = panel.querySelector('[data-limit-discard]');
    const previewHost = panel.querySelector('[data-limit-preview-result]');
    const recipeStatus = panel.querySelector('[data-limit-status]');
    const isRecovering = panel.dataset.recovering === 'true';
    const questionKeys = catalog.Questions.map(q => String(q.Key).toLowerCase());
    const categoriesByKey = catalog.Categories.filter(c => c && typeof c.Key === 'string');
    const duplicateQuestions = questionKeys.length !== new Set(questionKeys).size;
    const duplicateCategories = categoriesByKey.length !== new Set(categoriesByKey.map(c => c.Key.toLowerCase())).size;
    const ambiguous = duplicateQuestions || duplicateCategories || !Array.isArray(state.Calculations) || !Array.isArray(state.Rules);
    const eligibleSources = catalog.Questions.filter(q => q.Numeric === true && q.Key && q.CategoryKey);
    const selected = new Set();
    let readyProposal = null;
    let adding = false;
    const maxDecimal = 79228162514264337593543950335n;
    const normalizeDecimal = raw => {
      const text = String(raw ?? '').trim();
      if (!/^[+-]?(?:\d+(?:\.\d*)?|\.\d+)$/.test(text)) return null;
      const negative = text[0] === '-';
      const unsigned = /^[+-]/.test(text) ? text.slice(1) : text;
      const parts = unsigned.split('.');
      let whole = parts[0] || '0';
      let fraction = parts[1] || '';
      whole = whole.replace(/^0+(?=\d)/, '') || '0';
      fraction = fraction.replace(/0+$/, '');
      const scale = fraction.length;
      if (scale > 28) return null;
      const coefficientText = (whole + fraction).replace(/^0+/, '') || '0';
      let coefficient;
      try { coefficient = BigInt(coefficientText); } catch { return null; }
      if (coefficient > maxDecimal) return null;
      const zero = coefficient === 0n;
      const canonical = `${negative && !zero ? '-' : ''}${whole}${fraction ? `.${fraction}` : ''}`;
      const numeric = Number(canonical);
      if (!Number.isFinite(numeric)) return null;
      const serialized = JSON.stringify(numeric);
      const match = serialized.match(/^(-?)(\d+)(?:\.(\d+))?(?:e([+-]?\d+))?$/i);
      if (!match) return null;
      const sign = match[1], digits = match[2] + (match[3] || ''), point = match[2].length + Number(match[4] || 0);
      const expanded = point <= 0 ? `0.${'0'.repeat(-point)}${digits}`
        : point >= digits.length ? `${digits}${'0'.repeat(point - digits.length)}` : `${digits.slice(0, point)}.${digits.slice(point)}`;
      const roundTrip = normalizeDecimalWithoutNumber(`${sign}${expanded}`);
      return roundTrip === canonical ? { canonical, numeric } : null;
    };
    const normalizeDecimalWithoutNumber = raw => {
      const text = String(raw).trim();
      const negative = text[0] === '-';
      const unsigned = /^[+-]/.test(text) ? text.slice(1) : text;
      const parts = unsigned.split('.');
      const whole = (parts[0] || '0').replace(/^0+(?=\d)/, '') || '0';
      const fraction = (parts[1] || '').replace(/0+$/, '');
      const zero = /^0+$/.test(whole) && !fraction;
      return `${negative && !zero ? '-' : ''}${whole}${fraction ? `.${fraction}` : ''}`;
    };
    const categoryOrder = categoryKey => {
      const category = categoriesByKey.find(c => c.Key.toLowerCase() === String(categoryKey).toLowerCase());
      return category ? [Number(category.SortOrder), Number(category.Id)] : null;
    };
    const notLaterThan = (sourceQuestion, targetQuestion) => {
      const sourceOrder = categoryOrder(sourceQuestion.CategoryKey), targetOrder = categoryOrder(targetQuestion.CategoryKey);
      return !!sourceOrder && !!targetOrder && (sourceOrder[0] < targetOrder[0] || sourceOrder[0] === targetOrder[0] && sourceOrder[1] <= targetOrder[1]);
    };
    const setStatus = (message, error = false) => {
      recipeStatus.textContent = message;
      recipeStatus.classList.toggle('is-error', error);
    };
    const fingerprint = () => JSON.stringify({
      name: nameInput.value, sources: [...selected].sort(), target: target.value,
      maximum: maximumInput.value, message: messageInput.value, live: liveInput.checked
    });
    const clearProposal = () => {
      readyProposal = null;
      recipeProposal = null;
      recipeFingerprint = null;
      addButton.disabled = true;
      previewHost.replaceChildren();
      previewHost.hidden = true;
    };
    const invalidate = () => {
      recipeDirty = true;
      clearProposal();
      setStatus('Review this limit again after changing its settings. Add it or discard it before leaving this page.');
      mark();
    };
    for (const question of eligibleSources) {
      const label = node('label', '', 'composition-limit-source-choice');
      const checkbox = document.createElement('input');
      checkbox.type = 'checkbox'; checkbox.dataset.limitSourceName = question.Label; checkbox.value = question.Key;
      checkbox.addEventListener('change', () => { checkbox.checked ? selected.add(question.Key) : selected.delete(question.Key); invalidate(); });
      const category = catalog.Categories.find(c => String(c.Key).toLowerCase() === String(question.CategoryKey).toLowerCase());
      label.append(checkbox, node('span', `${question.Label} · ${category?.Name || 'Category unavailable'}`));
      sourceHost.append(label);
    }
    target.append(node('option', 'Choose a question…'));
    target.options[0].value = '';
    for (const question of catalog.Questions) {
      const category = catalog.Categories.find(c => String(c.Key).toLowerCase() === String(question.CategoryKey).toLowerCase());
      const option = node('option', `${question.Label} · ${category?.Name || 'Category unavailable'}`);
      option.value = question.Key;
      target.append(option);
    }
    for (const control of [nameInput, maximumInput, messageInput, liveInput]) control.addEventListener('input', invalidate);
    target.addEventListener('change', invalidate);
    panel.addEventListener('keydown', event => {
      if (event.key === 'Enter' && event.target.matches('input:not([type=checkbox])')) event.preventDefault();
    });
    toggle.addEventListener('click', () => {
      body.hidden = !body.hidden;
      toggle.setAttribute('aria-expanded', String(!body.hidden));
      if (!body.hidden) nameInput.focus();
    });
    const validationError = () => {
      const name = nameInput.value.trim();
      const message = messageInput.value.trim();
      const targetQuestion = catalog.Questions.find(q => String(q.Key).toLowerCase() === target.value.toLowerCase());
      if (isRecovering) return 'Recipe creation is unavailable while saved configuration is being recovered.';
      if (ambiguous) return 'Recipe creation is unavailable because question or category references are ambiguous.';
      if (saving) return 'Wait for the current configuration save to finish before adding a limit.';
      if (state.Calculations.length >= 200 || state.Rules.length >= 500) return 'The configuration has reached its calculation or rule limit.';
      if (!name || name.length > 200) return 'Enter a rule name of 1–200 characters.';
      if (selected.size < 2 || selected.size > 200) return 'Choose between 2 and 200 numeric answers.';
      if (!targetQuestion) return 'Choose the question that should receive the validation rule.';
      const sources = [...selected].map(key => eligibleSources.find(q => q.Key === key)).filter(Boolean);
      if (sources.length !== selected.size) return 'A selected numeric answer is no longer available.';
      const later = sources.filter(q => !notLaterThan(q, targetQuestion));
      if (later.length) return `Remove later-category sources or choose a later target: ${later.map(q => q.Label).join(', ')}.`;
      if (maximumInput.value.length > 128 || !normalizeDecimal(maximumInput.value)) return 'Maximum cannot be represented exactly by the editor. Enter a plain decimal of at most 128 characters.';
      if (!message || message.length > 500) return 'Enter a validation message of 1–500 characters.';
      return null;
    };
    const uniqueCalculationName = base => {
      let candidate = `${base} total`.slice(0, 200).trim();
      const existing = new Set(state.Calculations.map(item => String(item.Name || '').trim().toLowerCase()));
      if (!existing.has(candidate.toLowerCase())) return candidate;
      for (let suffix = 2; suffix < 10000; suffix++) {
        const tail = ` (${suffix})`;
        candidate = `${`${base} total`.slice(0, 200 - tail.length).trim()}${tail}`;
        if (!existing.has(candidate.toLowerCase())) return candidate;
      }
      return null;
    };
    const renderProposal = proposal => {
      const targetQuestion = catalog.Questions.find(q => q.Key === proposal.targetKey);
      const category = catalog.Categories.find(c => c.Key === targetQuestion?.CategoryKey);
      const list = node('ul', '', 'composition-limit-proposal-sources');
      proposal.sourceQuestions.forEach(q => list.append(node('li', `${q.Label} · ${categoryName(q.CategoryKey)}`)));
      previewHost.replaceChildren(
        node('h3', 'Review this proposed limit'),
        node('p', `Sum of ${proposal.sourceQuestions.map(q => q.Label).join(' and ')}. Calculated total “${proposal.calculation.Name}” will appear in ${category?.Name || 'the target category'}.`),
        list,
        node('p', `The rule “${proposal.rule.Name}” runs only while ${targetQuestion?.Label || 'the target question'} is visible. It shows “${proposal.rule.Message}” when the total is greater than ${proposal.maximum.canonical}.`),
        node('p', 'The displayed total is rounded to two decimal places. Validation compares the full value. Values that cannot be represented exactly are rejected.'),
        node('p', `It is checked on sequential forward navigation and final submission.${proposal.rule.ShowWhileAnswering ? ' It is also checked for live feedback while the target category is on screen.' : ''}`),
        node('p', 'Hidden or missing numeric answers contribute 0. A hidden target question skips this validation rule.')
      );
      previewHost.hidden = false;
    };
    previewButton.addEventListener('click', () => {
      clearProposal();
      const error = validationError();
      if (error) { setStatus(error, true); return; }
      const targetQuestion = catalog.Questions.find(q => q.Key === target.value);
      const sourceQuestions = [...selected].map(key => eligibleSources.find(q => q.Key === key));
      const maximum = normalizeDecimal(maximumInput.value);
      const calculationName = uniqueCalculationName(nameInput.value.trim());
      if (!calculationName) { setStatus('A unique calculated total name could not be created.', true); return; }
      const calculationId = crypto.randomUUID(), ruleId = crypto.randomUUID();
      const proposal = {
        fingerprint: fingerprint(), maximum, sourceQuestions, targetKey: targetQuestion.Key,
        calculation: { Id: calculationId, Name: calculationName, CategoryKey: targetQuestion.CategoryKey, Operation: 'Sum', Sources: sourceQuestions.map(q => ({ Kind: 'Question', Key: q.Key })) },
        rule: { Id: ruleId, Name: nameInput.value.trim(), CategoryKey: targetQuestion.CategoryKey, TargetQuestionKey: targetQuestion.Key,
          Kind: 'Validation', Message: messageInput.value.trim(), ShowWhileAnswering: liveInput.checked, Points: 0, BandGroup: '', AnyCheck: false,
          Conditions: [], Checks: [{ Left: { Kind: 'Calculation', Key: calculationId, Value: 0 }, Comparison: 'LessThanOrEqual', Right: { Kind: 'Constant', Key: emptyGuid, Value: maximum.numeric } }] }
      };
      proposal.definitionFingerprint = serializeDefinition();
      readyProposal = proposal; recipeProposal = proposal; recipeFingerprint = proposal.fingerprint;
      renderProposal(proposal);
      addButton.disabled = false;
      setStatus('Review the proposed total and rule. Adding them changes only this unsaved editor state.');
    });
    addButton.addEventListener('click', () => {
      if (adding || !readyProposal || readyProposal.fingerprint !== fingerprint() || recipeFingerprint !== fingerprint()
          || readyProposal.definitionFingerprint !== serializeDefinition()) {
        setStatus('The proposal is no longer current. Preview the limit again.', true); clearProposal(); return;
      }
      const error = validationError();
      if (error) { setStatus(error, true); clearProposal(); return; }
      adding = true;
      const proposal = readyProposal;
      state.Calculations.push(proposal.calculation);
      state.Rules.push(proposal.rule);
      proposal.rule._open = true;
      state.Rules.forEach(item => { if (item !== proposal.rule) item._open = false; });
      recipeDirty = false; readyProposal = null;
      clearProposal();
      pane = 'Rules';
      form.querySelectorAll('[data-pane]').forEach(button => button.setAttribute('aria-pressed', String(button.dataset.pane === pane)));
      render();
      mark();
      setStatus(`Added “${proposal.calculation.Name}” and “${proposal.rule.Name}” to this unsaved configuration. Save configuration to keep them.`);
      selected.clear();
      sourceHost.querySelectorAll('input[type=checkbox]').forEach(input => { input.checked = false; });
      nameInput.value = ''; maximumInput.value = ''; messageInput.value = ''; liveInput.checked = false; target.value = '';
      body.hidden = true; toggle.setAttribute('aria-expanded', 'false'); toggle.textContent = 'Create a limit';
      const addedCard = editor.querySelector(`.composition-item[data-rule-id="${proposal.rule.Id}"]`);
      focusCard(addedCard);
      adding = false;
    });
    const discard = () => {
      selected.clear();
      sourceHost.querySelectorAll('input[type=checkbox]').forEach(input => { input.checked = false; });
      nameInput.value = ''; maximumInput.value = ''; messageInput.value = ''; liveInput.checked = false; target.value = '';
      recipeDirty = false; recipeProposal = null; recipeFingerprint = null; readyProposal = null;
      clearProposal(); setStatus('Limit recipe cleared. Existing configuration edits are unchanged.');
      toggle.textContent = 'Create a limit';
      mark();
    };
    discardButton.addEventListener('click', discard);
    if (isRecovering || ambiguous || eligibleSources.length < 2 || !catalog.Questions.length) {
      toggle.disabled = true;
      toggle.textContent = isRecovering ? 'Unavailable during recovery' : 'Unavailable for this configuration';
      setStatus(isRecovering ? 'Recipe creation is unavailable while saved configuration is being recovered.'
        : ambiguous ? 'Recipe creation needs unambiguous question and category references.'
          : eligibleSources.length < 2 ? 'Recipe creation needs at least two numeric question answers.' : 'Recipe creation needs at least one question.');
    }
  };
  const source = (parent, title, value, change, exclude, onChange = () => {}) => {
    const box = node('div', '', `composition-source${value.Kind === 'Constant' ? ' is-constant' : ''}`); parent.append(box);
    const choices = [['Constant', 'Fixed number'], ...catalog.Questions.filter(q => q.Numeric).map(q => ['Question:' + q.Key, q.Label]),
      ...state.Calculations.filter(c => c.Id !== exclude).map(c => ['Calculation:' + c.Id, 'Calculated: ' + (c.Name || 'Untitled')]),
      ...catalog.Questions.flatMap(q => q.Options.map(o => ['OptionSelected:' + o.Key, 'Selected (1 or 0): ' + q.Label + ' / ' + o.Label]))];
    const key = value.Kind === 'Constant' ? 'Constant' : value.Kind + ':' + value.Key;
    if (!choices.some(c => c[0] === key)) choices.push([key, 'Missing source — select another']);
    select(box, title, key, choices, next => { const [kind, id] = next.split(':'); change({ Kind: kind, Key: id || emptyGuid, Value: value.Value || 0 }); render(); });
    if (value.Kind === 'Constant') input(box, 'Number', value.Value, n => { value.Value = n; onChange(); mark(); }, 'number');
  };
  const checks = (parent, list, title, onChange = () => {}) => {
    const box = node('fieldset'); box.append(node('legend', title)); parent.append(box);
    list.forEach((check, index) => {
      const row = node('div', '', 'composition-check'); box.append(row);
      source(row, 'Value', check.Left, v => check.Left = v, undefined, onChange);
      select(row, 'Comparison', check.Comparison, [['Equal', '='], ['NotEqual', '≠'], ['GreaterThan', '>'], ['GreaterThanOrEqual', '≥'], ['LessThan', '<'], ['LessThanOrEqual', '≤']], v => { check.Comparison = v; onChange(); });
      source(row, 'Compare with', check.Right, v => check.Right = v, undefined, onChange);
      action(row, 'Remove check', () => { list.splice(index, 1); render(); });
    });
    action(box, 'Add check', () => { list.push({ Left: { Kind: 'Constant', Value: 0, Key: emptyGuid }, Comparison: 'GreaterThanOrEqual', Right: { Kind: 'Constant', Value: 0, Key: emptyGuid } }); render(); });
  };
  const dynamicSourceKey = source => `${source.Kind}:${source.Key || emptyGuid}`;
  const dynamicSourceOptions = (expectedKind, excludeVariable) => {
    const matches = kind => expectedKind === 'Any' || kind === expectedKind || expectedKind === 'JoinText' && (kind === 'Text' || kind === 'TextList');
    const choices = [];
    if (matches('Text')) choices.push(['Text:' + emptyGuid, 'Text value']);
    if (matches('Number')) choices.push(['Number:' + emptyGuid, 'Number value']);
    if (matches('Date')) choices.push(['Date:' + emptyGuid, 'Date value']);
    for (const question of catalog.Questions) {
      const answerKind = question.AnswerType;
      const valueKind = ({ Textbox: 'Text', Textarea: 'Text', Dropdown: 'Text', RadioGroup: 'Text', UploadDocument: 'Text', Checkboxes: 'TextList', Number: 'Number', Date: 'Date' })[answerKind];
      if (matches(valueKind)) choices.push(['Question:' + question.Key, `${question.Label} (${answerKind})`]);
    }
    if (expectedKind === 'Any' || expectedKind === 'Number')
      for (const calculation of state.Calculations) choices.push(['Calculation:' + calculation.Id, 'Calculated: ' + (calculation.Name || 'Untitled')]);
    for (const variable of state.DynamicText.Variables) if (variable.Id !== excludeVariable)
      choices.push(['Variable:' + variable.Id, 'Value: ' + (variable.Name || 'Untitled')]);
    if (matches('Date')) choices.push(['Today:' + emptyGuid, 'Today']);
    return choices;
  };
  const makeDynamicSource = (encoded, fallbackText = '') => {
    const [kind, key] = encoded.split(':');
    if (kind === 'Text') return { Kind: kind, Text: '' };
    if (kind === 'Number') return { Kind: kind, Number: 0 };
    if (kind === 'Date') return { Kind: kind, Date: '2000-01-01' };
    return { Kind: kind, Key: key || emptyGuid };
  };
  const dynamicSourceControl = (parent, title, value, onChange, expectedKind = 'Any', excludeVariable = null, focusKey = null) => {
    const row = node('div', '', 'composition-source-row composition-dynamic-source');
    const key = dynamicSourceKey(value);
    const choices = dynamicSourceOptions(expectedKind, excludeVariable);
    if (!choices.some(choice => choice[0] === key)) choices.push([key, 'Unavailable source — choose another']);
    select(row, title, key, choices, next => { onChange(makeDynamicSource(next)); render(); }, focusKey);
    if (value.Kind === 'Text') input(row, 'Text value', value.Text, v => { value.Text = v; mark(); }, 'text');
    else if (value.Kind === 'Number') input(row, 'Number value', value.Number, v => { value.Number = v; mark(); }, 'number');
    else if (value.Kind === 'Date') input(row, 'Date value', value.Date, v => { value.Date = v; mark(); }, 'date');
    parent.append(row);
    return row;
  };
  const defaultDynamicSources = (operation, variableId) => {
    const firstDate = catalog.Questions.find(question => question.AnswerType === 'Date');
    const firstQuestion = catalog.Questions[0];
    const sourceFor = kind => kind === 'Date'
      ? firstDate ? { Kind: 'Question', Key: firstDate.Key } : { Kind: 'Date', Date: '2000-01-01' }
      : kind === 'Number' ? { Kind: 'Number', Number: 0 }
        : firstQuestion ? { Kind: 'Question', Key: firstQuestion.Key } : { Kind: 'Text', Text: '' };
    if (operation === 'Age') return [sourceFor('Date'), { Kind: 'Today' }];
    const kind = ['Add', 'Subtract', 'Multiply', 'Divide'].includes(operation) ? 'Number' : operation === 'FormatDate' ? 'Date' : 'Any';
    const count = ['Add', 'Subtract', 'Multiply', 'Divide', 'Age'].includes(operation) ? 2 : 1;
    return Array.from({ length: count }, () => sourceFor(kind));
  };
  const dynamicVariableSummary = variable => {
    const category = variable.CategoryKey ? categoryName(variable.CategoryKey) : 'All categories';
    const operation = ({ Value: 'Value', JoinText: 'Join text', FormatDate: 'Format date', Age: 'Age', Add: 'Add', Subtract: 'Subtract', Multiply: 'Multiply', Divide: 'Divide' })[variable.Operation] || variable.Operation;
    return `${category} · ${operation} · ${variable.Sources.length} source${variable.Sources.length === 1 ? '' : 's'}`;
  };
  const dynamicOwnerChoices = slot => slot === 'ElementLabel' || slot === 'ElementHelpText'
    ? catalog.Elements.map(element => [element.Key, `${element.Label}${element.Content ? ' (content)' : ''}`])
    : [[emptyGuid, slot === 'ReviewSummary' ? 'Review summary' : 'Confirmation text']];
  const dynamicTemplateSummary = template => {
    const slot = template.Slot || 'ElementLabel';
    const owner = dynamicOwnerChoices(slot).find(choice => choice[0] === template.OwnerKey)?.[1] || (slot === 'ReviewSummary' ? 'Review summary' : slot === 'ConfirmationText' ? 'Confirmation text' : 'Select a question or content block');
    const slotName = ({ ElementLabel: 'Question/content label', ElementHelpText: 'Help text', ReviewSummary: 'Review summary', ConfirmationText: 'Confirmation' })[slot] || slot;
    return `${slotName} · ${owner}`;
  };
  const checkbox = (parent, title, checked, onChange, description, focusKey) => {
    const wrap = node('div', '', 'composition-live-toggle-wrap');
    const label = makeFocusable(node('label', '', 'composition-live-toggle'), focusKey);
    const box = document.createElement('input'); box.type = 'checkbox'; box.checked = checked === true;
    box.addEventListener('change', () => { onChange(box.checked); mark(); });
    label.append(box, node('span', title)); wrap.append(label);
    if (description) wrap.append(node('p', description, 'composition-live-help'));
    parent.append(wrap); return box;
  };
  const renderDynamicTextEditor = () => {
    const dynamic = state.DynamicText;
    const settings = node('div', '', 'composition-dynamic-settings');
    const timeZoneChoices = catalog.TimeZones.map(zone => [zone.Id, `${zone.DisplayName} (${zone.Id})`]);
    if (!timeZoneChoices.some(choice => choice[0] === (dynamic.TimeZoneId || 'UTC')))
      timeZoneChoices.unshift([dynamic.TimeZoneId || '', `Unavailable time zone — ${dynamic.TimeZoneId || 'not selected'}`]);
    select(settings, 'Time zone', dynamic.TimeZoneId || 'UTC', timeZoneChoices, value => { dynamic.TimeZoneId = value; }, 'dynamic-time-zone');
    settings.append(node('p', 'Sets the local date used by “Today” and age calculations.', 'composition-live-help'));
    editor.append(settings);

    const variablesHeading = node('div', '', 'composition-dynamic-section-heading');
    variablesHeading.append(node('h3', 'Values'), node('p', 'Build reusable values from answers, calculations, dates, or fixed text and numbers.'));
    editor.append(variablesHeading);
    const variables = dynamic.Variables;
    if (!variables.length) editor.append(node('p', 'No dynamic values yet. Add one to use it in labels, help text, or summaries.', 'composition-empty'));
    variables.forEach((variable, index) => {
      variable.Sources ||= [];
      if (typeof variable._open !== 'boolean') variable._open = index === 0;
      const card = node('details', '', 'composition-item composition-dynamic-item'); card.open = variable._open; card.dataset.dynamicVariableId = variable.Id;
      card.addEventListener('toggle', () => { variable._open = card.open; });
      const summary = node('summary', '', 'composition-summary');
      summary.replaceChildren(node('span', variable.Name || 'Untitled value', 'composition-summary-name'), node('span', dynamicVariableSummary(variable), 'composition-summary-meta'));
      card.append(summary); editor.append(card);
      const fields = node('div', '', 'composition-fields'); card.append(fields);
      const metadata = node('div', '', 'composition-metadata'); fields.append(metadata);
      input(metadata, 'Name', variable.Name, value => { variable.Name = value; summary.firstElementChild.textContent = value || 'Untitled value'; summary.lastElementChild.textContent = dynamicVariableSummary(variable); }, 'text', `dynamic-variable-${variable.Id}-name`);
      const categoryOptions = [['', 'All categories (global)'], ...categories()];
      select(metadata, 'Available in', variable.CategoryKey || '', categoryOptions, value => { variable.CategoryKey = value || null; summary.lastElementChild.textContent = dynamicVariableSummary(variable); }, `dynamic-variable-${variable.Id}-category`);
      select(metadata, 'Operation', variable.Operation, [['Value', 'Use one value'], ['JoinText', 'Join text'], ['FormatDate', 'Format date'], ['Age', 'Age in years'], ['Add', 'Add numbers'], ['Subtract', 'Subtract numbers'], ['Multiply', 'Multiply numbers'], ['Divide', 'Divide numbers']], value => {
        variable.Operation = value;
        const count = value === 'JoinText' || value === 'Add' || value === 'Multiply' ? null : ['Age', 'Subtract', 'Divide'].includes(value) ? 2 : 1;
        if (count !== null) variable.Sources = variable.Sources.slice(0, count);
        const minimum = ['Age', 'Add', 'Subtract', 'Multiply', 'Divide'].includes(value) ? 2 : 1;
        while (variable.Sources.length < minimum) variable.Sources.push(defaultDynamicSources(value, variable.Id)[variable.Sources.length]);
        summary.lastElementChild.textContent = dynamicVariableSummary(variable); render();
      }, `dynamic-variable-${variable.Id}-operation`);

      if (variable.Operation === 'JoinText') {
        const joinSettings = node('div', '', 'composition-dynamic-inline-settings'); fields.append(joinSettings);
        const separator = input(joinSettings, 'Separator', variable.Separator ?? ' ', value => { variable.Separator = value; }, 'text', `dynamic-variable-${variable.Id}-separator`);
        separator.maxLength = 100;
        checkbox(joinSettings, 'Skip empty values', variable.SkipEmpty !== false, value => { variable.SkipEmpty = value; }, 'Empty sources are left out of the joined result.', `dynamic-variable-${variable.Id}-skip-empty`);
      }
      if (variable.Operation === 'FormatDate') {
        select(fields, 'Date format', variable.DateFormat || 'MM-dd-yyyy', [['MM-dd-yyyy', 'MM-DD-YYYY'], ['yyyy-MM-dd', 'YYYY-MM-DD'], ['dd-MM-yyyy', 'DD-MM-YYYY'], ['MMM d, yyyy', 'Mon D, YYYY']], value => { variable.DateFormat = value; }, `dynamic-variable-${variable.Id}-date-format`);
      }

      const exactCount = variable.Operation === 'JoinText' || variable.Operation === 'Add' || variable.Operation === 'Multiply' ? null : ['Age', 'Subtract', 'Divide'].includes(variable.Operation) ? 2 : 1;
      const minimumCount = ['Age', 'Add', 'Subtract', 'Multiply', 'Divide'].includes(variable.Operation) ? 2 : 1;
      const sourceExpected = variable.Operation === 'JoinText' ? 'JoinText'
        : variable.Operation === 'FormatDate' || variable.Operation === 'Age' ? 'Date'
        : ['Add', 'Subtract', 'Multiply', 'Divide'].includes(variable.Operation) ? 'Number' : 'Any';
      const addSourceFocusKey = `dynamic-variable-${variable.Id}-add-source`;
      variable.Sources.forEach((value, sourceIndex) => {
        const row = node('div', '', 'composition-source-row'); fields.append(row);
        const sourceNames = variable.Operation === 'Age' ? ['Date of birth', 'Reference date (Today by default)']
          : variable.Operation === 'Percentage' ? ['Numerator', 'Denominator']
            : variable.Operation === 'Add' ? ['First number', 'Second number']
              : variable.Operation === 'Subtract' ? ['Number to subtract from', 'Number to subtract']
                : variable.Operation === 'Multiply' ? ['First number', 'Second number']
                  : variable.Operation === 'Divide' ? ['Numerator', 'Denominator'] : [`Source ${sourceIndex + 1}`];
        dynamicSourceControl(row, sourceNames[sourceIndex] || `Source ${sourceIndex + 1}`, value,
          next => { variable.Sources[sourceIndex] = next; }, sourceExpected, variable.Id,
          `dynamic-variable-${variable.Id}-source-${sourceIndex}`);
        if (variable.Operation === 'JoinText' || variable.Sources.length > (exactCount ?? 0)) {
          const label = `Remove source ${sourceIndex + 1}`;
          const targetIndex = Math.min(sourceIndex, variable.Sources.length - 2);
          const focusAfter = variable.Sources.length > 1 ? `dynamic-variable-${variable.Id}-source-${targetIndex}` : addSourceFocusKey;
          iconAction(row, label, 'remove', () => { variable.Sources.splice(sourceIndex, 1); render(); }, `dynamic-variable-${variable.Id}-source-${sourceIndex}-remove`, focusAfter);
        }
      });
      if (variable.Sources.length < minimumCount)
        fields.append(node('p', `${variable.Operation} needs at least ${minimumCount} source${minimumCount === 1 ? '' : 's'}.`, 'composition-help'));
      if (exactCount !== null && variable.Sources.length !== exactCount)
        fields.append(node('p', `${variable.Operation} needs exactly ${exactCount} source${exactCount === 1 ? '' : 's'}.`, 'composition-help'));
      if (variable.Operation === 'JoinText' || variable.Operation === 'Add' || variable.Operation === 'Multiply' || variable.Sources.length < exactCount)
        action(fields, 'Add source', () => { variable.Sources.push(defaultDynamicSources(variable.Operation, variable.Id)[variable.Sources.length] || (sourceExpected === 'Number' ? { Kind: 'Number', Number: 0 } : { Kind: 'Text', Text: '' })); render(); }, { focusKey: addSourceFocusKey });

      const actions = node('div', '', 'composition-actions'); card.append(actions);
      if (index > 0) action(actions, 'Move value up', () => { [variables[index - 1], variables[index]] = [variables[index], variables[index - 1]]; render(); });
      action(actions, 'Delete value', () => { if (confirm('Delete this value? Templates that use it will need another source.')) { variables.splice(index, 1); render(); } });
    });
    action(editor, 'Add value', () => {
      const id = crypto.randomUUID();
      variables.forEach(variable => variable._open = false);
      variables.push({ Id: id, Name: '', CategoryKey: null, Operation: 'Value', Sources: defaultDynamicSources('Value', id), Separator: ' ', SkipEmpty: true, DateFormat: 'MM-dd-yyyy', _open: true });
      pendingFocusKey = `dynamic-variable-${id}-name`; render();
    });

    const templatesHeading = node('div', '', 'composition-dynamic-section-heading');
    templatesHeading.append(node('h3', 'Text templates'), node('p', 'Insert a reusable value into a question label, help text, review summary, or confirmation.'));
    editor.append(templatesHeading);
    const templates = dynamic.Templates;
    if (!templates.length) editor.append(node('p', 'No text templates yet. Add one to customize where values appear.', 'composition-empty'));
    const slots = [['ElementLabel', 'Question or content label'], ['ElementHelpText', 'Question or content help text'], ['ReviewSummary', 'Review summary'], ['ConfirmationText', 'Confirmation text']];
    templates.forEach((template, index) => {
      template.Parts ||= [];
      if (typeof template._open !== 'boolean') template._open = index === 0;
      const card = node('details', '', 'composition-item composition-dynamic-item'); card.open = template._open;
      card.dataset.dynamicTemplateSlot = template.Slot || '';
      card.dataset.dynamicTemplateOwner = template.OwnerKey || '';
      card.addEventListener('toggle', () => { template._open = card.open; });
      const summary = node('summary', '', 'composition-summary');
      summary.replaceChildren(node('span', dynamicTemplateSummary(template), 'composition-summary-name'), node('span', `${template.Parts.length} part${template.Parts.length === 1 ? '' : 's'}`, 'composition-summary-meta'));
      card.append(summary); editor.append(card);
      const fields = node('div', '', 'composition-fields'); card.append(fields);
      const metadata = node('div', '', 'composition-metadata'); fields.append(metadata);
      select(metadata, 'Text location', template.Slot, slots, value => {
        template.Slot = value;
        const owners = dynamicOwnerChoices(value);
        template.OwnerKey = owners.some(owner => owner[0] === template.OwnerKey) ? template.OwnerKey : owners[0]?.[0] || emptyGuid;
        if (value === 'ReviewSummary' || value === 'ConfirmationText') template.OwnerKey = emptyGuid;
        render();
      }, `dynamic-template-${index}-slot`);
      if (template.Slot === 'ElementLabel' || template.Slot === 'ElementHelpText') {
        const ownerChoices = dynamicOwnerChoices(template.Slot);
        if (!ownerChoices.some(owner => owner[0] === template.OwnerKey)) ownerChoices.unshift([template.OwnerKey || emptyGuid, 'Unavailable element — choose another']);
        select(metadata, 'Question or content block', template.OwnerKey, ownerChoices, value => { template.OwnerKey = value; pendingFocusKey = `dynamic-template-${index}-owner`; render(); }, `dynamic-template-${index}-owner`);
      }
      const owner = catalog.Elements.find(element => element.Key === template.OwnerKey);
      if (template.HideWhenUnavailable || template.Slot === 'ReviewSummary' || template.Slot === 'ElementLabel' && owner?.Content)
        checkbox(metadata, 'Hide when unavailable', template.HideWhenUnavailable, value => { template.HideWhenUnavailable = value; }, 'Hide this content until all inserted values are available.', `dynamic-template-${index}-hide`);

      template.Parts.forEach((part, partIndex) => {
        const row = node('div', '', 'composition-dynamic-part'); fields.append(row);
        const kind = select(row, `Part ${partIndex + 1}`, part.Kind, [['Literal', 'Text'], ['Value', 'Insert a value']], value => {
          part.Kind = value;
          if (value === 'Value') { part.Source ||= { Kind: 'Variable', Key: state.DynamicText.Variables[0]?.Id || emptyGuid }; part.Fallback ||= ''; }
          else { part.Source = null; part.Fallback = ''; }
          render();
        }, `dynamic-template-${index}-part-${partIndex}-kind`);
        if (part.Kind === 'Literal') {
          const text = input(row, `Text part ${partIndex + 1}`, part.Text, value => { part.Text = value; }, 'text', `dynamic-template-${index}-part-${partIndex}-text`);
          text.maxLength = 4000;
        } else {
          part.Source ||= { Kind: 'Variable', Key: emptyGuid };
          dynamicSourceControl(row, `Value to insert ${partIndex + 1}`, part.Source, value => { part.Source = value; }, 'Any', null,
            `dynamic-template-${index}-part-${partIndex}-source`);
          const fallback = input(row, `Fallback when unavailable ${partIndex + 1}`, part.Fallback, value => { part.Fallback = value; }, 'text', `dynamic-template-${index}-part-${partIndex}-fallback`);
          fallback.maxLength = 4000;
        }
        if (partIndex > 0) iconAction(row, `Move part ${partIndex + 1} up`, 'up', () => { [template.Parts[partIndex - 1], template.Parts[partIndex]] = [template.Parts[partIndex], template.Parts[partIndex - 1]]; render(); }, `dynamic-template-${index}-part-${partIndex}-up`, `dynamic-template-${index}-part-${partIndex - 1}-kind`);
        if (partIndex < template.Parts.length - 1) iconAction(row, `Move part ${partIndex + 1} down`, 'down', () => { [template.Parts[partIndex + 1], template.Parts[partIndex]] = [template.Parts[partIndex], template.Parts[partIndex + 1]]; render(); }, `dynamic-template-${index}-part-${partIndex}-down`, `dynamic-template-${index}-part-${partIndex + 1}-kind`);
        const remainingPartIndex = Math.min(partIndex, template.Parts.length - 2);
        const partFocusAfter = template.Parts.length > 1 ? `dynamic-template-${index}-part-${remainingPartIndex}-kind` : `dynamic-template-${index}-add-text`;
        iconAction(row, `Remove part ${partIndex + 1}`, 'remove', () => { template.Parts.splice(partIndex, 1); render(); }, `dynamic-template-${index}-part-${partIndex}-remove`, partFocusAfter);
      });
      const partActions = node('div', '', 'composition-actions'); fields.append(partActions);
      action(partActions, 'Add text', () => { template.Parts.push({ Kind: 'Literal', Text: '' }); render(); }, { focusKey: `dynamic-template-${index}-add-text` });
      action(partActions, 'Insert value', () => { template.Parts.push({ Kind: 'Value', Source: { Kind: 'Variable', Key: state.DynamicText.Variables[0]?.Id || emptyGuid }, Fallback: '' }); render(); }, { focusKey: `dynamic-template-${index}-insert-value` });
      const actions = node('div', '', 'composition-actions'); card.append(actions);
      if (index > 0) action(actions, 'Move template up', () => { [templates[index - 1], templates[index]] = [templates[index], templates[index - 1]]; render(); });
      action(actions, 'Delete template', () => { templates.splice(index, 1); render(); });
    });
    action(editor, 'Add text template', () => {
      const id = crypto.randomUUID();
      templates.forEach(template => template._open = false);
      const firstElement = catalog.Elements[0];
      const slot = firstElement ? 'ElementLabel' : 'ReviewSummary';
      const initialText = firstElement?.Label || 'Review your answers';
      templates.push({ Slot: slot, OwnerKey: firstElement?.Key || emptyGuid, Parts: [{ Kind: 'Literal', Text: initialText }], HideWhenUnavailable: false, _open: true });
      pendingFocusKey = `dynamic-template-${templates.length - 1}-slot`; render();
    });
  };
  const render = () => {
    const activeKey = pendingFocusKey || (editor.contains(document.activeElement) ? document.activeElement.dataset.focusKey : null);
    pendingFocusKey = null;
    editor.replaceChildren();
    focusSequence = 0;
    const headings = { Groups: ['Question groups', 'Arrange existing questions together. Table columns contain designer-provided information; answers remain normal questions.'], Calculations: ['Calculated fields', 'Sum selected numeric answers or calculate a percentage (numerator ÷ denominator × 100). Hidden answers contribute zero; a zero denominator displays an unavailable result.'], Rules: ['Combined rules', 'Validate combined answers or award points. Rules run in the order shown. Rules with the same scoring band name award only the first matching rule in that category.'], DynamicText: ['Dynamic text', 'Create reusable values and insert them into question labels, help text, review summaries, or confirmation text.'] };
    const help = node('details', '', 'composition-help-disclosure'); help.append(node('summary', 'About this section'), node('p', headings[pane][1]));
    editor.append(node('div', '', 'composition-section-heading'));
    editor.lastElementChild.append(node('h2', headings[pane][0]), help);
    if (pane === 'DynamicText') {
      renderDynamicTextEditor();
      if (activeKey !== null) editor.querySelector(`[data-focus-key="${activeKey}"]`)?.focus();
      return;
    }
    const list = state[pane];
    if (!list.length) editor.append(node('p', 'No ' + headings[pane][0].toLowerCase() + ' yet. Add one to get started.', 'composition-empty'));
    list.forEach((item, index) => {
      const card = node('details', '', 'composition-item'); card.open = item._open !== false;
      if (pane === 'Rules') { card.dataset.ruleId = item.Id; card.id = `composition-rule-${item.Id}`; }
      if (pane === 'Groups') card.dataset.groupId = item.Id;
      if (pane === 'Calculations') card.dataset.calculationId = item.Id;
      card.addEventListener('toggle', () => { item._open = card.open; });
      const summary = node('summary', '', 'composition-summary'); updateSummary(summary, item, pane); card.append(summary); editor.append(card);
      const ruleDescription = pane === 'Rules' ? node('div', '', 'composition-rule-description') : null;
      if (ruleDescription) { updateRuleDescription(ruleDescription, item); card.append(ruleDescription); }
      const updateRulePresentation = () => { updateSummary(summary, item, pane); updateRuleDescription(ruleDescription, item); };
      const fields = node('div', '', 'composition-fields'); card.append(fields);
      const metadata = node('div', '', 'composition-metadata'); fields.append(metadata);
      input(metadata, 'Name', item.Name, v => { item.Name = v; if (pane === 'Rules') updateRulePresentation(); else updateSummary(summary, item, pane); });
      select(metadata, 'Category', item.CategoryKey, categories(), v => { item.CategoryKey = v; render(); });
      if (pane === 'Calculations') select(metadata, 'Operation', item.Operation, [['Sum', 'Sum'], ['Percentage', 'Percentage']], v => { item.Operation = v; render(); });
      if (pane === 'Rules') select(metadata, 'Purpose', item.Kind, [['Validation', 'Validation'], ['Score', 'Scoring']], v => { item.Kind = v; render(); });
      if (pane === 'Groups') {
        select(metadata, 'Layout', String(item.TableLayout), [['true', 'Table on desktop, cards on mobile'], ['false', 'Stacked questions']], v => { item.TableLayout = v === 'true'; render(); });
        const groupControls = node('div', '', 'composition-group-controls'); fields.append(groupControls);
        input(groupControls, 'Information column headings (separate with |)', item.Columns.join(' | '), v => { item.Columns = v.split('|').map(x => x.trim()).filter(Boolean); updateSummary(summary, item, pane); });
        const refresh = action(groupControls, 'Apply columns to rows', () => { item.Rows.forEach(r => { r.Cells = item.Columns.map((_, i) => r.Cells[i] || ''); }); render(); });
        refresh.title = 'Apply the current column headings to every row. Existing cell values are kept by position.';
        const available = catalog.Questions.filter(q => q.CategoryKey === item.CategoryKey);
        const used = new Set(state.Groups.filter(g => g !== item).flatMap(g => g.Rows.map(r => r.QuestionKey)));
        select(groupControls, 'Question to add', '', [['', 'Add existing question…'], ...available.filter(q => !used.has(q.Key) && !item.Rows.some(r => r.QuestionKey === q.Key)).map(q => [q.Key, q.Label])], v => {
          if (v) { item.Rows.push({ QuestionKey: v, Cells: item.Columns.map(() => '') }); pendingFocusKey = `group-${item.Id}-row-${v}${item.Columns.length ? '-cell-0' : '-remove'}`; }
          render();
        }, `group-${item.Id}-add`);
        if (item.Rows.length) {
          const tableWrap = node('div', '', 'composition-group-table-wrap');
          const table = node('table', '', 'composition-group-table'), thead = document.createElement('thead'), headerRow = document.createElement('tr');
          for (const heading of ['Question', ...item.Columns, 'Actions']) { const th = node('th', heading); th.scope = 'col'; headerRow.append(th); }
          thead.append(headerRow); table.append(thead);
          const tbody = document.createElement('tbody');
          item.Rows.forEach((row, ri) => {
            const tr = node('tr', '', 'composition-group-row');
            const questionLabel = catalog.Questions.find(q => q.Key === row.QuestionKey)?.Label || 'Missing question';
            const questionCell = node('th', questionLabel); questionCell.scope = 'row'; questionCell.dataset.label = 'Question'; tr.append(questionCell);
            const rowKey = `group-${item.Id}-row-${row.QuestionKey}`;
            item.Columns.forEach((column, ci) => { const td = node('td'); td.dataset.label = column; cellInput(td, `${column} for ${questionLabel}`, row.Cells[ci], v => row.Cells[ci] = v, `${rowKey}-cell-${ci}`); tr.append(td); });
            const actionsCell = node('td', '', 'composition-row-actions'); actionsCell.dataset.label = 'Actions';
            if (ri > 0) iconAction(actionsCell, `Move ${questionLabel} up`, 'up', () => { [item.Rows[ri - 1], item.Rows[ri]] = [item.Rows[ri], item.Rows[ri - 1]]; render(); }, `${rowKey}-up`, item.Columns.length ? `${rowKey}-cell-0` : `${rowKey}-remove`);
            const nextRow = item.Rows[ri + 1] || item.Rows[ri - 1];
            const focusAfterRemove = nextRow ? `group-${item.Id}-row-${nextRow.QuestionKey}${item.Columns.length ? '-cell-0' : '-remove'}` : `group-${item.Id}-add`;
            iconAction(actionsCell, `Remove ${questionLabel}`, 'remove', () => { item.Rows.splice(ri, 1); render(); }, `${rowKey}-remove`, focusAfterRemove);
            tr.append(actionsCell); tbody.append(tr);
          });
          table.append(tbody); tableWrap.append(table); fields.append(tableWrap);
        } else fields.append(node('p', 'No questions in this group yet.', 'composition-empty'));
      } else if (pane === 'Calculations') {
        item.Sources.forEach((s, si) => { const row = node('div', '', 'composition-source-row'); fields.append(row); source(row, item.Operation === 'Percentage' ? (si === 0 ? 'Numerator' : si === 1 ? 'Denominator' : 'Extra source — remove this') : 'Source ' + (si + 1), s, v => item.Sources[si] = v, item.Id); action(row, 'Remove source', () => { item.Sources.splice(si, 1); render(); }); });
        if (item.Operation === 'Percentage' && item.Sources.length !== 2) {
          fields.append(node('p', item.Sources.length < 2 ? 'A percentage needs exactly two sources. Add a numerator, then a denominator.' : 'A percentage can use exactly two sources. Remove the extra sources.', 'composition-help'));
        }
        if (item.Operation === 'Percentage') {
          if (item.Sources.length < 2) action(fields, item.Sources.length === 0 ? 'Add numerator' : 'Add denominator', () => { item.Sources.push({ Kind: 'Constant', Value: 0, Key: emptyGuid }); render(); });
        } else action(fields, 'Add source', () => { item.Sources.push({ Kind: 'Constant', Value: 0, Key: emptyGuid }); render(); });
      } else {
        const ruleSettings = node('div', '', 'composition-rule-settings'); fields.append(ruleSettings);
        select(ruleSettings, 'Attach to question', item.TargetQuestionKey || emptyGuid, [[emptyGuid, 'First visible question in category'], ...catalog.Questions.filter(q => q.CategoryKey === item.CategoryKey).map(q => [q.Key, q.Label])], v => { item.TargetQuestionKey = v; updateRulePresentation(); });
        if (item.Kind === 'Validation') {
          input(ruleSettings, 'Validation message', item.Message, v => { item.Message = v; updateRulePresentation(); });
          const liveToggle = node('div', '', 'composition-live-toggle-wrap');
          const liveLabel = node('label', '', 'composition-live-toggle');
          const liveCheckbox = document.createElement('input');
          liveCheckbox.type = 'checkbox';
          liveCheckbox.checked = item.ShowWhileAnswering === true;
          liveCheckbox.setAttribute('aria-describedby', `composition-live-help-${item.Id}`);
          liveCheckbox.addEventListener('change', () => { item.ShowWhileAnswering = liveCheckbox.checked; updateRulePresentation(); mark(); });
          liveLabel.append(liveCheckbox, node('span', 'Show validation while answering'));
          liveToggle.append(liveLabel, node('p', 'Show this message after a short typing pause when the rule fails. Navigation and submission checks still apply.', 'composition-live-help'));
          liveToggle.lastElementChild.id = `composition-live-help-${item.Id}`;
          ruleSettings.append(liveToggle);
        }
        else {
          input(ruleSettings, 'Points', item.Points, v => { item.Points = v; updateRulePresentation(); }, 'number');
          input(ruleSettings, 'Scoring band name (optional)', item.BandGroup, v => { item.BandGroup = v; updateRulePresentation(); });
          const bandHelp = node('details', '', 'composition-help-disclosure'); bandHelp.append(node('summary', 'About scoring bands'), node('p', 'Use the same band name for mutually exclusive thresholds. Put the highest-priority rule first. Leave blank for independent awards.')); ruleSettings.append(bandHelp);
        }
        checks(fields, item.Conditions, 'Only apply when (all checks; blank means always)', updateRulePresentation);
        select(fields, 'Required checks', String(item.AnyCheck), [['false', 'All checks'], ['true', 'Any check']], v => { item.AnyCheck = v === 'true'; updateRulePresentation(); });
        checks(fields, item.Checks, item.Kind === 'Validation' ? 'The answer must satisfy' : 'Award points when', updateRulePresentation);
      }
      const actions = node('div', '', 'composition-actions'); card.append(actions);
      if (index > 0) action(actions, 'Move up', () => { [list[index - 1], list[index]] = [list[index], list[index - 1]]; render(); });
      action(actions, 'Delete ' + (pane === 'Groups' ? 'group' : pane === 'Calculations' ? 'calculation' : 'rule'), () => { if (confirm('Delete this configuration item? Existing question answers will be preserved.')) { list.splice(index, 1); render(); } });
    });
    action(editor, 'Add ' + (pane === 'Groups' ? 'group' : pane === 'Calculations' ? 'calculated field' : 'rule'), () => {
      const common = { Id: crypto.randomUUID(), Name: '', CategoryKey: catalog.Categories[0]?.Key || emptyGuid };
      list.forEach(i => i._open = false);
      list.push(pane === 'Groups' ? { ...common, TableLayout: true, Columns: [], Rows: [] } : pane === 'Calculations' ? { ...common, Operation: 'Sum', Sources: [] } : { ...common, TargetQuestionKey: emptyGuid, Kind: 'Validation', Message: '', Points: 0, BandGroup: '', AnyCheck: false, Conditions: [], Checks: [] }); render();
      editor.querySelector('.composition-item:last-of-type input')?.focus();
    });
    if (activeKey !== null) editor.querySelector(`[data-focus-key="${activeKey}"]`)?.focus();
  };
  const focusCard = card => {
    if (!card) return;
    card.open = true;
    const summary = card.querySelector(':scope > summary');
    summary?.focus({ preventScroll: true });
    card.scrollIntoView({ block: 'center' });
  };
  const selectPane = value => {
    const segments = String(value || '').replace(/^#/, '').split('/');
    const paneValue = segments[0];
    const selected = ['Groups', 'Calculations', 'Rules', 'DynamicText'].find(name => name.toLowerCase() === paneValue.toLowerCase());
    if (!selected) return;
    pane = selected;
    form.querySelectorAll('[data-pane]').forEach(button => button.setAttribute('aria-pressed', String(button.dataset.pane === pane)));
    const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
    let targetFinder = null;
    if (segments.length === 2 && ['Groups', 'Calculations', 'Rules'].includes(selected) && guidPattern.test(segments[1])) {
      const id = segments[1].toLowerCase();
      const stateList = state[selected] || [];
      const matches = stateList.filter(item => String(item.Id).toLowerCase() === id);
      if (matches.length === 1) {
        matches[0]._open = true;
        const attribute = selected === 'Groups' ? 'groupId' : selected === 'Calculations' ? 'calculationId' : 'ruleId';
        targetFinder = () => [...editor.querySelectorAll(`.composition-item[data-${selected.toLowerCase().replace(/s$/, '')}-id]`)]
          .find(card => String(card.dataset[attribute]).toLowerCase() === id) || null;
      }
    } else if (segments.length === 3 && selected === 'DynamicText' && segments[1] === 'Variables' && guidPattern.test(segments[2])) {
      const id = segments[2].toLowerCase();
      const matches = (state.DynamicText.Variables || []).filter(item => String(item.Id).toLowerCase() === id);
      if (matches.length === 1) {
        matches[0]._open = true;
        targetFinder = () => [...editor.querySelectorAll('.composition-dynamic-item[data-dynamic-variable-id]')]
          .find(card => card.dataset.dynamicVariableId.toLowerCase() === id) || null;
      }
    } else if (segments.length === 4 && selected === 'DynamicText' && segments[1] === 'Templates'
        && ['ElementLabel', 'ElementHelpText', 'ReviewSummary', 'ConfirmationText'].includes(segments[2]) && guidPattern.test(segments[3])) {
      const owner = segments[3].toLowerCase();
      const matches = (state.DynamicText.Templates || []).filter(item => item.Slot === segments[2] && String(item.OwnerKey).toLowerCase() === owner);
      if (matches.length === 1) {
        matches[0]._open = true;
        targetFinder = () => [...editor.querySelectorAll('.composition-dynamic-item[data-dynamic-template-slot]')]
          .find(card => card.dataset.dynamicTemplateSlot === segments[2] && card.dataset.dynamicTemplateOwner.toLowerCase() === owner) || null;
      }
    }
    render();
    if (targetFinder) focusCard(targetFinder());
  };
  form.querySelectorAll('[data-pane]').forEach(button => button.addEventListener('click', () => selectPane(button.dataset.pane)));
  window.addEventListener('hashchange', () => selectPane(location.hash));
  form.addEventListener('submit', async event => {
    event.preventDefault();
    if (saving) return;
    if (recipeDirty) {
      const recipeStatus = form.querySelector('[data-limit-status]');
      if (recipeStatus) { recipeStatus.textContent = 'Add this limit to configuration or discard the recipe before saving.'; recipeStatus.classList.add('is-error'); }
      form.querySelector('[data-limit-recipe-body]').hidden = false;
      form.querySelector('[data-limit-recipe-toggle]').setAttribute('aria-expanded', 'true');
      form.querySelector('[data-limit-recipe-toggle]').focus();
      return;
    }
    form.elements.definitionJson.value = JSON.stringify(state, (key, value) => key === '_open' ? undefined : value);
    const version = form.elements.definitionJson.value;
    const submit = form.querySelector('[type=submit]'); submit.disabled = true;
    saving = true; submittedDefinition = version;
    status.textContent = 'Saving configuration…'; status.classList.remove('is-error');
    try {
      const response = await fetch(form.action, { method: 'POST', body: new FormData(form), credentials: 'same-origin', headers: { Accept: 'application/json' } });
      const result = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(result.message || 'Configuration could not be saved. Check the fields and try again.');
      form.elements.expectedRevision.value = result.revision;
      savedDefinition = version;
      saving = false; saveUncertain = false; submittedDefinition = null;
      dirty = serializeDefinition() !== savedDefinition;
      status.textContent = dirty ? 'Saved. Newer edits still need saving.'
        : recipeDirty ? 'Configuration saved. Limit recipe still needs adding or discarding.' : result.message;
    } catch (error) {
      saving = false; saveUncertain = true; submittedDefinition = null;
      dirty = true;
      status.textContent = error.message; status.classList.add('is-error');
    }
    finally { submit.disabled = false; }
  });
  window.addEventListener('beforeunload', event => { if (dirty || recipeDirty || saving || saveUncertain) { event.preventDefault(); event.returnValue = ''; } });
  setupLimitRecipe();
  render();
  if (location.hash) selectPane(location.hash);
})();
