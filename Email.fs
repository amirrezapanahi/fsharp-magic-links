module EmailModule

open System.Text.RegularExpressions
open Giraffe

type ValidatedEmail = string
type UnvalidatedEmail = string

type Email =
    | UnvalidatedEmail of UnvalidatedEmail
    | ValidatedEmail of ValidatedEmail

    member this.Value =
        match this with
        | UnvalidatedEmail email -> email
        | ValidatedEmail email -> email


type EmailError =
    | EmptyEmail
    | InvalidEmail
    | UnauthorizedEmail

type CreateMagicCodeError =
    | EmailError of EmailError
    | DBError of string

let validateEmail (acceptableUsers: Email list) (input: string option) : Result<ValidatedEmail, CreateMagicCodeError> =
    match input with
    | None -> Error(EmailError EmptyEmail)
    | Some value when value.Length = 0 -> Error(EmailError EmptyEmail)
    | Some value ->
        let pattern = @"^[^@\s]+@[^@\s]+\.[^@\s]+$"
        let isAcceptable = List.contains (ValidatedEmail value) acceptableUsers

        match Regex.IsMatch(value, pattern), isAcceptable with
        | false, _ -> Error(EmailError InvalidEmail)
        | true, false -> Error(EmailError UnauthorizedEmail)
        | true, true -> Ok(value)

let handleEmailError (error: EmailError) : HttpHandler =
    fun next ctx ->
        match error with
        | EmptyEmail -> RequestErrors.BAD_REQUEST "Email is empty" next ctx
        | InvalidEmail -> RequestErrors.BAD_REQUEST "Email format is invalid" next ctx
        | UnauthorizedEmail -> RequestErrors.BAD_REQUEST "Email is not authorized to access resource" next ctx
