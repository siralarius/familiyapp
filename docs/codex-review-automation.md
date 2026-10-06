# Codex review and automatic merging

The developer requests `@codex review` after opening a ready PR and after pushing
fixes. The coordinator also requests a review once per head commit when no clean
result is present. It checks on review comments, CI completion, issue label changes,
manual dispatch, and approximately every five minutes (GitHub schedules can be delayed).

## Keep an issue or PR for your own merge

Add the **manual-merge** label to the issue before making its PR ready. You can also
add it directly to the PR. Any closing issue with this label blocks automatic merge,
even if the PR itself has no label. Removing the label makes the PR eligible again.
Create the label in GitHub if it does not exist. For an urgent stop, make the PR a
draft or disable the coordinator workflow; labels are checked immediately before
merging but GitHub has no atomic label-and-merge API.

## Merge requirements

- Ready, same-repository PR opened by the repository owner and targeting main.
- At least one closing issue in this repository (use `Closes #123` in the PR body).
  Ordinary `Relates to` mentions do not establish an automatic merge link.
- No manual-merge label on the PR or any closing issue.
- An explicit clean comment from the authenticated Codex connector bot, whose
  reviewed commit resolves to the full current head SHA. The observed result is
  “Didn't find any major issues”; no result, a reaction, or Completed alone is insufficient.
- No later bot feedback, unresolved review threads, or outstanding changes-requested
  reviews. Findings remain blocked until addressed and a fresh clean review is available.
- Both .NET CI workflows and Automation tests pass for this PR's current head.
  Netlify's deploy-preview status succeeds; other reported statuses and checks
  must not be pending or failing.
- The branch includes current main and GitHub reports it cleanly mergeable.

The workflow merges using the full reviewed head SHA. It respects GitHub merge
restrictions and never overrides branch protections. It uses the built-in
GITHUB_TOKEN; native GitHub auto-merge does not need to be enabled. Since that token's
pushes do not trigger push workflows, it explicitly dispatches all three CI workflows
on main after a successful merge. Netlify remains responsible for its own deployment.

## Activation and validation

Merge the setup PR manually to activate the workflow. Then use a small issue/PR to
verify that the GitHub Actions-authored `@codex review` request is accepted by your
Codex connection. This has not been verified before activation. The developer's
own request is a fallback; if bot-authored requests are ignored, the PR stays open
until Codex actually reports a clean review. No personal access token is required.

Add manual-merge before readiness for work requiring device acceptance or a personal
inspection. The merge automation does not establish native-device acceptance.
Codex's clean result covers the findings it reports; it is not proof that all defect
severities are absent. Format changes to Codex's result cause merging to stop until
the parser is deliberately updated.

This setup changes the previous human-only policy: development and review agents
still do not merge directly. Only the coordinator may merge eligible deliveries;
manual-merge issues/PRs remain under human control. The existing agent definition
files are unchanged.
