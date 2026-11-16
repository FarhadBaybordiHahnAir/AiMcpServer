const form = document.querySelector('#new-project-form');
const projectInput = document.querySelector('#new-project-id');
const status = document.querySelector('#status');
let isBusy = false;

form.addEventListener('submit', async event => {
  event.preventDefault();
  if (isBusy) return;
  isBusy = true;
  const controls = form.querySelectorAll('button, input');
  controls.forEach(control => { control.disabled = true; });
  status.classList.remove('error');
  status.textContent = 'Creating project...';
  try {
    const projectId = projectInput.value.trim();
    if (!projectId || projectId.length > 200) throw new Error('Enter a project name (1-200 characters).');
    await jsonRequest('/projects', { projectId });
    window.location.assign(`/?project=${encodeURIComponent(projectId)}`);
  } catch (error) {
    status.textContent = error.message || 'The project could not be created.';
    status.classList.add('error');
  } finally {
    controls.forEach(control => { control.disabled = false; });
    isBusy = false;
  }
});