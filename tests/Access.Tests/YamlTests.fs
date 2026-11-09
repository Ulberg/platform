module Platform.Access.Tests.YamlTests

open Expecto
open FsToolkit.ErrorHandling
open Platform.Access

let private root text =
    Yaml.parseDocument text |> Validation.bind Yaml.mapping

[<Tests>]
let tests =
    testList
        "Yaml"
        [
            test "a missing field names the field" {
                let v = root "a: 1" |> Validation.bind (Yaml.field "b")
                Expect.equal v (Error [ "missing 'b'" ]) "missing field"
            }

            test "within prefixes every error with its context" {
                let v: Validation<unit, string> = Error [ "x"; "y" ] |> Yaml.within "people.ann"
                Expect.equal v (Error [ "people.ann: x"; "people.ann: y" ]) "prefixed"
            }

            test "an empty scalar is rejected" {
                let v =
                    root "a: ''" |> Validation.bind (Yaml.field "a") |> Validation.bind Yaml.scalar

                Expect.isError v "empty value"
            }

            test "broken YAML is an error, not an exception" {
                Expect.isError (Yaml.parseDocument "a: [1, 2") "unclosed list"
            }
        ]
