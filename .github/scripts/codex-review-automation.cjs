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
  return comments.filter(isCodex).filter(comment => {
    const match = /^Codex Review: Didn't find any major issues\. Keep them coming!\s+\*\*Reviewed commit:\*\* `([a-f0-9]{10,40})`\s*(?:<details>[\s\S]*<\/details>)?\s*$/.exec(comment.body);
    return match && head.startsWith(match[1]);
  }).sort((a, b) => b.id - a.id)[0];
}
function successfulRuns(runs, number, head) {
  const latest = new Map();
  for (const run of runs.filter(run => run.head_sha === head && run.event === 'pull_request' && run.pull_requests?.some(pr => pr.number === number))) {
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

async function linkedState(github, owner, repo, number) {
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
  // Incomplete or missing links fail closed; ordinary issue mentions are not closing links.
  return { issues: issues.nodes, blocked: issues.pageInfo.hasNextPage || !issues.nodes.length ||
    issues.nodes.some(issue => issue.repository.nameWithOwner !== `${owner}/${repo}` || issue.labels.pageInfo.hasNextPage || isManual(issue.labels.nodes)) ||
    pr.reviewThreads.pageInfo.hasNextPage || pr.reviewThreads.nodes.some(thread => !thread.isResolved) };
}

async function run({ github, context, core }) {
  const { owner, repo } = context.repo;
  const params = { owner, repo };
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
    const pullParams = { ...params, pull_number: number };
    const { data: pr } = await github.rest.pulls.get(pullParams);
    if (pr.state !== 'open' || pr.draft || pr.base.ref !== 'main' || pr.head.repo?.full_name !== `${owner}/${repo}`) return;
    // Only the repository owner's delivery PRs are eligible. Public/fork PRs stay manual.
    if (pr.user.login !== owner) return;
    const comments = await github.paginate(github.rest.issues.listComments, { ...params, issue_number: number, per_page: 100 });
    const clean = cleanReview(comments, pr.head.sha);
    const marker = `<!-- codex-review-request:${pr.head.sha} -->`;
    if (!clean && !comments.some(comment => comment.user?.login === 'github-actions[bot]' && comment.user?.type === 'Bot' && comment.body.includes(marker))) {
      await github.rest.issues.createComment({ ...params, issue_number: number, body: `@codex review\n\nReview commit ${pr.head.sha}.\n${marker}` });
      core.info(`PR #${number}: requested review for ${pr.head.sha}`);
    }
    if (!clean || isManual(pr.labels)) return;
    const linked = await linkedState(github, owner, repo, number);
    if (linked.blocked) return;
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
    if (!successfulRuns(runs, number, pr.head.sha)) return;
    const checks = await github.paginate(github.rest.checks.listForRef, { ...params, ref: pr.head.sha, filter: 'latest', per_page: 100 });
    const statuses = (await github.rest.repos.getCombinedStatusForRef({ ...params, ref: pr.head.sha, per_page: 100 })).data;
    // Combined statuses are capped here: do not silently ignore later pages.
    if (statuses.total_count > 100 || !successfulChecks(checks, statuses.statuses)) return;
    const compare = (await github.rest.repos.compareCommits({ ...params, base: 'main', head: pr.head.sha })).data;
    if (compare.behind_by !== 0) return;
    const fresh = (await github.rest.pulls.get(pullParams)).data;
    if (fresh.head.sha !== pr.head.sha || fresh.base.sha !== pr.base.sha || fresh.state !== 'open' || fresh.draft || isManual(fresh.labels) || fresh.mergeable !== true || fresh.mergeable_state !== 'clean') return;
    // Re-read linked issue labels immediately before merging, including changes made while checks ran.
    const freshLinks = await linkedState(github, owner, repo, number);
    if (freshLinks.blocked || JSON.stringify(freshLinks.issues.map(i => i.number).sort()) !== JSON.stringify(linked.issues.map(i => i.number).sort())) return;
    const result = await github.rest.pulls.merge({ ...pullParams, sha: pr.head.sha, merge_method: 'merge' });
    if (result.data.merged) {
      core.info(`PR #${number}: merged reviewed commit ${pr.head.sha}`);
      // GITHUB_TOKEN-generated pushes do not start push workflows, so explicitly run both CI workflows.
      for (const workflow_id of ['ci.yml', 'dotnet-ci.yml', 'automation-tests.yml']) {
        await github.rest.actions.createWorkflowDispatch({ ...params, workflow_id, ref: 'main' });
      }
      return;
    }
    core.info(`PR #${number}: GitHub did not permit merge`);
  }
}

module.exports = { run, cleanReview, isManual, successfulRuns, successfulChecks, REQUIRED_WORKFLOWS, PREVIEW_CONTEXT };
