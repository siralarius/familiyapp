# Development workflow

Family App is developed in small pull requests under the Iteration 1 roadmap.

## Hosts

- `FamilyApp.Shared` contains host-agnostic Razor UI.
- `FamilyApp.Web` is a standalone Blazor WebAssembly host for laptop development and Netlify PR previews.
- `FamilyApp.Maui` is the native iPhone host and will reference the same shared UI.
- Native persistence, secure storage and peer networking remain behind platform abstractions and are not simulated as production capabilities in the preview.

## Validation

Every pull request must build and run automated tests in GitHub Actions. Netlify publishes `FamilyApp.Web` as static WebAssembly files. Native-only capabilities are validated on iPhones when their milestone is reached.
