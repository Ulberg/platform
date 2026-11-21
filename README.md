# platform

Infrastructure and platform for the `todo` showcase (Azure, Entra, AKS, ArgoCD, observability), written as Pulumi in F#.

- Applications describe what they need in a `platform.yaml` in their own repo. This repo decides how to provide it.
- Changes are applied only by CI, after approval from a platform lead.
- People, groups and repo leads are defined as code in `access/`.

See `AGENTS.md` for the working rules and `Ulberg-Org/todo/PLAN.md` for the overall plan.

## Layout

| Path | What |
|---|---|
| `access/people.yaml` | People, groups and repo leads. The only place access is defined |
| `bootstrap/` | One-time setup Pulumi needs before it can run (state, CI identities). See its README |
| `cli/` | The platform CLI (`platform up / sync / test / down`) and its image (D63) |
| `components/`, `environments/`, `apps/` | What Argo CD deploys per environment, and the apps it runs (D67, D68) |
| `renderer/` | `platform.yaml` + an environment → Kubernetes manifests; Argo CD's plugin (D60, D68) |
| `stacks/access/` | Pulumi stack `main`: Entra groups (`platform-read` ⊃ `platform-write` ⊃ `platform-admin`), PIM for Groups, Conditional Access, Azure Reader, GitHub rulesets and environments, CODEOWNERS check |
| `tests/` | Unit tests (Expecto) |

## Tools

`flake.nix` pins the tools for working on the platform (D79): `nix develop` gives developers the same versions as
Claude's sandbox (platform profile). On macOS it uses your system's .NET 11 (`brew install dotnet`).

## The platform CLI (local development)

`platform` runs an app locally the way it runs in Azure (D61, D67): a kind cluster where Argo CD deploys the
platform and the app, with Postgres outside and Grafana for telemetry. It ships as a public, signed image
(D63), so a developer needs only Docker and cosign:

```bash
brew install cosign            # verifies the image's signature (with nix-darwin: add "cosign" to the
                               # Homebrew brews in your flake, then darwin-rebuild switch)
# Docker Desktop with 12 GiB of memory, sharing your home folder (the default)
```

App repos run it through `make` at a pinned version: `make up`, `make sync`, `make test-platform`, `make down`
(see the app's README). To have `platform` on your PATH for any app repo, add this to your shell profile, with
the version and digest from the latest release (its summary lists them):

```bash
platform() {
  local image=ghcr.io/ulberg-org/platform-cli:vX.Y.Z@sha256:...
  cosign verify "$image" --certificate-oidc-issuer https://token.actions.githubusercontent.com \
    --certificate-identity-regexp '^https://github\.com/Ulberg-Org/platform/\.github/workflows/release\.yml@refs/tags/v' >/dev/null &&
    docker run --rm -i -v /var/run/docker.sock:/var/run/docker.sock -v "$PWD:$PWD" -w "$PWD" "$image" "$@"
}
# then, in an app repo: platform up .
```

Developing the platform itself: run the CLI from this checkout instead, which builds the renderer from it:
`PLATFORM=../platform/cli/platform make up` in the app repo.

Releasing: push a `vX.Y.Z` tag (MAJOR.MINOR.PATCH). `.github/workflows/release.yml` builds both images for arm64
and amd64, publishes them on GHCR and signs them; then bump the app repos' pins.

## Checks (all in CI, no git hooks)

```bash
dotnet tool restore
dotnet fantomas --check stacks tests
dotnet build platform.slnx -c Release
dotnet run --project tests/Access.Tests -c Release --no-build
```

`.github/workflows/pulumi.yml` runs `pulumi preview` on every PR and on `main`, and `pulumi up` on `main` only after a platform lead approves the `dev` environment.

Changing who has access: edit `access/people.yaml`, update `.github/CODEOWNERS` in every affected repo to what the preview error prints (merge the other repos' CODEOWNERS first), and open a PR.
