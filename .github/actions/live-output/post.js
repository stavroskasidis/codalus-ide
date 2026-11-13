// The job's end, when the runner is still there to run it: stops the uploader, prints its log and removes what it
// uploaded. Passed or failed, the job's own log and its test-results artifact have everything by now. A runner that
// went silent never gets here, and its last upload stays. Never fails the job.
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { client } = require('./artifacts.js');

const state = path.join(process.env.RUNNER_TEMP || process.cwd(), 'codalus-live-output');
const sleep = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
const read = file => {
  try {
    return fs.readFileSync(path.join(state, file), 'utf8');
  } catch {
    return '';
  }
};

function alive(pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
}

async function main() {
  if (!fs.existsSync(state)) {
    return;
  }

  // An upload under way finishes first: its name is in the status only once it began.
  fs.writeFileSync(path.join(state, 'stop'), '');
  const pid = Number(read('pid'));
  for (let waited = 0; pid > 0 && alive(pid) && waited < 30000; waited += 100) {
    await sleep(100);
  }

  if (pid > 0 && alive(pid)) {
    try {
      process.kill(pid);
    } catch {
      // gone meanwhile
    }
  }

  console.log(read('uploader.log').trimEnd());
  let status = { uploads: 0, failures: 0, lastError: null, up: [], unsure: [] };
  try {
    status = JSON.parse(read('status.json'));
  } catch {
    console.log('::warning title=Live output::The uploader left no status: it never started, or ended at once (its log is above).');
  }

  const service = client();
  for (const name of status.up || []) {
    try {
      await service.remove(name);
      console.log(`Removed ${name}.`);
    } catch (error) {
      console.log(`::warning title=Live output::Couldn't remove ${name}: ${error.message}`);
    }
  }

  // An upload that failed may have got as far as creating its artifact; one that didn't isn't there to remove.
  for (const name of status.unsure || []) {
    try {
      await service.remove(name);
      console.log(`Removed ${name} (its upload had failed).`);
    } catch {
      // never created
    }
  }

  if (status.failures > 0) {
    // Said where it is seen: an uploader that can't upload is found out here, not the day a runner is lost.
    console.log(`::warning title=Live output::${status.failures} upload(s) failed, ${status.uploads} worked. The last error: ${status.lastError}`);
  }
}

main().catch(error => console.log(`::warning title=Live output::The job's end couldn't tidy up: ${error.message}`));
