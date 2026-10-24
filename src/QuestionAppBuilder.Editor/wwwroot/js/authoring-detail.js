(() => {
  const root = document.querySelector('.cv-management');
  if (!root) return;
  const panels = [...root.querySelectorAll('[data-category-panel], #manage-add-category')];
  const select = () => {
    const target = document.getElementById(location.hash.slice(1));
    const selected = target?.closest('[data-category-panel], #manage-add-category') || panels[0];
    if (target && root.dataset.authoringRestoringHash !== 'true' && selected?.matches('[data-category-panel], #manage-add-category')) {
      const publishing = root.querySelector('#application-publishing');
      if (publishing) publishing.open = false;
    }
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
  root.addEventListener('click', event => {
    if (!event.target.closest('.cv-category-nav a, [data-category-panel], #manage-add-category')) return;
    const publishing = root.querySelector('#application-publishing');
    if (publishing) publishing.open = false;
  });
  root.addEventListener('focusin', event => {
    if (root.dataset.authoringRestoring === 'true' || !event.target.closest('.cv-category-nav a, [data-category-panel], #manage-add-category')) return;
    const publishing = root.querySelector('#application-publishing');
    if (publishing) publishing.open = false;
  });
  window.addEventListener('hashchange', select);
  select();
})();
