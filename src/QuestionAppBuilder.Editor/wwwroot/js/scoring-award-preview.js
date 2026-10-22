(() => {
  const refresh = root => {
    const method = root.querySelector('[data-award-method]');
    const perUnitOption = method?.querySelector('option[value="PointsPerUnit"]');
    const supportsPerUnit = root.dataset.hasNumericAnswer === 'true';
    if (perUnitOption) perUnitOption.disabled = !supportsPerUnit && method?.value !== 'PointsPerUnit';
    const mode = method?.value;
    const perUnit = mode === 'PointsPerUnit';
    const details = root.querySelector('[data-per-unit-fields]');
    const fixed = root.querySelector('[data-fixed-award]')?.closest('.cv-field, .aw-field');
    if (details) details.hidden = !perUnit;
    if (fixed) fixed.hidden = perUnit;
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
  document.querySelectorAll('[data-award-editor]').forEach(refresh);
  document.addEventListener('change', event => {
    if (event.target.matches('#structure-question')) {
      const root = document.querySelector('#question-assets [data-award-editor]');
      if (root) {
        root.dataset.hasNumericAnswer = event.target.selectedOptions[0]?.dataset.answerType === 'Number' ? 'true' : 'false';
        refresh(root);
      }
    }
    if (event.target.matches('[data-award-method]') && event.target.value === 'PointsPerUnit') {
      const root = event.target.closest('[data-award-editor]');
      const family = root?.querySelector('[data-rule-family]');
      if (root?.dataset.hasNumericAnswer === 'true' && family) family.value = 'NumericRange';
    }
    if (event.target.matches('[data-award-method], [data-rule-family]')) refresh(event.target.closest('[data-award-editor]'));
  });
  document.addEventListener('input', event => {
    if (event.target.matches('[data-points-per-unit]')) updatePreview(event.target);
  });
})();
