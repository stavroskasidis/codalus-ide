# Web lint fixture

A JavaScript package with ESLint 10 (flat config, `eslint.config.mjs`) and Prettier 3 (`.prettierrc.json`), pinned by `package-lock.json`, for the M16d tests (ADR 0027). `src/app.js` has an unused variable (`no-unused-vars`, a warning, at `// [unused]`), a loose comparison (`eqeqeq`, an error), and statements Prettier reformats. Tests copy the folder and run `npm ci` there.
