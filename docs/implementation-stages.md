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

Status: MVP implementation complete, with provider credentials and final release verification remaining.

- Implemented: draft, pending-approval, published, rejected and archived status transitions with role-controlled moderation.
- Implemented: content editing, school-scoped archive/delete, audience validation and feed text search with existing type, urgency, academic-scope and pagination filters.
- Implemented: academic calendar replacement with automatic deactivation of the previous official calendar.
- Implemented: separate private Backblaze B2 storage configuration, server-side upload adapter, signed downloads, safe object names, MIME validation and size limits.
- Implemented: event, advertisement and media DTOs, school-scoped moderation, and publication status transitions.
- Implemented: optional Paystack transaction initialization and verification endpoints. Paystack keys remain deployment secrets; payment is outside the MVP unless product scope changes.
- Remaining: expanded contract and endpoint tests for the new Stage 2 operations and provider-side release verification.

## 3. Notifications, telemetry and release verification

Suggested commit: `feat: add targeted notifications analytics and release checks`

Status: Core implementation complete; deployment credentials, mobile integration, and external release checks remain.

- Implemented: Firebase Cloud Messaging sender, device token registration/deactivation, preference-aware audience targeting when content is published, and persisted notification history.
- Implemented: notification pagination, read state, per-device delivery/open acknowledgement, idempotent content notification records, durable publication recovery, and retry processing with attempt limits and delay.
- Implemented: Vercel scheduled retry endpoint, protected with `CRON_SECRET`, plus SuperAdmin manual retry endpoint.
- Implemented: unique content view tracking, registration/login/session refresh telemetry, and institution-level SuperAdmin analytics.
- Remaining external setup: create Firebase service account credentials and set `Fcm__ServiceAccountJson` and `CRON_SECRET` in Vercel; the mobile app must register its FCM token and send open acknowledgements.
- Remaining release work: apply pending EF migrations in staging and production, run end-to-end checks with real devices/provider credentials, and complete the deployment smoke checklist. Automated feature tests remain to be added.

The workflow is a CI quality gate (format, build, HTTP regression tests, migration SQL generation, model/snapshot consistency, and PostgreSQL migration/bootstrap/seed checks), not an automatic production deployment. Stages 2 and 3 must extend its test coverage before release.

### Acceptance-criteria follow-up

Added student/staff audience selection; secured student/staff media downloads; calendar multipart upload/compression/current alias/empty state; feed activity tracking; DAU/registered-user/active-post/notification receipt metrics; durable scheduled usage-counter aggregation; and race-safe unique views. See [Stage 3 integration](stage-3-integration.md) for client payloads and operational prerequisites. Backend source changes are local until committed and deployed; live cloud verification remains outstanding.

Staging uses a daily Vercel Cron schedule on the Hobby plan. Published content still sends immediately; scheduled retries and usage-counter aggregation may lag by up to a day. Analytics DAU and event totals query durable records directly.
