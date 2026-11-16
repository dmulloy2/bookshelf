# AGENTS.md

Bookshelf is a fork of Readarr (.NET backend in `src/`, React frontend in
`frontend/`) that uses rreading-glasses for book metadata. The project and
binaries are still named `Readarr`/`NzbDrone` internally.

## Remotes and branches

- `origin` is `dmulloy2/bookshelf`. The default branch is `develop`; `master`
  also builds and publishes.
- Pushing to `develop` or `master` publishes images to
  `ghcr.io/dmulloy2/bookshelf`, so confirm before pushing.

## Commands

Tool versions come from `mise.toml` (.NET SDK and Node); run through `mise`.

- `./build.sh --backend --frontend` builds into `_output/`.
- `./test.sh Linux Unit Test` runs the unit tests (needs a build first; CI copies
  `_output/net10.0/linux-x64/publish/Readarr` into `_tests/bin/`).
- `yarn lint` and `yarn stylelint-linux` lint the frontend; `yarn lint-fix` edits
  files in place.
- `_output`, `_tests`, `_artifacts` and `_temp` are build output.

## Conventions

- Commit messages are short and imperative, with no type prefix (e.g. "Send ntfy
  import and download failure notifications"). Dependabot PRs use a `chore` prefix.
- NuGet versions are centralized in `src/Directory.Packages.props`.
- GitHub Actions in `.github/workflows` are pinned to commit SHAs with a version
  comment. Keep that when editing or adding actions.
- Dependabot (`.github/dependabot.yml`) uses cooldowns and grouped minor/patch PRs;
  don't bypass these.

## CI and publishing

- `build.yml` builds, tests, and builds two image variants (`softcover` and
  `hardcover`, amd64 and arm64). It pushes except on pull requests, and also runs
  weekly (Sunday 08:00 UTC) to pick up base image fixes. Changes under `.github/**`
  don't trigger it.
- `codeql.yml` and `dependency-review.yml` scan code and pull request dependencies.
