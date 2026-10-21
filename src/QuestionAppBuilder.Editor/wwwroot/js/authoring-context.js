// Only navigation state is retained, never questionnaire answers or credentials.
(() => {
  const root = document.querySelector('[data-authoring-context]');
  if (!root) return;
  const key = `qab-authoring:${location.pathname}`;
  const details = () => [...root.querySelectorAll('details')];
  const remember = control => {
    let active = control || document.activeElement;
    if (active?.matches('button[type="submit"]'))
      active = (active.closest('[data-workspace-pane]') || active.form)?.querySelector('input:not([type="hidden"]), select, textarea') || active;
    const state = {
      time: Date.now(), y: window.scrollY,
      open: details().filter(item => item.id && item.open).map(item => item.id),
      name: active?.getAttribute('name'), id: active?.id,
      select: [...root.querySelectorAll('select[data-context-selection]')].map(item => [item.name, item.value])
    };
    try { sessionStorage.setItem(key, JSON.stringify(state)); } catch { /* Storage may be unavailable in an embedded host. */ }
  };
  window.qabAuthoringContext = { remember };
  root.addEventListener('submit', event => { if (!event.defaultPrevented) remember(); });
  let state;
  try { state = JSON.parse(sessionStorage.getItem(key)); sessionStorage.removeItem(key); } catch { return; }
  if (!state || Date.now() - state.time > 120000) return;
  window.addEventListener('load', () => {
    details().forEach(item => { if (item.id) item.open = state.open.includes(item.id); });
    for (const [name, value] of state.select || []) {
      const select = [...root.querySelectorAll('select[data-context-selection]')].find(item => item.name === name);
      if (select && [...select.options].some(option => option.value === value)) {
        select.value = value; select.dispatchEvent(new Event('change', { bubbles: true }));
      }
    }
    const control = state.id ? document.getElementById(state.id) :
      [...root.querySelectorAll('[name]')].find(item => item.name === state.name && item.type !== 'hidden');
    control?.focus({ preventScroll: true });
    requestAnimationFrame(() => window.scrollTo({ top: state.y, behavior: 'instant' }));
  }, { once: true });
})();
