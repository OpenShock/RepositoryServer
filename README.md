# OpenShock Repository Server

[![Documentation](https://img.shields.io/badge/docs-mkdocs-blue.svg)](https://openshock.org)
[![GitHub license](https://img.shields.io/github/license/openshock/repositoryserver.svg)](https://raw.githubusercontent.com/openshock/repositoryserver/master/LICENSE)
[![GitHub Sponsors](https://img.shields.io/badge/GitHub-Sponsors-ff69b4)](https://github.com/sponsors/openshock)
[![Discord](https://img.shields.io/discord/1078124408775901204)](https://discord.gg/openshock)

<table>
  <tr>
    <td>master</td>
    <td><a href="https://github.com/OpenShock/RepositoryServer/actions/workflows/ci-build.yml"><img src="https://github.com/OpenShock/RepositoryServer/actions/workflows/ci-build.yml/badge.svg?branch=master" alt="Build Status" /></a></td>
  </tr>
  <tr>
    <td>develop</td>
    <td><a href="https://github.com/OpenShock/RepositoryServer/actions/workflows/ci-build.yml"><img src="https://github.com/OpenShock/RepositoryServer/actions/workflows/ci-build.yml/badge.svg?branch=develop" alt="Build Status" /></a></td>
  </tr>
</table>

Distribution and ingestion server for OpenShock hub firmware and desktop modules.

It serves:

- Hub OTA updates, so an ESP32 can ask whether a newer build exists for its board and channel
- The board, chip and USB catalog the web flashtool needs before it can flash anything
- The desktop module index and module zip downloads

Publishing is done by GitHub Actions using short-lived OIDC tokens, so there are no long-lived CI
secrets.

### API Documentation

The Scalar API reference is served at `/scalar` on any running instance. It is generated from the
controllers, one document per API version, and covers response shapes, caching rules and the problem
details returned on error.

# Configuration

The server can be configured using the following environment variables. Values may also be supplied
through `appsettings.Custom.json`, user secrets or command line arguments.

| Variable                              | Required | Default value                        | Allowed / Example value                                                                                    |
|---------------------------------------|----------|--------------------------------------|------------------------------------------------------------------------------------------------------------|
| `OPENSHOCK__DB__CONN`                 | x        |                                      | `Host=postgres-server-host;Port=5432;Database=repo-server;Username=openshock;Password=superSecurePassword` |
| `OPENSHOCK__DB__SKIPMIGRATION`        |          | `false`                              | `true`, `false`                                                                                            |
| `OPENSHOCK__DB__DEBUG`                |          | `false`                              | `true`, `false`                                                                                            |
| `OPENSHOCK__GITHUB__CLIENTID`         | x        |                                      | Client ID of the GitHub OAuth app                                                                          |
| `OPENSHOCK__GITHUB__CLIENTSECRET`     | x        |                                      | Client secret of the same app                                                                              |
| `OPENSHOCK__GITHUB__ORGANIZATION`     | x        |                                      | `OpenShock`                                                                                                |
| `OPENSHOCK__GITHUB__TEAM`             | x        |                                      | Team **slug** whose members are admins, e.g. `repo-maintainers`                                            |
| `OPENSHOCK__GITHUB__CALLBACKPATH`     |          | `/auth/callback`                     | Must match the callback URL registered on the OAuth app                                                    |
| `OPENSHOCK__GITHUB__SESSIONLIFETIME`  |          | `08:00:00`                           | How long an admin session lasts before a fresh login                                                       |
| `OPENSHOCK__GITHUB__DATAPROTECTIONKEYPATH` |     |                                      | Shared path for cookie encryption keys, required for more than one replica                                 |
| `OPENSHOCK__CICD__AUDIENCE`           | x        | `openshock-repository-server`        | Audience that publishing workflows request their OIDC token for                                            |
| `OPENSHOCK__FIRMWARE__CDNBASEURL`     | x        | `https://cdn.openshock.app/firmware` | Public base URL firmware artifacts are served from                                                         |
| `OPENSHOCK__FIRMWARE__STORAGE__TYPE`  | x        | `Local`                              | `Local`, `S3`, `BunnyCdn`                                                                                  |
| `OPENSHOCK__FIRMWARE__STAGEDRELEASETTL`  |       | `01:00:00`                           | How long a release may sit in `staging` before it is aborted                                               |
| `OPENSHOCK__FIRMWARE__EDITINGRELEASETTL` |       | `7.00:00:00`                         | How long a release may sit in `editing` before it is aborted                                               |
| `OPENSHOCK__FIRMWARE__VERIFYPUBLISHEDARTIFACTS` |  | `true`                          | Check each artifact is retrievable at `CDNBASEURL` before publishing, and refuse to publish if not          |
| `OPENSHOCK__REPO__ID`                 | x        |                                      | `openshock-desktop-modules`                                                                                |
| `OPENSHOCK__REPO__NAME`               | x        |                                      | `OpenShock Modules`                                                                                        |
| `OPENSHOCK__REPO__AUTHOR`             | x        |                                      | `OpenShock`                                                                                                |
| `OPENSHOCK__REPO__HOMEPAGE`           |          |                                      | `https://openshock.org`                                                                                    |
| `OPENSHOCK__REPO__CDNBASEURL`         | x        |                                      | Public base URL desktop module zips are served from                                                        |
| `OPENSHOCK__METRICS__ALLOWEDNETWORKS` |          | private networks                     | CIDRs allowed to scrape `/metrics`                                                                         |

Refer to the [Npgsql Connection String](https://www.npgsql.org/doc/connection-string-parameters.html)
documentation page for details about `OPENSHOCK__DB__CONN`.

Configuration is validated on startup. If a required value is missing or invalid, the offending
fields are printed and the process exits with code `-10`.

## Storage

Firmware artifacts and module zips are written to one of three backends, selected with
`OPENSHOCK__FIRMWARE__STORAGE__TYPE`. The server only ever writes. Serving the files is the job of
whatever CDN sits in front of the storage, and clients use the absolute URLs returned by the API
instead of building paths themselves.

### Local

Writes to disk. Intended for development.

| Variable                                        | Required | Default value    | Allowed / Example value |
|-------------------------------------------------|----------|------------------|-------------------------|
| `OPENSHOCK__FIRMWARE__STORAGE__LOCAL__BASEPATH` | x        | `./cdn-storage`  | `/var/lib/repo-server`  |

### S3

Works with AWS S3 and any S3-compatible service such as Cloudflare R2 or MinIO.

| Variable                                          | Required | Default value | Allowed / Example value              |
|---------------------------------------------------|----------|---------------|--------------------------------------|
| `OPENSHOCK__FIRMWARE__STORAGE__S3__BUCKETNAME`    | x        |               | `openshock-firmware`                 |
| `OPENSHOCK__FIRMWARE__STORAGE__S3__ACCESSKEY`     | x        |               |                                      |
| `OPENSHOCK__FIRMWARE__STORAGE__S3__SECRETKEY`     | x        |               |                                      |
| `OPENSHOCK__FIRMWARE__STORAGE__S3__SERVICEURL`    |          |               | Set for non-AWS endpoints            |
| `OPENSHOCK__FIRMWARE__STORAGE__S3__REGION`        |          |               | `eu-central-1`, required for AWS S3  |
| `OPENSHOCK__FIRMWARE__STORAGE__S3__KEYPREFIX`     |          |               | `firmware/`                          |

### BunnyCdn

| Variable                                            | Required | Default value | Allowed / Example value              |
|-----------------------------------------------------|----------|---------------|--------------------------------------|
| `OPENSHOCK__FIRMWARE__STORAGE__BUNNYCDN__STORAGEURL` | x       |               | `https://storage.bunnycdn.com/myzone` |
| `OPENSHOCK__FIRMWARE__STORAGE__BUNNYCDN__APIKEY`     | x       |               |                                       |

## Logging

Everything under `Serilog` in `appsettings.json` is passed to Serilog. Console, Grafana Loki and
OpenTelemetry sinks are available. Prometheus metrics are exposed on `/metrics`, restricted to the
networks in `OPENSHOCK__METRICS__ALLOWEDNETWORKS`.

# Authentication

There are two schemes, and they do not overlap.

## Admin, through GitHub

Admin endpoints require a session established by a GitHub OAuth login. There is no static token and
no other credential.

```
GET /auth/login    starts the login, redirects to GitHub
GET /auth/logout   ends the local session
GET /auth/me       reports the current session
```

The login is an authorization code flow. The code is exchanged server-side, so no token reaches the
browser; what the browser holds is an encrypted session cookie. Team membership is checked once at
login against `/orgs/{org}/teams/{team}/memberships/{login}`, using
`OPENSHOCK__GITHUB__ORGANIZATION` and `OPENSHOCK__GITHUB__TEAM`. An account outside the team is
refused at the callback rather than being handed a session that cannot do anything.

Four consequences worth knowing:

- Membership is captured at login, so removing someone from the team takes effect when their session
  expires, not immediately. `SESSIONLIFETIME` bounds that window.
- `TEAM` is the team **slug**, the form that appears in the URL. A team displayed as
  "Repo Maintainers" is `repo-maintainers`; the display name will not resolve.
- Only an `active` membership counts. Someone invited to the team but who has not accepted is
  `pending`, and is refused.
- Logout is local only. GitHub has no front-channel logout for OAuth apps, so the account stays
  signed in to GitHub and a later `/auth/login` will sign it straight back in without a prompt.
  Revoking the grant properly is done from the account's authorized-apps settings.

GitHub setup: create an OAuth app (Settings → Developer settings → OAuth Apps) with authorization
callback URL `https://your-server/auth/callback`. The login requests the `read:org` scope, which is
what makes the team membership endpoint answer; without it GitHub returns 404 for a team the user is
genuinely in, which is indistinguishable from not being a member. If the org enforces OAuth app
access restrictions, the app has to be approved for the org or every membership check comes back
404.

### Admin UI

Administration is the UI at `/admin`. There are no admin endpoints: the pages call the admin services
directly, so there is no second surface to keep in step. The pages render interactively over a
Blazor server circuit, and each carries the admin policy as endpoint metadata, so an unauthenticated
visitor is turned away at the endpoint before any markup is produced.

The sidebar groups pages by the job rather than by URL:

| Group | Page | What it is for |
|-------|------|----------------|
| | `/admin` | Overview: what needs attention, what each channel serves, what is in flight |
| Firmware | `/admin/firmware/releases` | Changelog fixes and unpublishing versions |
| Firmware | `/admin/firmware/advisories` | Warnings on the public manifest |
| Hardware | `/admin/firmware/boards`, `/chips`, `/usb-devices`, `/usb-serial-filters` | Reference data for the catalog |
| Desktop | `/admin/desktop/modules` | Desktop modules, their owners and how many versions each has |
| Desktop | `/admin/desktop/modules/{id}` | One module: its metadata, and the versions it advertises |
| Administration | `/admin/repositories` | Publish allowlist, shared by firmware and desktop |
| Administration | `/admin/discord-webhooks` | Notification targets, shared |
| Administration | `/admin/import` | Seed or update the catalog from one file |
| | `/auth/signed-out` | Where signing out lands, anonymous. The only page that renders without a session |

If a machine-readable admin surface is needed later, it will be a separate automation API with its own
tokens, built on the same services.

## GitHub OIDC

Publishing endpoints take a GitHub Actions OIDC token as a bearer token. The server verifies the
signature against GitHub's JWKS, checks the issuer and the configured audience, then looks up
`(provider, owner, repo)` in the `repositories` table. A workflow from an unregistered repository is
rejected, so registration is the authorization decision, not the token itself.

Registered repositories carry scopes, `publish_firmware` and `publish_modules`. A repository
registered with no scopes authenticates but cannot publish anything. Ownership is additionally
checked per object: a release records the repository that created it, and uploads, publish and abort
each re-check it.

Publishing workflow side:

```yaml
permissions:
  id-token: write

steps:
  - run: |
      TOKEN=$(curl -s -H "Authorization: bearer $ACTIONS_ID_TOKEN_REQUEST_TOKEN" \
        "$ACTIONS_ID_TOKEN_REQUEST_URL&audience=openshock-repository-server" | jq -r .value)
```

# Endpoints

## Firmware (v2)

| Endpoint                                            | Auth  | Purpose                                                            |
|-----------------------------------------------------|-------|--------------------------------------------------------------------|
| `GET /2/firmware/manifest`                          | none  | Channels, latest versions, boards, chips, USB filters, advisories  |
| `GET /2/firmware/latest/{channel}`                  | none  | Newest release on a channel, all boards                            |
| `GET /2/firmware/latest/{channel}/{board}?version=` | none  | Hub OTA check, returns 204 when already current                    |
| `GET /2/firmware/versions`                          | none  | Paginated version history                                          |
| `GET /2/firmware/versions/{version}[/{board}]`      | none  | A specific published version                                       |
| `GET /2/firmware/boards`, `GET /2/firmware/chips`   | none  | Catalog listings                                                   |
| `/2/firmware/releases/...`                          | OIDC  | Release ingestion                                                  |

Channels cascade, `stable` is visible to `beta`, and both are visible to `develop`, so a stable
release is also the newest thing a beta hub should be offered. Boards are addressed by name, the
PlatformIO env name a hub compiles in as `OPENSHOCK_FW_BOARD`. Board UUIDs are still accepted.

## Desktop modules (v1)

| Endpoint                                     | Auth  | Purpose                                     |
|----------------------------------------------|-------|---------------------------------------------|
| `GET /1/`                                    | none  | Repository index with every module version  |
| `PUT /1/cicd/modules/{id}/versions/{version}` | OIDC | Upload a module zip                         |

## Release workflow

Firmware releases are staged, then promoted. Artifacts are uploaded under an internal staging prefix
and copied to their published keys only on publish, so a published key is written exactly once and
aborting a release can never remove an object a live version is serving.

```
POST   /2/firmware/releases[?nofail]              init, parses the changelog markdown
PUT    /2/firmware/releases/{id}/boards/{board}   upload artifacts, multipart, 64 MB per request
POST   /2/firmware/releases/{id}/publish          promote to live
DELETE /2/firmware/releases/{id}                  abort and clean up
```

Publish promotes the staged objects to their published keys, then checks each one is actually
retrievable at the URL it is about to advertise, and only commits if they all are. Uploading to
storage and serving from `CDNBASEURL` are independent facts: an instance writing to a `Local`
backend while advertising a CDN, or to a bucket that CDN is not fronting, uploads every artifact
successfully and would otherwise publish a version whose every download is a 404. Nothing about
that release looks wrong through the API — the hashes and sizes are right — so the client that
finds out is a hub partway through an update. A failed check rolls the promotion back and returns
`Firmware.ArtifactsNotRetrievable`. Set `VERIFYPUBLISHEDARTIFACTS=false` only where the server
genuinely cannot reach its own CDN hostname.

Uploads carry a `sha256` form field mapping each artifact type to its expected hash, and the whole
request is verified before anything is written. If the changelog fails to parse, init returns 400 and
the build fails. With `?nofail` the release is created in `editing` status instead and cannot be
published until a maintainer fixes the notes through the admin endpoint. Releases that outlive their
TTL are aborted by a background job that runs every five minutes.

# Deployment / Self Hosting

The stack is small:

- Postgres 17 as database
- One or multiple repository servers (container, `ghcr.io/openshock/repository-server`)
- Object storage or a CDN serving the artifacts the server writes

Build the image yourself with:

```bash
docker build -f docker/RepositoryServer.Dockerfile -t repository-server .
```

The entrypoint generates a self-signed certificate at `/defaultcert.pfx` if none is mounted, and the
container configuration binds Kestrel to `https://*:443`. Database migrations are applied on startup
unless `OPENSHOCK__DB__SKIPMIGRATION` is set, so do not run them by hand.

## Seeding a fresh instance

A new database has no chips and no boards, and release init rejects a board it does not know, so the
first CI publish will fail until the catalog exists.

The quickest way to get there is `/admin/import`, which takes a JSON file describing the whole
catalog and shows what it would create or change before writing anything. Comments and trailing
commas are accepted, so a catalog kept in git can carry its own notes; `seed/openshock-catalog.jsonc`
is the OpenShock one. Rows reference each other by name rather than by id — a board names its chip,
a module names its publisher as `owner/repo` — so the same file applies to a fresh instance and an
existing one, and re-importing it reports everything unchanged rather than duplicating it.

Import never deletes: a row dropped from the file stays on the server, because removing a chip a
board still references, or a board that published artifacts still point at, wants the individual
pages that can name what holds the reference. Publisher scopes are the exception to "additive" —
they are replaced, so the file is the whole grant for a repository it lists.

The same three things can be created by hand instead, in this order, by an administrator signed in
at `/auth/login`:

1. **Chips**, at `/admin/firmware/chips`. Names must match esptool-js chip identifiers exactly
   (`ESP32`, `ESP32-S2`, `ESP32-S3`, `ESP32-C3`), since the web flashtool passes them straight
   through. Architecture is `xtensa` for the ESP32/S2/S3 family and `riscv` for the C3.
2. **Boards**, at `/admin/firmware/boards`, each referencing a chip. A board's name has to match the
   board name in the firmware repository exactly — its `boards/<name>.defaults` basename — because
   that is what a hub compiles in as its board and reports as its own. Required artifacts are
   normally app and staticfs, the two an OTA update must supply.
3. **Publishers**, at `/admin/repositories`, one per repository allowed to publish, with the scopes it
   is granted. A repository registered with no scopes cannot publish anything.

`/admin` shows whether any of these are still missing.

# Development

Requires the .NET SDK version pinned in `global.json` and a Postgres instance. Two commands:

```bash
docker run -d --name repo-server-pg -p 5433:5432 \
  -e POSTGRES_DB=repo-server -e POSTGRES_USER=openshock -e POSTGRES_PASSWORD=openshock \
  postgres:17-alpine

dotnet run --project RepositoryServer
```

The Development profile listens on `http://localhost:5080`, applies migrations on startup, and prints
a banner confirming the login bypass. Port 5433 keeps this out of the way of a local OpenShock API
stack, which uses 5432.

| URL | What it is |
|-----|------------|
| `http://localhost:5080/admin` | Admin UI |
| `http://localhost:5080/auth/me` | Current session, useful for checking the bypass took |
| `http://localhost:5080/scalar` | API reference |

## Login bypass

`appsettings.Development.json` sets `DevAuth:BypassLogin`, which replaces the GitHub login with a
handler that treats every caller as an administrator, so local work needs no OAuth app at all. Set it
to `false` to exercise the real login against GitHub.

Three separate things have to line up for it to do anything:

1. The code is inside `#if DEBUG`, so a Release build does not contain the handler
2. The environment must be `Development`
3. The flag must be set

The published image is built with `-c Release`, so the bypass is not in it. That is a stronger
guarantee than a runtime check on an environment variable somebody could set by mistake.

## Tests

```bash
dotnet test --project RepositoryServer.Tests/RepositoryServer.Tests.csproj
```

TUnit on the Microsoft Testing Platform. The integration suite starts a real Postgres container
through Testcontainers and applies the actual migrations against it, so Docker has to be available.

## Migrations

Migrations live in `RepositoryServer/Migrations` and target `MigrationOpenShockContext`. The
design-time factory connects to `Host=localhost;Database=repo-server;Username=openshock;Password=openshock`.

```bash
dotnet ef migrations add MyMigration --project RepositoryServer
```

## Support development!

You can support the OpenShock Dev Team here: [Sponsor OpenShock](https://github.com/sponsors/OpenShock)
