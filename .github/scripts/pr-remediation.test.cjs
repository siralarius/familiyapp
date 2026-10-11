const test = require('node:test');
const assert = require('node:assert/strict');
const { eligible, reason, receipts, validateChanges, prepare, publish, verification } = require('./pr-remediation.cjs');
const { isManual, PREVIEW_CONTEXT } = require('./codex-review-automation.cjs');
const sha = '50bb98fceb3195747217921df25f1a322b945623';
const pr = { number: 38, state: 'open', draft: false, base: { ref: 'main' }, head: { sha, ref: 'fix/dynamic-home-day-greeting', repo: { full_name: 'siralarius/familiyapp' } }, user: { login: 'siralarius' }, labels: [{ name: 'agent-remediate' }] };
const status = { context: PREVIEW_CONTEXT, state: 'failure', target_url: 'https://app.netlify.com/projects/profound-cajeta-7439db/deploys/6acbca9a947b9f0008efa7c2' };
const state = { sha, runs: [{ name: 'CI', status: 'completed', conclusion: 'success' }, { name: 'iOS', status: 'completed', conclusion: 'failure' }], statuses: [status], reviews: [], threads: [{ id: 'PRRC-test', isResolved: false, isOutdated: false, comments: { nodes: [{ commit: { oid: sha }, body: '**P2** Refresh the header when time boundaries pass' }] } }] };
test('PR38 observed case routes Netlify failure, iOS failure and current P2 finding', () => {
  const result = reason(state);
  assert.equal(result.actionable, true); assert.equal(result.preview, status);
  assert.deepEqual(result.failed.map(x => x.name), ['iOS']); assert.equal(result.findings.length, 1);
});
test('stale, resolved, optional reviews and pending checks do not dispatch', () => {
  const quiet = { ...state, runs: [{ name: 'CI', status: 'in_progress', conclusion: null }], statuses: [{ ...status, state: 'pending' }], threads: state.threads.map(x => ({ ...x, isOutdated: true })) };
  assert.equal(reason(quiet).actionable, false);
  assert.equal(reason({ ...quiet, threads: [{ ...state.threads[0], comments: { nodes: [{ commit: { oid: sha }, body: 'Optional naming preference' }] } }] }).actionable, false);
});
test('only owner-authored opted-in open same-repo PRs may be edited', () => {
  assert.equal(eligible(pr, 'siralarius/familiyapp'), true);
  for (const change of [{ draft: true }, { state: 'closed' }, { labels: [] }, { user: { login: 'stranger' } }, { head: { ...pr.head, repo: { full_name: 'fork/repo' } } }, { head: { ...pr.head, ref: 'main' } }, { base: { ref: 'release' } }]) assert.equal(eligible({ ...pr, ...change }, 'siralarius/familiyapp'), false);
  assert.equal(isManual(pr.labels), true, 'remediation label holds existing merge coordinator');
});
test('human-forged and malformed receipts cannot consume budget or claim verification', () => {
  const body = '<!-- familyapp-remediation:{"kind":"verified","sha":"fake"} -->';
  assert.deepEqual(receipts([{ body, user: { login: 'siralarius', type: 'User' } }]), []);
  assert.equal(receipts([{ body, user: { login: 'github-actions[bot]', type: 'Bot' } }]).length, 1);
  assert.deepEqual(receipts([{ body: '<!-- familyapp-remediation:oops -->', user: { login: 'github-actions[bot]', type: 'Bot' } }]), []);
});
test('publisher rejects policy edits, traversal, symlinks, duplicates, oversized patches and secrets', () => {
  const file = { path: 'src/file.cs', mode: '100644', content: 'valid' };
  assert.equal(validateChanges([file]).length, 1);
  for (const path of ['.github/workflows/ci.yml', 'AGENTS.md', 'src/AGENTS.md', 'src/../x', 'src//x', 'src\\x', 'src/.codex/config.toml', 'docs/x.md']) assert.throws(() => validateChanges([{ ...file, path }]));
  assert.throws(() => validateChanges([{ ...file, mode: '120000' }]));
  assert.throws(() => validateChanges([file, file]));
  assert.throws(() => validateChanges([{ ...file, content: 'a'.repeat(500001) }]));
  assert.throws(() => validateChanges([{ ...file, content: '-----BEGIN RSA PRIVATE KEY-----' }]));
  assert.throws(() => validateChanges([]));
});
test('stale PR head is rejected before any publisher mutation', async () => {
  const oldEnv = { ...process.env };
  Object.assign(process.env, { PR_NUMBER: '38', EXPECTED_SHA: sha, PR_BRANCH: pr.head.ref });
  try {
    await assert.rejects(publish({ github: { rest: { pulls: { get: async () => ({ data: { ...pr, head: { ...pr.head, sha: 'f'.repeat(40) } } }) } } }, context: { repo: { owner: 'siralarius', repo: 'familiyapp' } }, core: {} }), /changed/);
  } finally { process.env = oldEnv; }
});
function mock(recordList = [], options = {}) {
  const writes = []; const outputs = {};
  const run = { id: 1, workflow_id: 1, name: 'CI', path: '.github/workflows/ci.yml', head_sha: sha, event: 'pull_request', pull_requests: [{ number: 38 }], status: 'completed', conclusion: 'failure' };
  const rest = { pulls: { list: 'pulls', get: async () => ({ data: pr }), listReviews: 'reviews' },
    issues: { listComments: 'comments', createComment: async data => writes.push(data) },
    actions: { listWorkflowRunsForRepo: 'runs', listJobsForWorkflowRun: 'jobs', downloadJobLogsForWorkflowRun: async () => ({ data: 'compiler error CS1001' }) },
    checks: { listForRef: 'checks' }, repos: { getCombinedStatusForRef: async () => ({ data: { statuses: options.preview ? [status] : [], total_count: options.preview ? 1 : 0 } }) } };
  const data = { pulls: [pr], reviews: [], comments: recordList.map(record => ({ body: `<!-- familyapp-remediation:${JSON.stringify(record)} -->`, user: { login: 'github-actions[bot]', type: 'Bot' } })), runs: [run], jobs: [{ id: 5, name: 'build', conclusion: 'failure' }], checks: [] };
  return { writes, outputs, github: { rest, paginate: async key => data[key], graphql: async () => ({ repository: { pullRequest: { reviewThreads: { pageInfo: { hasNextPage: !!options.incomplete }, nodes: [] } } } }) }, context: { repo: { owner: 'siralarius', repo: 'familiyapp' }, runId: 123 }, core: { setOutput: (key, val) => { outputs[key] = val; }, warning: () => {} } };
}
test('deduplicates a head and stops after two attempts across new commits', async () => {
  let m = mock([{ kind: 'attempt', sha }]); await prepare(m); assert.equal(m.writes.length, 0); assert.deepEqual(m.outputs, {});
  m = mock([{ kind: 'attempt', sha: 'a'.repeat(40) }, { kind: 'attempt', sha: 'b'.repeat(40) }]);
  await prepare(m); assert.match(m.writes[0].body, /stopped after two/); assert.deepEqual(m.outputs, {});
});
test('incomplete review data and inaccessible Netlify logs block dispatch and closure', async () => {
  for (const options of [{ incomplete: true }, { preview: true }]) {
    const m = mock([], options); await prepare(m);
    assert.deepEqual(m.outputs, {}); assert.equal(m.writes.length, 1); assert.match(m.writes[0].body, /blocked/);
  }
});
test('verification requires fresh clean review, full CI, iOS and successful preview', () => {
  const comments = [{ id: 10, user: { id: 199175422, login: 'chatgpt-codex-connector[bot]', type: 'Bot' }, body: `Codex Review: Didn't find any major issues.\n\n**Reviewed commit:** \`${sha}\`` }];
  const runs = ['ci', 'dotnet-ci', 'automation-tests', 'ios'].map((name, i) => ({ id: i + 1, path: `.github/workflows/${name}.yml`, head_sha: sha, event: 'pull_request', pull_requests: [{ number: 38 }], status: 'completed', conclusion: 'success' }));
  const good = { ...state, runs, reviews: [], checks: [{ status: 'completed', conclusion: 'success' }], statuses: [{ ...status, state: 'success' }] };
  assert.equal(verification(good, comments, 38), true);
  for (const change of [{ runs: runs.slice(0, 3) }, { runs: runs.slice(1) }, { statuses: [] }, { statuses: [status] }, { statuses: [{ ...status, state: 'pending' }] }, { checks: [{ status: 'in_progress', conclusion: null }] }, { reviews: [{ user: { id: 1 }, state: 'CHANGES_REQUESTED' }] }]) assert.equal(verification({ ...good, ...change }, comments, 38), false);
  assert.equal(verification(good, [], 38), false);
  assert.equal(verification({ ...good, sha: 'a'.repeat(40) }, comments, 38), false);
  assert.equal(verification(good, [...comments, { ...comments[0], id: 11, body: 'New finding' }], 38), false);
});
test('only fully verified repaired heads resolve targeted outdated findings; PR and issues stay open', async () => {
  for (const scenario of ['verified', 'missing-review', 'pending-preview', 'active-finding', 'unrelated-finding', 'stale-head']) {
    const m = mock(); const resolved = [];
    const clean = { id: 10, user: { id: 199175422, login: 'chatgpt-codex-connector[bot]', type: 'Bot' }, body: `Codex Review: Didn't find any major issues.\n\n**Reviewed commit:** \`${sha}\`` };
    const records = [{ kind: 'attempt', sha: 'a'.repeat(40), threads: ['target'] }, { kind: 'published', sha }].map(x => ({ body: `<!-- familyapp-remediation:${JSON.stringify(x)} -->`, user: { login: 'github-actions[bot]', type: 'Bot' } }));
    const runs = ['ci', 'dotnet-ci', 'automation-tests', 'ios'].map((name, i) => ({ id: i + 1, path: `.github/workflows/${name}.yml`, workflow_id: i + 1, head_sha: sha, event: 'pull_request', pull_requests: [{ number: 38 }], status: 'completed', conclusion: 'success' }));
    m.github.paginate = async key => ({ pulls: [pr], reviews: [], comments: scenario === 'missing-review' ? records : [...records, clean], runs, checks: [{ status: 'completed', conclusion: 'success' }] })[key];
    m.github.rest.repos.getCombinedStatusForRef = async () => ({ data: { total_count: 1, statuses: [{ ...status, state: scenario === 'pending-preview' ? 'pending' : 'success' }] } });
    m.github.rest.repos.getCommit = async () => ({ data: { sha: scenario === 'stale-head' ? 'b'.repeat(40) : sha } });
    m.github.graphql = async (query, args) => {
      if (query.startsWith('mutation')) { resolved.push(args.id); return {}; }
      return { repository: { pullRequest: { reviewThreads: { pageInfo: { hasNextPage: false }, nodes: [{ id: scenario === 'unrelated-finding' ? 'other' : 'target', isResolved: false, isOutdated: scenario !== 'active-finding', comments: { pageInfo: { hasNextPage: false }, nodes: [] } }] } } } };
    };
    await prepare(m);
    assert.deepEqual(m.outputs, {});
    assert.deepEqual(resolved, scenario === 'verified' ? ['target'] : []);
    assert.equal(m.writes.length, scenario === 'verified' ? 1 : 0);
    if (scenario === 'verified') assert.match(m.writes[0].body, /PR remains open.*Linked issues remain open/);
  }
});
