// Executed on the unprivileged agent runner; the publisher independently validates everything.
const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const { validateChanges } = require('./pr-remediation.cjs');
const root = path.resolve('work');
function git(args) { return execFileSync('git', args, { cwd: root, encoding: 'utf8', maxBuffer: 1000000 }); }
const sha = process.env.EXPECTED_SHA;
if (!/^[a-f0-9]{40}$/.test(sha)) throw new Error('Invalid base SHA');
// Compare against the trusted base even if the agent staged changes or made local commits.
const names = new Set([...git(['diff', '--name-only', '-z', sha]).split('\0'),
  ...git(['ls-files', '--others', '--exclude-standard', '-z']).split('\0')].filter(Boolean));
const changes = [];
for (const name of names) {
  if (name.startsWith('remediation-evidence/')) continue;
  const file = path.resolve(root, name);
  if (!file.startsWith(root + path.sep)) throw new Error('Invalid path');
  let stat; try { stat = fs.lstatSync(file); } catch (error) { if (error.code !== 'ENOENT') throw error; }
  if (stat && (!stat.isFile() || stat.isSymbolicLink())) throw new Error('Only regular files allowed');
  // Also reject symlink parents before reading candidate contents.
  let parent = path.dirname(file);
  while (parent !== root) {
    if (fs.existsSync(parent) && fs.lstatSync(parent).isSymbolicLink()) throw new Error('Symlink parent');
    parent = path.dirname(parent);
  }
  if (stat?.size > 500000) throw new Error('File too large');
  const content = stat ? fs.readFileSync(file, 'utf8') : null;
  if (content?.includes('\0') || content?.includes('\ufffd')) throw new Error('Binary/invalid UTF-8 patch');
  const mode = stat && (stat.mode & 0o111) ? '100755' : '100644';
  changes.push({ path: name, mode, content });
}
validateChanges(changes);
fs.mkdirSync('patch', { recursive: true });
fs.writeFileSync('patch/changes.json', JSON.stringify(changes));
