# Web scripts fixture

An ASP.NET Core site that serves static files (`wwwroot/index.html` and `wwwroot/js/site.js`), with a `package.json`, for the M16d tests (ADR 0027):

- npm scripts in the Task Runner Explorer: `hello` prints a line, `greet` uses the local package `local-greeting` (a `file:` dependency, so restoring needs no registry), and `stamp` writes `stamp.txt`.
- `stamp` is bound to Before Build (`"-vs-binding"`), so a build runs it first.
- `wwwroot/js/site.js` has a `// [sum]` marker for a JavaScript breakpoint (Debug a Web Page on `index.html`).

`node_modules/`, `package-lock.json` and `stamp.txt` are created by the tests in their copy.
