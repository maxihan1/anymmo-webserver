# Introduction

AccountManager is a .net core 8 web application designed to run on docker for portability.
It allows users to go to a web site and create accounts that can be used to log into an AnyMMO network server.

# Project Organization

The project has 2 main parts: a dockerfile, and a folder that contains the .net core 8 web app,


# Requirements

1. A MySQL database.  The schema must already be created and the user account created with appropriate permissions assigned to the schema.

2. Docker.  Ensure the docker daemon or service is running.

# Application Setup

To install or run the application, the database must be configured.  Running the entity framework migration tool as described below will create and configure the database tables.

## Add database connection string to appsettings

Copy appsettings.json to appsettings.Development.json and add a connection string, using the proper values for your database.

**Note:** do not use localhost for the database server hostname.  If you do, the docker based web site will assume the database is running in the same container and fail to connect.

```
"ConnectionStrings": {
    "Db": "server=mysql.yourdomain.com;database=your_database_name;user=your_database_user_name;password=your_database_password"
  },
```

## Update database tables

In Visual Studio, right click the solution name in the solution explorer and choose *Open In Terminal*

`dotnet tool install --global dotnet-ef --version 8.*`

Replace the word Initial with a database commit message

`dotnet ef migrations add Initial`

Look at the new file in the Migrations folder in visual studio and check the code that will be run.  If it looks good, then run the update.

`dotnet ef database update`

## Docker Setup

Build the image from the project root folder

`docker build -t anymmo-webserver .`

Run the container

`docker run -p 8080:8080 anymmo-webserver`

Or as a daemon

`docker run -d -p 8080:8080 anymmo-webserver`

## Nginx Setup

If you are running the service locally, you will need to run the nginx container with the self signed certificate, as Unity will not allow insecure (http) connections by default.

Build the image from the project root folder

`docker build -t anymmo-nginx anymmo-nginx`

Run both the webserver and the nginx containers at the same time

`docker-compose up`

or as a daemon

`docker-compose up -d`
# Operator API (ops tool)

Added in this fork for a game operations tool (`maxihan1/anyrpg-ops`). Operators are stored in their own table and sign in with their own JWT key, so a game token cannot call `/opsapi` and an operator token cannot call `/api`.

## Settings

| Key | Meaning |
|---|---|
| `AnyMMOOpsSettings__OperatorBearerKey` | Signing key for operator tokens. 32+ characters, must differ from `AnyMMOWebServerSettings__BearerKey` (the server refuses to start otherwise) |
| `AnyMMOOpsSettings__SeedOperatorUserName` / `__SeedOperatorPassword` | Creates this operator at startup if it does not exist |
| `AnyMMOOpsSettings__BaseCurrencyName` | Currency name used for mailed currency (default `Gold`) — must match the game's `DefaultCurrencyGroup.BaseCurrency` |

Migrations are applied automatically at startup.

## Endpoints (`/opsapi`, operator bearer token)

| Method | Path | What |
|---|---|---|
| POST | `/opsapi/login` | `{userName, password}` → `{token, operator}` |
| GET | `/opsapi/summary` | counts for the dashboard |
| GET | `/opsapi/users?q=&page=&size=` · `/opsapi/users/{id}` | account search (name, email, id, character name) · detail with characters and sanction history |
| POST | `/opsapi/users/{id}/sanctions` · `/opsapi/sanctions/{id}/release` | `SUSPEND` (login blocked) · `CHAT_MUTE` (read by the game server) · release with reason |
| GET | `/opsapi/characters?q=` | character search with level / class / items parsed from save data |
| GET · POST | `/opsapi/grants` · `/opsapi/grants/{id}/recall` | mail items / currency to characters · recall unclaimed attachments |
| GET | `/opsapi/items/known` | item names seen in item instances (hint list) |
| GET · POST · PUT · DELETE | `/opsapi/notices[/{id}]` | notices |
| GET · PUT | `/opsapi/maintenance` | maintenance switch, message, window, allowed account ids |
| GET | `/opsapi/logs/audit` · `/opsapi/logs/items` | operator actions · item flow (granted / taken / recalled) |

Errors are `{code, message}` with an HTTP status.

## Endpoints for the game

| Method | Path | Auth | What |
|---|---|---|---|
| GET | `/api/serverstatus` | none | maintenance state and notices showing now |
| POST | `/api/getaccountrestrictions` | server token | `{accountId}` → suspension, chat mute, maintenance block |
| POST | `/api/login` | — | now returns `403 {code:"SUSPENDED"}` or `503 {code:"MAINTENANCE"}` after a correct password |

Note for the game server: mailed items are new rows in `ItemInstances` (generator id 1023 in the IdGen layout). A game server that loaded all item instances at startup must load missing ones before `TakeAttachment`, otherwise the attachment is skipped.

## Tests

`dotnet test app/AnyMMOWebServer/AnyMMOWebServer.Tests` (xUnit, EF Core in-memory, `WebApplicationFactory`).
