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
  let pane = 'Groups', dirty = false, focusSequence = 0, pendingFocusKey = null;
  const mark = () => { dirty = true; status.textContent = 'Unsaved configuration changes.'; status.classList.remove('is-error'); };
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
    return `${categoryName(item.CategoryKey)} · ${item.Kind} · ${item.Checks.length} check${item.Checks.length === 1 ? '' : 's'}`;
  };
  const updateSummary = (summary, item, currentPane) => {
    summary.replaceChildren(node('span', item.Name || 'Untitled', 'composition-summary-name'), node('span', summaryMeta(item, currentPane), 'composition-summary-meta'));
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
  const source = (parent, title, value, change, exclude) => {
    const box = node('div', '', `composition-source${value.Kind === 'Constant' ? ' is-constant' : ''}`); parent.append(box);
    const choices = [['Constant', 'Fixed number'], ...catalog.Questions.filter(q => q.Numeric).map(q => ['Question:' + q.Key, q.Label]),
      ...state.Calculations.filter(c => c.Id !== exclude).map(c => ['Calculation:' + c.Id, 'Calculated: ' + (c.Name || 'Untitled')]),
      ...catalog.Questions.flatMap(q => q.Options.map(o => ['OptionSelected:' + o.Key, 'Selected (1 or 0): ' + q.Label + ' / ' + o.Label]))];
    const key = value.Kind === 'Constant' ? 'Constant' : value.Kind + ':' + value.Key;
    if (!choices.some(c => c[0] === key)) choices.push([key, 'Missing source — select another']);
    select(box, title, key, choices, next => { const [kind, id] = next.split(':'); change({ Kind: kind, Key: id || emptyGuid, Value: value.Value || 0 }); render(); });
    if (value.Kind === 'Constant') input(box, 'Number', value.Value, n => { value.Value = n; mark(); }, 'number');
  };
  const checks = (parent, list, title) => {
    const box = node('fieldset'); box.append(node('legend', title)); parent.append(box);
    list.forEach((check, index) => {
      const row = node('div', '', 'composition-check'); box.append(row);
      source(row, 'Value', check.Left, v => check.Left = v);
      select(row, 'Comparison', check.Comparison, [['Equal', '='], ['NotEqual', '≠'], ['GreaterThan', '>'], ['GreaterThanOrEqual', '≥'], ['LessThan', '<'], ['LessThanOrEqual', '≤']], v => check.Comparison = v);
      source(row, 'Compare with', check.Right, v => check.Right = v);
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
      const card = node('details', '', 'composition-item composition-dynamic-item'); card.open = variable._open;
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
      card.addEventListener('toggle', () => { item._open = card.open; });
      const summary = node('summary', '', 'composition-summary'); updateSummary(summary, item, pane); card.append(summary); editor.append(card);
      const fields = node('div', '', 'composition-fields'); card.append(fields);
      const metadata = node('div', '', 'composition-metadata'); fields.append(metadata);
      input(metadata, 'Name', item.Name, v => { item.Name = v; updateSummary(summary, item, pane); });
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
        select(ruleSettings, 'Attach to question', item.TargetQuestionKey || emptyGuid, [[emptyGuid, 'First visible question in category'], ...catalog.Questions.filter(q => q.CategoryKey === item.CategoryKey).map(q => [q.Key, q.Label])], v => item.TargetQuestionKey = v);
        if (item.Kind === 'Validation') {
          input(ruleSettings, 'Validation message', item.Message, v => item.Message = v);
          const liveToggle = node('div', '', 'composition-live-toggle-wrap');
          const liveLabel = node('label', '', 'composition-live-toggle');
          const liveCheckbox = document.createElement('input');
          liveCheckbox.type = 'checkbox';
          liveCheckbox.checked = item.ShowWhileAnswering === true;
          liveCheckbox.setAttribute('aria-describedby', `composition-live-help-${item.Id}`);
          liveCheckbox.addEventListener('change', () => { item.ShowWhileAnswering = liveCheckbox.checked; mark(); });
          liveLabel.append(liveCheckbox, node('span', 'Show validation while answering'));
          liveToggle.append(liveLabel, node('p', 'Show this message after a short typing pause when the rule fails. Navigation and submission checks still apply.', 'composition-live-help'));
          liveToggle.lastElementChild.id = `composition-live-help-${item.Id}`;
          ruleSettings.append(liveToggle);
        }
        else {
          input(ruleSettings, 'Points', item.Points, v => item.Points = v, 'number');
          input(ruleSettings, 'Scoring band name (optional)', item.BandGroup, v => item.BandGroup = v);
          const bandHelp = node('details', '', 'composition-help-disclosure'); bandHelp.append(node('summary', 'About scoring bands'), node('p', 'Use the same band name for mutually exclusive thresholds. Put the highest-priority rule first. Leave blank for independent awards.')); ruleSettings.append(bandHelp);
        }
        checks(fields, item.Conditions, 'Only apply when (all checks; blank means always)');
        select(fields, 'Required checks', String(item.AnyCheck), [['false', 'All checks'], ['true', 'Any check']], v => item.AnyCheck = v === 'true');
        checks(fields, item.Checks, item.Kind === 'Validation' ? 'The answer must satisfy' : 'Award points when');
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
  form.querySelectorAll('[data-pane]').forEach(button => button.addEventListener('click', () => { pane = button.dataset.pane; form.querySelectorAll('[data-pane]').forEach(b => b.setAttribute('aria-pressed', String(b === button))); render(); }));
  form.addEventListener('submit', async event => {
    event.preventDefault();
    form.elements.definitionJson.value = JSON.stringify(state, (key, value) => key === '_open' ? undefined : value);
    const version = form.elements.definitionJson.value;
    const submit = form.querySelector('[type=submit]'); submit.disabled = true;
    status.textContent = 'Saving configuration…'; status.classList.remove('is-error');
    try {
      const response = await fetch(form.action, { method: 'POST', body: new FormData(form), credentials: 'same-origin', headers: { Accept: 'application/json' } });
      const result = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(result.message || 'Configuration could not be saved. Check the fields and try again.');
      form.elements.expectedRevision.value = result.revision;
      dirty = JSON.stringify(state, (key, value) => key === '_open' ? undefined : value) !== version;
      status.textContent = dirty ? 'Saved. Newer edits are waiting to save.' : result.message;
    } catch (error) { status.textContent = error.message; status.classList.add('is-error'); }
    finally { submit.disabled = false; }
  });
  window.addEventListener('beforeunload', event => { if (dirty) { event.preventDefault(); event.returnValue = ''; } });
  render();
})();
