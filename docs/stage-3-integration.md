# Stage 3 frontend integration

All paths below include `/api/v1`. Use the team's staging or production API base URL. Every operation requires a bearer access token unless described as a scheduler operation.

## Audience selection

Each object in `audiences` on content creation/update accepts `targetAudience`: `All`, `Students`, or `Staff`. Omission defaults to `All` for compatibility. Combine it with institution, faculty, department, programme and academic-level IDs. All-campus feed mode broadens academic selections within the same institution; it does not bypass the student/staff audience.

Example audience:

```json
{"institutionId":"<institution UUID>","facultyId":null,"departmentId":null,"programmeId":null,"academicLevelId":null,"targetAudience":"Students"}
```

SchoolAdmins can publish for their institution; SuperAdmins can publish across institutions. Publish a saved draft with `PATCH /api/v1/content/{id}/status` and `{"status":"Published"}`. Feed search uses `search`. Content create/update accepts an optional `category` string (up to 100 characters); feed `category` filters by that exact value. The independent `type` filter accepts `News`, `Announcement`, `Event`, or `Advertisement`.

## Calendar images

`POST /api/v1/calendar/upload` accepts multipart/form-data fields `institutionId`, `title`, `academicSession`, and `file`. Only SchoolAdmin/SuperAdmin accounts can upload. Images must be valid PNG, JPEG or WebP, at most 5 MB and 40 megapixels, with dimensions at most 20,000 pixels. The server compresses the image to WebP, removes image metadata, and replaces the institution's current calendar after successful storage.

`GET /api/v1/calendar/current` returns `id`, `institutionId`, `title`, `academicSession`, `imageUrl`, and `publishedAt`. The existing `/calendar` and `/calendar/latest` aliases remain available. Bucket images use a signed URL valid for 15 minutes; fetch the endpoint again to refresh it. A missing calendar returns HTTP 404 with a ProblemDetails title and description suitable for an empty state. A SchoolAdmin uploading for a different institution receives 403.

## Devices and notification receipts

Register after login using `POST /api/v1/devices` with `{"token":"<FCM registration token>","platform":"Android"}` (`Ios` and `Web` are also supported). Re-register when the token changes. Deactivate the returned device ID on logout with `DELETE /api/v1/devices/{id}`.

Read the inbox through `GET /api/v1/notifications?page=1&pageSize=30`. FCM data includes `notificationId`, `contentId`, and `type`. Send `POST /api/v1/notifications/{notificationId}/ack` with `{"state":"delivered"}` when the app receives the message, and `{"state":"opened"}` when the user opens it. These operations are idempotent and restricted to the notification owner. Opening also marks the notification read and confirms receipt. FCM send acceptance is recorded as `Sent`; `deliveredAt` is set only by a client acknowledgement. Push retries are tracked per device.

`PATCH /api/v1/notifications/{id}/read` marks an inbox notification read. Delivery and open totals depend on mobile/web clients implementing acknowledgements.

## Analytics

SuperAdmins use `GET /api/v1/admin/analytics?days=30` (1?365). `totals` includes `totalRegisteredUsers`, `dau`, `activePosts`, `activeUsers`, `newUsers`, `loginEvents`, `publishedContent`, `uniqueContentViews`, `notifications`, `notificationsDelivered`, `notificationsOpened`, and `pushFailures`. `institutions` groups user counts, DAU, active posts, login events and unique content views by school.

DAU counts distinct users with registration/login/refresh/feed activity since midnight UTC. `activeUsers` counts enabled accounts and is distinct from DAU. `activePosts` counts all currently Published content; `publishedContent` counts publications in the requested period. Views count each authenticated student/staff user once per content item, including concurrent requests.

Auth and feed operations save durable activity events asynchronously. The scheduled job aggregates those events into `usageCounters` (registrations, session activity events, and feed fetches by day/institution). Counters can lag by one scheduler interval. A session activity event is a login, refresh or feed fetch, not a distinct person; DAU is the distinct-person measure.

## Deployment and verification

Apply the EF migrations before deployment. Set `Fcm__ServiceAccountJson` and `CRON_SECRET` in each deployment. The existing authenticated scheduler `/api/v1/internal/notifications/retry` also aggregates usage events every five minutes. Background aggregation uses database transactions and resumes from saved events after interruption.

Development mock data is created using the Development-only `--seed-development` command with a `Seed__Password` of at least 12 characters. It includes a synthetic Computing faculty, Computer Science department/programme, 100?400 levels, role-specific accounts, sample news, an urgent announcement, an event, a sponsored advertisement and a calendar record. The development calendar uses a generated PNG served by the Development-only `/api/v1/mock/calendar.png` endpoint; production and staging do not expose this mock route. Upload a real calendar image to test bucket storage. Re-running the seed does not duplicate the fixture. The sample source labels are Official School, CAMPUS UPDATE and Sponsored.

Build and HTTP regression checks validate the local implementation. Live Firebase delivery, Backblaze uploads and the scheduler must still be verified in the deployed environments.
