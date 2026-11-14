// The artifact service, as actions/upload-artifact talks to it (the toolkit's @actions/artifact, version 4 of the
// protocol): CreateArtifact answers a signed URL, the zip is put there, FinalizeArtifact makes it the run's. The
// run's and the job's ids are in the token.
'use strict';
const crypto = require('node:crypto');
const zlib = require('node:zlib');

function client(environment = process.env) {
  const token = environment.ACTIONS_RUNTIME_TOKEN;
  const address = environment.ACTIONS_RESULTS_URL;
  if (!token || !address) {
    throw new Error('ACTIONS_RUNTIME_TOKEN or ACTIONS_RESULTS_URL is missing: not an action\'s process.');
  }

  const claims = JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString('utf8'));
  const scope = String(claims.scp || '').split(' ').map(s => s.split(':')).find(parts => parts[0] === 'Actions.Results' && parts.length === 3);
  if (!scope) {
    throw new Error('The token has no Actions.Results scope.');
  }

  const ids = { workflow_run_backend_id: scope[1], workflow_job_run_backend_id: scope[2] };

  async function call(method, body) {
    const response = await fetch(new URL('twirp/github.actions.results.api.v1.ArtifactService/' + method, address.endsWith('/') ? address : address + '/'), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: 'Bearer ' + token, 'User-Agent': 'codalus-live-output' },
      body: JSON.stringify({ ...ids, ...body }),
      signal: AbortSignal.timeout(30000),
    });
    const text = await response.text();
    if (!response.ok) {
      throw new Error(`${method}: HTTP ${response.status} ${text.slice(0, 300)}`);
    }

    return text ? JSON.parse(text) : {};
  }

  return {
    /** Uploads a zip as the artifact `name`, kept for `days`. */
    async upload(name, zip, days) {
      const created = await call('CreateArtifact', { name, version: 4, expires_at: new Date(Date.now() + days * 86400000).toISOString() });
      const url = created.signed_upload_url || created.signedUploadUrl;
      if (!url) {
        throw new Error('CreateArtifact gave no upload URL: ' + JSON.stringify(created).slice(0, 300));
      }

      const put = await fetch(url, {
        method: 'PUT',
        headers: { 'x-ms-blob-type': 'BlockBlob', 'Content-Type': 'application/zip' },
        body: zip,
        signal: AbortSignal.timeout(120000),
      });
      if (!put.ok) {
        throw new Error(`The upload: HTTP ${put.status} ${(await put.text()).slice(0, 300)}`);
      }

      await call('FinalizeArtifact', { name, size: String(zip.length), hash: 'sha256:' + crypto.createHash('sha256').update(zip).digest('hex') });
    },

    async remove(name) {
      await call('DeleteArtifact', { name });
    },
  };
}

/** A zip of `entries` ({ name, data, modified }), deflated. */
function zip(entries) {
  const files = [];
  const directory = [];
  let offset = 0;
  for (const entry of entries) {
    const name = Buffer.from(entry.name, 'utf8');
    const packed = zlib.deflateRawSync(entry.data);
    const when = entry.modified || new Date();
    const time = (when.getHours() << 11) | (when.getMinutes() << 5) | (when.getSeconds() >> 1);
    const date = ((Math.max(when.getFullYear(), 1980) - 1980) << 9) | ((when.getMonth() + 1) << 5) | when.getDate();
    const fields = Buffer.alloc(26);
    fields.writeUInt16LE(20, 0); // the version needed
    fields.writeUInt16LE(0x0800, 2); // names in UTF-8
    fields.writeUInt16LE(8, 4); // deflate
    fields.writeUInt16LE(time, 6);
    fields.writeUInt16LE(date, 8);
    fields.writeUInt32LE(zlib.crc32(entry.data), 10);
    fields.writeUInt32LE(packed.length, 14);
    fields.writeUInt32LE(entry.data.length, 18);
    fields.writeUInt16LE(name.length, 22);

    const local = Buffer.alloc(4);
    local.writeUInt32LE(0x04034b50, 0);
    files.push(local, fields, name, packed);

    const central = Buffer.alloc(6);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(20, 4); // the version that made it
    const rest = Buffer.alloc(14); // comment length, disk, attributes
    rest.writeUInt32LE(offset, 10);
    directory.push(central, fields, rest, name);
    offset += 4 + fields.length + name.length + packed.length;
  }

  const central = Buffer.concat(directory);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(entries.length, 8);
  end.writeUInt16LE(entries.length, 10);
  end.writeUInt32LE(central.length, 12);
  end.writeUInt32LE(offset, 16);
  return Buffer.concat([...files, central, end]);
}

module.exports = { client, zip };
