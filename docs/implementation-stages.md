# Backend implementation stages

Each stage is a coherent commit, with passing repository quality gates. This plan is based on the supplied checklist; repeated FCM screenshots and the repeated school-admin scoping item are counted once.

## 1. Foundation and access control

Suggested commit: `feat: complete backend foundation and tenant access controls`

- Environment templates for Development, Staging, and Production; development-only defaults, explicit deployment secrets, configured browser origins.
- Relational academic hierarchy, users, core content and calendar models (existing); institution state and personalized/all-campus preference (new).
- Existing registration, login, refresh and profile APIs; persisted all-campus preference.
- Current-user token validation, active-account enforcement, school-admin assignment protection, school-scoped reads/writes, audience hierarchy validation.
- Super-admin school creation, school-admin provisioning and role/activation updates; audit records saved with each change.
- Explicit first-super-admin bootstrap command; development-only synthetic school/users/content/calendar seeds.
- HTTP authentication/authorization regression tests, EF migration and API/setup documentation.

Cloud acceptance criteria are external tasks, not automatically completed by these files: create three separate database projects, select and measure a hosting region for the Nigeria pilot, configure deployed secrets, invite team members with environment-specific permissions. See `environments.md`.

## 2. Content, publishing and media

Suggested commit: `feat: add publishing feeds media and moderation workflows`

- Draft/review/publish transitions, content update/delete, and enforcement of school scope for every new operation.
- Student/staff/all audience selection, complete feed filters, search and news categories. All-campus remains limited to the user's institution.
- Cloud storage adapter and buckets, public/authenticated asset rules, validated JPEG/PNG/WebP/PDF uploads, size limits, safe names and image compression.
- Calendar image upload (maximum 5 MB), active-calendar replacement, `/calendar/current`, and a defined 404 empty-state response.
- Ad submission with image, targeting and click URL; moderation queue and approved lifecycle including payment confirmation. Payment provider and cloud storage provider require decisions before implementation.
- Complete content/event/ad DTOs and contract tests; update API specifications.

## 3. Notifications, telemetry and release verification

Suggested commit: `feat: add targeted notifications analytics and release checks`

- FCM credentials and device registration, reliable publication triggers, segmented delivery and notification preference enforcement.
- Notification history, read/unread status, device delivery/open acknowledgements.
- Registration/session telemetry, unique content views, per-school aggregates and super-admin analytics.
- Notification delivery retries/idempotency and metrics tests.
- Final API specification, integration fixtures, deployment smoke tests and external environment/access verification.

The workflow is a CI quality gate (format, build, HTTP regression tests, migration SQL generation, model/snapshot consistency, and PostgreSQL migration/bootstrap/seed checks), not an automatic production deployment. Stages 2 and 3 must extend its test coverage before release.
