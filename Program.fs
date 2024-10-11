open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Hosting
open Microsoft.AspNetCore.Authentication
open System
open Storage
open EmailModule
open Microsoft.Extensions.DependencyInjection
open Giraffe
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Http
open System.Security.Claims
open Microsoft.Extensions.Logging
open MailKit.Net.Smtp
open MimeKit

let builder = WebApplication.CreateBuilder()
builder.Services.AddGiraffe() |> ignore

builder.Services.AddLogging(fun logging ->
    logging.AddConfiguration(builder.Configuration.GetSection("Logging")) |> ignore
    logging.AddConsole() |> ignore
    logging.AddDebug() |> ignore)
|> ignore

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(fun options ->
        options.LoginPath <- PathString("/login")
        options.LogoutPath <- PathString("/logout")
        options.Cookie.Name <- "AuthCookie"
        options.Cookie.HttpOnly <- true
        options.Cookie.SecurePolicy <- CookieSecurePolicy.Always
        options.Cookie.SameSite <- SameSiteMode.Strict
        options.ExpireTimeSpan <- TimeSpan.FromDays(14.0) // Persistent for 14 days
        options.SlidingExpiration <- true)
|> ignore

builder.Services.AddAuthorization() |> ignore

let smtpPass = builder.Configuration["SmtpPassword"]
let smtpUser = builder.Configuration["SmtpUser"]

let app = builder.Build()

type MagicCode =
    | MagicCode of int

    member this.Value =
        match this with
        | MagicCode value -> value

type ExpiryDate = ExpiryDate of System.DateTime

let createCookie (ctx: HttpContext) (email: ValidatedEmail) =
    task {
        // Create user claims
        let claims =
            [ Claim(ClaimTypes.Name, email)
              // Add additional claims if necessary
              ]

        let claimsIdentity =
            ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)

        let claimsPrincipal = ClaimsPrincipal(claimsIdentity)

        // Set authentication properties for persistent login
        let authProperties =
            AuthenticationProperties(
                IsPersistent = true, // Enables persistent cookie
                ExpiresUtc = Nullable(DateTimeOffset.UtcNow.AddDays(14.0)) // 14-day expiration
            )

        // Sign in the user
        do! ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, claimsPrincipal, authProperties)
    }

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

let sendEmailWithMagicCode (email: ValidatedEmail) (uri: string) : Result<unit, unit> =

    let message = new MimeMessage()
    message.From.Add(MailboxAddress("Amir", smtpUser))
    message.To.Add(MailboxAddress("Someone", email))
    message.Subject <- "Your Magic Link"

    let body = new TextPart("plain")
    body.Text <- sprintf "Your magic link is: %s" uri

    message.Body <- body

    use client = new SmtpClient()
    client.Connect("smtp.protonmail.ch", 587, MailKit.Security.SecureSocketOptions.StartTlsWhenAvailable)
    client.Authenticate(smtpUser, smtpPass)
    let _ = client.Send(message)
    client.Disconnect(true)

    Ok()


//TODO: replace with user emails from DataMiner DB
let getAcceptedEmails: (Email list) = [ ValidatedEmail "amirpanahi11@gmail.com" ]

let navigateUser: HttpHandler =
    fun next ctx ->
        task {
            // get query params
            let code = ctx.TryGetQueryStringValue "code" |> Option.defaultValue "" |> int32
            let email = ctx.TryGetQueryStringValue "email" |> Option.defaultValue ""

            // compare queryCode to code in db
            let codeInDb = retrieveItem<{| expiryDate: string; magicCode: int |}> email

            printfn $"{email} code in db: {codeInDb}"

            match codeInDb with
            | Failure failure ->
                // **Database Retrieval Failure: Log and Redirect**
                printfn $"Couldn't get DB data: {failure}"
                return! redirectTo false "/" next ctx

            | Success None ->
                // **No Record Found in Database: Log and Redirect**
                printfn "No record found in DB for the provided email."
                return! redirectTo false "/" next ctx

            | Success(Some dbValue) ->
                // **4. Validate Code and Expiration**
                let expiryDate =
                    try
                        DateTime.Parse(dbValue.expiryDate)
                    with :? FormatException ->
                        printfn "Invalid expiry date format in DB."
                        DateTime.MinValue

                let isExpired = DateTime.Now >= expiryDate
                let isMatch = dbValue.magicCode = code

                printfn $"Code Match: {isMatch}, Expired: {isExpired}"

                if isMatch && not isExpired then
                    // **5. Successful Validation: Delete Record, Create Cookie, Redirect**
                    let storeResult = storeItem email {| |}

                    match storeResult with
                    | Failure storeError ->
                        printfn $"Error deleting DB record: {storeError}"
                        // Optionally, handle the error (e.g., notify admin)
                        // Proceed to redirect even if deletion fails
                        ()
                    | Success() -> printfn "DB record deleted successfully."

                    // **Create the Authentication Cookie**
                    do! createCookie ctx email

                    // **Redirect to Protected Route**
                    return! redirectTo false "/" next ctx
                else
                    // **Validation Failed: Redirect**
                    return! redirectTo false "/" next ctx
        }

let trySendMagicLink (acceptedEmails: Email list) email =
    email
    |> validateEmail acceptedEmails
    |> Result.map createMagicCode
    |> Result.map (fun x ->
        match x with
        | Ok(email, code, _) -> (createMagicLinkURI "http://localhost:5000" email code, email)
        | Error err -> "", "")
    |> Result.map (fun (uri, email) -> sendEmailWithMagicCode email uri)

let sendMagicLink: HttpHandler =
    fun next ctx ->
        let logger = ctx.GetLogger("sendMagicLink")
        logger.LogInformation("smtp : {smtpUser}, {smtpPass}", smtpUser, smtpPass)
        let acceptedEmails = getAcceptedEmails

        ctx.TryGetQueryStringValue "email"
        |> trySendMagicLink acceptedEmails
        |> function
            | Ok _ -> text $"magic code was sent" next ctx
            | Error err ->
                match err with
                | EmailError err -> handleEmailError err next ctx
                | DBError error -> RequestErrors.BAD_REQUEST $"DB Error: {error}" next ctx

let webApp =
    choose
        [ route "/login" >=> sendMagicLink
          route "/login-callback"
          >=> setHttpHeader "Cache-Control" "no-store,no-cache"
          >=> navigateUser
          route "/"
          >=> requiresAuthentication (challenge CookieAuthenticationDefaults.AuthenticationScheme)
          >=> text "You have special access!" ]

app.UseAuthentication() |> ignore
app.UseAuthorization() |> ignore
app.UseGiraffe(webApp)

app.Run()
