# Campus Update API

Central REST API for the Campus Update mobile, school-admin, and super-admin applications.

## Stack

- ASP.NET Core 10
- PostgreSQL 17 with Entity Framework Core
- JWT access and rotating refresh tokens
- OpenAPI at `/openapi/v1.json`
- Swagger UI at `/swagger/index.html` 
- Firebase Cloud Messaging integration is planned for push delivery only

## Core schema

The initial migration provides:

- institutions → faculties → departments → programmes → academic levels
- users, roles, academic assignments, and feed preferences
- news, announcements, events, and advertisements through a shared content model
- audience targeting at every academic hierarchy level
- urgency, source attribution, publishing approval states, and attachments

## Run locally

Requirements: .NET SDK 10 and Docker Desktop.

```powershell
Copy-Item .env.example .env
docker compose up -d postgres
dotnet tool restore
dotnet tool run dotnet-ef database update --project src/CampusUpdate.Infrastructure --startup-project src/CampusUpdate.Api
dotnet run --project src/CampusUpdate.Api
```

The API starts using the URL shown by `dotnet run`. Check `/health` and, in development, use the interactive Swagger UI at `/swagger` or the OpenAPI document at `/openapi/v1.json`.

## Quality checks

```powershell
dotnet restore
dotnet format --verify-no-changes --no-restore
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
```

Production deployments must override `ConnectionStrings__Postgres` and `Jwt__Key` with secrets.

## Implementation milestones

See [the three-stage implementation plan](docs/implementation-stages.md), [environment and seed setup](docs/environments.md), and [foundation API changes](docs/foundation-api.md).

The development database password must match `ConnectionStrings__Postgres` in your shell when running the API or migrations. `.env` is read by Docker Compose, not automatically by ASP.NET Core. Base configuration contains no production connection string or signing secret.

# Foundation API additions

All HTTP routes use `/api/v1`. JWT role values are `Student`, `Staff`, `SchoolAdmin`, `SuperAdmin`. Existing authentication and content contracts remain in place. Development Swagger is available at `/swagger`.

| Method and route | Access | Request / result |
| --- | --- | --- |
| POST `/admin/schools` | SuperAdmin | `{name, slug, state, isActive}`; 201 with institution ID and fields; duplicate slug 409 |
| POST `/admin/users` | SuperAdmin | `{email, password, firstName, lastName, institutionId}`; creates a SchoolAdmin for an active institution, 201 without credentials; duplicate email 409 |
| PATCH `/admin/users/{id}` | SuperAdmin | `{role, isActive}`; roles Student/Staff/SchoolAdmin only; invalidates refresh token; 200 without credentials |
| PUT `/auth/academic-settings` | Authenticated active user | Existing fields plus optional `allCampusFeed` (default false) |
| GET `/auth/profile` | Authenticated active user | Existing profile fields plus `allCampusFeed` |

Administrator passwords require at least 12 characters. School name/state are required and bounded; slugs contain lowercase letters, numbers and single hyphen separators. Initial super-admin creation is an operator command documented in `environments.md`, not an HTTP route. This API cannot create or modify other super administrators.

Authentication validates signature, issuer, audience, expiry and the current user's active status and role. A disabled/deleted account or stale role token receives 401. Authenticated non-super-admin users receive 403 on admin operations. School administrators cannot change their own assigned institution. New administrator operations create audit records in the same database save; records contain actor, action, target, institution, timestamp and non-secret change details.

Personalized feeds match the user's academic hierarchy; all-campus feeds include published content throughout the same institution. The preference never enables cross-school access. Direct content reads follow the same scope; school administrators may review unpublished content only in their own institution. Foreign or inaccessible content returns 404. Audience creation checks institution/faculty/department/programme/level relationships, rejecting invalid combinations with 400.

Content creation still produces drafts. Publishing, media uploads, ad moderation, notification delivery and analytics belong to later stages. Content read responses still omit some event/ad/media detail; stage 2 will complete those DTOs. Notification preferences are stored now but do not deliver notifications until stage 3.

Apply the `FoundationAccessControl` migration before running this version. It adds nullable institution state, an all-campus preference defaulting to false for existing users, and the audit-log table. Existing institutions and personalized feeds retain their previous behavior.

HTTP regression tests exercise real JWT authentication with an isolated in-memory database. CI additionally applies migrations to PostgreSQL 17, runs initial administrator bootstrap and checks its repeat refusal, verifies repeatable development seeding, and confirms that staging rejects demo seeding. These checks do not provision or validate deployed cloud environments.

