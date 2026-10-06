const CODEX_ID = 199175422;
const CODEX_LOGIN = 'chatgpt-codex-connector[bot]';
const MANUAL_LABEL = 'manual-merge';
const PREVIEW_CONTEXT = 'netlify/profound-cajeta-7439db/deploy-preview';
const REQUIRED_WORKFLOWS = ['.github/workflows/ci.yml', '.github/workflows/dotnet-ci.yml', '.github/workflows/automation-tests.yml'];

function isCodex(item) {
  return item.user?.id === CODEX_ID && item.user?.login === CODEX_LOGIN && item.user?.type === 'Bot';
}
function isManual(labels) {
  return labels.some(label => label.name.toLowerCase() === MANUAL_LABEL);
}
function cleanReview(comments, head) {
  // Accept only the observed connector result, with an explicit reviewed commit.
  // No thumbs-up, empty review list, or mere "Completed" summary authorizes merge.
  const summaries = comments.filter(isCodex).filter(comment => comment.body.startsWith('<!-- codex-pull-request-review-summary -->')).sort((a, b) => b.id - a.id);
  if (summaries.length) {
    const rows = summaries[0].body.split('\n').filter(line => /^\|/.test(line) && /\*\*(?:Code Review|Security Review)\*\*/.test(line));
    const code = rows.find(line => /\*\*Code Review\*\*/.test(line));
    const summaryRef = code && /`([a-f0-9]{7,40})`/.exec(code)?.[1];
    if (!summaryRef || !head.startsWith(summaryRef) || rows.some(line => !/\*\*Completed\*\*/.test(line))) return;
  }
  return comments.filter(isCodex).filter(comment => {
    // The connector varies the friendly sentence; it is not the review verdict.
    const match = /^Codex Review: Didn't find any major issues\.([^\r\n]*)\s+\*\*Reviewed commit:\*\* `([a-f0-9]{10,40})`\s*(?:<details>[\s\S]*<\/details>)?\s*$/.exec(comment.body);
    return match && !/\b(?:P[0-3]|finding|defect|bug|however|but)\b/i.test(match[1]) && head.startsWith(match[2]);
  }).sort((a, b) => b.id - a.id)[0];
}
function successfulRuns(runs, number, head) {
  const latest = new Map();
  for (const run of runs.filter(run => run.head_sha === head && run.event === 'pull_request' && run.pull_requests?.some(pr => pr.number === number))) {
    if (typeof run.path !== 'string') continue;
    const key = run.path.split('@')[0];
    if (!latest.has(key) || latest.get(key).id < run.id || (latest.get(key).id === run.id && latest.get(key).run_attempt < run.run_attempt)) latest.set(key, run);
  }
  return REQUIRED_WORKFLOWS.every(path => latest.has(path)) && [...latest.values()].every(run => run.status === 'completed' && run.conclusion === 'success');
}
function successfulChecks(checks, statuses) {
  return checks.every(check => check.status === 'completed' && ['success', 'neutral', 'skipped'].includes(check.conclusion)) &&
    statuses.some(status => status.context === PREVIEW_CONTEXT && status.state === 'success') &&
    statuses.every(status => status.state === 'success');
}

function referencedIssueNumbers(body, owner, repo) {
  const numbers = new Set();
  // Local mentions, including "Relates to", do not close a parent issue.
  for (const match of (body || '').matchAll(/(?<![\w/])#([1-9]\d*)\b/g)) numbers.add(Number(match[1]));
  for (const match of (body || '').matchAll(/https:\/\/github\.com\/([^/\s]+)\/([^/\s]+)\/issues\/([1-9]\d*)\b/g)) {
    if (match[1].toLowerCase() === owner.toLowerCase() && match[2].toLowerCase() === repo.toLowerCase()) numbers.add(Number(match[3]));
  }
  return [...numbers].sort((a, b) => a - b);
}
async function linkedState(github, owner, repo, number, body) {
  const data = await github.graphql(`query($owner:String!, $repo:String!, $number:Int!) {
    repository(owner:$owner, name:$repo) { pullRequest(number:$number) {
      closingIssuesReferences(first:100) { pageInfo { hasNextPage } nodes {
        number repository { nameWithOwner } labels(first:100) { pageInfo { hasNextPage } nodes { name } }
      } }
      reviewThreads(first:100) { pageInfo { hasNextPage } nodes { isResolved } }
    } }
  }`, { owner, repo, number });
  const pr = data.repository.pullRequest;
  const issues = pr.closingIssuesReferences;
  // Preserve structured closing links, and check labels on local body references too.
  const mentioned = [];
  for (const issue_number of referencedIssueNumbers(body, owner, repo)) {
    const issue = (await github.rest.issues.get({ owner, repo, issue_number })).data;
    // GitHub shares issue numbering with PRs; mentioning a previous PR is not an issue link.
    if (issue.pull_request) continue;
    const labels = await github.paginate(github.rest.issues.listLabelsOnIssue, { owner, repo, issue_number, per_page: 100 });
    mentioned.push({ number: issue_number, labels });
  }
  const issueNumbers = [...new Set([...issues.nodes.map(issue => issue.number), ...mentioned.map(issue => issue.number)])].sort((a, b) => a - b);
  return { issueNumbers, blocked: issues.pageInfo.hasNextPage ||
    issues.nodes.some(issue => issue.repository.nameWithOwner !== `${owner}/${repo}` || issue.labels.pageInfo.hasNextPage || isManual(issue.labels.nodes)) ||
    mentioned.some(issue => isManual(issue.labels)) ||
    pr.reviewThreads.pageInfo.hasNextPage || pr.reviewThreads.nodes.some(thread => !thread.isResolved) };
}

async function dispatchValidation({ github, owner, repo, core }) {
  // Attempt every workflow independently; a failure must not skip subsequent workflows.
  const failed = [];
  for (const path of REQUIRED_WORKFLOWS) {
    try {
      await github.rest.actions.createWorkflowDispatch({ owner, repo, workflow_id: path.split('/').pop(), ref: 'main' });
    } catch (error) {
      failed.push(path); core.warning(`${path}: dispatch failed; recovery will retry: ${error.message}`);
    }
  }
  return failed;
}

async function recoverValidation({ github, owner, repo, core }) {
  // Trusted intent receipts survive closing the PR, making retries durable across runs.
  const closed = await github.paginate(github.rest.pulls.list, { owner, repo, state: 'closed', base: 'main', sort: 'updated', direction: 'desc', per_page: 100 });
  for (const pr of closed.filter(pr => pr.merged_at && Date.parse(pr.merged_at) > Date.now() - 7 * 86400000)) {
    const comments = await github.paginate(github.rest.issues.listComments, { owner, repo, issue_number: pr.number, per_page: 100 });
    const trusted = comments.filter(comment => comment.user?.login === 'github-actions[bot]' && comment.user?.type === 'Bot');
    if (!trusted.some(comment => comment.body === `<!-- codex-merge-intent:${pr.head.sha} -->`) ||
      trusted.some(comment => comment.body === `<!-- codex-post-merge-validated:${pr.merge_commit_sha} -->`)) continue;
    const main = (await github.rest.repos.getCommit({ owner, repo, ref: 'main' })).data.sha;
    // Dispatches run against main; after subsequent merges, validate the newest main.
    const runs = await github.paginate(github.rest.actions.listWorkflowRunsForRepo, { owner, repo, head_sha: main, per_page: 100 });
    const latest = new Map();
    for (const run of runs.filter(run => run.head_sha === main && ['push', 'workflow_dispatch'].includes(run.event))) {
      if (typeof run.path !== 'string') continue;
      const path = run.path.split('@')[0];
      if (!latest.has(path) || run.id > latest.get(path).id || (run.id === latest.get(path).id && run.run_attempt > latest.get(path).run_attempt)) latest.set(path, run);
    }
    if (REQUIRED_WORKFLOWS.every(path => latest.get(path)?.status === 'completed' && latest.get(path)?.conclusion === 'success')) {
      await github.rest.issues.createComment({ owner, repo, issue_number: pr.number, body: `<!-- codex-post-merge-validated:${pr.merge_commit_sha} -->` });
      continue;
    }
    for (const path of REQUIRED_WORKFLOWS) {
      // A started workflow has fulfilled the dispatch obligation. CI failures are visible failures,
      // not permission to rerun failing code indefinitely. Only missing runs need recovery.
      if (latest.has(path)) continue;
      try {
        await github.rest.actions.createWorkflowDispatch({ owner, repo, workflow_id: path.split('/').pop(), ref: 'main' });
      } catch (error) { core.warning(`PR #${pr.number}: recovery dispatch ${path}: ${error.message}`); }
    }
  }
}

async function run({ github, context, core }) {
  const { owner, repo } = context.repo;
  const params = { owner, repo };
  try { await recoverValidation({ github, owner, repo, core }); }
  catch (error) { core.warning(`Post-merge recovery: ${error.message}`); }
  const pullRequests = await github.paginate(github.rest.pulls.list, { ...params, state: 'open', base: 'main', per_page: 100 });
  for (const initial of pullRequests) {
    try {
      await processPull(initial.number);
    } catch (error) {
      // Never merge on API failures or partial state; let the next event retry.
      core.warning(`PR #${initial.number}: ${error.message}`);
    }
  }

  async function processPull(number) {
    const wait = reason => core.info(`PR #${number}: waiting — ${reason}`);
    const pullParams = { ...params, pull_number: number };
    const { data: pr } = await github.rest.pulls.get(pullParams);
    if (pr.state !== 'open' || pr.draft || pr.base.ref !== 'main' || pr.head.repo?.full_name !== `${owner}/${repo}`) return;
    // Only the repository owner's delivery PRs are eligible. Public/fork PRs stay manual.
    if (pr.user.login !== owner) return;
    const comments = await github.paginate(github.rest.issues.listComments, { ...params, issue_number: number, per_page: 100 });
    const clean = cleanReview(comments, pr.head.sha);
    // Native Codex reviews or the authenticated-account bridge request the review.
    // Actions-authored mentions were ignored, so do not post misleading requests here.
    if (!clean) return wait('no explicit clean Codex result for the current commit');
    if (isManual(pr.labels)) return wait('manual-merge label on the PR');
    const linked = await linkedState(github, owner, repo, number, pr.body);
    if (linked.blocked) return wait('manual issue label, unresolved findings, or incomplete issue data');
    // An abbreviated bot SHA must resolve unambiguously to the current full SHA.
    const reviewedRef = /\*\*Reviewed commit:\*\* `([a-f0-9]+)`/.exec(clean.body)[1];
    if ((await github.rest.repos.getCommit({ ...params, ref: reviewedRef })).data.sha !== pr.head.sha) return;
    // Any later connector result other than its status summary invalidates this clean result.
    if (comments.some(comment => isCodex(comment) && comment.id > clean.id && !comment.body.startsWith('<!-- codex-pull-request-review-summary -->'))) return;
    const reviews = await github.paginate(github.rest.pulls.listReviews, { ...pullParams, per_page: 100 });
    const latestReviews = new Map();
    for (const review of reviews) {
      if (['APPROVED', 'CHANGES_REQUESTED', 'DISMISSED'].includes(review.state)) latestReviews.set(review.user.id, review);
      if (isCodex(review) && review.commit_id === pr.head.sha && !['APPROVED', 'DISMISSED'].includes(review.state)) return;
    }
    if ([...latestReviews.values()].some(review => review.state === 'CHANGES_REQUESTED')) return;
    const runs = await github.paginate(github.rest.actions.listWorkflowRunsForRepo, { ...params, head_sha: pr.head.sha, event: 'pull_request', per_page: 100 });
    if (!successfulRuns(runs, number, pr.head.sha)) return wait('current-commit CI is missing, running, or failed');
    const checks = await github.paginate(github.rest.checks.listForRef, { ...params, ref: pr.head.sha, filter: 'latest', per_page: 100 });
    const statuses = (await github.rest.repos.getCombinedStatusForRef({ ...params, ref: pr.head.sha, per_page: 100 })).data;
    // Combined statuses are capped here: do not silently ignore later pages.
    if (statuses.total_count > 100 || !successfulChecks(checks, statuses.statuses)) return wait('preview or another reported check has not passed');
    const compare = (await github.rest.repos.compareCommits({ ...params, base: 'main', head: pr.head.sha })).data;
    if (compare.behind_by !== 0) return wait('branch must be updated from main and reviewed again');
    const fresh = (await github.rest.pulls.get(pullParams)).data;
    if (fresh.head.sha !== pr.head.sha || fresh.base.sha !== pr.base.sha || fresh.state !== 'open' || fresh.draft || isManual(fresh.labels) || fresh.mergeable !== true || fresh.mergeable_state !== 'clean') return;
    // Re-read linked issue labels immediately before merging, including changes made while checks ran.
    const freshLinks = await linkedState(github, owner, repo, number, fresh.body);
    if (freshLinks.blocked || JSON.stringify(freshLinks.issueNumbers) !== JSON.stringify(linked.issueNumbers)) return;
    // Persist an intent before merging, so failed dispatches remain discoverable on a closed PR.
    const intent = `<!-- codex-merge-intent:${pr.head.sha} -->`;
    if (!comments.some(comment => comment.user?.login === 'github-actions[bot]' && comment.user?.type === 'Bot' && comment.body === intent)) {
      await github.rest.issues.createComment({ ...params, issue_number: number, body: intent });
    }
    // Writing a receipt is another API round trip. Recheck holds and the exact head afterward.
    const last = (await github.rest.pulls.get(pullParams)).data;
    if (last.head.sha !== pr.head.sha || last.base.sha !== fresh.base.sha || last.state !== 'open' || last.draft || isManual(last.labels)) return;
    const lastLinks = await linkedState(github, owner, repo, number, last.body);
    if (lastLinks.blocked || JSON.stringify(lastLinks.issueNumbers) !== JSON.stringify(linked.issueNumbers)) return;
    const result = await github.rest.pulls.merge({ ...pullParams, sha: pr.head.sha, merge_method: 'merge' });
    if (result.data.merged) {
      core.info(`PR #${number}: merged reviewed commit ${pr.head.sha}`);
      // GITHUB_TOKEN-generated pushes do not start push workflows, so explicitly run both CI workflows.
      await dispatchValidation({ github, owner, repo, core });
      return;
    }
    core.info(`PR #${number}: GitHub did not permit merge`);
  }
}

module.exports = { run, cleanReview, isManual, referencedIssueNumbers, dispatchValidation, recoverValidation, successfulRuns, successfulChecks, REQUIRED_WORKFLOWS, PREVIEW_CONTEXT };
