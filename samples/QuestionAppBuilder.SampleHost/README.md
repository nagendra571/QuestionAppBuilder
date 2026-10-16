# QuestionAppBuilder Sample Host

A reference ASP.NET Core host that mounts the QuestionAppBuilder editor (designer) surface. It is the working example of how a consuming app integrates the platform: the host owns identity, roles, layout, and database; the editor ships as a Razor Class Library mounted at a configurable base path.

## Prerequisites

- .NET 8 SDK
- SQL Server LocalDB (Windows) for the default connection string

## Run

```powershell
dotnet run --project samples/QuestionAppBuilder.SampleHost
```

The host prints its listening address (default `http://localhost:5000`). `/` redirects to the designer index at `/ConfigureApp/Applications`. The default connection string in `appsettings.json` points at LocalDB (`(localdb)\MSSQLLocalDB`, database `QuestionAppBuilderSampleHost`); the sample host applies the EF Core migrations at startup when the provider is SQL Server.

### Dev users

The sample host uses a dev-only cookie sign-in (`DevAuth`). All users share the password `DevPassword!1`:

| Username | Role     | Can use the designer |
| -------- | -------- | -------------------- |
| designer | Designer | Yes                  |
| filler   | Filler   | No (403)             |
| analyst  | Analyst  | No (403)             |

Replace `AccountController`/`DevUserStore` with the host's real authentication in any non-sample app; the editor consumes whatever cookie principal the host establishes.

## Configuration

| Key                                      | Default       | Purpose                                                        |
| ---------------------------------------- | ------------- | -------------------------------------------------------------- |
| `QuestionAppBuilder:ConnectionString`    | LocalDB       | Database for definitions and submissions (host-owned).         |
| `QuestionAppBuilderEditor:BasePath`      | `/ConfigureApp` | Route prefix the designer mounts under. Must start with `/`. |
| `QuestionAppBuilderEditor:DesignerRole`  | `Designer`    | Host role allowed to use the designer; blank disables the gate. |

## Host integration

1. Reference the platform packages (in this repo, project references):

   - core engine: `QuestionAppBuilder.Infrastructure` (domain, application services, EF Core context, migrations)
   - editor surface: `QuestionAppBuilder.Editor` (MVC area with controllers and Razor views)

2. Register services in `Program.cs`:

```csharp
builder.Services.AddQuestionAppBuilder(o => o.ConnectionString = connectionString);
builder.Services.AddQuestionAppBuilderEditor(o =>
{
    o.BasePath = builder.Configuration["QuestionAppBuilderEditor:BasePath"] ?? o.BasePath;
    o.DesignerRole = builder.Configuration["QuestionAppBuilderEditor:DesignerRole"] ?? o.DesignerRole;
});
```

3. Wire authentication/authorization, then mount the routes:

```csharp
app.UseAuthentication();
app.UseAuthorization();

app.MapQuestionAppBuilderEditor();
```

`AddQuestionAppBuilderEditor` registers the area's controllers/views and the options singleton. `MapQuestionAppBuilderEditor` mounts the area under `BasePath` (`{basePath}/{controller=Applications}/{action=Index}/{id?}`) and, when `DesignerRole` is set, requires that role on every editor route. Views and redirects use tag helpers/`RedirectToAction`, so they all honor a custom base path without further work.

### Role mapping

The host owns identity and roles; the editor maps onto them. Set `DesignerRole` to the host's designer/admin role. The sample host issues role claims at sign-in (`designer` → `Designer`, etc.), so a signed-in user without the role gets 403 on every editor route. Blank `DesignerRole` skips the gate (useful for anonymous demos only).

### Layout

Editor views render inside the host's layout. `QuestionAppBuilderEditorOptions.LayoutName` picks it (default `_Layout`): provide a view with that name under the host's `Views/Shared/`, and the editor's `_ViewStart` resolves it. The sample host's `Views/Shared/_Layout.cshtml` shows the minimal contract.

### Database and migrations

The platform ships its EF Core migrations inside the core package so consuming hosts never hand-author schema. Apply them to the host's database:

- sample host (development convenience): `db.Database.Migrate()` after `builder.Build()`, guarded by `db.Database.IsSqlServer()` so tests using the in-memory provider are untouched;
- production hosts: run migrations as part of the deployment (for example `dotnet ef database update` with the platform migrations assembly, or a scripted `dotnet ef migrations script` step) before the new app version takes traffic.
