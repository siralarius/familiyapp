# Agent development workflow

FamilyApp uses two distinct AI roles with a human-controlled merge.

## Flow

GitHub Issue → .NET Developer Agent → Pull Request → .NET CI → Code Reviewer Agent → Developer corrections if needed → Human approval → Merge

## .NET Developer
The developer receives an issue, implements only the requested scope, adds tests, validates the solution, and opens a pull request. Instructions live in `.github/agents/dotnet-developer.md`.

## Code Reviewer
The reviewer independently evaluates the pull request for correctness, security, architecture, tests, and maintainability. Instructions live in `.github/agents/code-reviewer.md`.

## Execution model
The intended model is cloud-first. Codex can run tasks in OpenAI-managed cloud environments, while GitHub Actions runs deterministic CI. A developer laptop is not required to remain online for cloud tasks.

GitHub also supports third-party coding agents, including OpenAI Codex, that can be assigned work and create pull requests. Availability depends on the GitHub/Copilot account configuration.

## Human gate
Agents must not merge to `main`. A human reviews the PR and decides whether to merge.

## Repository setup still required
After merging this configuration:
1. Enable/configure Codex access to this repository in the chosen cloud integration.
2. Configure repository rules/branch protection so CI must pass before merge.
3. Configure automatic Codex review if desired, or request Codex review on each PR.
4. If GitHub Agentic Workflows are later used, configure the required agent credentials/secrets separately; never store them in the repository.
