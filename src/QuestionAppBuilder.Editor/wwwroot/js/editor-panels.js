(() => {
  document.addEventListener('submit', event => {
    const message = event.target.dataset.confirm;
    if (message && !window.confirm(message)) event.preventDefault();
  });
  document.addEventListener('click', event => {
    const button = event.target.closest('[data-close-panel]');
    if (!button) return;
    const disclosure = button.closest('details');
    if (disclosure) {
      disclosure.open = false;
      disclosure.querySelector(':scope > summary')?.focus({ preventScroll: true });
      return;
    }
    const returnPanel = button.closest('[data-close-return]');
    if (returnPanel) {
      const root = returnPanel.closest('[data-authoring-context]');
      const destination = root?.dataset.categoryReturn || returnPanel.dataset.closeReturn;
      location.hash = destination;
      [...root.querySelectorAll('.cv-category-nav a')].find(link => link.hash === destination)?.focus({ preventScroll: true });
      return;
    }
    if (button.closest('[data-workspace-pane]')) {
      location.hash = '#content';
      document.querySelector('[data-workspace-nav] a[href="#content"]')?.focus({ preventScroll: true });
    }
  });
})();
