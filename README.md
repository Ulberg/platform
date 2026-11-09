# platform

Infrastructure and platform for the `todo` showcase (Azure, Entra, AKS, ArgoCD, observability), written as Pulumi in F#.

- Applications describe what they need in a `platform.yaml` in their own repo. This repo decides how to provide it.
- Changes are applied only by CI, after approval from a platform lead.
- People, groups and repo leads are defined as code in `access/`.

See `AGENTS.md` for the working rules and `Ulberg/todo/PLAN.md` for the overall plan.

## Layout

| Path | What |
|---|---|
| `access/people.yaml` | People, groups and repo leads. The only place access is defined |
| `bootstrap/` | One-time setup Pulumi needs before it can run (state, CI identities). See its README |
| `stacks/access/` | Pulumi stack `main`: Entra groups (`platform-read` ⊃ `platform-write` ⊃ `platform-admin`), PIM for Groups, Conditional Access, Azure Reader, GitHub rulesets and environments, CODEOWNERS check |
| `tests/` | Unit tests (Expecto) |

## Checks (all in CI, no git hooks)

```bash
dotnet tool restore
dotnet fantomas --check stacks tests
dotnet build platform.slnx -c Release
dotnet run --project tests/Access.Tests -c Release --no-build
```

`.github/workflows/pulumi.yml` runs `pulumi preview` on every PR and on `main`, and `pulumi up` on `main` only after a platform lead approves the `dev` environment.

Changing who has access: edit `access/people.yaml`, update `.github/CODEOWNERS` in every affected repo to what the preview error prints (merge the other repos' CODEOWNERS first), and open a PR.
