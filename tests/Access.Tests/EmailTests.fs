module Platform.Access.Tests.EmailTests

open Expecto
open Platform.Access.People

let private parse raw =
    Email.parseList "PLATFORM_ALERT_EMAILS" raw |> Result.map (List.map Email.value)

[<Tests>]
let tests =
    testList
        "alert emails"
        [
            test "a comma-separated list parses, ignoring spaces and empty entries" {
                Expect.equal
                    (parse (Some " a@example.com,,b@example.org "))
                    (Ok [ "a@example.com"; "b@example.org" ])
                    "two addresses"
            }

            test "a bad entry is reported by position, never by value" {
                Expect.equal
                    (parse (Some "a@example.com, secret-not-an-email"))
                    (Error [ "PLATFORM_ALERT_EMAILS: entry 2 is not an email address" ])
                    "position only"
            }

            test "the list can't be missing or empty" {
                let expected = Error [ "PLATFORM_ALERT_EMAILS: needs at least one address" ]
                Expect.equal (parse None) expected "missing"
                Expect.equal (parse (Some " , ")) expected "empty"
            }
        ]
