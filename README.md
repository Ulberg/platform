# platform

Infrastructure and platform for the `todo` showcase (Azure, Entra, AKS, ArgoCD, observability), written as Pulumi in F#.

- Applications describe what they need in a `platform.yaml` in their own repo. This repo decides how to provide it.
- Changes are applied only by CI, after approval from a platform lead.
- People, groups and repo leads are defined as code in `access/`.

See `AGENTS.md` for the working rules and `Ulberg/todo/PLAN.md` for the overall plan.
