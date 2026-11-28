# Trakt My Year

A web app for exploring a Trakt account's yearly movie and show history. Includes CSV, Simkl CSV,
and [Personal Rewind JSON](personal-rewind/README.md) exports.

## Requirements

- .NET 11 SDK, or Docker
- A Trakt account and a [Trakt API application](https://trakt.tv/oauth/applications)

Set the Trakt application's redirect URI to the same value as `TRAKT_CALLBACK_URL`.

## Configuration

Create a `.env` file in the repository root:

```dotenv
TRAKT_CLIENT_ID=your-trakt-client-id
TRAKT_CALLBACK_URL=http://localhost:5000/api/v1/auth/trakt/callback
TRAKT_TIMEZONE=UTC
```

`TRAKT_CLIENT_ID` and `TRAKT_CALLBACK_URL` are required. The OAuth flow uses PKCE; no client secret
is needed. Optional settings:

- `TRAKT_TIMEZONE` — calendar time zone; defaults to `UTC`.
- `TRAKT_API_BASE_URL` — Trakt API URL; defaults to `https://api.trakt.tv`.
- `TRAKT_ALLOW_INSECURE_LOOPBACK=true` — allows HTTP cookies for localhost testing only. Do not use
  for public access.

Do not commit `.env` files, credentials, or personal exports.

## Run locally

```powershell
dotnet restore TraktMyYear.slnx
dotnet run --project src/TraktMyYear.Api
```

Open <http://localhost:5000/api/v1/auth/trakt/login> to connect Trakt, then visit
<http://localhost:5000/>.

## Run with Docker

The image is available on [Docker Hub](https://hub.docker.com/r/pangspang/trakt-my-year) for `linux/amd64`:

```powershell
docker pull pangspang/trakt-my-year:latest
docker run --detach --name trakt-my-year --publish 127.0.0.1:5000:8080 --env-file .env pangspang/trakt-my-year:latest
```

For plain HTTP localhost use, set `TRAKT_ALLOW_INSECURE_LOOPBACK=true` in `.env`. For HTTPS behind a
TLS-terminating reverse proxy:

- Set `TRAKT_CALLBACK_URL` to the public HTTPS callback URL and register the same URL with Trakt.
- Configure the proxy to send `X-Forwarded-Proto: https`.
- Set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` in the container environment.
- Restrict direct access to the application port so only the trusted proxy can reach it.

Recreate the container after changing environment variables. No image rebuild is needed.
To build the image from source, run `docker build --tag trakt-my-year:local .`.

## Data and API

The app supports multiple independent accounts, but sessions, tokens, and cached reviews are held in
memory. Restarting the app disconnects all accounts and clears cached data.

For HTTP examples, see [TraktMyYear.Api.http](src/TraktMyYear.Api/TraktMyYear.Api.http).

## Tests

```powershell
dotnet test TraktMyYear.slnx
```

## License

[MIT](LICENSE). 

## Trakt notice

Trakt My Year uses Trakt but is not endorsed or certified by Trakt.
