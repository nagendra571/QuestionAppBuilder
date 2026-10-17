# QuestionAppBuilder Sample Host

The sample host consumes the platform's Core runtime and Editor Razor Class Libraries. It owns the database connection, identity configuration, application layout, and response email delivery. Questionnaire definitions and response behavior live in the referenced platform libraries.

## Run locally

Prerequisites: .NET 8 SDK and SQL Server LocalDB on Windows.

```powershell
dotnet dev-certs https --trust
dotnet run --project samples/QuestionAppBuilder.SampleHost --urls https://localhost:5001
```

The sample applies EF Core migrations only in Development. Its default connection string targets the `QuestionAppBuilderSampleHost` LocalDB catalog. `/` opens the designer, `/ConfigureApp/Applications` lists applications, `/App/Mine` lists the signed-in user's responses, `/App/{applicationId}` opens a published questionnaire, and `/Reports` opens restricted submission analysis. Respondents can save and resume; each response stays pinned to its starting published version. Result visibility is configured per version, and scores are never shown during completion.

Development mode exposes a local-only sign-in and an in-memory mail sink. All sample users use `DevPassword!1`:

| Username | Role | Access |
| --- | --- | --- |
| `designer` | `Designer` | Design applications |
| `filler` | `Filler` | Respond to questionnaires |
| `filler2` | `Filler` | Second respondent account for owner-isolation checks |
| `analyst` | `Analyst` | View submission reports |

The Development mail sink keeps response links in process memory; it is not durable email delivery. `DevAuth` and the mail sink are registered only in Development and are absent from deployed environments.

## Host configuration

| Key | Purpose |
| --- | --- |
| `QuestionAppBuilder:ConnectionString` | Host-owned SQL Server database. |
| `QuestionAppBuilderEditor:BasePath` | Editor prefix, default `/ConfigureApp`. |
| `QuestionAppBuilderEditor:DesignPolicy` | Required named design policy, default `QuestionAppBuilder.Design`; blank values fail startup. |
| `QuestionAppBuilderRuntime:BasePath` | Respondent prefix, default `/App`. |
| `QuestionAppBuilderRuntime:PublicBaseUri` | Trusted HTTPS origin used to construct email access links. |
| `QuestionAppBuilderReports:BasePath` | Reports prefix, default `/Reports`. |
| `QuestionAppBuilderReports:DisplayTimeZone` | Time zone used to interpret local report date filters, default `UTC`. |
| `QuestionAppBuilder:Authorization:DesignRole` | Host role required by the design policy; default `Designer`. |
| `QuestionAppBuilder:Authorization:ReportRole` | Host role required by the report policy; default `Analyst`. |
| `QuestionAppBuilder:Identity:Authority` | HTTPS OpenID Connect authority, required outside Development. |
| `QuestionAppBuilder:Identity:Issuer` | Expected HTTPS token issuer, required outside Development. |
| `QuestionAppBuilder:Identity:ClientId` / `ClientSecret` | OIDC client credentials, required outside Development. Supply secrets from the deployment secret provider. |
| `QuestionAppBuilder:DataProtection:KeyRingPath` | Absolute persistent key-ring path shared by host instances. |
| `QuestionAppBuilder:Smtp:Host`, `Port`, `FromAddress` | SMTP endpoint and sender, required outside Development. |
| `QuestionAppBuilder:Smtp:Username`, `Password` | Optional SMTP credentials; provide both or neither. Supply the password from the deployment secret provider. |
| `QuestionAppBuilder:Smtp:ImplicitTls` | Use implicit TLS when true; otherwise SMTP STARTTLS is required. |

Outside Development, the host fails startup if OIDC, persistent Data Protection keys, or SMTP settings are incomplete. The host validates issuer and stable subject claims before mapping signed-in users to respondent ownership. It does not create a second user database. Use a durable, protected, shared key ring for production instances.

Report CSV exports are UTF-8. Text cells that begin with spreadsheet formula markers (`=`, `+`, `-`, `@`) or control characters after leading whitespace are prefixed with an apostrophe so spreadsheet software treats them as data. CSV quotes, commas, and line breaks are escaped using standard CSV quoting.

## Integration pattern

Reference `QuestionAppBuilder.Infrastructure`, `QuestionAppBuilder.Core`, and `QuestionAppBuilder.Editor`, then register the host identity before mounting the surfaces:

```csharp
var hostIdentity = HostIdentityRegistration.AddHostIdentity(
    builder.Services, builder.Configuration, builder.Environment);
builder.Services.AddQuestionAppBuilder(o => o.ConnectionString = connectionString);
builder.Services.AddQuestionAppBuilderRuntime(o =>
{
    o.BasePath = "/App";
    o.FillPolicy = HostIdentityRegistration.FillPolicy;
    o.HostAuthenticationScheme = hostIdentity.AuthenticationScheme;
    o.TrustedIssuer = hostIdentity.Issuer;
    o.PublicBaseUri = new Uri("https://surveys.example.org");
});
builder.Services.AddQuestionAppBuilderEditor(o =>
{
    o.BasePath = "/ConfigureApp";
    o.DesignPolicy = HostIdentityRegistration.DesignPolicy;
});
HostIdentityRegistration.AddPlatformPolicies(builder.Services, builder.Configuration);
```

The host also owns the standard authentication, rate-limiting, and authorization middleware. Call `UseAuthentication()`, `UseRateLimiter()`, and `UseAuthorization()` in that order before mapping `MapQuestionAppBuilderEditor()`, `MapQuestionAppBuilderRuntime()`, and `MapQuestionAppBuilderReports()`. Keep each surface's named policy on its controller and route group; do not disable it for anonymous demos. The sample configures host sign-in identity; applications may separately choose the platform's email-link access mode.

The sample host runs migrations automatically only in Development. Production deployments must apply the generated EF migration through an explicit release step before traffic is moved to the new host version; production startup never mutates the schema.
