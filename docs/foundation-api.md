# Foundation API additions

All HTTP routes use `/api/v1`. JWT role values are `Student`, `Staff`, `SchoolAdmin`, `SuperAdmin`. Existing authentication and content contracts remain in place. Development Swagger is available at `/swagger`.

## Notifications and analytics

Authenticated mobile clients register or deactivate FCM devices with `POST /devices` and `DELETE /devices/{id}`. The notification inbox is `GET /notifications`; clients use `PATCH /notifications/{id}/read` or `POST /notifications/{id}/ack` with `{"state":"delivered"}` or `{"state":"opened"}`. Published content creates audience-targeted notification records and attempts FCM delivery when configured. SuperAdmins can read `GET /admin/analytics?days=30` and manually retry failed/pending deliveries with `POST /admin/notifications/retry`. Vercel invokes `/api/v1/internal/notifications/retry` daily at approximately 03:00 UTC (Hobby-compatible); configure `CRON_SECRET` in Vercel for the scheduled request.

| Method and route | Access | Request / result |
| --- | --- | --- |
| POST `/admin/schools` | SuperAdmin | `{name, slug, state, isActive}`; 201 with institution ID and fields; duplicate slug 409 |
| POST `/admin/users` | SuperAdmin | `{email, password, firstName, lastName, institutionId}`; creates a SchoolAdmin for an active institution, 201 without credentials; duplicate email 409 |
| PATCH `/admin/users/{id}` | SuperAdmin | `{role, isActive}`; roles Student/Staff/SchoolAdmin only; invalidates refresh token; 200 without credentials |
| GET `/admin/content/pending` | SuperAdmin | Lists PendingApproval content; optional `institutionId`, `type`, `search`, `page`, `pageSize`; paginated response includes content, event, author, and audience details |
| GET `/admin/platform-settings` | SuperAdmin | Reads platform name, support contacts, maintenance mode, listing fee, invoice days and event payment hold setting; returns defaults until saved |
| PUT `/admin/platform-settings` | SuperAdmin | Saves the same settings; request fields: `platformName`, `supportEmail`, `supportPhone`, `maintenanceMode`, `listingFee`, `invoiceDays`, `holdEventsUntilPayment` |
| GET `/institution/stats` | SchoolAdmin, SuperAdmin | SchoolAdmins see counts and distinct students reached for their own institution; SuperAdmins see platform-wide totals |
| POST `/media/calendar/{calendarId}` | SchoolAdmin, SuperAdmin | Uploads/replaces the calendar image using multipart field `file`; stores the media URL on the calendar and returns the calendar with a signed `imageUrl`; SchoolAdmins are restricted to their institution |
| GET `/departments?institutionId={id}` | SchoolAdmin, SuperAdmin | Lists departments with faculty, institution, programme and academic-level details; SchoolAdmins are scoped to their institution |
| GET `/departments/{id}` | SchoolAdmin, SuperAdmin | Reads one department in the caller's permitted institution scope |
| GET `/auth/notification-preferences` | SchoolAdmin, SuperAdmin | Reads the caller's email/push toggles and summary cadence |
| PUT `/auth/notification-preferences` | SchoolAdmin, SuperAdmin | Saves `{emailNotificationsEnabled, pushNotificationsEnabled, summaryCadence}`; cadence is `Immediate`, `Daily`, `Weekly`, or `Never` |
| PUT `/auth/academic-settings` | Authenticated active user | Existing fields plus optional `allCampusFeed` (default false) |
| GET `/auth/profile` | Authenticated active user | Existing profile fields plus `allCampusFeed` |

Administrator passwords require at least 12 characters. School name/state are required and bounded; slugs contain lowercase letters, numbers and single hyphen separators. Initial super-admin creation is an operator command documented in `environments.md`, not an HTTP route. This API cannot create or modify other super administrators.

Authentication validates signature, issuer, audience, expiry and the current user's active status and role. A disabled/deleted account or stale role token receives 401. Authenticated users outside the roles permitted for an admin route receive 403. SchoolAdmins see only their institution's statistics and cannot change their own assigned institution. New administrator operations create audit records in the same database save; records contain actor, action, target, institution, timestamp and non-secret change details.

Personalized feeds match the user's academic hierarchy; all-campus feeds include published content throughout the same institution. The preference never enables cross-school access. Direct content reads follow the same scope; school administrators may review unpublished content only in their own institution. Foreign or inaccessible content returns 404. Audience creation checks institution/faculty/department/programme/level relationships, rejecting invalid combinations with 400.

SuperAdmin moderation reach and `views` are based on durable unique `ContentViews` records. Student/staff detail requests to `GET /content/{id}` create those records; listing feed items alone does not count a view. `GET /content` returns all statuses in the caller's institution for SchoolAdmins and supports an optional `status` filter, while student/staff feeds remain published-only. Content responses include event, advertisement, image and timestamp fields. The platform stats response currently treats inactive institutions as pending verification because the schema has no separate verification status yet.

Content creation produces drafts. Publishing, media uploads, advertisement moderation, notification delivery and analytics are implemented. Cloud delivery requires the deployment credentials described in environments.md.

Apply the pending EF migrations before deploying. `AddPlatformSettingsAndNotificationPreferences` creates persistent platform settings and adds the super-admin email/push preference fields and summary cadence to user preferences.

HTTP regression tests exercise real JWT authentication with an isolated in-memory database. CI additionally applies migrations to PostgreSQL 17, runs initial administrator bootstrap and checks its repeat refusal, verifies repeatable development seeding, and confirms that staging rejects demo seeding. These checks do not provision or validate deployed cloud environments.

Staging uses a daily Vercel Cron schedule on the Hobby plan. Published content still sends immediately; scheduled retries and usage-counter aggregation may lag by up to a day. Analytics DAU and event totals query durable records directly.
