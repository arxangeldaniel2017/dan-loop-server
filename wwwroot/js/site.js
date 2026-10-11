document.querySelectorAll('.tab').forEach(button => {
  button.addEventListener('click', () => {
    document.querySelectorAll('.tab').forEach(x => x.classList.remove('active'));
    document.querySelectorAll('.tab-panel').forEach(x => x.classList.remove('active'));
    button.classList.add('active');
    document.getElementById(button.dataset.tab)?.classList.add('active');
    history.replaceState(null, '', `#${button.dataset.tab}`);
  });
});

const initialTab = location.hash.slice(1);
if (initialTab) document.querySelector(`.tab[data-tab="${CSS.escape(initialTab)}"]`)?.click();
