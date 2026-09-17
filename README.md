# Campus Update API

Central REST API for the Campus Update mobile, school-admin, and super-admin applications.

## Stack

- ASP.NET Core 10
- PostgreSQL 17 with Entity Framework Core
- JWT access and rotating refresh tokens
- OpenAPI at `/openapi/v1.json` in development
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
