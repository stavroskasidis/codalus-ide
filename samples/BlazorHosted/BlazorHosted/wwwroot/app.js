// A script of the page, for JavaScript breakpoints in a compound session.
function pageLoaded(title) {
  const message = 'Loaded ' + title;
  return message; // [script]
}

window.addEventListener('load', () => {
  document.body.dataset.loaded = pageLoaded(document.title);
});
