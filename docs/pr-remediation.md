# PR auto-remediation

An opt-in coding workflow, separate from review and merge. It never merges a PR or
writes to main. The existing coordinator treats `agent-remediate` as a manual merge
hold on PRs and referenced issues. Leave it in place until a human decides to merge.
Existing PRs without this label retain their existing merge policy.

## Setup after human review

1. Manually merge the implementation PR. The trusted controller/publisher must be
   on main before execution. Keep that PR draft or labelled `manual-merge` until approved.
2. Create label `agent-remediate`. Apply it only to branches authorized for agent edits.
   Eligibility requires an open, ready, owner-authored, same-repository PR targeting
   main. Forks, drafts, non-owner PRs, closed PRs and main as a head branch are excluded.
3. Configure the secrets and variables below. Set `PR_REMEDIATION_ENABLED=true` and
   run **PR remediation** manually or wait for the ten-minute scheduled scan.
4. Keep native Codex every-push review or the existing authenticated-account review
   bridge active. Actions-authored mentions were ignored in this repository. Missing
   independent current-head review blocks completion.

| Name | Type | Access/use |
| --- | --- | --- |
| `OPENAI_API_KEY` | Actions secret | API project key with access to the configured/default Codex model; separate API billing and spending limit. The installed review app/ChatGPT subscription alone does not supply this. Only the isolated coding job uses it via the action's protected proxy. |
| `PR_REMEDIATION_TOKEN` | Actions secret | Fine-grained PAT limited to this repository, Contents: read/write, Metadata: read. Prefer a dedicated bot identity. Do not grant Administration or Workflows write. Only the trusted publisher receives it. Monitor expiry. |
| `NETLIFY_AUTH_TOKEN` | Actions secret | Account authorized to read this site's deploy metadata and logs. Use the lowest Netlify role permitting those reads; tokens can have broader account access. Only trusted preparation receives it. |
| `NETLIFY_SITE_ID` | Actions variable | Actual site UUID for `profound-cajeta-7439db`, from Netlify project settings. Deploy site and full commit must match before fetching logs. |
| `PR_REMEDIATION_ENABLED` | Actions variable | Exact value `true` enables scanning, dispatch and verification. Unset/false disables them. |

Preparation's job-scoped `GITHUB_TOKEN` reads Actions/checks/statuses and writes
comments/receipts; Pull requests: write allows eligible thread resolution. The agent
job has Contents: read only, no GitHub writer/PAT/Netlify token, and checkouts do not
persist credentials. A fresh publisher runner executes only trusted main code.
Its PAT-authored branch update starts normal PR CI and Netlify continuous deployment;
`GITHUB_TOKEN`-authored pushes normally suppress subsequent workflow events.

Protect main with rulesets requiring PRs/checks and give the publishing identity no
bypass. Contents permission is repository-wide, not branch-scoped; the trusted
publisher validation and main ruleset enforce the boundary. No credentials/settings
have been provisioned by this PR. Remove the PR label to revoke eligibility; cancel
an active run for an immediate stop. Publication rechecks eligibility and head.

## Execution and gates

- CI completion, external check completion and commit statuses wake the default-branch
  controller. Review findings are polled by the schedule: review events are deliberately
  excluded because their workflow context can be PR code.
- Current-head failed runs of CI, .NET CI, Automation tests or iOS; failed Netlify
  preview; current P0–P2 inline findings; and current changes-requested reviews are
  actionable. Pending checks, optional suggestions and outdated findings alone are not.
- Preparation reads actual failed-job logs and authenticated Netlify historical deploy
  logs. Missing, mismatched, oversized or inaccessible evidence blocks dispatch and
  posts a blocked comment. Passing .NET CI does not explain a failed Netlify build.
- The coding job checks out an immutable SHA on an ephemeral GitHub-hosted Linux
  runner. Codex uses `drop-sudo` and `:workspace` permissions. It edits and runs relevant
  tests/build/publish; its final message is not a success signal. Restricted networking
  can limit dependency restores; authoritative subsequent CI still must pass.
- The publisher treats candidate JSON as untrusted, permits only application/test/build
  files (30 files/500 KB), rejects policy edits, traversal, symlinks, duplicates and
  obvious secrets, and creates a commit parented by the expected head. It rechecks
  authorization/head before a non-forced fast-forward: concurrent edits are not overwritten.
- A global queue serializes attempts. Two attempts maximum per PR across all commits,
  one per SHA. Failed/cancelled/no-op/forbidden patches consume their reservation.
  Trusted bot receipts persist across reruns; toggling labels does not reset the budget.
- A new commit starts normal CI/preview. Verification requires the published full head
  SHA, all three required CI workflows, every other PR workflow run (including iOS),
  reported checks/statuses, exact Netlify preview context, and an explicit independent
  clean Codex review for that commit. Missing/pending results and outstanding changes
  requests block completion.
- Only originally targeted inline threads that are now outdated may be resolved after
  all gates pass. Active/unrelated unresolved threads require a reviewer. The PR stays
  open for human merge. Linked issues stay open until their full acceptance criteria,
  main and production checks are verified; green checks alone never close a broader issue.

## PR #38 / Issue #39 test case

Observed on 11 October 2026 at head `50bb98fceb3195747217921df25f1a322b945623`:

- Preview context `netlify/profound-cajeta-7439db/deploy-preview` failed at deploy
  `6acbca9a947b9f0008efa7c2`. Its authenticated deploy log was not retrieved in this task.
- CI, .NET CI and Automation tests passed. iOS failed installing its MAUI workload.
- Codex P2 inline finding `4242558373` asks for refreshing the mounted home header
  across noon, 17:00 and midnight. Copilot approval does not clear this finding.
- Issue #39 requires local weekdays/greetings, time-boundary refresh and successful
  preview. It remains open; this infrastructure PR does not fix those acceptance criteria.

Controller tests reproduce these observations and exercise routing, stale/optional
findings, missing logs, forged receipts, stale-head publication, patch restrictions
and attempt deduplication/exhaustion.

After configuration and manual merge of this infrastructure PR:

1. Keep `manual-merge` on PR #38 and Issue #39; add `agent-remediate` to PR #38.
2. Run PR remediation; confirm evidence matches the full head/site. Missing Netlify
   access must produce a blocked comment and no attempt reservation.
3. Inspect the candidate commit and CI/preview. Browser acceptance includes Sunday
   11 October 2026, 19:41 Europe/Amsterdam → `Sunday` / `Good evening.`, all weekdays,
   greeting boundaries, mounted-page midnight changes and timer disposal.
4. Request a fresh independent review. Pending/failed/stale/missing results must leave
   findings open and success unverified.
5. The iOS workload failure needs `.github/workflows/ios.yml` edits, forbidden to the
   restricted publisher. Handle separately in a human-reviewed PR; the agent must not
   modify its own CI/execution policy. Completion cannot pass while iOS is failing.
6. After all gates and browser acceptance pass, a human may merge PR #38. Verify main
   and production Netlify before closing Issue #39 per the existing issue-resolver
   policy. This workflow never closes a PR as a substitute for merging its fix.

## Limits

This PR provides implementation and offline tests. A paid agent run, authenticated
Netlify log replay, PAT push → CI/Netlify, and subsequent verification still require
configured secrets and trusted code on main. They are not claimed as verified here.
The historical-log websocket follows Netlify's official CLI but may change; empty,
error or timed-out replay fails closed. Logs/artifacts are bounded, common tokens
redacted, artifacts expire after one day. Arbitrary build secrets cannot reliably be
redacted: keep build logs free of secrets and limit Actions artifact access.

Only named workflows and the Netlify context route failures. Other reported failures
block success but need manual diagnosis. No branch rebases, main writes, policy fixes,
automatic merge, production deployment or native-device acceptance are provided. A
successful push followed by a failed receipt write leaves verification blocked;
repair the trusted receipt after inspecting the commit. Cancellation before the final
failure-report job may need manual inspection. API errors/partial pagination fail closed.

References: [Codex action](https://developers.openai.com/codex/github-action),
[pinned v1.13](https://github.com/openai/codex-action/tree/bdf19a4a223ec2549a3e2274a0cf61556bc07675),
[GitHub events](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows),
[Netlify historical logs](https://github.com/netlify/cli/blob/main/src/commands/logs/sources/deploy.ts),
[existing review bridge](codex-review-automation.md).
