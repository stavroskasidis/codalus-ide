// The process main.js starts: every LIVE_INTERVAL seconds it zips the end of each shard's files under LIVE_PATH and
// uploads them as LIVE_NAME-<n>, then removes the upload before, so one whole artifact is always there. It ends when
// post.js asks (LIVE_STATE/stop), or with the runner. Nothing here may fail the job: errors go to its log, which
// post.js prints.
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { client, zip } = require('./artifacts.js');

const name = process.env.LIVE_NAME;
const root = process.env.LIVE_PATH;
const state = process.env.LIVE_STATE;
const interval = Math.max(1, Number(process.env.LIVE_INTERVAL) || 60) * 1000;

// What eng/ci-test.sh writes as a shard runs: every test as it ends (and the watchdog's stacks), and the samples.
const files = ['console.txt', 'memory.txt', 'load.txt', 'disk.txt'];
const tail = 2 * 1024 * 1024; // of each file: where a run was when its runner went silent is at the end
const days = 7;

const say = text => console.log(`${new Date().toISOString().slice(11, 19)} ${text}`);
const sleep = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
const stopping = () => fs.existsSync(path.join(state, 'stop'));

/** The end of each file there is, and what tells one collection from the next. */
function collect() {
  const entries = [];
  let signature = '';
  let shards = [];
  try {
    shards = fs.readdirSync(root, { withFileTypes: true }).filter(entry => entry.isDirectory()).map(entry => entry.name).sort();
  } catch {
    return { entries, signature }; // no results yet
  }

  for (const shard of shards) {
    for (const file of files) {
      const full = path.join(root, shard, file);
      try {
        const size = fs.statSync(full).size;
        if (size === 0) {
          continue;
        }

        const length = Math.min(size, tail);
        const data = Buffer.alloc(length);
        const handle = fs.openSync(full, 'r');
        try {
          fs.readSync(handle, data, 0, length, size - length);
        } finally {
          fs.closeSync(handle);
        }

        entries.push({ name: `${shard}/${file}`, data, modified: new Date() });
        signature += `${shard}/${file}:${size};`;
      } catch {
        // not there, or being replaced: the next round has it
      }
    }
  }

  return { entries, signature };
}

async function main() {
  const service = client();
  // up: uploaded and not removed yet; unsure: an upload that failed, which may have left something.
  const status = { uploads: 0, failures: 0, lastError: null, up: [], unsure: [] };
  const save = () => fs.writeFileSync(path.join(state, 'status.json'), JSON.stringify(status));
  let sequence = 0;
  let last = '';
  say(`watching ${root} for ${name}`);
  save();
  while (!stopping()) {
    const { entries, signature } = collect();
    if (entries.length > 0 && signature !== last) {
      const next = `${name}-${String(++sequence).padStart(4, '0')}`;
      try {
        const archive = zip(entries);

        // Named before it is there: an upload that fails halfway may have left it, and the job's end removes it.
        status.unsure.push(next);
        save();
        await service.upload(next, archive, days);
        status.unsure = status.unsure.filter(n => n !== next);
        status.up.push(next);
        status.uploads++;
        last = signature;
        say(`uploaded ${next}: ${entries.length} file(s), ${archive.length} bytes`);
        for (const earlier of status.up.filter(n => n !== next)) {
          try {
            await service.remove(earlier);
            status.up = status.up.filter(n => n !== earlier);
          } catch (error) {
            say(`couldn't remove ${earlier}: ${error.message}`);
          }
        }
      } catch (error) {
        status.failures++;
        status.lastError = error.message;
        say(`couldn't upload ${next}: ${error.message}`);
      }

      save();
    }

    // A second at a time: the job's end doesn't wait a whole interval.
    for (let waited = 0; waited < interval && !stopping(); waited += 1000) {
      await sleep(Math.min(1000, interval - waited));
    }
  }

  say('stopped');
}

main().catch(error => say(`ended: ${error.stack || error}`));
