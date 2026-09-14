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

The API starts using the URL shown by `dotnet run`. Check `/health` and, in development, `/openapi/v1.json`.

## Quality checks

```powershell
dotnet restore
dotnet format --verify-no-changes --no-restore
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
```

Production deployments must override `ConnectionStrings__Postgres` and `Jwt__Key` with secrets.
