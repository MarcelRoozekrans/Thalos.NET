# AGENTS.md: Thalos.NET

Thalos.NET is a Hermes-style, ZeroAlloc-native agent framework for .NET, built on Microsoft Agent Framework. It ships
19 NuGet packages from one solution (`Thalos.NET.slnx`): abstractions and runtime, MCP, Anthropic and Ollama providers,
AI.Sentinel scanning, memory, skills, channels, git, workflow and run sandboxes. The README lists every package.

## Layout

- `src/` ships (19 packages). `tests/` and `samples/` do not (`IsPackable=false`).
- Test helpers that are not tests: `tests/Thalos.NET.Tests.McpServer` (stdio MCP server the Mcp, Sandbox and
  Sandbox.Host tests launch), `tests/Thalos.NET.Tests.Workflow.Orm.Host`, and the two Native AOT publish targets
  `tests/Thalos.NET.Tests.Native.Probe` and `tests/Thalos.NET.Tests.Sandbox.Host.Native`.
- `Thalos.NET.Testing` is a shipping package that references xunit and AwesomeAssertions on purpose. The
  architecture tests exclude it from the production layering rules.
- Compiler-generated output goes to `obj/generated` (`EmitCompilerGeneratedFiles`).
- A new package must be added to the `expected` list and both count checks (`19`) in the `pack-validate` job of
  `.github/workflows/ci.yml`, and to the `case` that lists net10.0-only packages if it has no `net8.0` build.

## Build and test

Prerequisites: the SDK pinned by the only `global.json` (`11.0.100`, `rollForward: latestFeature`) and the .NET 10
and .NET 11 runtimes, including ASP.NET Core, because every test project targets `net10.0` and `net11.0`
(`tests/Directory.Build.props`); some test projects, the helpers and both samples target `net11.0` only. Libraries also build for
`net8.0`, but nothing runs on it, so no .NET 8 runtime is needed. The Native AOT smoke test needs `clang` and
`zlib1g-dev` on Linux (installed by CI). The `pack-validate` commands need `jq`. Docker with Linux containers is needed for the 22 tests tagged
`Category=Docker`.

CI job `build-test` (ubuntu-latest and windows-latest):

```bash
dotnet restore Thalos.NET.slnx
dotnet build Thalos.NET.slnx --no-restore -c Release
dotnet build tests/Thalos.NET.Tests.McpServer -c Release --no-restore
dotnet test Thalos.NET.slnx --no-build -c Release --logger "trx;LogFileName=tests.trx" --collect "XPlat Code Coverage" --settings coverage.runsettings --results-directory TestResults
# windows-latest only: append --filter Category!=Docker (Windows runs Windows containers, the pgvector image is Linux)
```

The ubuntu leg also runs the `Coverage gate` step (`MarcelRoozekrans/ci-baseline/actions/coverage-gate@v1` over
`TestResults/**/coverage.cobertura.xml`), which fails below a line-coverage floor and when `codecov.yml` would let
coverage drop. The commands above do not reproduce that gate. `codecov-upload` then uploads both legs' reports.

CI job `pack-validate` (ubuntu-latest; needs `fetch-depth: 0` for GitVersion):

```bash
dotnet tool restore
PACKAGE_VERSION=$(dotnet dotnet-gitversion /output json | jq -r '.SemVer')
[[ "$PACKAGE_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+ ]] || { echo "not a version: '$PACKAGE_VERSION'"; exit 1; }
dotnet pack Thalos.NET.slnx -c Release -o artifacts/packages -p:Version="$PACKAGE_VERSION"
```

The job then checks each package in `artifacts/packages` with an inline shell script (files per target framework,
nuspec version, MIT licence, repository metadata, 19 packages) and rehearses `dotnet nuget push` against a local
directory feed. That script is not reproducible from a command; read the `Validate the produced packages` step.
`publish-nuget` repeats the derive and pack steps and refuses any commit that is not tagged `v$PACKAGE_VERSION`.

Inner loop, not run by a CI job (these passed in this repo):

```bash
dotnet build Thalos.NET.slnx -c Release
dotnet test tests/Thalos.NET.Tests.Unit -c Release -f net11.0
dotnet test tests/Thalos.NET.Tests.Architecture -c Release -f net10.0
dotnet test tests/Thalos.NET.Tests.Memory.RagNet -c Release -f net10.0 --filter "Category!=Docker"
```

`-f` picks one target framework. `Tests.Mcp`, `Tests.Sandbox`, `Tests.Sandbox.Host` and `Tests.Workflow.Orm` only have
`net11.0`. `scripts/pack-local.ps1` (PowerShell 7) packs into a local feed for a consumer.

## Conventions and gotchas

- `TreatWarningsAsErrors` is on with Meziantou, Roslynator and ZeroAlloc analyzers, `LangVersion` 13.0 and
  `AnalysisLevel latest-recommended`. `CS1591`, `MA0048` and `MA0158` are suppressed repo-wide on purpose (comments in
  `Directory.Build.props`): `MA0158` because libraries target `net8.0`, which has no `System.Threading.Lock`.
- Package versions are central (`Directory.Packages.props`). `<VersionPrefix>` in `Directory.Build.props` is only the
  fallback for a local build; releases take the version from GitVersion and release-please.
- Single-target projects set `<TargetFrameworks />` or `<TargetFrameworks>net11.0</TargetFrameworks>`. `net8.0` is the
  default for libraries; `Memory.RagNet`, `Workflow.Orm` and the three `Sandbox*` packages are `net10.0` and
  `net11.0` only because their dependencies are. Do not add `net8.0` to them.
- `DaedalusIndependenceTests` fails if any project, props or C# file references Daedalus. Keep this repo
  standalone.
- `LayeringTests` use ArchUnitNET over an explicit assembly array. A new shipping assembly must be added there, or its
  rules pass vacuously.
- `Sandbox*` packages: the sandbox host is consumed as a Native AOT publish, so it must stay trim and AOT clean.
  `NativeAotSmokeTests` publishes `Tests.Sandbox.Host.Native` for `linux-x64` and fails on any trim or AOT warning;
  it proves only `Thalos.NET.Sandbox.Host` and its dependencies, not the other packages. `Tests.Native.Probe`
  publishes all packages as Native AOT and is compared with a JIT run by `Tests.Workflow.Orm`.
- Flaky: `SandboxHostTests.A_roslyn_tool_list_that_times_out_leaves_the_route_empty_and_is_retried` can fail on
  `net11.0` on a loaded runner, see
  [#540](https://github.com/MarcelRoozekrans/Thalos.NET/issues/540). Re-run once, do not weaken the assertion.
- The README documents the public API with copied signatures; update it when a port in
  `src/Thalos.NET.Abstractions/Ports/` changes.

<!-- org-core:start sha256=08a88996bc9165f888fc0260607cfd0670cae128c323e16707c6b452ee66222d (generated from docker-nuc scripts/agents-md/org-core-personal.md; do not edit here) -->
## Org rules

These rules hold in every public MarcelRoozekrans repo.

### Commits and releases

- Conventional Commits. release-please reads them on the default branch, and the changelog sections
  in `release-please-config.json` decide which types count. The standard: `feat`, `fix`, `perf`,
  `deps` and `revert` open or update the release PR; `docs`, `test`, `refactor`, `build`, `ci`,
  `chore` and `style` do not, unless the commit is breaking or has a `Release-As:` footer. The
  release happens when that PR merges. A breaking change needs `!` or a `BREAKING CHANGE:` footer.
- A commit body never contains nested parentheses. release-please cannot parse them and silently
  drops the whole commit from the changelog, while the workflow still reports success.
- After merging a commit that should release, check that it appears in the release-please PR.

### Branches and pull requests

- Every change goes through a branch and a pull request. Never push to the default branch directly.
- A PR body says what the diff does, and nothing the diff does not do.
- Fix review findings in the PR under review, including small ones. File an issue only for a finding
  that belongs to other code, needs a design decision, is its own piece of work, or waits on
  something external.

### CI

- The .NET repos call the shared workflows of `MarcelRoozekrans/ci-baseline`, pinned to `@v1`, never
  `@main`.
- Its coverage gate fails the build when line coverage drops below the floor of 80%, and Codecov
  fails a PR that lowers project coverage by more than 1 percentage point. New code comes with
  tests.
- A repo with a lower floor documents the reason and links the issue that raises it.
<!-- org-core:end -->
