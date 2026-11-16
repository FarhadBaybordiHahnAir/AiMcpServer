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