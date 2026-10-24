(() => {
  const root = document.querySelector('[data-authoring-context]');
  const dataElement = document.querySelector('#aw-help-data');
  if (!root || !dataElement) return;
  const data = JSON.parse(dataElement.textContent);
  const choice = (value, label, description, example) => ({ value, label, description, example });
  const topics = {
    scoringMode: {
      description: 'How this question combines its matching scoring rules. This does not decide whether an answer is correct or required.', hint: true,
      choices: [
        choice('Additive', 'Additive', 'Add the points from every matching rule. A rule contributes once, even if several of its options match.', 'A 2-point rule and a 3-point rule both match: the question earns 5 points.'),
        choice('FirstMatch', 'First match', 'Use only the first matching rule in order, even if it awards 0 points. Later matches contribute nothing.', 'With matching 2-point and 3-point rules, the first one awards 2 points. Give rules distinct order numbers when priority matters.')
      ]
    },
    family: {
      description: 'What must be true for this rule to award its points. The family must suit the question\'s answer type.', hint: true,
      choices: [
        choice('Presence', 'Presence', 'Award points when an answer is present. Blank or whitespace-only text is unanswered; the number 0 counts as an answer.', 'Award 1 point for providing an organization name.'),
        choice('Selection', 'Selection', 'For dropdown, radio, or checkbox questions. Choose target answer options, then choose Any of or All of under Membership.', 'Award 5 points when the respondent chooses a qualifying organization type.'),
        choice('NumericRange', 'Numeric range', 'For Number questions. The answer must fall within the rule\'s numeric bounds. This awards points; it does not restrict what numbers can be entered.', 'Award 3 points for values from 5 through 10. New rules include both boundaries; edit Include minimum/maximum to exclude one.')
      ]
    },
    membership: {
      description: 'Used by Selection rules. Choose at least one target option from the rule\'s option checkboxes. Points are awarded per rule, not per chosen option.', hint: true,
      choices: [
        choice('', 'Not used', 'Use this for Presence or Numeric range rules. A Selection rule needs Any of or All of.'),
        choice('AnyOf', 'Any of', 'At least one target option must be selected. Choosing several targets still awards this rule\'s points only once.', 'Targets are Training and Equipment. Training alone, Equipment alone, or both will match.'),
        choice('AllOf', 'All of', 'Every target option must be selected. Extra selected options are allowed; this is not an exact-set match.', 'With Training and Equipment as targets, both must be selected. Dropdown and radio questions can select only one option, so use a single target with All of for those types.')
      ]
    },
    matchMode: {
      description: 'How a visibility condition combines separate checks. This differs from Membership, which compares answer options within one scoring rule.', hint: true,
      choices: [
        choice('All', 'All checks (AND)', 'Every check in this condition must be true.', 'Show a later question only when organization type is Nonprofit AND years of experience is at least 3.'),
        choice('Any', 'Any check (OR)', 'At least one check in this condition must be true.', 'Show a later question when either Nonprofit is selected OR years of experience is at least 3. With just one check, All and Any behave the same.')
      ]
    },
    operator: {
      description: 'How to test an earlier question\'s answer. Numeric comparisons need a Number question and a comparison value. Option checks need a choice question and target options.',
      choices: [
        choice('Answered', 'Answered', 'There is an answer: nonblank text, a number (including 0), or at least one selected option.'),
        choice('Unanswered', 'Unanswered', 'There is no answer, including when the source question is hidden.'),
        choice('HasAnyOption', 'Has any option', 'At least one of the target options is selected.'),
        choice('HasAllOptions', 'Has all options', 'All target options are selected. Extra selections are allowed.'),
        choice('Equal', 'Equal', 'The numeric answer equals the comparison value.'),
        choice('NotEqual', 'Not equal', 'A numeric answer is present and differs from the comparison value. An unanswered question does not pass this check.'),
        choice('GreaterThan', 'Greater than', 'The numeric answer is above the comparison value; equality does not match.'),
        choice('GreaterThanOrEqual', 'Greater than or equal', 'The numeric answer is at least the comparison value.'),
        choice('LessThan', 'Less than', 'The numeric answer is below the comparison value; equality does not match.'),
        choice('LessThanOrEqual', 'Less than or equal', 'The numeric answer is at most the comparison value.')
      ]
    },
    navigation: {
      description: 'How respondents move between categories. Required visible answers are still checked before submission.',
      choices: [choice('Sequential', 'Sequential', 'Move through categories in order. Complete visible required answers before continuing.'), choice('Free', 'Free', 'Move between categories freely to work in a preferred order.')]
    },
    access: {
      description: 'How respondents identify themselves and return to saved responses.',
      choices: [choice('HostSignIn', 'Host sign-in', 'Use the host application\'s sign-in and access policies.'), choice('EmailLink', 'Email link', 'Use access links sent to an email address, without a host account. The host must have an email sender configured before publishing.')]
    },
    results: {
      description: 'What respondents see immediately after submission. This does not change how points are calculated or the settings for reopening a submitted application.',
      choices: [choice('ConfirmationOnly', 'Confirmation only', 'Show the confirmation message without scores.'), choice('Total', 'Total', 'Show the overall total score.'), choice('CategoryBreakdown', 'Category breakdown', 'Show the overall total and category scores.'), choice('ConfirmationAndAnswerReview', 'Confirmation, total & answer review', 'Show the confirmation, total, and all submitted answers by category. Respondents can later reopen the application using the separate submitted application view setting.')]
    },
    submittedApplicationVisibility: {
      description: 'What score details respondents see when they reopen a submitted application. This is separate from the immediate receipt shown after submission.',
      choices: [choice('WithoutScores', 'Read-only application without scores', 'Show visible categories, frozen wording, and submitted answers without score details.'), choice('WithScores', 'Read-only application with total and positive scores', 'Show the submitted application with the stored total and positive question, category, calculated-field, and combined-award results.')]
    },
    answerType: {
      description: 'Choose the shape of the answer. Required controls whether a visible question must be answered; scoring is configured separately.',
      choices: [choice('UploadDocument', 'Upload Document', 'Upload one PDF, Word (.docx), or UTF-8 plain-text document up to 10 MB. Files save with the response. Supports required-answer validation and presence scoring. Available for option-linked follow-ups too.'), choice('Textbox', 'Textbox', 'A short, single-line text answer.'), choice('Textarea', 'Textarea', 'A longer, multi-line text answer.'), choice('Dropdown', 'Dropdown', 'Choose one answer option from a menu.'), choice('RadioGroup', 'Radio group', 'Choose one answer option from a visible list.'), choice('Checkboxes', 'Checkboxes', 'Choose multiple answer options.'), choice('Number', 'Number', 'Enter a numeric value. Use numeric answer limits to restrict accepted values.')]
    },
    order: { description: 'Rules run from the lowest order number to the highest. In First match mode, the first matching rule wins. Use distinct numbers to make priority clear.', example: 'Order 0 is checked before order 1. Put a specific exception before a broader rule.' },
    points: { description: 'Points awarded when this rule matches and contributes under the question\'s scoring mode. Allowed values are 0 through 1,000,000. A matching rule worth 0 can still stop later rules in First match mode.' },
    ruleBounds: { description: 'Bounds for a Numeric range scoring rule. Leave a side blank for no limit on that side. Include minimum/maximum determines whether equality matches. New rules include both endpoints.', example: 'Minimum 5 and maximum 10 with both included matches 5, 7, and 10. Excluding the minimum stops 5 from matching. Use Preview to check boundary values and overlapping rules.' },
    answerBounds: { description: 'Allowed numeric answers for a Number question. Both endpoints are included; leave a side blank for no limit on that side. These are validation limits, not scoring rules.', example: 'Minimum 0 and maximum 100 accepts numbers from 0 through 100; scoring ranges can award points within that allowed range.' },
    textLimit: { description: 'Maximum characters for Textbox or Textarea answers, from 1 through 4,000. Blank uses the platform limit of 4,000 characters. This setting does not apply to numeric or choice answers.' },
    selectionLimit: { description: 'Limit the number of selected options on a choice question. Dropdown and radio questions allow at most one; checkboxes allow several. Leave a limit blank when it is not needed. Required separately controls whether an answer must be provided.', example: 'For checkboxes, minimum 1 and maximum 3 asks for between one and three selections.' },
    storedValue: { description: 'An internal code on the option definition. Respondents see the option label, not this code. Use a stable, meaningful code so a wording change does not require renaming the code.', example: 'Label: Not-for-profit organization. Stored value: nonprofit.' },
    source: { description: 'Choose the earlier question whose answer drives this visibility check. It must appear before the category or question being controlled. Numeric operators need a Number source; option operators need a choice source.' },
    compareNumber: { description: 'The comparison value for numeric visibility operators such as Equal or Greater than. Use a Number source question. Leave this blank for Answered, Unanswered, or option-based checks.' },
    helpText: { description: 'Instructions displayed to respondents below this question or content heading. Explain what information to provide or how it will be used. This is respondent-facing text, not an internal authoring note.' },
    contentVisibility: { description: 'Static content currently follows its category\'s visibility. To hide a content block conditionally, put it in a category with the appropriate Show when condition. An item-level condition does not currently hide static content.', hint: true, note: 'Static content follows its category. Set conditional visibility on the category.' }
  };

  const input = name => document.getElementsByName(name)[0];
  const value = name => input(name)?.value || '';
  const questionLabel = id => document.querySelector(`.aw-element[data-question-id="${Number(id)}"] [name^="Elements"][name$=".Label"]`)?.value || data.questions[id] || `Question ${id}`;
  const optionInputs = new Map([...document.querySelectorAll('input[name^="Options["][name$=".Id"]')].map(control => [control.value, input(control.name.replace('.Id', '.Label'))]));
  const optionLabel = id => optionInputs.get(String(id))?.value || data.options[id] || `Option ${id}`;
  const checkDescription = prefix => {
    const source = questionLabel(value(`${prefix}.SourceQuestionId`));
    const op = value(`${prefix}.Operator`);
    const number = value(`${prefix}.NumberValue`).replace(/(\.\d*?)0+$/, '$1').replace(/\.$/, '');
    const selected = [...document.getElementsByName(`${prefix}.OptionIds`)].filter(control => control.checked).map(control => optionLabel(control.value)).join(', ') || 'no target options';
    const descriptions = {
      Answered: 'has an answer', Unanswered: 'has no answer', HasAnyOption: `includes any of: ${selected}`, HasAllOptions: `includes all of: ${selected}`,
      Equal: `equals ${number || '(set a number)'}`, NotEqual: `has a number different from ${number || '(set a number)'}`, GreaterThan: `is greater than ${number || '(set a number)'}`,
      GreaterThanOrEqual: `is at least ${number || '(set a number)'}`, LessThan: `is less than ${number || '(set a number)'}`, LessThanOrEqual: `is at most ${number || '(set a number)'}`
    };
    return `${source}: ${descriptions[op] || 'choose a check'}`;
  };
  const visibility = () => ({
    description: 'Set up and edit conditions in the Visibility section of this workspace: choose which earlier question to check, the comparison, and any value or matching options. Then select a saved condition here to control when this item appears. Only checks added to that condition are considered. A follow-up also requires its parent option to be selected and its parent question to be visible; an additional condition cannot bypass that link. Always show removes the condition on this item. A category condition also applies to all questions inside it; hidden questions do not require answers or contribute points. The available conditions depend on this draft.', hint: true,
    choices: [choice('', 'Always show', 'No additional condition on this item. Category visibility and any parent-option link still apply.'), ...data.conditions.map(condition => {
      const all = value(`Conditions[${condition.index}].Junction`) === 'All';
      const checks = condition.prefixes.map(checkDescription);
      return choice(String(condition.id), `Condition ${condition.id}: ${all ? 'all checks' : 'any check'}`, `${all ? 'Every check must match' : 'At least one check must match'}: ${checks.join('; ')}.`, checks.length > 1 ? `The checks are combined with ${all ? 'AND' : 'OR'}.` : 'This condition currently has one check, so All and Any give the same result.');
    })]
  });
  const topicFor = control => {
    const name = control.name;
    if (name.endsWith('.VisibilityConditionId')) {
      const element = control.closest('.aw-element');
      return element && !element.dataset.questionId ? 'contentVisibility' : 'visibility';
    }
    if (name.endsWith('.ScoringMode')) return 'scoringMode';
    if (name.endsWith('.Family') || name === 'family') return 'family';
    if (name.endsWith('.MembershipOperator') || name === 'membershipOperator') return 'membership';
    if (name.endsWith('.Junction') || name === 'junction') return 'matchMode';
    if (name.endsWith('.Operator') || name === 'conditionOperator') return 'operator';
    if (name.endsWith('.SourceQuestionId') || name === 'sourceQuestionId') return 'source';
    if (name.endsWith('.NumberValue') || name === 'numberValue') return 'compareNumber';
    if (name.endsWith('.NavigationMode')) return 'navigation';
    if (name.endsWith('.RespondentAccessMode')) return 'access';
    if (name.endsWith('.ResultVisibility')) return 'results';
    if (name.endsWith('.SubmittedApplicationVisibility')) return 'submittedApplicationVisibility';
    if (name.endsWith('.AnswerType') || name === 'elementType') return 'answerType';
    if (name.startsWith('Rules[') && name.endsWith('.SortOrder')) return 'order';
    if (name.endsWith('.Points') || name === 'points') return 'points';
    if (name.endsWith('.MinValue') || name.endsWith('.MaxValue') || name === 'minValue' || name === 'maxValue') return name.startsWith('Questions[') ? 'answerBounds' : 'ruleBounds';
    if (name.endsWith('.MaxLength')) return 'textLimit';
    if (name.endsWith('.MinSelections') || name.endsWith('.MaxSelections')) return 'selectionLimit';
    if (name.startsWith('Options[') && name.endsWith('.Value') || name === 'optionValue') return 'storedValue';
    if (name.endsWith('.HelpText') || name === 'elementHelpText') return 'helpText';
    return null;
  };

  let openEntry = null;
  const entries = [];
  const textElement = (tag, text, className) => {
    const element = document.createElement(tag); element.textContent = text;
    if (className) element.className = className;
    return element;
  };
  const topicForEntry = entry => {
    if (entry.key === 'visibility') return visibility();
    if (entry.key === 'answerType' && entry.control.name === 'elementType')
      return { ...topics.answerType, choices: [choice('Content', 'Content block', 'Display instructions or explanatory text without collecting an answer or awarding points.'), ...topics.answerType.choices] };
    return topics[entry.key];
  };
  const shortNotes = {
    scoringMode: { Additive: 'Add points from every matching rule.', FirstMatch: 'Use the first matching rule in order; later rules contribute nothing.' },
    family: { Presence: 'Points for a nonblank answer; the number 0 counts as answered.', Selection: 'Points for matching target answer options.', NumericRange: 'Points when a number falls within the rule\'s bounds.' },
    membership: { '': 'Only Selection rules use membership.', AnyOf: 'One or more target options must be selected.', AllOf: 'Every target option must be selected; extras are allowed.' },
    matchMode: { All: 'Every check must pass (AND).', Any: 'At least one check must pass (OR).' }
  };
  const close = (restoreFocus = false) => {
    if (!openEntry) return;
    const entry = openEntry; openEntry = null;
    entry.panel.hidden = true; entry.button.setAttribute('aria-expanded', 'false');
    if (restoreFocus) entry.button.focus({ preventScroll: true });
  };
  const render = entry => {
    const topic = topicForEntry(entry);
    entry.body.replaceChildren(textElement('p', topic.description));
    if (topic.choices) {
      const choices = document.createElement('dl'); choices.className = 'aw-help-choices';
      const ordered = [...topic.choices].sort((left, right) => Number(right.value === entry.control.value) - Number(left.value === entry.control.value));
      for (const item of ordered) {
        const term = textElement('dt', item.label);
        if (item.value === entry.control.value) term.append(textElement('span', 'Selected', 'aw-help-selected'));
        const definition = textElement('dd', item.description);
        if (item.example) definition.append(textElement('p', `Example: ${item.example}`, 'aw-help-example'));
        choices.append(term, definition);
      }
      entry.body.append(choices);
    }
    if (topic.example) entry.body.append(textElement('p', `Example: ${topic.example}`, 'aw-help-example'));
  };
  const refreshHints = () => {
    const currentVisibility = visibility();
    for (const entry of entries) {
      if (entry.key === 'visibility') {
        for (const option of entry.control.options) {
          const condition = currentVisibility.choices.find(item => item.value === option.value);
          if (condition) option.textContent = condition.label;
        }
      }
      if (!entry.hint) continue;
      const topic = entry.key === 'visibility' ? currentVisibility : topicForEntry(entry);
      entry.hint.textContent = topic.note || shortNotes[entry.key]?.[entry.control.value] || topic.choices?.find(item => item.value === entry.control.value)?.description || topic.description;
    }
    if (openEntry) render(openEntry);
  };
  root.querySelectorAll('label.aw-field').forEach((oldLabel, index) => {
    const control = oldLabel.querySelector('select, input:not([type="checkbox"]):not([type="hidden"]), textarea');
    const key = control && topicFor(control);
    if (!control) return;
    const labelText = [...oldLabel.childNodes].filter(node => node.nodeType === Node.TEXT_NODE).map(node => node.textContent).join('').trim();
    const id = `aw-field-help-${index}`;
    if (!control.id) control.id = `aw-control-${control.name.replace(/[^a-zA-Z0-9_-]+/g, '-')}`;
    const wrapper = document.createElement('div'); wrapper.className = oldLabel.className;
    const row = document.createElement('div'); row.className = 'aw-help-label-row';
    const label = textElement('label', labelText); label.htmlFor = control.id;
    row.append(label); wrapper.append(row);
    oldLabel.before(wrapper);
    [...oldLabel.childNodes].forEach(node => { if (node.nodeType !== Node.TEXT_NODE) wrapper.append(node); });
    oldLabel.remove();
    if (!key) return;
    const button = document.querySelector('#aw-help-button-template').content.firstElementChild.cloneNode(true);
    button.setAttribute('aria-label', `Help for ${labelText}`); button.title = `Explain ${labelText}`;
    button.setAttribute('aria-controls', id); button.setAttribute('aria-expanded', 'false'); row.append(button);
    const panel = document.createElement('section'); panel.className = 'aw-field-help'; panel.id = id; panel.hidden = true;
    panel.setAttribute('aria-labelledby', `${id}-heading`);
    const header = document.createElement('header');
    const title = textElement('h4', labelText); title.id = `${id}-heading`;
    const dismiss = document.querySelector('#aw-help-close-template').content.firstElementChild.cloneNode(true);
    dismiss.setAttribute('aria-label', `Close help for ${labelText}`); dismiss.title = 'Close help';
    header.append(title, dismiss);
    const body = document.createElement('div'); body.className = 'aw-help-body'; body.tabIndex = 0;
    body.setAttribute('role', 'region'); body.setAttribute('aria-label', `${labelText} explanations`);
    panel.append(header, body);
    const entry = { key, control, wrapper, button, panel, body };
    if (topicForEntry(entry).hint) {
      entry.hint = textElement('p', '', 'aw-field-hint'); entry.hint.id = `${id}-hint`;
      wrapper.append(entry.hint);
      control.setAttribute('aria-describedby', [control.getAttribute('aria-describedby'), entry.hint.id].filter(Boolean).join(' '));
    }
    wrapper.append(panel); entries.push(entry);
    button.addEventListener('click', () => {
      const wasOpen = openEntry === entry; close();
      if (!wasOpen) { render(entry); panel.hidden = false; button.setAttribute('aria-expanded', 'true'); openEntry = entry; }
    });
    dismiss.addEventListener('click', () => close(true));
  });
  root.addEventListener('change', refreshHints);
  root.addEventListener('input', event => { if (/\.(Label|Name)$/.test(event.target.name || '')) refreshHints(); });
  document.addEventListener('click', event => { if (openEntry && !openEntry.wrapper.contains(event.target)) close(); });
  document.addEventListener('keydown', event => { if (event.key === 'Escape' && openEntry) { event.preventDefault(); close(true); } });
  window.addEventListener('hashchange', () => close());
  refreshHints();
})();
