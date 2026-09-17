# Environment configuration

The API uses ASP.NET Core configuration: `appsettings.json`, `appsettings.{Environment}.json`, and process environment variables. It does **not** automatically read `.env` files. Never commit real environment files or credentials.

| Environment | Configuration | Database/access policy |
| --- | --- | --- |
| Development | `appsettings.Development.json` or `.env.development.example` copied to `.env.development` for Compose | Local disposable PostgreSQL or separate development project; synthetic data only |
| Staging | Copy `.env.staging.example` to an ignored file; set those values in the hosting secret store | Independent staging project, credentials and signing key |
| Production | Copy `.env.production.example` to an ignored file; set those values in the hosting secret store | Independent production project, least-privilege application user, restricted operator access |

Development Compose: `docker compose --env-file .env.development up -d postgres`. For the optional API container, use the same env file and `up -d --build api`. The development template uses Compose variables (`POSTGRES_*`, `JWT_KEY`); staging/production templates list actual API process variables. Do not use the development Compose stack as production infrastructure.

For local `dotnet run` and EF migrations, explicitly set `ConnectionStrings__Postgres` to the same database/password used by Compose. Copying a `.env` file alone does not configure the .NET process. The EF design-time factory accepts that variable; its fallback is the original local development database.

Staging and production must supply `ConnectionStrings__Postgres`, `Jwt__Key` (a unique cryptographically random secret of at least 32 bytes), and `ASPNETCORE_ENVIRONMENT`. Set `Cors__AllowedOrigins__0`, `Cors__AllowedOrigins__1`, etc. to exact admin-site origins. Base configuration has no usable database or signing-key default. Replace every template placeholder before deployment.

Apply migrations explicitly before starting the API:

```powershell
dotnet tool restore
dotnet tool run dotnet-ef database update --project src/CampusUpdate.Infrastructure --startup-project src/CampusUpdate.Api
```

## Initial administrator

After applying migrations, an operator supplies `Bootstrap__Email`, `Bootstrap__Password` (at least 12 characters), `Bootstrap__SchoolName`, `Bootstrap__SchoolSlug`, and `Bootstrap__SchoolState` as process secrets. Then run:

```powershell
dotnet run --no-launch-profile --project src/CampusUpdate.Api -- --bootstrap-admin
```

This creates the first super administrator and institution (or uses an active matching slug), hashes the password, and writes an audit record atomically. It refuses to run if any super administrator exists. Remove bootstrap variables afterwards. No public signup can create administrators. Later school administrators are provisioned through the super-admin endpoints.

## Development fixtures

Set `ASPNETCORE_ENVIRONMENT=Development`, `ConnectionStrings__Postgres`, and `Seed__Password` (a local-only password of at least 12 characters), then run:

```powershell
dotnet run --no-launch-profile --project src/CampusUpdate.Api -- --seed-development
```

The command applies migrations and creates one synthetic FOCIT demo hierarchy, 100/200 levels, four users, published news/urgent announcement/event/sponsored fixtures and calendar metadata in one transaction. Re-running skips an existing `focit-demo` institution. It refuses non-Development environments. Accounts are `superadmin@campus-update.test`, `admin@campus-update.test`, `student@campus-update.test` and `staff@campus-update.test`, using the supplied password. The school data is a placeholder, not a verified FOCIT structure; the calendar URL is illustrative, not a hosted image. Real media arrives in stage 2.

## External setup still required

- Provision independent development, staging and production database projects; verify migrations and backups for each.
- Choose an available API/database region together after measuring latency from Nigeria. No region is provisioned or latency claim made by this commit.
- Give developers development access, limit staging writers, and restrict production credentials/migrations to authorized operators. Record actual invitations/roles in the provider dashboard.
- Configure hosting secrets and allowed origins; verify HTTPS/proxy handling on the chosen host before release.

Do not mark these provider-side checklist items complete based on templates alone.
