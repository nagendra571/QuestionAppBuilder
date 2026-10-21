(() => {
  const root = document.querySelector('.cv-management');
  if (!root) return;
  const panels = [...root.querySelectorAll('[data-category-panel], #manage-add-category')];
  const select = () => {
    const target = document.getElementById(location.hash.slice(1));
    const selected = target?.closest('[data-category-panel], #manage-add-category') || panels[0];
    if (selected?.matches('[data-category-panel]')) root.dataset.categoryReturn = `#${selected.id}`;
    panels.forEach(panel => { panel.hidden = panel !== selected; });
    root.querySelectorAll('.cv-category-nav a').forEach(link => {
      if (link.hash === `#${selected?.id}`) link.setAttribute('aria-current', 'location');
      else link.removeAttribute('aria-current');
    });
  };
  // Keep a stable category URL across redirects from legacy authoring actions.
  root.addEventListener('submit', event => {
    const panel = event.target.closest('[data-category-panel], #manage-add-category');
    if (panel) history.replaceState(null, '', `#${panel.id}`);
  });
  window.addEventListener('hashchange', select);
  select();
})();
