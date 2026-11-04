// Runs before first paint so a saved theme doesn't flash. Only known values reach the attributes.
(() => {
  try {
    const saved = JSON.parse(localStorage.getItem('arbetswatch.v1'));
    if (saved && (saved.theme === 'light' || saved.theme === 'dark')) {
      document.documentElement.dataset.theme = saved.theme;
    }
    if (saved && ['violet', 'blue', 'magenta'].includes(saved.accent)) {
      document.documentElement.dataset.accent = saved.accent;
    }
  } catch {
    // Unreadable or blocked storage: the system theme applies.
  }
})();
