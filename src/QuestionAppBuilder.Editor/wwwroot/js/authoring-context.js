// Only navigation state is retained, never questionnaire answers or credentials.
(() => {
  const root = document.querySelector('[data-authoring-context]');
  if (!root) return;
  const key = `qab-authoring:${location.pathname}`;
  const details = () => [...root.querySelectorAll('details')];
  const remember = (control, form) => {
    let active = control || document.activeElement;
    if (active?.matches('button[type="submit"]'))
      active = (active.closest('[data-workspace-pane]') || active.form)?.querySelector('input:not([type="hidden"]), select, textarea') || active;
    const state = {
      time: Date.now(), y: window.scrollY,
      hash: form?.closest('[data-category-panel], #manage-add-category')?.id
        ? `#${form.closest('[data-category-panel], #manage-add-category').id}` : location.hash,
      open: details().filter(item => item.id && item.open).map(item => item.id),
      name: active?.getAttribute('name'), id: active?.id,
      select: [...root.querySelectorAll('select[data-context-selection]')].map(item => [item.name, item.value])
    };
    try { sessionStorage.setItem(key, JSON.stringify(state)); } catch { /* Storage may be unavailable in an embedded host. */ }
  };
  const clear = () => { try { sessionStorage.removeItem(key); } catch { /* Storage may be unavailable in an embedded host. */ } };
  window.qabAuthoringContext = { remember, clear };
  root.addEventListener('submit', event => { if (!event.defaultPrevented) remember(event.submitter, event.target); });
  let state;
  try { state = JSON.parse(sessionStorage.getItem(key)); sessionStorage.removeItem(key); } catch { state = null; }
  const target = root.dataset.authoringTarget;
  const closeId = root.dataset.authoringClose;
  if (state && Date.now() - state.time > 120000) state = null;
  if (!state && !target && !closeId) return;
  window.addEventListener('load', () => {
    if (target) {
      const hash = target.startsWith('#') ? target : `#${target}`;
      if (location.hash !== hash) location.hash = hash;
      if (closeId) {
        const panel = document.getElementById(closeId);
        if (panel) panel.open = false;
      }
      return;
    }
    const restoreHash = state?.hash && location.hash !== state.hash;
    if (restoreHash) {
      root.dataset.authoringRestoringHash = 'true';
      location.hash = state.hash;
    }
    details().forEach(item => {
      if (!item.id) return;
      if (state) item.open = item.id !== closeId && (state.open || []).includes(item.id);
      else if (item.id === closeId) item.open = false;
    });
    for (const [name, value] of state?.select || []) {
      const select = [...root.querySelectorAll('select[data-context-selection]')].find(item => item.name === name);
      if (select && [...select.options].some(option => option.value === value)) {
        select.value = value; select.dispatchEvent(new Event('change', { bubbles: true }));
      }
    }
    const restoreFocusAndScroll = () => {
      const closeSummary = closeId && document.querySelector(`#${CSS.escape(closeId)} > summary`);
      const control = closeSummary || (state?.id ? document.getElementById(state.id) : state ?
        [...root.querySelectorAll('[name]')].find(item => item.name === state.name && item.type !== 'hidden') : null);
      if (control) {
        root.dataset.authoringRestoring = 'true';
        control.focus({ preventScroll: true });
        setTimeout(() => { delete root.dataset.authoringRestoring; }, 0);
      }
      if (closeSummary) requestAnimationFrame(() => closeSummary.scrollIntoView({ block: 'nearest', behavior: 'instant' }));
      else if (state) requestAnimationFrame(() => window.scrollTo({ top: state.y, behavior: 'instant' }));
    };
    if (restoreHash) window.addEventListener('hashchange', () => {
      delete root.dataset.authoringRestoringHash;
      restoreFocusAndScroll();
    }, { once: true });
    else restoreFocusAndScroll();
  }, { once: true });
})();
