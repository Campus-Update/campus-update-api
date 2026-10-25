# Stage 3 frontend integration

All paths below include `/api/v1`. Use the team's staging or production API base URL. Every operation requires a bearer access token unless described as a scheduler operation.

## Audience selection

Each object in `audiences` on content creation/update accepts `targetAudience`: `All`, `Students`, or `Staff`. Omission defaults to `All` for compatibility. Combine it with institution, faculty, department, programme and academic-level IDs. All-campus feed mode broadens academic selections within the same institution; it does not bypass the student/staff audience.

Example audience:

```json
{"institutionId":"<institution UUID>","facultyId":null,"departmentId":null,"programmeId":null,"academicLevelId":null,"targetAudience":"Students"}
```

SchoolAdmins can submit a draft for moderation with `PATCH /api/v1/content/{id}/status` and `{"status":"PendingApproval"}`. SuperAdmins approve or reject pending content with the same route and `{"status":"Published"}` or `{"status":"Rejected"}`. Feed search uses `search`. Content create/update accepts an optional `category` string (up to 100 characters); feed `category` filters by that exact value. The independent `type` filter accepts `News`, `Announcement`, `Event`, or `Advertisement`.

`GET /api/v1/content` returns only Published items, including for SuperAdmins; it is the same published feed query as `/api/v1/feed`. SuperAdmins can list items awaiting review with `GET /api/v1/admin/content/pending`. Optional query parameters are `institutionId`, `type` (`News`, `Announcement`, `Event`, `Advertisement`), `search`, `page` (default 1), and `pageSize` (default 20, maximum 100). The response is `{page, pageSize, totalCount, items}`. Each item includes content and event fields, author details, and audience IDs. This endpoint only returns `PendingApproval` records. Approve or reject a returned item with `PATCH /api/v1/content/{id}/status` and `{"status":"Published"}` or `{"status":"Rejected"}`. The endpoint is restricted to SuperAdmins; other roles receive 403.

Example moderation-list response:

```json
{
  "page": 1,
  "pageSize": 20,
  "totalCount": 1,
  "items": [
    {
      "id": "<content UUID>",
      "title": "Orientation update",
      "body": "Details for reviewers",
      "summary": null,
      "category": "Campus",
      "imageUrl": null,
      "type": "Event",
      "status": "PendingApproval",
      "urgency": "Normal",
      "sourceType": "Official",
      "sourceName": "Osun State University",
      "createdAt": "2026-10-13T10:00:00Z",
      "eventStartsAt": "2026-10-20T09:00:00Z",
      "eventEndsAt": null,
      "eventLocation": "Main auditorium",
      "registrationUrl": null,
      "sponsorName": null,
      "targetUrl": null,
      "authorId": "<user UUID>",
      "authorEmail": "admin@example.test",
      "authorName": "Campus Admin",
      "audiences": [
        {
          "institutionId": "<institution UUID>",
          "facultyId": null,
          "departmentId": null,
          "programmeId": null,
          "academicLevelId": null,
          "targetAudience": "All"
        }
      ]
    }
  ]
}
```

## Calendar images

`POST /api/v1/calendar/upload` accepts multipart/form-data fields `institutionId`, `title`, `academicSession`, `file`, optional `status` (`Draft` or `Published`, defaults to `Published`), optional `publishedAt`, and optional `calendarId` to replace an existing calendar. Only SchoolAdmin/SuperAdmin accounts can upload. Images must be valid PNG, JPEG or WebP, at most 5 MB and 40 megapixels, with dimensions at most 20,000 pixels. The server compresses the image to WebP and removes image metadata. A Draft is saved without changing the current published calendar; a Published upload replaces the institution's current calendar after successful storage. Drafts may omit an image, but an image is required before publishing.

Upload or replace the image on an existing calendar with `POST /api/v1/calendar/{calendarId}/media`, using multipart/form-data field `file`. The endpoint returns the updated calendar with a signed image URL and does not change its status or publication state. SchoolAdmins can upload only to calendars belonging to their institution; SuperAdmins can upload to any institution.

School Admins can list calendars for their institution with `GET /api/v1/calendar/manage?status=Draft` (omit `status` to include drafts and published history). SuperAdmins must pass `institutionId`; they may also filter by `status`. Create or update calendar records through `POST /api/v1/calendar` and `PUT /api/v1/calendar/{id}` with `status` set to `Draft` or `Published`. Drafts can be deleted with `DELETE /api/v1/calendar/{id}`; published calendars cannot be deleted through this endpoint. Publish a saved draft with `POST /api/v1/calendar/{id}/publish`. Publishing makes it the institution's current official calendar and removes the official flag from the previous one. Drafts never appear in the mobile-facing calendar endpoints.

`GET /api/v1/calendar/current` returns `id`, `institutionId`, `title`, `academicSession`, `imageUrl`, `publishedAt`, `status`, and `isOfficial`. The existing `/calendar` and `/calendar/latest` aliases remain available and return only the current official Published calendar. Bucket images use a signed URL valid for 15 minutes; fetch the endpoint again to refresh it. A missing calendar returns HTTP 404 with a ProblemDetails title and description suitable for an empty state. A SchoolAdmin accessing or uploading for a different institution receives 403.

Content create and update requests do not accept `imageUrl`; upload content images separately with `POST /api/v1/media/content/{contentId}` and multipart/form-data field `file`.

## Devices and notification receipts

Register after login using `POST /api/v1/devices` with `{"token":"<FCM registration token>","platform":"Android"}` (`Ios` and `Web` are also supported). Re-register when the token changes. Deactivate the returned device ID on logout with `DELETE /api/v1/devices/{id}`.

Read the inbox through `GET /api/v1/notifications?page=1&pageSize=30`. FCM data includes `notificationId`, `contentId`, and `type`. Send `POST /api/v1/notifications/{notificationId}/ack` with `{"state":"delivered"}` when the app receives the message, and `{"state":"opened"}` when the user opens it. These operations are idempotent and restricted to the notification owner. Opening also marks the notification read and confirms receipt. FCM send acceptance is recorded as `Sent`; `deliveredAt` is set only by a client acknowledgement. Push retries are tracked per device.

`PATCH /api/v1/notifications/{id}/read` marks an inbox notification read. Delivery and open totals depend on mobile/web clients implementing acknowledgements.

## Analytics

SuperAdmins use `GET /api/v1/admin/analytics?days=30` (1?365). `totals` includes `totalRegisteredUsers`, `dau`, `activePosts`, `activeUsers`, `newUsers`, `loginEvents`, `publishedContent`, `uniqueContentViews`, `notifications`, `notificationsDelivered`, `notificationsOpened`, and `pushFailures`. `institutions` groups user counts, DAU, active posts, login events and unique content views by school.

DAU counts distinct users with registration/login/refresh/feed activity since midnight UTC. `activeUsers` counts enabled accounts and is distinct from DAU. `activePosts` counts all currently Published content; `publishedContent` counts publications in the requested period. Views count each authenticated student/staff user once per content item, including concurrent requests.

## SuperAdmin portal data

All routes below require a SuperAdmin bearer token. Lists use `{page, pageSize, totalCount, items}` with a default page size of 20 and a maximum of 100.

`GET /api/v1/admin/schools?search=osun&isActive=true&page=1&pageSize=20` lists active or inactive institutions. Each item includes school identity/status and `studentCount`, `staffCount`, `adminCount` (SchoolAdmin accounts), and `liveContentCount` (distinct published items targeted to the institution). Counts include inactive user accounts; use the user list's `isActive` filter for an active-only breakdown. `GET /api/v1/admin/schools/{id}` returns the same summary plus the school's faculty, department, programme and academic-level hierarchy. Missing school IDs return 404.

`GET /api/v1/admin/users?institutionId=<UUID>&role=Student&isActive=true&search=ada&page=1&pageSize=20` lists users with their role, institution name/ID, activation state, creation time and `lastActiveAt`. Filters are optional. `lastActiveAt` is the latest recorded authentication/feed activity or first content-detail view; it is null if the account has no recorded activity. `PATCH /api/v1/admin/users/{id}` remains the access-update operation.

`GET /api/v1/admin/audit?institutionId=<UUID>&actorId=<UUID>&targetId=<UUID>&action=Content&from=<ISO-8601>&to=<ISO-8601>&page=1&pageSize=20` returns newest-first entries with actor ID/name/email, action, target ID/type, institution, details and timestamp. `from` is inclusive and `to` is exclusive. The audit log currently records school creation, school-admin provisioning, account role/activation changes, and content creation, editing, status changes and archiving.

Content responses include `uniqueReachCount` and `notificationOpenCount`. `uniqueReachCount` counts distinct student/staff accounts with a recorded content-detail view; each account is counted once per content item. `notificationOpenCount` counts opened notification records for that content. These are separate signals: feed-card appearances are not tracked as impressions, and opening a push notification does not by itself mean the user opened the content detail page.

Auth and feed operations save durable activity events asynchronously. The scheduled job aggregates those events into `usageCounters` (registrations, session activity events, and feed fetches by day/institution). Counters can lag by one scheduler interval. A session activity event is a login, refresh or feed fetch, not a distinct person; DAU is the distinct-person measure.

## SuperAdmin frontend follow-up

The portal data routes provide the fields needed by the newer admin screens:

- `GET /api/v1/admin/schools` returns school status and the existing `studentCount`, `staffCount`, `adminCount`, and `liveContentCount`, plus `weeklyActiveCount` (distinct users active during the previous seven days) and `viewCount` (recorded unique content-detail views for the institution).
- `GET /api/v1/admin/users` returns name, email, role, institution, `isActive`, and `lastActiveAt`, with paging and filters for institution, role, status, and text search.
- `GET /api/v1/admin/audit` supports `search`, `administratorId` (also `actorId`), `action`, `actionTag`, `institutionId`, `from`, `to`, and paging. Rows include actor name/email/role, action text and tag, target, institution ID/name, details, and timestamp. Date `from` is inclusive and `to` is exclusive.
- Content responses expose `views`, `uniqueReachCount`, and `notificationOpenCount`. `views` currently counts unique content-detail views, the same measure as `uniqueReachCount`; feed impressions are not collected.

Authenticated users can change passwords with `POST /api/v1/auth/change-password` and `{ "currentPassword": "...", "newPassword": "..." }`. Success returns 204 and invalidates the refresh token. `GET /api/v1/auth/sessions`, `DELETE /api/v1/auth/sessions/{id}`, and `DELETE /api/v1/auth/sessions/others` support the Security screen. The current token model permits one refreshable session per user, so there are no concurrent refresh sessions to revoke; existing access tokens expire normally. Device, IP, and browser metadata are not recorded.

The optional browser-only platform settings and notification preference APIs are not part of this implementation. To add them later, the platform settings need persistent storage, and notification email/cadence preferences need corresponding user preference fields.

## Deployment and verification

Apply the EF migrations before deployment. Set `Fcm__ServiceAccountJson` and `CRON_SECRET` in each deployment. The existing authenticated scheduler `/api/v1/internal/notifications/retry` also aggregates usage events daily at approximately 03:00 UTC (Hobby-compatible). Background aggregation uses database transactions and resumes from saved events after interruption.

Development mock data is created using the Development-only `--seed-development` command with a `Seed__Password` of at least 12 characters. It includes a synthetic Computing faculty, Computer Science department/programme, 100?400 levels, role-specific accounts, sample news, an urgent announcement, an event, a sponsored advertisement and a calendar record. The development calendar uses a generated PNG served by the Development-only `/api/v1/mock/calendar.png` endpoint; production and staging do not expose this mock route. Upload a real calendar image to test bucket storage. Re-running the seed does not duplicate the fixture. The sample source labels are Official School, CAMPUS UPDATE and Sponsored.

Build and HTTP regression checks validate the local implementation. Live Firebase delivery, Backblaze uploads and the scheduler must still be verified in the deployed environments.

Staging uses a daily Vercel Cron schedule on the Hobby plan. Published content still sends immediately; scheduled retries and usage-counter aggregation may lag by up to a day. Analytics DAU and event totals query durable records directly.
