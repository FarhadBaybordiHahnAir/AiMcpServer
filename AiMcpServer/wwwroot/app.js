const projectInput = document.querySelector('#project');
const status = document.querySelector('#status');
const documents = document.querySelector('#documents');
const results = document.querySelector('#results');
let isBusy = false;

function project() {
  const value = projectInput.value.trim();
  if (!value || value.length > 200) throw new Error('Enter a project label (1–200 characters).');
  return value;
}

async function run(action) {
  if (isBusy) return;
  isBusy = true;
  const controls = [...document.querySelectorAll('button, input, textarea')];
  controls.forEach(control => { control.disabled = true; });
  status.textContent = 'Working…';
  status.classList.remove('error');
  try {
    status.textContent = await action();
  } catch (error) {
    status.textContent = error.message || 'The request could not be completed.';
    status.classList.add('error');
  } finally {
    document.querySelectorAll('button, input, textarea').forEach(control => { control.disabled = false; });
    isBusy = false;
  }
}

function element(tag, text, className) {
  const node = document.createElement(tag);
  node.textContent = text;
  if (className) node.className = className;
  return node;
}

function sourceNode(source) {
  try {
    const url = new URL(source);
    if (url.protocol === 'https:' || url.protocol === 'http:') {
      const link = element('a', source);
      link.href = url.href;
      link.target = '_blank';
      link.rel = 'noopener noreferrer';
      return link;
    }
  } catch { /* Uploaded filenames are displayed as text, never as local links. */ }
  return element('span', source);
}

async function selectProject(projectId) {
  projectInput.value = projectId;
  results.replaceChildren();
  documents.replaceChildren(element('p', 'Loading documents for this project…'));
  updateProjectSelection();
  return loadDocuments(projectId);
}

function updateProjectSelection() {
  const selected = projectInput.value.trim();
  document.querySelector('#upload-destination').textContent = selected
    ? `Adding documents to: ${selected}`
    : 'Select a project from the table before uploading documents.';
}

async function loadDocuments(projectId) {
  const entries = await request(`/documents?projectId=${encodeURIComponent(projectId)}`);
  documents.replaceChildren();
  if (!entries.length) documents.append(element('p', 'No documents indexed for this project.'));
  for (const entry of entries) {
    const card = element('article', '', 'card');
    card.append(element('h3', entry.title), sourceNode(entry.source));
    card.append(element('p', `${entry.chunkCount} chunks · ${entry.model} · ${entry.dimensions} dimensions · ${new Date(entry.createdAt).toLocaleString()}`));
    const remove = element('button', 'Delete document');
    remove.type = 'button';
    remove.addEventListener('click', () => {
      if (!confirm(`Delete "${entry.title}" and all its indexed passages?`)) return;
      run(async () => {
        await request(`/documents/${encodeURIComponent(entry.id)}?projectId=${encodeURIComponent(projectId)}`, { method: 'DELETE' });
        results.replaceChildren();
        await loadDocuments(projectId);
        return 'Document deleted.';
      });
    });
    card.append(remove);
    documents.append(card);
  }
  return entries.length;
}

run(async () => {
  const projectId = new URLSearchParams(window.location.search).get('project');
  if (!projectId) {
    window.location.replace('/projects.html');
    return 'Opening Projects...';
  }
  const entries = await request('/projects');
  if (!entries.some(entry => entry.projectId === projectId)) throw new Error('The requested project does not exist. Open the Projects page to select another.');
  await selectProject(projectId);
  return `Project ${projectId} selected. Add or remove documents below.`;
});

document.querySelector('#refresh').addEventListener('click', () => run(async () => {
  const count = await loadDocuments(project());
  return `${count} document(s) indexed for this project.`;
}));

document.querySelector('#upload-form').addEventListener('submit', event => {
  event.preventDefault();
  run(async () => {
    const projectId = project();
    const files = [...document.querySelector('#file').files];
    if (!files.length || files.length > 10) throw new Error('Choose between 1 and 10 files.');
    if (files.some(file => file.size === 0 || file.size > 10 * 1024 * 1024)) {
      throw new Error('Each file must be non-empty and at most 10 MiB.');
    }
    const form = new FormData();
    form.append('projectId', projectId);
    files.forEach(file => form.append('file', file));
    const saved = await request('/upload', { method: 'POST', body: form });
    await loadDocuments(projectId);
    return `Indexed ${saved.length} document(s): ${saved.map(document => `${document.title} (${document.chunkCount} chunks)`).join('; ')}.`;
  });
});

document.querySelector('#url-form').addEventListener('submit', event => {
  event.preventDefault();
  run(async () => {
    const projectId = project();
    const saved = await jsonRequest('/url', { projectId, url: document.querySelector('#url').value.trim() });
    await loadDocuments(projectId);
    return `Indexed ${saved.title}: ${saved.chunkCount} chunks.`;
  });
});

document.querySelector('#search-form').addEventListener('submit', event => {
  event.preventDefault();
  run(async () => {
    results.replaceChildren();
    const passages = await jsonRequest('/search', {
      projectId: project(),
      query: document.querySelector('#query').value.trim(),
      limit: Number(document.querySelector('#limit').value),
      minimumScore: Number(document.querySelector('#score').value)
    });
    for (const passage of passages) {
      const card = element('article', '', 'card');
      card.append(element('h3', passage.title), sourceNode(passage.source));
      card.append(element('p', `Citation: ${passage.citation}`, 'citation'));
      card.append(element('p', `Similarity: ${passage.score.toFixed(3)}${passage.page ? ` · PDF page ${passage.page}` : ''}`));
      card.append(element('pre', passage.text, 'passage'));
      results.append(card);
    }
    return passages.length ? `Retrieved ${passages.length} passage(s).` : 'No matching passages. Check the project label, imported documents, or minimum similarity.';
  });
});
