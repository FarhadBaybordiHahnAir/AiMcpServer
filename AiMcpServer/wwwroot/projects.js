const projects = document.querySelector('#projects');
const status = document.querySelector('#status');
let isBusy = false;

function element(tag, text) {
  const node = document.createElement(tag);
  node.textContent = text;
  return node;
}

async function run(action) {
  if (isBusy) return;
  isBusy = true;
  document.querySelectorAll('button').forEach(button => { button.disabled = true; });
  status.classList.remove('error');
  status.textContent = 'Working...';
  try {
    status.textContent = await action();
  } catch (error) {
    status.textContent = error.message || 'The request could not be completed.';
    status.classList.add('error');
  } finally {
    isBusy = false;
    document.querySelectorAll('button').forEach(button => { button.disabled = false; });
  }
}

async function loadProjects() {
  const entries = await request('/projects');
  projects.replaceChildren();
  if (!entries.length) {
    projects.append(element('p', 'No projects yet. Click New project to create one.'));
    return 'No projects yet.';
  }
  const table = document.createElement('table');
  const head = document.createElement('thead');
  const header = document.createElement('tr');
  for (const label of ['Project', 'Documents', 'Last activity', 'Actions']) {
    const cell = element('th', label);
    cell.scope = 'col';
    header.append(cell);
  }
  head.append(header);
  table.append(head);
  const body = document.createElement('tbody');
  for (const entry of entries) {
    const destination = `/?project=${encodeURIComponent(entry.projectId)}`;
    const row = document.createElement('tr');
    row.className = 'project-row';
    row.addEventListener('click', event => {
      if (!isBusy && !event.target.closest('a, button')) window.location.assign(destination);
    });
    const name = document.createElement('td');
    const link = element('a', entry.projectId);
    link.href = destination;
    name.append(link);
    const actions = document.createElement('td');
    const remove = element('button', 'Delete project');
    remove.type = 'button';
    remove.className = 'danger';
    remove.disabled = isBusy;
    remove.setAttribute('aria-label', `Delete project ${entry.projectId}`);
    remove.addEventListener('click', event => {
      event.stopPropagation();
      if (isBusy || !confirm(`Delete project "${entry.projectId}" and all its documents and indexed passages? This cannot be undone.`)) return;
      run(async () => {
        await request(`/projects?projectId=${encodeURIComponent(entry.projectId)}`, { method: 'DELETE' });
        await loadProjects();
        return `Deleted project ${entry.projectId}.`;
      });
    });
    actions.append(remove);
    row.append(name, element('td', String(entry.documentCount)),
      element('td', new Date(entry.updatedAt).toLocaleDateString()), actions);
    body.append(row);
  }
  table.append(body);
  projects.append(table);
  return `${entries.length} project(s) available.`;
}

document.querySelector('#refresh-projects').addEventListener('click', () => run(loadProjects));
run(loadProjects);