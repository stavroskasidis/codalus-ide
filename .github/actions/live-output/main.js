// Starts the uploader (uploader.js) as a process of its own that outlives this step, with what it needs in its
// environment: the runner gives the artifact service's address and token to an action's own process only, so they
// stay out of the tests' environment. The job's end (post.js) stops it and removes what it uploaded.
'use strict';
const { spawn } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

const input = name => (process.env['INPUT_' + name.toUpperCase()] || '').trim();
const state = path.join(process.env.RUNNER_TEMP || process.cwd(), 'codalus-live-output');
fs.rmSync(state, { recursive: true, force: true });
fs.mkdirSync(state, { recursive: true });

const log = fs.openSync(path.join(state, 'uploader.log'), 'a');
const child = spawn(process.execPath, [path.join(__dirname, 'uploader.js')], {
  detached: true,
  windowsHide: true,
  stdio: ['ignore', log, log],
  env: {
    ...process.env,
    LIVE_NAME: input('name'),
    LIVE_PATH: path.resolve(process.env.GITHUB_WORKSPACE || process.cwd(), input('path') || 'artifacts/test-results'),
    LIVE_INTERVAL: input('interval') || '60',
    LIVE_STATE: state,
  },
});
child.unref();
fs.writeFileSync(path.join(state, 'pid'), String(child.pid));
console.log(`Live output: process ${child.pid} uploads ${input('name')}-<n> every ${input('interval') || '60'} s while the job runs.`);
