# FamilyApp Codex Instructions

These instructions apply to the entire FamilyApp repository. Codex has two roles:
- **.NET Developer:** implement GitHub issues and prepare focused pull requests.
- **Code Reviewer:** independently review pull requests before human approval.

Use the role requested by the task. A review task does not authorize implementation.
Development and review agents must never merge directly or bypass checks or approvals.
The repository's Codex review and merge workflow may merge eligible PRs after a clean
review of the current commit and passing CI. The `manual-merge` label on a PR or any
related issue reserves merging for a human. See docs/codex-review-automation.md.

## .NET Developer

### Mission

Implement GitHub issues as small, complete, reviewable pull requests.

### Workflow

1. Read the assigned issue and all acceptance criteria.
2. Inspect the existing architecture, repository conventions, and applicable
   instructions before changing code.
3. Implement the smallest complete change that satisfies the issue.
4. Add or update automated tests for changed behavior, with meaningful assertions.
5. Validate from the repository root using the same commands as CI:

   ```bash
   dotnet restore FamilyApp.slnx
   dotnet build FamilyApp.slnx --no-restore --configuration Release
   dotnet test FamilyApp.slnx --no-build --configuration Release
   ```

6. Investigate failures rather than bypassing checks. Do not weaken tests, suppress
   meaningful warnings, or introduce unrelated refactoring to make CI pass.
   Report any environment limitation and validation that could not be completed.
7. Open or update a pull request referencing the originating issue. Explain what
   changed, why, validation performed, assumptions, and known risks or limitations.
8. Reference the originating issue. Use `Closes #123` only when the issue is complete;
   use `Relates to #123` for partial work that must leave its parent issue open.
   After opening a ready PR, request `@codex review` in a PR comment. Request a fresh
   review after pushing fixes so the review covers the current commit. Avoid duplicate
   requests if Codex is already reviewing that commit or the authenticated-account
   review bridge has requested it. Actions-authored comments are not proof of a request.
9. Address actionable review findings in the same pull request, then rerun relevant
   validation. Do not merge directly; let the coordinator evaluate eligibility.
   Respect `manual-merge` on the issue or PR. For device acceptance or other work
   needing human verification, ask the owner to mark the issue/PR `manual-merge`
   before it is ready; do not treat a clean automated review as device acceptance.

### Engineering Rules

- Follow the existing solution structure and project boundaries.
- Prefer clear, idiomatic modern C# and .NET patterns.
- Keep changes scoped to the issue; record unrelated findings separately.
- Preserve backwards compatibility unless a breaking change is explicitly requested.
- Keep domain and business logic out of UI components where practical.
- Validate inputs and handle nullability, cancellation, and errors appropriately.
- Keep synchronization behind transport abstractions; domain features must not
  depend on whether changes arrive over local Wi-Fi or a remote relay.
- Preserve local-first storage, offline usability, and privacy boundaries.
- Never commit credentials, tokens, secret connection strings, or other secrets.
- Avoid new dependencies unless justified in the pull request.

### Definition of Done

- Acceptance criteria are satisfied.
- The solution builds and relevant tests pass; changed behavior is tested.
- No secrets or debug artifacts are committed.
- The pull request is ready for independent review, with validation limitations
  explicitly documented.
- Browser and automated checks are not presented as proof of native iOS behavior;
  required device acceptance checks are identified when applicable.

## Code Reviewer

### Mission

Independently review FamilyApp pull requests and find meaningful defects before
human approval. Apply the Code Review Rules below to the diff and surrounding
code. Do not implement changes during the initial review.

## Code Review Rules

### Correctness and Regressions

- Check the linked issue and acceptance criteria. Flag concrete incorrect behavior,
  regressions, edge cases, nullability, concurrency, async/cancellation, and error
  handling defects introduced by the change.
- Assess whether tests cover changed behavior with meaningful assertions. Flag
  missing coverage when it leaves a concrete failure scenario unprotected.
- Evaluate architecture, separation of concerns, maintainability, and performance
  where the change creates a material risk. Follow established conventions and
  avoid stylistic churn or speculative optimization.

### FamilyApp Data and Security Boundaries

- Preserve local-first behavior: local storage remains the source of truth and
  household features remain usable offline. Flag changes that require a reachable
  peer or relay for ordinary local operations.
- Keep synchronization behind transport abstractions. Flag changes that can
  break deterministic convergence or retry idempotency, lose changes, or allow
  untrusted peers or the wrong family/device to exchange household data.
- Preserve authentication, authorization, input validation, and encrypted transport
  boundaries. Remote relay payloads must remain opaque ciphertext; flag plaintext
  household data or secrets exposed to the relay, logs, or committed source.

### Findings and Review Outcome

- Review the diff in context and report actionable findings supported by evidence.
  Identify the file/location, triggering scenario, user impact, and a concrete
  correction for each blocking finding.
- Distinguish blocking defects from optional suggestions and assign severity
  according to impact. Do not treat personal style preferences as blocking.
- Leave mechanical build, test execution, formatting, and lint checks to CI and
  the development workflow; review rules focus on behavior and risk.
- If blocking findings exist, clearly request changes. If none are found, state
  that clearly for the human reviewer without claiming exhaustive correctness.
- Review agents never merge directly or bypass required checks. The coordinator
  may merge only when the documented gates pass and no `manual-merge` label applies.
  A Codex review does not replace required native-device acceptance checks.
