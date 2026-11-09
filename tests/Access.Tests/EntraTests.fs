module Platform.Access.Tests.EntraTests

open System.Text.Json
open Expecto
open Platform.Access
open Platform.Access.Desired

let private rules =
    Entra.policyRules
        {
            role = GlobalAdministrator
            maxHours = 1
            eligible = []
        }
    |> List.map (fun (id, body) -> id, JsonDocument.Parse(body).RootElement)

let private rule id =
    rules |> List.find (fst >> (=) id) |> snd

let private text (name: string) (e: JsonElement) = e.GetProperty(name).ToString()

[<Tests>]
let tests =
    testList
        "directory-role PIM policy"
        [
            test "sets activation length, activation checks and eligibility expiry" {
                Expect.equal
                    (rules |> List.map fst)
                    [
                        "Expiration_EndUser_Assignment"
                        "Enablement_EndUser_Assignment"
                        "Expiration_Admin_Eligibility"
                    ]
                    "rule IDs"
            }

            test "activation lasts at most maxHours" {
                let r = rule "Expiration_EndUser_Assignment"
                Expect.equal (text "maximumDuration" r) "PT1H" "duration"
                Expect.equal (text "isExpirationRequired" r) "True" "required"
            }

            test "activation needs MFA and a justification" {
                Expect.equal
                    (text "enabledRules" (rule "Enablement_EndUser_Assignment"))
                    """["MultiFactorAuthentication","Justification"]"""
                    "enabled rules"
            }

            test "eligibility may be permanent" {
                Expect.equal (text "isExpirationRequired" (rule "Expiration_Admin_Eligibility")) "False" "permanent"
            }

            test "each body names its rule and Graph type, as Graph's PATCH requires" {
                for id, body in rules do
                    Expect.equal (text "id" body) id id
                    Expect.stringStarts (text "@odata.type" body) "#microsoft.graph.unifiedRoleManagementPolicy" id
            }
        ]
