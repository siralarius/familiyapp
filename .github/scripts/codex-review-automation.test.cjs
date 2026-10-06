const test = require('node:test');
const assert = require('node:assert/strict');
const { run, cleanReview, isManual, referencedIssueNumbers, dispatchValidation, recoverValidation, successfulRuns, successfulChecks, REQUIRED_WORKFLOWS, PREVIEW_CONTEXT } = require('./codex-review-automation.cjs');
const head = '65373582fb0b503d689ef7fc9ae01528dcd013f9';
const bot = { id: 199175422, login: 'chatgpt-codex-connector[bot]', type: 'Bot' };
const clean = { id: 10, user: bot, body: "Codex Review: Didn't find any major issues. Keep them coming!\n\n**Reviewed commit:** `65373582fb`\n\n<details>About Codex</details>" };
const runs = REQUIRED_WORKFLOWS.map((path, index) => ({ id: index + 1, path, head_sha: head, event: 'pull_request', pull_requests: [{ number: 42 }], status: 'completed', conclusion: 'success', run_attempt: 1 }));
const checks = [{ status: 'completed', conclusion: 'success' }];
const statuses = [{ context: PREVIEW_CONTEXT, state: 'success' }];

test('clean evidence requires the exact observed bot identity and reviewed commit', () => {
  assert.equal(cleanReview([clean], head), clean);
  for (const user of [{ ...bot, id: 123 }, { ...bot, login: 'someone' }, { ...bot, type: 'User' }]) assert.equal(cleanReview([{ ...clean, user }], head), undefined);
  assert.equal(cleanReview([clean], 'a'.repeat(40)), undefined);
  for (const body of ['Completed', '👍', clean.body.replace('65373582fb', '6537358'), clean.body + '\n[P1] Data loss', clean.body.replace('any major issues', 'any issues')]) assert.equal(cleanReview([{ ...clean, body }], head), undefined);
});
test('manual label matching is case insensitive', () => {
  assert.ok(isManual([{ name: 'Manual-Merge' }]));
  assert.ok(!isManual([{ name: 'bug' }]));
});
test('cosmetic clean-review wording does not affect the verdict', () => {
  for (const ending of ['', ' More of your lovely PRs please.', ' Onward and upward!']) assert.ok(cleanReview([{ ...clean, body: clean.body.replace(' Keep them coming!', ending) }], head));
  assert.equal(cleanReview([{ ...clean, body: clean.body.replace(' Keep them coming!', ' But P1 data loss remains.') }], head), undefined);
});
test('a running review or a summary for a stale commit invalidates clean evidence', () => {
  const summary = status => ({ id: 1, user: bot, body: `<!-- codex-pull-request-review-summary -->\n| 📝 **Code Review** | **${status}** | \`6537358\` | Manual request |` });
  assert.ok(cleanReview([clean, summary('Completed')], head));
  assert.equal(cleanReview([clean, summary('Running')], head), undefined);
  assert.equal(cleanReview([clean, { ...summary('Completed'), body: summary('Completed').body.replace('6537358', '1111111') }], head), undefined);
});
test('every completed review row must cover the current commit', () => {
  const summary = ref => ({ id: 1, user: bot, body: `<!-- codex-pull-request-review-summary -->\n| **Code Review** | **Completed** | \`6537358\` | Manual |\n| **Security Review** | **Completed** | \`${ref}\` | Manual |` });
  assert.ok(cleanReview([clean, summary('65373582fb')], head));
  assert.equal(cleanReview([clean, summary('1111111')], head), undefined);
  assert.equal(cleanReview([clean, summary('')], head), undefined);
});
test('related issues and same-repository issue URLs are recognized without closing them', () => {
  assert.deepEqual(referencedIssueNumbers('<a href="/siralarius/familiyapp/issues/25">#25</a> <a href="/siralarius/familiyapp/issues/1">#1</a> <a href="https://github.com/siralarius/familiyapp/issues/9">link</a>', 'siralarius', 'familiyapp'), [1, 9, 25]);
  assert.deepEqual(referencedIssueNumbers('<a href="https://github.com/other/repo/issues/8">foreign</a>', 'siralarius', 'familiyapp'), []);
});
test('all documented local GitHub reference forms inherit issue holds', () => {
  assert.deepEqual(referencedIssueNumbers('<a href="/siralarius/familiyapp/issues/25">GH-25</a><a href="/siralarius/familiyapp/issues/26">siralarius/familiyapp#26</a><a href="https://redirect.github.com/siralarius/familiyapp/issues/29">link</a>', 'siralarius', 'familiyapp'), [25, 26, 29]);
});
test('code examples and plain non-reference tokens do not inherit issue holds', () => {
  assert.deepEqual(referencedIssueNumbers('<p>Color #123456</p><code>GH-25</code><pre>&lt;a href="/siralarius/familiyapp/issues/99"&gt;example&lt;/a&gt;</pre>', 'siralarius', 'familiyapp'), []);
});
test('GitHub-rendered relative issue links inherit holds', () => {
  assert.deepEqual(referencedIssueNumbers('<a href="/siralarius/familiyapp/issues/25">parent</a><a href="../issues/26">parent</a>', 'siralarius', 'familiyapp'), [25, 26]);
});
test('CI is required for this PR and commit, latest reruns must succeed', () => {
  assert.ok(successfulRuns(runs, 42, head));
  assert.ok(!successfulRuns(runs.slice(1), 42, head));
  assert.ok(!successfulRuns(runs, 43, head));
  assert.ok(!successfulRuns(runs, 42, 'a'.repeat(40)));
  for (const conclusion of ['failure', 'cancelled', 'skipped', null]) assert.ok(!successfulRuns([...runs, { ...runs[0], id: 100, conclusion }], 42, head));
  assert.ok(!successfulRuns([...runs, { ...runs[0], run_attempt: 2, status: 'in_progress', conclusion: null }], 42, head));
});
test('preview must exist and all checks/statuses must complete successfully', () => {
  assert.ok(successfulChecks(checks, statuses));
  assert.ok(!successfulChecks(checks, []));
  assert.ok(!successfulChecks([{ status: 'in_progress' }], statuses));
  assert.ok(!successfulChecks(checks, [...statuses, { context: 'other', state: 'failure' }]));
});

function fixture(options = {}) {
  const calls = [];
  const issue = { number: 7, repository: { nameWithOwner: 'siralarius/familiyapp' }, labels: { pageInfo: { hasNextPage: false }, nodes: [] } };
  const pr = { number: 42, state: 'open', draft: false, body: '', user: { login: 'siralarius' }, labels: [], base: { ref: 'main', sha: 'base' }, head: { sha: head, repo: { full_name: 'siralarius/familiyapp' } }, mergeable: true, mergeable_state: 'clean', ...options.pr };
  let gets = 0, links = 0;
  const list = values => async () => ({ data: values });
  const github = {
    paginate: async (method, params) => (await method(params)).data,
    graphql: async () => {
      links++;
      const nodes = options.noIssues ? [] : [{ ...issue, labels: { ...issue.labels, nodes: options.manualIssue || (options.lateManualIssue && links > 1) ? [{ name: 'manual-merge' }] : [] } }];
      return { repository: { pullRequest: {
        bodyHTML: options.bodyHTML || '',
        closingIssuesReferences: { pageInfo: { hasNextPage: !!options.moreIssues }, nodes },
        reviewThreads: { pageInfo: { hasNextPage: false }, nodes: options.unresolved ? [{ isResolved: false }] : [] }
      } } };
    },
    rest: {
      pulls: {
        list: async params => ({ data: params.state === 'closed' ? [] : [pr] }), get: async () => { gets++; return { data: gets > 1 ? { ...pr, ...options.fresh } : pr }; },
        listReviews: list(options.reviews || []),
        merge: async params => { calls.push({ kind: 'merge', params }); return { data: { merged: true } }; }
      },
      issues: {
        get: async () => ({ data: options.mentionedPR ? { pull_request: {} } : {} }),
        listComments: list(options.comments || [clean]),
        createComment: async params => { calls.push({ kind: 'comment', params }); },
        listLabelsOnIssue: async () => ({ data: options.mentionedManual || (options.lateMentionedManual && links > 1) ? [{ name: 'manual-merge' }] : [] })
      },
      repos: {
        getCommit: async () => ({ data: { sha: options.resolvedSha || head } }),
        getCombinedStatusForRef: async () => ({ data: { statuses, total_count: 1 } }),
        compareCommits: async () => ({ data: { behind_by: options.behind || 0 } })
      },
      checks: { listForRef: list(checks) },
      actions: {
        listWorkflowRunsForRepo: list(options.runs || runs),
        createWorkflowDispatch: async params => { calls.push({ kind: 'dispatch', params }); }
      }
    }
  };
  return { github, context: { repo: { owner: 'siralarius', repo: 'familiyapp' } }, core: { info() {}, warning(message) { calls.push({ kind: 'warning', message }); } }, calls };
}
test('eligible PR merges using the full reviewed SHA and dispatches post-merge checks', async () => {
  const f = fixture(); await run(f);
  assert.equal(f.calls.filter(c => c.kind === 'merge').length, 1);
  assert.equal(f.calls.find(c => c.kind === 'merge').params.sha, head);
  assert.equal(f.calls.filter(c => c.kind === 'dispatch').length, 3);
});
for (const [name, options] of Object.entries({
  'manual PR': { pr: { labels: [{ name: 'manual-merge' }] } },
  'manual issue': { manualIssue: true },
  'label added during processing': { lateManualIssue: true },
  'related issue is manual': { noIssues: true, bodyHTML: '<a href="/siralarius/familiyapp/issues/25">#25</a>', mentionedManual: true },
  'related issue marked manual late': { noIssues: true, bodyHTML: '<a href="/siralarius/familiyapp/issues/25">#25</a>', lateMentionedManual: true },
  'GH reference is manual': { noIssues: true, bodyHTML: '<a href="/siralarius/familiyapp/issues/25">GH-25</a>', mentionedManual: true },
  'qualified local reference is manual': { noIssues: true, bodyHTML: '<a href="/siralarius/familiyapp/issues/25">siralarius/familiyapp#25</a>', mentionedManual: true },
  'rendered issue link is manual': { noIssues: true, bodyHTML: '<a href="https://github.com/siralarius/familiyapp/issues/25">parent</a>', mentionedManual: true },
  'incomplete issue pagination': { moreIssues: true },
  'unresolved findings': { unresolved: true },
  'CI missing': { runs: [] },
  'draft': { pr: { draft: true } },
  'fork': { pr: { head: { sha: head, repo: { full_name: 'someone/familiyapp' } } } },
  'non-owner PR': { pr: { user: { login: 'someone' } } },
  'head changed before merge': { fresh: { head: { sha: 'a'.repeat(40) } } },
  'label added to PR': { fresh: { labels: [{ name: 'manual-merge' }] } },
  'base changed': { fresh: { base: { sha: 'new-base', ref: 'main' } } },
  'behind main': { behind: 1 },
  'GitHub merge blocked': { fresh: { mergeable_state: 'blocked' } },
  'ambiguous abbreviation': { resolvedSha: 'a'.repeat(40) },
  'later bot feedback': { comments: [clean, { ...clean, id: 11, body: 'Found a defect' }] },
  'human requests changes': { reviews: [{ user: { id: 123 }, state: 'CHANGES_REQUESTED' }] },
  'Codex review has findings': { reviews: [{ user: bot, state: 'COMMENTED', commit_id: head }] }
})) test(`never merges: ${name}`, async () => { const f = fixture(options); await run(f); assert.equal(f.calls.filter(c => c.kind === 'merge').length, 0); });
test('missing review waits without posting ignored Actions-authored requests', async () => {
  const f = fixture({ comments: [] }); await run(f);
  assert.equal(f.calls.filter(c => c.kind === 'comment').length, 0);
  assert.equal(f.calls.filter(c => c.kind === 'merge').length, 0);
  const pending = { user: { login: 'github-actions[bot]', type: 'Bot' }, body: `@codex review\n<!-- codex-review-request:${head} -->` };
  const second = fixture({ comments: [pending] }); await run(second);
  assert.equal(second.calls.filter(c => c.kind === 'comment').length, 0);
  assert.equal(second.calls.filter(c => c.kind === 'merge').length, 0);
});
for (const [name, options] of Object.entries({
  'Relates to keeps parent open': { noIssues: true, bodyHTML: '<p>Relates to <a href="/siralarius/familiyapp/issues/25">#25</a>. Keep it open.</p>' },
  'PR without issue references': { noIssues: true },
  'code example referencing a manual issue': { noIssues: true, bodyHTML: '<code>GH-25</code>', mentionedManual: true },
  'mentioning a manual PR is not an issue link': { noIssues: true, bodyHTML: '<a href="/siralarius/familiyapp/issues/32">#32</a>', mentionedPR: true, mentionedManual: true }
})) test(`can merge: ${name}`, async () => {
  const f = fixture(options); await run(f);
  assert.equal(f.calls.filter(c => c.kind === 'merge').length, 1);
});
test('dispatch attempts all workflows when the first one fails', async () => {
  const attempted = []; const warnings = [];
  const github = { rest: { actions: { createWorkflowDispatch: async p => { attempted.push(p.workflow_id); if (p.workflow_id === 'ci.yml') throw Error('temporary failure'); } } } };
  const failed = await dispatchValidation({ github, owner: 'siralarius', repo: 'familiyapp', core: { warning: m => warnings.push(m) } });
  assert.equal(attempted.length, 3); assert.deepEqual(failed, ['.github/workflows/ci.yml']); assert.equal(warnings.length, 1);
});
test('closed automatically merged PR recovers missing validation dispatches', async () => {
  const f = fixture(); const comments = [{ user: { login: 'github-actions[bot]', type: 'Bot' }, body: `<!-- codex-merge-intent:${head} -->` }];
  const closed = { number: 42, head: { sha: head }, merge_commit_sha: 'merged-sha', merged_at: new Date().toISOString() };
  f.github.rest.pulls.list = async () => ({ data: [closed] });
  f.github.rest.issues.listComments = async () => ({ data: comments });
  f.github.rest.actions.listWorkflowRunsForRepo = async () => ({ data: [] });
  await recoverValidation({ github: f.github, owner: 'siralarius', repo: 'familiyapp', core: f.core });
  assert.equal(f.calls.filter(c => c.kind === 'dispatch').length, 3);
});
test('post-merge recovery ignores forged receipts and manually merged PRs', async () => {
  const f = fixture();
  f.github.rest.pulls.list = async () => ({ data: [{ number: 42, head: { sha: head }, merged_at: new Date().toISOString() }] });
  f.github.rest.issues.listComments = async () => ({ data: [{ user: { login: 'siralarius', type: 'User' }, body: `<!-- codex-merge-intent:${head} -->` }] });
  await recoverValidation({ github: f.github, owner: 'siralarius', repo: 'familiyapp', core: f.core });
  assert.equal(f.calls.filter(c => c.kind === 'dispatch').length, 0);
});
test('API error fails closed', async () => {
  const f = fixture(); f.github.graphql = async () => { throw Error('API unavailable'); }; await run(f);
  assert.equal(f.calls.filter(c => c.kind === 'merge').length, 0);
  assert.equal(f.calls.filter(c => c.kind === 'warning').length, 1);
});
