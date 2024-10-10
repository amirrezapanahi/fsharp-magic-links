open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Hosting
open System.Text.RegularExpressions
open System
open Storage
open EmailModule
open Giraffe

let builder = WebApplication.CreateBuilder()
builder.Services.AddGiraffe() |> ignore
let app = builder.Build()

// MAGIC CODE ------
type MagicCode =
    | MagicCode of int

    member this.Value =
        match this with
        | MagicCode value -> value

type ExpiryDate = ExpiryDate of System.DateTime


let createMagicCode (email: ValidatedEmail) : Result<(ValidatedEmail * MagicCode * ExpiryDate), CreateMagicCodeError> =
    // create code with email and hash
    let code = email |> (fun str -> str + Guid.NewGuid().ToString()) |> hash

    // create expiry date -> Date.Now + 5 minutes
    let expiryDate = System.DateTime.Now.AddMinutes(5.0)

    // store code + expiry date in storage (side effect)
    let result =
        storeItem
            email
            ({| magicCode = code
                expiryDate = expiryDate |})

    match result with
    | Success x -> Ok(email, MagicCode code, ExpiryDate expiryDate)
    | Failure failure -> Error(DBError failure)

//domain should include "http//" or "https//"
let createMagicLinkURI (domain: string) (email: ValidatedEmail) (magicCode: MagicCode) : string =
    $"{domain}/login-callback?code={magicCode.Value}&email={email}"

let sendEmailWithMagicCode (email: Email) (code: MagicCode * ExpiryDate) : Result<unit, unit> =
    let magicCode, expiryDate = code

    //create a uri with the magic code inside it --> "https://url.com/login-callback/code?={code}"

    //format some sort of email html template and inject the uri

    //send email to user using MickGeorge email service (side effect) --> result?

    //handle the result of email sending
    Ok()



//TODO: replace with user emails from DataMiner DB
let getAcceptedEmails: (Email list) =
    [ ValidatedEmail "amir@email.com"; ValidatedEmail "jaz@email.com" ]

let navigateUser: HttpHandler =
    fun next ctx ->
        // get query param -> code
        let code = ctx.TryGetQueryStringValue "code" |> Option.defaultValue "" |> int32

        let email = ctx.TryGetQueryStringValue "email" |> Option.defaultValue ""

        // compare queryCode to code in db
        let codeInDb = retrieveItem<{| expiryDate: string; magicCode: int |}> email

        printfn $"{email} code in db: {codeInDb}"

        match codeInDb with
        | Success value ->
            match value with
            | Some dbValue ->
                // check that it hasnt expired
                let expired = System.DateTime.Now >= DateTime.Parse(dbValue.expiryDate)

                // if matches then create a cookie in users browser
                let matches = dbValue.magicCode = code

                // redirect user to upload page (protected route)
                if (matches && not expired) then
                    printfn "matches and not expired"
                    //delete the record in db

                    let result = storeItem email ({| |})

                    match result with
                    | Success x -> redirectTo true "/success" next ctx
                    | Failure y -> redirectTo true "/failure" next ctx

                else
                    printfn $"matches: {matches}, expired: {expired}"
                    redirectTo true "/failure" next ctx
            | None ->
                printfn "no value from db"
                redirectTo true "/failure" next ctx
        | Failure failure ->
            printfn $"couldnt get db data {failure}"
            redirectTo true "/failure" next ctx

let trySendMagicLink (acceptedEmails: Email list) email =
    email
    |> validateEmail acceptedEmails
    |> Result.bind createMagicCode
    |> Result.map (fun (email, code, expiry) -> createMagicLinkURI "http://localhost:5000" email code)

let sendMagicLink: HttpHandler =
    fun next ctx ->
        let acceptedEmails = getAcceptedEmails

        ctx.TryGetQueryStringValue "email"
        |> trySendMagicLink acceptedEmails
        |> function
            | Ok code -> text $"magi code {code}" next ctx
            | Error err ->
                match err with
                | EmailError err -> handleEmailError err next ctx
                | DBError error -> RequestErrors.BAD_REQUEST $"DB Error: {error}" next ctx

let webApp =
    choose
        [ route "/login" >=> sendMagicLink
          route "/failure" >=> text "failed login"
          route "/success" >=> text "success login"
          route "/login-callback"
          >=> setHttpHeader "Cache-Control" "no-store,no-cache"
          >=> navigateUser
          route "/" >=> text "Upload page: You have special access!" ]

app.UseGiraffe(webApp)
app.Run()
