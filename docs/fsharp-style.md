# F# style

These rules apply to **every F# file** in `Ulberg/todo` and `Ulberg/platform`: application code, Pulumi stacks, scripts and tests. There are no per-file exceptions. This file is identical in both repos (platform CI checks that), so change it in both at once.

Most of this is standard F#, explained well by Scott Wlaschin's *F# for Fun and Profit*. The reading list at the end links the articles each rule comes from. `reference/` shows the same patterns in a real codebase.

## 1. Program shape: input → domain → action → report

Every program, handler, job and stack is a pipeline in four steps, in this order:

1. **Input data.** Read files, requests, config or environment as DTOs. This is the only place untrusted or untyped data enters.
2. **Domain model.** Parse the DTOs into domain types and compute what should happen, with **pure functions** only. All decisions are made here, and this is what the unit tests cover.
3. **Action.** Perform the effects: write to the database, call an API, register Pulumi resources. Action code makes no decisions; it turns domain values into effects and returns what it did.
4. **Report.** Log results, failures and alerts from the values the earlier steps returned. Failures are values (`Result`), collected and reported in one place.

Effects live only at the edges (steps 1, 3 and 4), in functions whose name and signature make that obvious (`deploy`, `read…`, `save…`). Example: `platform/stacks/access/Program.fs` reads `people.yaml` and config, parses them into `Access`, builds a pure `Desired.Plan`, deploys the plan, and logs either the errors or a summary.

## 2. No mutation and no hidden side effects (CI-enforced)

- No `let mutable`, no `<-` assignment, no setting properties on an object after creating it. Build values in one expression (record construction, `List.map`, folds, object initializers in the constructor call).
- No `|> ignore` to throw away a result. If a function returns something, use it or return it. If it returns nothing useful, it shouldn't be called in the middle of pure code.
- A function is either **pure** (same input, same output, no effects) or an **action** at the edge. A pure-looking function must never register, write, send or log.
- Repeating an action with the same input must give the same result (idempotent). Pure planning plus declarative actions (Pulumi, `INSERT … ON CONFLICT`, PUT) gives that for free.
- Collections are F# immutable ones (`list`, `Map`, `Set`). Arrays and .NET collections appear only at library boundaries.

## 3. No `null`: use `Option` and `Result` (CI-enforced)

- Absence is `Option`. Failure is `Result` (or `Validation` when you want every error at once). `null` never appears in our types or our code.
- Libraries that return `null` are wrapped **at the call site** with `Option.ofObj` / `Option.ofNullable`, and nothing nullable goes further than that line. Leave a comment there saying so.
- DTO fields that may be missing are `option`, not nullable. System.Text.Json maps F# records, options, lists and maps natively.

## 4. Parse, don't validate: DTO → domain

- **DTOs** are plain records that mirror the wire or file format exactly. Every field that can be missing is an `option`, and there's no logic in them.
- **Domain types** can only hold valid values. Parsing a DTO either returns a domain value or the reasons it can't.
- Combine field parsers applicatively so every problem is reported at once: `Person.create <!^> PersonKey.parse key <*^> GitHubHandle.parse github …` or `validation { let! a = … and! b = … }`.
- Unknown input is an error, not ignored. For example, deserializers reject unmapped members.

## 5. Types

- **Single-case unions with a private constructor** for every domain primitive (`PersonKey`, `EntraObjectId`, `Email`, …), each with a companion module of the same name exposing `parse`, `value` (or `string`) and, where useful, `create`.
- **Discriminated unions** for closed sets (`GroupName`, `CaState`), never strings. Use `[<RequireQualifiedAccess>]` when case names would clash with other types.
- Make illegal states unrepresentable: if a combination isn't allowed, the types shouldn't allow it.
- Record fields are camelCase; types, union cases and modules are PascalCase.

## 6. Errors

- Expected failures are `Result`/`Validation` values with messages that say where (`people.bob: 'entra': … is not an object ID`).
- Exceptions are only for bugs and for the outermost edge (e.g. failing a Pulumi run once, after reporting). Catch library exceptions at the boundary and turn them into `Result`.
- Use **FsToolkit.ErrorHandling** (`result {}`, `validation {}`, `taskResult {}`, `List.traverseValidationA`, `Result.requireSome`, …). Don't write your own builders or traversal helpers.

## 7. Modules and libraries

- Prefer established libraries over hand-written helpers (FsToolkit.ErrorHandling, System.Text.Json's built-in F# support, YamlDotNet). Check first; write a helper only when nothing fits.
- Generic, reusable helpers go in their own module (e.g. `Parse.fs`), not as a pile of `private` functions in a domain file. Domain files contain domain rules.
- Order files top-down in the `.fsproj`: primitives → domain → planning → actions → program.

## 8. Formatting and warnings (CI-enforced)

- Fantomas formats everything (`dotnet fantomas --check`). `TreatWarningsAsErrors` and nullness checking are on.
- No warning suppressions without a comment saying why, and no `TODO` hacks.

## Reading list (F# for Fun and Profit)

- [Designing with types](https://fsharpforfunandprofit.com/series/designing-with-types/): the series behind §4–5, especially [single case union types](https://fsharpforfunandprofit.com/posts/designing-with-types-single-case-dus/) and [making illegal states unrepresentable](https://fsharpforfunandprofit.com/posts/designing-with-types-making-illegal-states-unrepresentable/).
- [The Option type](https://fsharpforfunandprofit.com/posts/the-option-type/): why F# uses `Option` instead of `null` (§3).
- [Railway oriented programming](https://fsharpforfunandprofit.com/rop/): `Result`-based error handling and keeping effects at the edges (§1, §6).
- [Using the core functions in practice](https://fsharpforfunandprofit.com/posts/elevated-world-3/): applicative validation that collects every error (§4).
- [Serializing your domain model](https://fsharpforfunandprofit.com/posts/serializing-your-domain-model/): DTOs at the boundary, domain types inside (§4).
