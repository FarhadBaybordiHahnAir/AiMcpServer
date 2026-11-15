const projectInput = document.querySelector('#project');
const status = document.querySelector('#status');
const documents = document.querySelector('#documents');
const results = document.querySelector('#results');

function project() {
  const value = projectInput.value.trim();
  if (!value || value.length > 200) throw new Error('Enter a project label (1–200 characters).');
  return value;
}

async function request(path, options = {}) {
  const response = await fetch(`/api/rag${path}`, {
    ...options,
    headers: { 'X-Rag-Request': '1', ...options.headers }
  });
  if (!response.ok) {
    let error = `Request failed (HTTP ${response.status}).`;
    try {
      const payload = await response.json();
      error = payload.error || payload.detail || payload.title || error;
    } catch { /* The server may reject a request before producing JSON. */ }
    throw new Error(error);
  }
  return response.status === 204 ? null : response.json();
}

function jsonRequest(path, body) {
  return request(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body)
  });
}

async function run(action) {
  const controls = [...document.querySelectorAll('button, input, textarea')];
  controls.forEach(control => { control.disabled = true; });
  status.textContent = 'Working… Document indexing may take several minutes.';
  status.classList.remove('error');
  try {
    status.textContent = await action();
  } catch (error) {
    status.textContent = error.message || 'The request could not be completed.';
    status.classList.add('error');
  } finally {
    controls.forEach(control => { control.disabled = false; });
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

projectInput.addEventListener('input', () => {
  documents.replaceChildren(element('p', 'Refresh to load this project.'));
  results.replaceChildren();
});

document.querySelector('#refresh').addEventListener('click', () => run(async () => {
  const count = await loadDocuments(project());
  return `${count} document(s) indexed for this project.`;
}));

document.querySelector('#upload-form').addEventListener('submit', event => {
  event.preventDefault();
  run(async () => {
    const projectId = project();
    const file = document.querySelector('#file').files[0];
    if (!file || file.size === 0 || file.size > 10 * 1024 * 1024) throw new Error('Choose a non-empty PDF/text file of at most 10 MiB.');
    const form = new FormData();
    form.append('projectId', projectId);
    form.append('file', file);
    const saved = await request('/upload', { method: 'POST', body: form });
    await loadDocuments(projectId);
    return `Indexed ${saved.title}: ${saved.chunkCount} chunks.`;
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
