# Codex review and automatic merging

## Reviews

Native Codex reviews and the developer's own `@codex review` comments can start reviews.
GitHub Actions-authored mentions did not start reviews in this repository, so the
merge coordinator no longer posts them.

A recurring check attached to the Codex chat uses the connected GitHub account to
request missing current-commit reviews. It runs approximately every five minutes
when Codex can run the local automation. It skips commits with an existing running
or completed review and records one request per full head SHA. Keep this automation
active while relying on this fallback. Native review settings with an every-push
trigger can eventually replace this bridge after that path is verified.

## Reserve merging for yourself

Add **manual-merge** to the PR or an issue referenced by it before making the PR
ready. Both structured closing links and local issue references in the PR body
(including `Relates to #123` and same-repository issue URLs) are checked. Related
parent issues stay open. You do not need to add a closing keyword to permit merging.
PRs without issue references are eligible, subject to the same review and CI gates.

Labels are checked again immediately before merging. For an urgent stop, make the
PR a draft or disable the merge workflow; GitHub has no atomic label-and-merge API.
Removing the label makes the PR eligible again.

## Merge requirements

- Ready, same-repository PR authored by siralarius and targeting main.
- No manual-merge label on the PR or any closing or referenced local issue.
- Explicit clean result from the authenticated Codex connector bot with a reviewed
  commit that resolves to the current full head SHA. Friendly wording may vary;
  the clean verdict and commit identity cannot. Completed alone or a thumbs-up
  without a commit-bound clean result never authorizes merging.
- If a summary exists, it must show the current commit's reviews completed.
- No later bot feedback, unresolved review threads, outstanding changes-requested
  reviews, or findings submitted by Codex against the current commit.
- Both .NET CI workflows and Automation tests pass for this PR's current head.
  Netlify deploy-preview succeeds; other reported statuses and checks must not
  be pending or failing.
- The branch includes current main, and GitHub reports it cleanly mergeable.

The coordinator reports why a PR is waiting in its Actions log. It merges using
the full reviewed SHA and respects GitHub merge restrictions and branch protections.
It does not fix feature defects or update branches automatically. The developer
must push fixes or integrate main, then get CI and a review for the resulting commit.

## Post-merge validation

GITHUB_TOKEN-generated pushes do not trigger push workflows, so the coordinator
dispatches all three CI workflows independently after merging. One failed dispatch
does not stop attempts for the others.

A trusted merge-intent receipt is written before merging. On subsequent runs, the
coordinator examines recently merged PRs with these receipts and retries missing
dispatches for seven days. Once all three workflows succeed on main, it records a
validation receipt. If main has advanced, validation runs on the newest main.
Started but failing CI is left visible for diagnosis, rather than rerun indefinitely.
Manually merged PRs have normal push-triggered CI and are excluded from receipt recovery.

## Limits

Codex's observed clean result says it found no major issues; this is not proof that
all defect severities are absent. Unknown result formats, stale commits, API errors,
and incomplete linked-issue data block merging.

Use manual-merge for native-device acceptance or other work needing personal
verification. Automated reviews and browser/CI checks do not prove two-iPhone behavior.
Development and review agents do not merge directly; the repository coordinator
is responsible for eligible automatic merges. Existing agent definitions are unchanged.

## Verify the complete flow

For a small documentation-only PR, first apply a temporary manual-merge label.
Wait for the current commit's CI, preview, and explicit Codex result, and confirm
that the coordinator leaves the labelled PR open. Remove that temporary label
and confirm that the coordinator merges the same reviewed commit and dispatches
post-merge validation. This verifies both the hold and the automatic merge path.
