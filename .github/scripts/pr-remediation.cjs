const fs = require('node:fs');
const { cleanReview, successfulRuns, successfulChecks, PREVIEW_CONTEXT } = require('./codex-review-automation.cjs');
const LABEL = 'agent-remediate';
const PREFIX = '<!-- familyapp-remediation:';
const FAILURE = new Set(['failure', 'timed_out', 'action_required', 'startup_failure']);
const WORKFLOWS = new Set(['CI', '.NET CI', 'Automation tests', 'iOS']);
function eligible(pr, repository) {
  return pr.state === 'open' && !pr.draft && pr.base.ref === 'main' &&
    pr.head.repo?.full_name === repository && pr.head.ref !== 'main' &&
    pr.user.login === repository.split('/')[0] && pr.labels.some(x => x.name === LABEL);
}
function receipts(comments) {
  return comments.filter(x => x.user?.login === 'github-actions[bot]' && x.user.type === 'Bot')
    .flatMap(x => {
      try {
        const record = JSON.parse(x.body.match(/^<!-- familyapp-remediation:(.*?) -->/s)?.[1]);
        if (!record || typeof record !== 'object' || Array.isArray(record) ||
            !['attempt', 'published', 'verified', 'blocked', 'exhausted'].includes(record.kind)) return [];
        if (record.kind !== 'exhausted' && !/^[a-f0-9]{40}$/.test(record.sha || '')) return [];
        if (record.threads !== undefined && (!Array.isArray(record.threads) || record.threads.length > 100 || record.threads.some(id => typeof id !== 'string'))) return [];
        return [record];
      } catch { return []; }
    });
}
function decisiveReviews(reviews) {
  const latest = new Map();
  for (const review of [...reviews].sort((a, b) => a.id - b.id)) {
    if (['APPROVED', 'CHANGES_REQUESTED', 'DISMISSED'].includes(review.state)) latest.set(review.user?.id ?? `unknown:${review.id}`, review);
  }
  return [...latest.values()];
}
function reason(snapshot) {
  const failed = snapshot.runs.filter(x => WORKFLOWS.has(x.name) && x.status === 'completed' && FAILURE.has(x.conclusion));
  const preview = snapshot.statuses.find(x => x.context === PREVIEW_CONTEXT && ['failure', 'error'].includes(x.state));
  const copilot = snapshot.reviews.filter(x => x.user?.id === 175728472 && x.body?.startsWith('<!-- ccr-overview-v2 -->')).sort((a, b) => b.id - a.id)[0];
  const recommendedReview = copilot && /### 🟡 Changes recommended/.test(copilot.body) ? copilot.id : undefined;
  const latestReviews = decisiveReviews(snapshot.reviews);
  const superseded = comment => {
    const original = snapshot.reviews.find(r => r.id === comment.pullRequestReview?.databaseId);
    if (!original?.user?.id || original.state !== 'CHANGES_REQUESTED') return false;
    return latestReviews.some(r => r.user?.id === original.user.id && r.id > original.id &&
      ['APPROVED', 'DISMISSED'].includes(r.state));
  };
  const findings = snapshot.threads.filter(x => !x.isResolved && !x.isOutdated && x.comments.nodes.some(c => !superseded(c) && (
    /\bP[0-2]\b/.test(c.body) || c.pullRequestReview?.state === 'CHANGES_REQUESTED' ||
    (recommendedReview !== undefined && c.pullRequestReview?.databaseId === recommendedReview))));
  const reviews = decisiveReviews(snapshot.reviews).filter(x => x.state === 'CHANGES_REQUESTED');
  return { failed, preview, findings, reviews, actionable: !!(failed.length || preview || findings.length || reviews.length) };
}
function allowedPath(path) {
  return typeof path === 'string' && path.length < 250 && !/[\\\x00-\x1f]/.test(path) &&
    !path.split('/').some(x => !x || x === '.' || x === '..' || x.startsWith('.')) &&
    !/(^|\/)(AGENTS\.md|SKILL\.md)$/i.test(path) &&
    (/^(src|tests|scripts)\//.test(path) || ['netlify.toml', 'global.json', 'Directory.Build.props', 'FamilyApp.slnx'].includes(path));
}
function validateChanges(changes) {
  if (!Array.isArray(changes) || !changes.length || changes.length > 30) throw new Error('Expected 1–30 changed files');
  const seen = new Set(); let total = 0;
  for (const x of changes) {
    if (!allowedPath(x.path) || seen.has(x.path) || !['100644', '100755'].includes(x.mode)) throw new Error('Forbidden path, duplicate, or file mode');
    seen.add(x.path);
    if (x.content !== null && typeof x.content !== 'string') throw new Error('Invalid file content');
    total += Buffer.byteLength(x.content || '');
    if (total > 500000) throw new Error('Patch exceeds 500 KB');
    if (/(?:sk-(?:proj-)?[A-Za-z0-9_-]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|-----BEGIN .*PRIVATE KEY-----)/.test(x.content || '')) throw new Error('Possible credential in patch');
  }
  return changes;
}
function redact(value) {
  let text = String(value);
  for (const key of ['NETLIFY_AUTH_TOKEN', 'GITHUB_TOKEN']) if (process.env[key]) text = text.split(process.env[key]).join('[REDACTED]');
  return text.replace(/(?:sk-(?:proj-)?[A-Za-z0-9_-]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|Bearer\s+\S+)/g, '[REDACTED]');
}
function verification(state, comments, number) {
  const clean = cleanReview(comments, state.sha);
  const laterFeedback = clean && comments.some(x => x.user?.id === 199175422 && x.user.login === 'chatgpt-codex-connector[bot]' &&
    x.id > clean.id && !x.body.startsWith('<!-- codex-pull-request-review-summary -->'));
  return !!clean && !laterFeedback && !decisiveReviews(state.reviews).some(x => x.state === 'CHANGES_REQUESTED') &&
    successfulRuns(state.runs, number, state.sha) && successfulChecks(state.checks, state.statuses) &&
    state.runs.some(x => x.path?.split('@')[0] === '.github/workflows/ios.yml' && x.status === 'completed' && x.conclusion === 'success');
}
async function snapshot(github, params, pr) {
  const sha = pr.head.sha;
  const runs = await github.paginate(github.rest.actions.listWorkflowRunsForRepo, { ...params, head_sha: sha, event: 'pull_request', per_page: 100 });
  const latest = new Map();
  for (const r of runs.filter(r => r.head_sha === sha && r.pull_requests?.some(p => p.number === pr.number))) {
    if (!latest.has(r.workflow_id) || latest.get(r.workflow_id).id < r.id ||
      (latest.get(r.workflow_id).id === r.id && latest.get(r.workflow_id).run_attempt < r.run_attempt)) latest.set(r.workflow_id, r);
  }
  const checks = await github.paginate(github.rest.checks.listForRef, { ...params, ref: sha, filter: 'latest', per_page: 100 });
  const combined = (await github.rest.repos.getCombinedStatusForRef({ ...params, ref: sha, per_page: 100 })).data;
  if (combined.total_count > 100) throw new Error('Too many statuses: fail closed');
  const reviews = await github.paginate(github.rest.pulls.listReviews, { ...params, pull_number: pr.number, per_page: 100 });
  const result = await github.graphql(`query($owner:String!,$repo:String!,$number:Int!){repository(owner:$owner,name:$repo){pullRequest(number:$number){reviewThreads(first:100){pageInfo{hasNextPage} nodes{id isResolved isOutdated comments(first:100){pageInfo{hasNextPage} nodes{body commit{oid} pullRequestReview{state databaseId}}}}}}}}`, { ...params, number: pr.number });
  const threads = result.repository.pullRequest.reviewThreads;
  if (threads.pageInfo.hasNextPage || threads.nodes.some(x => x.comments.pageInfo.hasNextPage)) throw new Error('Incomplete review data');
  return { sha, runs: [...latest.values()], checks, statuses: combined.statuses, reviews, threads: threads.nodes };
}
async function netlifyLog(status, sha) {
  const url = new URL(status.target_url);
  const match = /^\/(?:projects|sites)\/profound-cajeta-7439db\/deploys\/([a-f0-9]{24})$/.exec(url.pathname);
  if (url.origin !== 'https://app.netlify.com' || !match || !process.env.NETLIFY_AUTH_TOKEN || !process.env.NETLIFY_SITE_ID) throw new Error('Netlify log access needs NETLIFY_AUTH_TOKEN and NETLIFY_SITE_ID');
  const response = await fetch(`https://api.netlify.com/api/v1/deploys/${match[1]}`, {
    headers: { Authorization: `Bearer ${process.env.NETLIFY_AUTH_TOKEN}` }, redirect: 'error', signal: AbortSignal.timeout(15000)
  });
  if (!response.ok) throw new Error(`Netlify deploy metadata HTTP ${response.status}`);
  const deploy = await response.json();
  if (deploy.commit_ref !== sha || deploy.site_id !== process.env.NETLIFY_SITE_ID) throw new Error('Netlify deploy does not match the PR head/site');
  // Netlify's official CLI uses this authenticated websocket for historical deploy logs.
  return new Promise((resolve, reject) => {
    const ws = new WebSocket('wss://socketeer.services.netlify.com/build/logs');
    let lines = ''; let done = false;
    const finish = error => {
      if (done) return; done = true; clearTimeout(timer); ws.close();
      if (error || !lines.trim()) reject(error || new Error('Netlify returned no deploy logs'));
      else resolve(redact(lines));
    };
    const timer = setTimeout(() => finish(new Error('Netlify log replay timed out')), 30000);
    ws.addEventListener('open', () => ws.send(JSON.stringify({ deploy_id: deploy.id, site_id: deploy.site_id, access_token: process.env.NETLIFY_AUTH_TOKEN })));
    ws.addEventListener('message', event => {
      let data; try { data = JSON.parse(String(event.data)); } catch { return; }
      if (data.type === 'report' && data.section === 'building') return finish();
      lines += `${data.log || data.message || ''}\n`;
      if (lines.length > 1000000) finish(new Error('Netlify log exceeds limit'));
    });
    ws.addEventListener('error', () => finish(new Error('Netlify log access failed')));
    ws.addEventListener('close', () => finish());
  });
}
async function prepare({ github, context, core }) {
  const params = context.repo; const repository = `${params.owner}/${params.repo}`;
  const pulls = await github.paginate(github.rest.pulls.list, { ...params, state: 'open', base: 'main', per_page: 100 });
  for (const initial of pulls) {
    try {
      const pr = (await github.rest.pulls.get({ ...params, pull_number: initial.number })).data;
      if (!eligible(pr, repository)) continue;
      const ip = { ...params, issue_number: pr.number };
      const comments = await github.paginate(github.rest.issues.listComments, { ...ip, per_page: 100 });
      const records = receipts(comments);
      const state = await snapshot(github, params, pr); const why = reason(state);
      // Verification uses authoritative GitHub results, never the agent's final message.
      if (records.some(x => x.kind === 'published' && x.sha === pr.head.sha) &&
          verification(state, comments, pr.number)) {
        const reviewedRef = /\*\*Reviewed commit:\*\* `([a-f0-9]+)`/.exec(cleanReview(comments, pr.head.sha).body)[1];
        if ((await github.rest.repos.getCommit({ ...params, ref: reviewedRef })).data.sha !== pr.head.sha) continue;
        const targets = new Set(records.filter(x => x.kind === 'attempt').flatMap(x => x.threads || []));
        const unresolved = state.threads.filter(x => !x.isResolved);
        // Close only originally targeted, now-outdated findings after independent current-head review.
        if (unresolved.every(x => x.isOutdated && targets.has(x.id))) {
          const fresh = (await github.rest.pulls.get({ ...params, pull_number: pr.number })).data;
          if (!eligible(fresh, repository) || fresh.head.sha !== pr.head.sha) continue;
          for (const thread of unresolved) await github.graphql('mutation($id:ID!){resolveReviewThread(input:{threadId:$id}){thread{isResolved}}}', { id: thread.id });
          if (!records.some(x => x.kind === 'verified' && x.sha === pr.head.sha)) await github.rest.issues.createComment({ ...ip,
            body: `${PREFIX}${JSON.stringify({ kind: 'verified', sha: pr.head.sha })} -->\nRemediation verified at ${pr.head.sha}: current-head CI, iOS/reported checks, Netlify preview and independent Codex review passed; targeted outdated findings resolved. The PR remains open for human merge. Linked issues remain open until their full acceptance criteria and production checks are verified.` });
          continue;
        }
      }
      if (!why.actionable) continue;
      if (records.some(x => x.kind === 'attempt' && x.sha === pr.head.sha)) continue;
      if (records.filter(x => x.kind === 'attempt').length >= 2) {
        if (!records.some(x => x.kind === 'exhausted')) await github.rest.issues.createComment({ ...ip, body: `${PREFIX}{"kind":"exhausted"} -->\nAutomatic remediation stopped after two attempts. A maintainer must investigate; nothing has been marked successful or closed.` });
        continue;
      }
      const evidence = { pr: pr.number, sha: pr.head.sha, body: pr.body, ...why, logs: [] };
      for (const run of why.failed) {
        const jobs = await github.paginate(github.rest.actions.listJobsForWorkflowRun, { ...params, run_id: run.id, filter: 'latest', per_page: 100 });
        const failedJobs = jobs.filter(x => FAILURE.has(x.conclusion));
        if (!failedJobs.length) throw new Error('Failed workflow has no available failed-job logs');
        for (const job of failedJobs) {
          const log = await github.rest.actions.downloadJobLogsForWorkflowRun({ ...params, job_id: job.id });
          // Octokit returns decoded text for the logs endpoint. Do not follow arbitrary URLs ourselves.
          if (typeof log.data !== 'string' || log.data.length > 1000000) throw new Error('GitHub job logs unavailable or too large');
          evidence.logs.push({ job: job.name, url: job.html_url, text: redact(log.data) });
        }
      }
      if (why.preview) evidence.logs.push({ provider: 'Netlify', url: why.preview.target_url, text: await netlifyLog(why.preview, pr.head.sha) });
      const evidenceText = redact(JSON.stringify(evidence));
      if (Buffer.byteLength(evidenceText) > 1500000) throw new Error('Evidence bundle exceeds 1.5 MB');
      const fresh = (await github.rest.pulls.get({ ...params, pull_number: pr.number })).data;
      if (!eligible(fresh, repository) || fresh.head.sha !== pr.head.sha) continue;
      const record = { kind: 'attempt', sha: pr.head.sha, run: String(context.runId), threads: why.findings.map(x => x.id) };
      await github.rest.issues.createComment({ ...ip, body: `${PREFIX}${JSON.stringify(record)} -->\nCoding agent dispatched for this commit. Success remains unverified until new CI, Netlify preview and independent review pass. [Run](https://github.com/${repository}/actions/runs/${context.runId}).` });
      fs.mkdirSync('evidence', { recursive: true }); fs.writeFileSync('evidence/context.json', evidenceText);
      core.setOutput('pr', pr.number); core.setOutput('sha', pr.head.sha); core.setOutput('branch', pr.head.ref);
      return;
    } catch (error) {
      core.warning(`PR #${initial.number}: blocked: ${error.message}`);
      if (eligible(initial, repository)) {
        const ip = { ...params, issue_number: initial.number };
        const comments = await github.paginate(github.rest.issues.listComments, { ...ip, per_page: 100 });
        if (!receipts(comments).some(x => x.kind === 'blocked' && x.sha === initial.head.sha)) await github.rest.issues.createComment({ ...ip,
          body: `${PREFIX}${JSON.stringify({ kind: 'blocked', sha: initial.head.sha })} -->\nRemediation blocked while reading authoritative CI/review/deploy evidence. Check [the workflow log](https://github.com/${repository}/actions/runs/${context.runId}) for the missing access or data. No fix, success claim, or closure was made. Scheduled runs retry evidence access.` });
      }
    }
  }
}
async function publish({ github, context, core }) {
  const params = context.repo; const number = Number(process.env.PR_NUMBER); const sha = process.env.EXPECTED_SHA;
  if (!Number.isSafeInteger(number) || number < 1 || !/^[a-f0-9]{40}$/.test(sha)) throw new Error('Invalid trusted inputs');
  const pp = { ...params, pull_number: number };
  const pr = (await github.rest.pulls.get(pp)).data;
  if (!eligible(pr, `${params.owner}/${params.repo}`) || pr.head.sha !== sha || pr.head.ref !== process.env.PR_BRANCH) throw new Error('PR head/authorization changed');
  const raw = fs.readFileSync('patch/changes.json');
  if (raw.length > 600000) throw new Error('Artifact too large');
  const changes = validateChanges(JSON.parse(raw));
  const commit = (await github.rest.git.getCommit({ ...params, commit_sha: sha })).data;
  const tree = [];
  for (const file of changes) {
    if (file.content === null) tree.push({ path: file.path, mode: file.mode, type: 'blob', sha: null });
    else {
      const blob = (await github.rest.git.createBlob({ ...params, content: file.content, encoding: 'utf-8' })).data;
      tree.push({ path: file.path, mode: file.mode, type: 'blob', sha: blob.sha });
    }
  }
  const createdTree = (await github.rest.git.createTree({ ...params, base_tree: commit.tree.sha, tree })).data;
  if (createdTree.sha === commit.tree.sha) throw new Error('Agent produced no changes');
  const fix = (await github.rest.git.createCommit({ ...params, message: `Fix PR #${number} CI/review findings`, tree: createdTree.sha, parents: [sha] })).data;
  const fresh = (await github.rest.pulls.get(pp)).data;
  if (!eligible(fresh, `${params.owner}/${params.repo}`) || fresh.head.sha !== sha || fresh.head.ref !== pr.head.ref) throw new Error('PR changed before publication');
  // Fast-forward only: a concurrent branch update makes this fail, never overwrites work.
  await github.rest.git.updateRef({ ...params, ref: `heads/${pr.head.ref}`, sha: fix.sha, force: false });
  core.setOutput('sha', fix.sha);
  core.info(`Published fix ${fix.sha}; verification pending`);
}
module.exports = { prepare, publish, eligible, receipts, reason, allowedPath, validateChanges, snapshot, netlifyLog, verification, redact };
