(() => {
  const form = document.querySelector('#draft-workspace');
  if (!form) return;
  const panes = [...document.querySelectorAll('[data-workspace-pane]')];
  const categories = [...form.querySelectorAll('.aw-category')];
  const elements = [...form.querySelectorAll('.aw-element')];
  const outline = document.querySelector('.aw-rail');
  const elementSelect = form.querySelector('[name="categoryId"]');
  const questionSelect = form.querySelector('#structure-question');
  const selectionKey = `qab-selection:${location.pathname}`;
  let previous;
  try { previous = JSON.parse(sessionStorage.getItem(selectionKey)); } catch { /* Optional navigation memory. */ }
  let categoryId = categories.find(item => item.id === previous?.categoryId)?.id || categories[0]?.id;
  let elementId = elements.find(item => item.id === previous?.elementId)?.id;

  for (const category of categories) {
    const back = document.createElement('a');
    back.className = 'aw-back-category'; back.href = `#${category.id}`; back.textContent = 'Back to category';
    category.querySelector('.aw-category-title').after(back);
    const list = document.createElement('nav');
    list.className = 'aw-question-list'; list.setAttribute('aria-label', 'Questions in this category');
    category.querySelectorAll('.aw-element').forEach(element => {
      const link = document.createElement('a'); link.href = `#${element.id}`;
      const label = element.querySelector('[name^="Elements"][name$=".Label"]');
      link.textContent = label?.value || 'Untitled item';
      link.dataset.elementLink = element.id;
      link.style.setProperty('--branch-depth', element.dataset.branchDepth || '0');
      if (element.dataset.branchDepth !== '0') link.classList.add('aw-follow-up-link');
      list.append(link);
    });
    const add = document.createElement('a'); add.href = '#add-element'; add.textContent = 'Add question or content';
    list.append(add);
    category.querySelector('.aw-category-head').after(list);
  }

  const followUpSelect = document.querySelector('#add-follow-up-form [name="parentOptionId"]');
  const setFollowUp = link => {
    if (!link || !followUpSelect) return;
    followUpSelect.value = link.dataset.followUpOption;
    document.querySelector('[data-follow-up-back]').href = `#${link.dataset.followUpReturn}`;
  };
  document.querySelectorAll('[data-follow-up-option]').forEach(link => link.addEventListener('click', () => setFollowUp(link)));
  const initialOption = new URL(location.href).searchParams.get('followUpOption');
  if (initialOption) setFollowUp([...document.querySelectorAll('[data-follow-up-option]')].find(link => link.dataset.followUpOption === initialOption));
  const select = () => {
    const id = location.hash.slice(1);
    const target = document.getElementById(id);
    const element = target?.closest('.aw-element');
    const category = target?.closest('.aw-category');
    const pane = target?.closest('[data-workspace-pane]');
    const selectedPane = pane?.dataset.workspacePane || 'content';
    if (category) { categoryId = category.id; elementId = element?.id; }
    else if (!categoryId) categoryId = categories[0]?.id;
    try { sessionStorage.setItem(selectionKey, JSON.stringify({ categoryId, elementId })); } catch { /* Optional navigation memory. */ }
    panes.forEach(item => { item.hidden = item.dataset.workspacePane !== selectedPane; });
    categories.forEach(item => {
      item.hidden = item.id !== categoryId;
      item.dataset.editingQuestion = String(item.id === categoryId && Boolean(elementId));
    });
    elements.forEach(item => { item.hidden = item.id !== elementId; });
    if (elementSelect && categoryId) elementSelect.value = categoryId.replace('category-', '');
    const selectedElement = document.getElementById(elementId);
    const questionId = selectedElement?.dataset.questionId;
    if (questionSelect && questionId) {
      questionSelect.value = questionId;
      questionSelect.dispatchEvent(new Event('change', { bubbles: true }));
    }
    document.querySelectorAll('[data-workspace-nav] a, .aw-rail a, [data-element-link]').forEach(link => {
      const active = link.closest('[data-workspace-nav]') ? link.hash === `#${selectedPane}` : link.hash === `#${id}`;
      if (active) link.setAttribute('aria-current', 'location'); else link.removeAttribute('aria-current');
    });
    if (target) {
      for (let parent = target.parentElement; parent; parent = parent.parentElement)
        if (parent instanceof HTMLDetailsElement) parent.open = true;
      if (target instanceof HTMLDetailsElement) target.open = true;
    }
    if (outline && window.matchMedia('(max-width: 800px)').matches && id) outline.querySelector('details').open = false;
  };
  form.addEventListener('input', event => {
    const control = event.target;
    if (!/^(Elements\[.*\.Label|Categories\[.*\.Name)$/.test(control.name || '')) return;
    const owner = control.closest('.aw-element, .aw-category');
    if (!owner) return;
    document.querySelectorAll(`a[href="#${owner.id}"]`).forEach(link => { link.textContent = control.value || 'Untitled item'; });
    const heading = owner.querySelector('[data-item-title]');
    if (heading) heading.textContent = control.value || 'Untitled item';
  });
  window.addEventListener('hashchange', () => {
    select();
    const target = document.getElementById(location.hash.slice(1));
    target?.scrollIntoView({ block: 'start', behavior: 'instant' });
  });
  select();
})();
