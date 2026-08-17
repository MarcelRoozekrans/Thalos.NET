# Versioning and releases

Same design as [Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET) and AdoNet.Async.

- **No prereleases.** nuget.org only ever receives stable `X.Y.Z` versions, and only from the commit
  release-please tagged `vX.Y.Z`. `publish-nuget` refuses everything else.
- **GitVersion** (`GitVersion.yml`, dotnet local tool pinned in `.config/dotnet-tools.json`) derives every
  build's version from git history. `main` carries no label, so it derives plain stable numbers (`0.1.0`
  until the first tag, then the tag's version bumped per GitHubFlow); the commit tagged `vX.Y.Z` derives
  exactly `X.Y.Z`; other branches derive `X.Y.Z-<branch>.N`. `ci.yml`'s `pack-validate` job packs with
  `-p:Version=$PACKAGE_VERSION` on every push and rehearses the nuget.org push against a local feed —
  those packages never leave the runner.
- **release-please** (`.github/workflows/release-please.yml`, manifest mode:
  `release-please-config.json` + `.release-please-manifest.json`) proposes releases from conventional
  commits and cuts the tag. Manual dispatch only.
- **Conventional commits** are enforced on pull requests by the `commitlint` job (`.commitlintrc.yml`).
- **Publishing** is the `publish-nuget` job in `ci.yml`: manual dispatch with `publish_to_nuget=true`,
  Trusted Publishing (no stored API key), gated on the full matrix and `pack-validate`.

## One-time setup

```bash
# 1. nuget.org → Account → Trusted Publishing: add a policy with repository owner MarcelRoozekrans,
#    repository Thalos.NET, workflow file ci.yml (no environment). Do this while logged in as the
#    account that owns / will own the Thalos.NET.* package ids.
# 2. The account name the policy belongs to, as a repository *variable* (it is a username, not a secret):
gh variable set NUGET_USER --repo MarcelRoozekrans/Thalos.NET --body "<nuget.org username>"
```

## Cutting a release

```bash
# 1. First release only: release-please proposes 1.0.0 by default. Override with an empty commit
#    carrying a Release-As footer before the first dispatch (already done for 0.1.0).
git commit --allow-empty -m "chore: set the release version" -m "Release-As: 0.1.0"
git push origin main

# 2. Open the release PR — release-please reads the conventional commits since the last release
#    and proposes the version they imply (CHANGELOG.md + version.txt on the PR branch).
gh workflow run release-please.yml --ref main

# 3. Review and merge the release PR ("chore(main): release X.Y.Z"), like every PR.

# 4. Dispatch again: release-please sees the merged release PR and creates the GitHub release and
#    the vX.Y.Z tag — the tag GitVersion derives the stable version from.
gh workflow run release-please.yml --ref main

# 5. Publish that exact commit: dispatch CI on the release tag with the publish input. build-test
#    (both OS) and pack-validate run first; publish-nuget refuses to start until both are green, and
#    refuses to push unless the checked-out commit is tagged vX.Y.Z (a prerelease or an untagged
#    commit fails the gate). `--ref main` also works as long as main still points at the release commit.
gh workflow run ci.yml --ref vX.Y.Z -f publish_to_nuget=true
```

Pre-1.0 bump rules (`release-please-config.json`): a `feat!:`/`BREAKING CHANGE` bumps the minor
(0.1.0 → 0.2.0), a `feat:` bumps the patch (0.1.0 → 0.1.1). Once 1.0.0 is cut those become
major/minor as usual. Because `feat:` only bumps the patch, a deliberate minor (0.2.0 for the memory
packages) needs the same empty commit as step 1 with `Release-As: 0.2.0` before the first dispatch.

0.2.0 ships eight packages (`Thalos.NET.Memory` and `Thalos.NET.Memory.RagNet` joined the six of 0.1.x);
`pack-validate` checks the package list and each package's TFMs (`Thalos.NET.Memory.RagNet` is
`net10.0`-only, the others ship `net8.0` + `net10.0`) and rehearses the push of all eight.

## Local development against a consumer (Daedalus)

`scripts/pack-local.ps1` packs `0.2.0-local.<timestamp>` (the `VersionPrefix` in `Directory.Build.props`) into `C:\Projects\Prive\.nuget-local`
(no GitVersion involved) — the consumer pins that exact version until the release is on nuget.org.

## Renovate

`renovate.json` ignores `dotnet-sdk` on purpose: `global.json` pins the lowest 10.0.x feature band with
`rollForward: latestFeature` so both dev machines and CI resolve; a bumped pin above locally installed
SDKs breaks local builds. Everything else is bumped by PRs that must pass the same gates.
