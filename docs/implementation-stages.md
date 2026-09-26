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

Status: In progress. The content-management and calendar foundations are implemented; provider-backed uploads and advertisement workflows remain.

- Implemented: draft, pending-approval, published, rejected and archived status transitions with role-controlled moderation.
- Implemented: content editing, school-scoped archive/delete, audience validation and feed text search with existing type, urgency, academic-scope and pagination filters.
- Implemented: academic calendar replacement with automatic deactivation of the previous official calendar.
- Configured: separate private Backblaze B2 buckets and application keys for staging and production; environment templates and deployment documentation are updated.
- Remaining: Backblaze upload adapter, signed/authorized media URLs, JPEG/PNG/WebP/PDF validation, size limits, safe names and image compression.
- Remaining: calendar image upload limits, complete event/advertisement/media DTOs, advertisement moderation and payment confirmation.
- Remaining: contract and endpoint tests for the new Stage 2 operations.

## 3. Notifications, telemetry and release verification

Suggested commit: `feat: add targeted notifications analytics and release checks`

Status: Planned.

- FCM credentials and device registration, reliable publication triggers, segmented delivery and notification preference enforcement.
- Notification history, read/unread status, device delivery/open acknowledgements.
- Registration/session telemetry, unique content views, per-school aggregates and super-admin analytics.
- Notification delivery retries/idempotency and metrics tests.
- Final API specification, integration fixtures, deployment smoke tests and external environment/access verification.

The workflow is a CI quality gate (format, build, HTTP regression tests, migration SQL generation, model/snapshot consistency, and PostgreSQL migration/bootstrap/seed checks), not an automatic production deployment. Stages 2 and 3 must extend its test coverage before release.
