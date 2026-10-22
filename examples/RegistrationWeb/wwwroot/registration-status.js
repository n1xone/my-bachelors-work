(() => {
  const element = document.getElementById('registration-status');
  if (!element) return;
  async function check() {
    if (!document.hidden) {
      try {
        const response = await fetch(element.dataset.statusUrl, { cache: 'no-store', credentials: 'same-origin' });
        if (response.ok) {
          const result = await response.json();
          if (result.state !== element.dataset.state) {
            location.replace(element.dataset.resultUrl);
            return;
          }
        }
      } catch { /* Keep the manual refresh link available during outages. */ }
    }
    setTimeout(check, 10000);
  }
  check();
})();
