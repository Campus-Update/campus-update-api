# Foundation API additions

All HTTP routes use `/api/v1`. JWT role values are `Student`, `Staff`, `SchoolAdmin`, `LeadSchoolAdmin`, `NewsAdmin`, `CalendarAdmin`, and `SuperAdmin`. Existing authentication and content contracts remain in place. Development Swagger is available at `/swagger`.

## Notifications and analytics

Authenticated mobile clients register or deactivate FCM devices with `POST /devices` and `DELETE /devices/{id}`. The notification inbox is `GET /notifications`; clients use `PATCH /notifications/{id}/read` or `POST /notifications/{id}/ack` with `{"state":"delivered"}` or `{"state":"opened"}`. Published content creates audience-targeted notification records and attempts FCM delivery when configured. SuperAdmins can read `GET /admin/analytics?days=30` and manually retry failed/pending deliveries with `POST /admin/notifications/retry`. Vercel invokes `/api/v1/internal/notifications/retry` daily at approximately 03:00 UTC (Hobby-compatible); configure `CRON_SECRET` in Vercel for the scheduled request.

| Method and route | Access | Request / result |
| --- | --- | --- |
| POST `/admin/schools` | SuperAdmin | `{name, slug, state, isActive}`; 201 with institution ID and fields; duplicate slug 409 |
| POST `/admin/users` | SuperAdmin | Compatibility alias for initial LeadSchoolAdmin provisioning; same request shape as `/admin/lead-administrators` |
| POST `/admin/lead-administrators` | SuperAdmin | `{email, password, firstName, lastName, institutionId}`; creates the institution's one initial LeadSchoolAdmin; conflicts if one already exists |
| PATCH `/admin/users/{id}` | SuperAdmin | `{role, isActive}`; Student/Staff assignments; an existing SchoolAdmin may retain its role while changing activation; cannot promote another account to SchoolAdmin; invalidates refresh token |
| GET `/admin/content/pending` | SuperAdmin | Lists PendingApproval content; optional `institutionId`, `type`, `search`, `page`, `pageSize`; paginated response includes content, event, author, and audience details |
| GET `/admin/administrators` | LeadSchoolAdmin, SuperAdmin | Lists delegated NewsAdmin and CalendarAdmin accounts; LeadSchoolAdmins are limited to their institution; SuperAdmins can filter by `institutionId` |
| GET `/admin/administrators/{id}` | LeadSchoolAdmin, SuperAdmin | Reads a delegated administrator in the caller's permitted institution scope |
| POST `/admin/administrators` | LeadSchoolAdmin, SuperAdmin | Creates a NewsAdmin or CalendarAdmin with `{email, password, firstName, lastName, institutionId, role}`; LeadSchoolAdmins can only assign roles in their institution; password requires at least 12 characters |
| PATCH `/admin/administrators/{id}` | LeadSchoolAdmin, SuperAdmin | Changes a delegated admin's role between NewsAdmin and CalendarAdmin or activates/deactivates the account; invalidates its refresh token |
| GET `/content/pending-approval` | LeadSchoolAdmin, SuperAdmin | Lists pending content; supports optional `institutionId` and `type`; LeadSchoolAdmins are limited to their institution |
| POST `/emergency-alerts` | LeadSchoolAdmin, SuperAdmin | Publishes an urgent announcement to active students and staff in the sender's institution and immediately queues notifications, regardless of announcement/push preferences |
| GET `/admin/platform-settings` | SuperAdmin | Reads platform name, support contacts, maintenance mode, listing fee, invoice days and event payment hold setting; returns defaults until saved |
| PUT `/admin/platform-settings` | SuperAdmin | Saves the same settings; request fields: `platformName`, `supportEmail`, `supportPhone`, `maintenanceMode`, `listingFee`, `invoiceDays`, `holdEventsUntilPayment` |
| POST `/calendar/{id}/submit` | SchoolAdmin, CalendarAdmin, SuperAdmin | Submits a calendar draft for LeadSchoolAdmin review |
| POST `/calendar/{id}/publish` | LeadSchoolAdmin, SuperAdmin | Publishes only a `PendingApproval` calendar |
| POST `/calendar/{id}/reject` | LeadSchoolAdmin, SuperAdmin | Rejects a pending calendar with a required `reason`; the calendar admin can revise and resubmit it |
| GET `/institution/stats` | SchoolAdmin, LeadSchoolAdmin, NewsAdmin, CalendarAdmin, SuperAdmin | Institution-scoped admins see their own institution; SuperAdmins see platform-wide totals |
| POST `/calendar/{id}/media` | SchoolAdmin, CalendarAdmin, SuperAdmin | Uploads/replaces a calendar image using multipart field `file` for Draft or Rejected calendars; institution admins are restricted to their institution |
| GET `/departments?institutionId={id}` | SchoolAdmin, LeadSchoolAdmin, NewsAdmin, CalendarAdmin, SuperAdmin | Lists departments with faculty, institution, programme and academic-level details; institution admins are scoped to their institution |
| GET `/departments/{id}` | SchoolAdmin, LeadSchoolAdmin, NewsAdmin, CalendarAdmin, SuperAdmin | Reads one department in the caller's permitted institution scope |
| GET `/auth/notification-preferences` | SchoolAdmin, LeadSchoolAdmin, NewsAdmin, CalendarAdmin, SuperAdmin | Reads the caller's email/push toggles and summary cadence |
| PUT `/auth/notification-preferences` | SchoolAdmin, LeadSchoolAdmin, NewsAdmin, CalendarAdmin, SuperAdmin | Saves `{emailNotificationsEnabled, pushNotificationsEnabled, summaryCadence}`; cadence is `Immediate`, `Daily`, `Weekly`, or `Never` |
| PUT `/auth/academic-settings` | Authenticated active user | Existing fields plus optional `allCampusFeed` (default false) |
| GET `/auth/profile` | Authenticated active user | Existing profile fields plus `allCampusFeed` |

Administrator passwords require at least 12 characters. School name/state are required and bounded; slugs contain lowercase letters, numbers and single hyphen separators. Initial super-admin creation is an operator command documented in `environments.md`, not an HTTP route. This API cannot create or modify other super administrators.

Authentication validates signature, issuer, audience, expiry and the current user's active status and role. A disabled/deleted account or stale role token receives 401. Authenticated users outside the roles permitted for an admin route receive 403. SchoolAdmins and delegated institution admins see only their institution's statistics and cannot change their assigned institution. SuperAdmins can provision one initial LeadSchoolAdmin per institution through `POST /api/v1/admin/lead-administrators`. LeadSchoolAdmins can create, reassign, activate, and deactivate only NewsAdmin and CalendarAdmin accounts within their institution; they cannot create or promote another LeadSchoolAdmin. New administrator, publication, rejection, and emergency-alert operations create audit records in the same database save; records contain actor, action, target, institution, timestamp and non-secret change details.

Personalized feeds match the user's academic hierarchy; all-campus feeds include published content throughout the same institution. The preference never enables cross-school access. Emergency alerts are persisted as Urgent published announcements and bypass announcement/push preferences for active student and staff recipients in the originating institution. Direct content reads follow the same scope; administrators may review unpublished content only in their own institution and delegated roles only see content types they manage. Foreign or inaccessible content returns 404. Audience creation checks institution/faculty/department/programme/level relationships, rejecting invalid combinations with 400.

SuperAdmin moderation reach and `views` are based on durable unique `ContentViews` records. Student/staff detail requests to `GET /content/{id}` create those records; listing feed items alone does not count a view. `GET /content` returns all statuses in the caller's institution for SchoolAdmins and supports an optional `status` filter, while student/staff feeds remain published-only. Content responses include event, advertisement, image and timestamp fields. The platform stats response currently treats inactive institutions as pending verification because the schema has no separate verification status yet.

NewsAdmins can create and submit news/announcements; only LeadSchoolAdmins can designate an Announcement as Urgent. CalendarAdmins, SchoolAdmins, and SuperAdmins can create and submit calendar drafts; LeadSchoolAdmins approve/reject pending news, announcements, and calendars. LeadSchoolAdmins cannot submit content for their own approval. Calendar create and edit operations save Drafts, and publication is only allowed from PendingApproval by a LeadSchoolAdmin or SuperAdmin. Existing SchoolAdmin content permissions remain available for backward compatibility but do not bypass calendar approval. Calendar image upload/replacement is available at `POST /api/v1/calendar/{id}/media` using multipart field `file` for Draft or Rejected calendars. Calendar states are Draft, PendingApproval, Published, and Rejected. Publishing, media uploads, advertisement moderation, notification delivery and analytics are implemented. Cloud delivery requires the deployment credentials described in environments.md.

Apply the pending EF migrations before deploying. `AddPlatformSettingsAndNotificationPreferences` creates persistent platform settings and adds the super-admin email/push preference fields and summary cadence to user preferences.

HTTP regression tests exercise real JWT authentication with an isolated in-memory database. CI additionally applies migrations to PostgreSQL 17, runs initial administrator bootstrap and checks its repeat refusal, verifies repeatable development seeding, and confirms that staging rejects demo seeding. These checks do not provision or validate deployed cloud environments.

Staging uses a daily Vercel Cron schedule on the Hobby plan. Published content still sends immediately; scheduled retries and usage-counter aggregation may lag by up to a day. Analytics DAU and event totals query durable records directly.
