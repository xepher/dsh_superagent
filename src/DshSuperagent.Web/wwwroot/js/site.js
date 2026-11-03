// Small progressive enhancements. No framework, no external requests.
document.addEventListener('submit', (event) => {
  const form = event.target;
  if (!(form instanceof HTMLFormElement)) return;
  const message = form.getAttribute('data-confirm');
  if (message && !window.confirm(message)) {
    event.preventDefault();
  }
});

document.addEventListener('click', async (event) => {
  const button = event.target.closest('[data-copy]');
  if (!button) return;
  event.preventDefault();
  const value = button.getAttribute('data-copy') || '';
  try {
    await navigator.clipboard.writeText(value);
    const previous = button.textContent;
    button.textContent = '已复制';
    button.disabled = true;
    setTimeout(() => {
      button.textContent = previous;
      button.disabled = false;
    }, 1500);
  } catch {
    window.prompt('复制以下内容', value);
  }
});

for (const alert of document.querySelectorAll('.alert.success, .alert.info')) {
  setTimeout(() => alert.classList.add('is-fading'), 6000);
}
