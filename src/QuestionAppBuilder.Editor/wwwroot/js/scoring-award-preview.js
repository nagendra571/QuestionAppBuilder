(() => {
  const settingsFor = root => {
    const questionId = root.dataset.questionId;
    return questionId ? document.getElementById(`answer-settings-${questionId}`) : null;
  };
  const answerTypeFor = root => {
    const selected = root.closest('#question-assets') ? document.querySelector('#structure-question') : null;
    return settingsFor(root)?.querySelector('[name$=".AnswerType"]')?.value
      || selected?.selectedOptions[0]?.dataset.answerType
      || (root.dataset.hasNumericAnswer === 'true' ? 'Number' : '');
  };
  const showGuidance = root => {
    const settings = settingsFor(root);
    const answerType = answerTypeFor(root);
    const wholeOnly = settings?.querySelector('[name$=".WholeNumbersOnly"][type="checkbox"]')?.checked
      || (!settings && root.dataset.wholeNumbersOnly === 'true');
    const minimumField = settings?.querySelector('[name$=".MinValue"]');
    const maximumField = settings?.querySelector('[name$=".MaxValue"]');
    const minimum = minimumField ? minimumField.value : root.dataset.questionMin;
    const maximum = maximumField ? maximumField.value : root.dataset.questionMax;
    const ruleFamily = root.querySelector('[data-rule-family]')?.value;
    const rateValue = root.querySelector('[data-points-per-unit]')?.value;
    const rate = Number(rateValue);
    const cap = root.querySelector('[name$="MaximumPoints"], [name="maximumPoints"]')?.value;
    const reasons = [];
    if (ruleFamily && ruleFamily !== 'NumericRange') reasons.push('Choose the Numeric range rule family.');
    if (answerType !== 'Number') reasons.push('This must be a Number question.');
    if (answerType === 'Number' && !wholeOnly) reasons.push('Enable Whole numbers only.');
    if (answerType === 'Number' && (!minimum || Number(minimum) < 0)) reasons.push('Set the question Minimum value to 0 or above.');
    const numericRate = rateValue !== '' && Number.isSafeInteger(rate) && rate >= 0 && rate <= 1000000;
    const validCap = cap === '' || cap == null || Number.isSafeInteger(Number(cap)) && Number(cap) >= 0 && Number(cap) <= 1000000;
    if (answerType === 'Number' && !numericRate) reasons.push('Set Points per unit to a whole number from 0 to 1,000,000.');
    if (answerType === 'Number' && !validCap) reasons.push('Set Maximum points to a whole number from 0 to 1,000,000.');
    if (answerType === 'Number' && rate > 0 && maximum && Math.trunc(Number(maximum)) < 0) reasons.push('The question Maximum value must be 0 or above.');
    if (answerType === 'Number' && !maximum && (cap === '' || cap == null)) reasons.push('Set the question Maximum value or a Maximum points cap.');
    if (answerType === 'Number' && maximum && Math.trunc(Number(maximum)) >= 0 && Number.isFinite(rate) && rate > 0 && (cap === '' || cap == null) && Math.trunc(Number(maximum)) * rate > 1000000)
      reasons.push('The rate and question Maximum value can exceed 1,000,000 points; lower the rate or set a cap.');
    const guidance = root.querySelector('[data-per-unit-guidance]');
    if (guidance) guidance.textContent = reasons.length
      ? `Needs attention: ${reasons.join(' ')}`
      : 'Requirements are met. Rule minimum and maximum choose when this rule matches; question minimum and maximum validate answers and bound the award.';
    const link = root.closest('#question-assets')?.querySelector('[data-per-unit-settings-link]');
    if (link && root.dataset.questionId) link.href = `#answer-settings-${root.dataset.questionId}`;
  };
  const refresh = root => {
    const method = root.querySelector('[data-award-method]');
    const perUnitOption = method?.querySelector('option[value="PointsPerUnit"]');
    const supportsPerUnit = answerTypeFor(root) === 'Number';
    if (perUnitOption) perUnitOption.disabled = !supportsPerUnit && method?.value !== 'PointsPerUnit';
    const mode = method?.value;
    const perUnit = mode === 'PointsPerUnit';
    const details = root.querySelector('[data-per-unit-fields]');
    const fixed = root.querySelector('[data-fixed-award]')?.closest('.cv-field, .aw-field');
    if (details) details.hidden = !perUnit;
    if (fixed) fixed.hidden = perUnit;
    if (perUnit) showGuidance(root);
  };
  const updatePreview = input => {
    const root = input.closest('[data-award-editor]');
    if (!root) return;
    const parsed = Number(input.value);
    const rate = Number.isSafeInteger(parsed) && parsed >= 0 ? parsed : 0;
    const product = 12 * rate;
    const rateNode = root.querySelector('[data-award-rate-preview]');
    const productNode = root.querySelector('[data-award-product-preview]');
    if (rateNode) rateNode.textContent = String(rate);
    if (productNode) productNode.textContent = String(product);
  };
  document.querySelectorAll('[data-award-editor]').forEach(root => {
    if (root.closest('#question-assets')) {
      const selected = document.querySelector('#structure-question');
      if (selected) root.dataset.questionId = selected.value;
    }
    const settings = settingsFor(root);
    if (settings) {
      root.dataset.hasNumericAnswer = settings.querySelector('[name$=".AnswerType"]')?.value === 'Number' ? 'true' : 'false';
      root.dataset.wholeNumbersOnly = settings.querySelector('[name$=".WholeNumbersOnly"][type="checkbox"]')?.checked ? 'true' : 'false';
    }
    refresh(root);
  });
  document.addEventListener('change', event => {
    if (event.target.matches('#structure-question')) {
      const root = document.querySelector('#question-assets [data-award-editor]');
      if (root) {
        root.dataset.questionId = event.target.value;
        const settings = settingsFor(root);
        root.dataset.hasNumericAnswer = (settings?.querySelector('[name$=".AnswerType"]')?.value || event.target.selectedOptions[0]?.dataset.answerType) === 'Number' ? 'true' : 'false';
        refresh(root);
      }
    }
    if (event.target.matches('[name$=".AnswerType"], [name$=".WholeNumbersOnly"], [name$=".MinValue"], [name$=".MaxValue"]'))
      document.querySelectorAll('[data-award-editor]').forEach(root => {
        const settings = settingsFor(root);
        if (settings) {
          const type = settings.querySelector('[name$=".AnswerType"]')?.value;
          root.dataset.hasNumericAnswer = type === 'Number' ? 'true' : 'false';
          root.dataset.wholeNumbersOnly = settings.querySelector('[name$=".WholeNumbersOnly"][type="checkbox"]')?.checked ? 'true' : 'false';
          refresh(root);
        } else if (root.querySelector('[data-award-method]')?.value === 'PointsPerUnit') showGuidance(root);
      });
    if (event.target.matches('[data-award-method]') && event.target.value === 'PointsPerUnit') {
      const root = event.target.closest('[data-award-editor]');
      const family = root?.querySelector('[data-rule-family]');
      if (root?.dataset.hasNumericAnswer === 'true' && family) family.value = 'NumericRange';
    }
    if (event.target.matches('[data-award-method], [data-rule-family], [data-points-per-unit], [name$="MaximumPoints"], [name="maximumPoints"]')) refresh(event.target.closest('[data-award-editor]'));
  });
  document.addEventListener('input', event => {
    if (event.target.matches('[data-points-per-unit]')) { updatePreview(event.target); showGuidance(event.target.closest('[data-award-editor]')); }
    if (event.target.matches('[name$=".MinValue"], [name$=".MaxValue"], [name$="MaximumPoints"], [name="maximumPoints"]'))
      document.querySelectorAll('[data-award-editor]').forEach(root => { if (root.querySelector('[data-award-method]')?.value === 'PointsPerUnit') showGuidance(root); });
  });
})();
