# Development workflow

Family App is developed in small pull requests under the Iteration 1 roadmap.

## Hosts

- FamilyApp.Shared contains host-agnostic Razor UI.
- FamilyApp.Web provides fast laptop/browser development.
- FamilyApp.Maui will be the native iPhone host and will be added with the MAUI workload in the bootstrap follow-up.
- Netlify deploy previews will use a static/WebAssembly-compatible preview host so they don't depend on an ASP.NET Core server runtime.

## Validation

Every pull request must build and run automated tests in GitHub Actions. Native-only capabilities are isolated behind interfaces and validated on iPhones when their milestone is reached.
