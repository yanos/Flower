# Repository Guidelines

## Project Structure & Module Organization

Flower is a .NET 10, C# cross-platform music player and self-hosted music server. `Flower/` contains shared Avalonia UI, view models, services, and persistence. Platform launchers live in `Flower.Desktop/`, `Flower.Android/`, and `Flower.iOS/`. `Flower.Core/` holds shared server/client domain code; `Flower.Server/` exposes the web and OpenSubsonic APIs, while `Flower.Web/` contains its browser UI. Tests are in `Flower.Tests/` and `Flower.Server.Tests/`. Keep design records in `docs/` and native audio dependencies under `native/miniaudio/`.

## Build, Test, and Development Commands

- `dotnet build Flower.sln` builds all managed projects.
- `dotnet test Flower.Tests/Flower.Tests.csproj` runs client and shared-library xUnit tests.
- `dotnet test Flower.Server.Tests/Flower.Server.Tests.csproj` runs in-process server/API tests.
- `dotnet run --project Flower.Server` starts the server and hosts its browser UI. Use a scratch data directory for experiments: `--Flower:DataDirectory=/tmp/flower-server`.
- `docker compose up --build` builds and starts the containerized server when working on deployment.

Stop locally started servers when finished: they advertise over mDNS and can interfere with other Flower clients.

## Coding Style & Naming Conventions

Use nullable-enabled C# with file-scoped namespaces and implicit usings. Indent with four spaces; put every `if` body on its own line, even for one statement. Use PascalCase for types, methods, properties, and test classes; camelCase for locals and parameters. Prefer clear, small services and records for immutable contracts. Preserve existing XML/comments where they explain protocol, persistence, or security decisions. Keep platform-specific work in its platform project and maintain cross-platform behavior.

## Testing Guidelines

Add or update xUnit tests alongside behavior changes; name files and classes `*Tests.cs` and use descriptive test method names. Run the relevant test project before handing off a change. Some audio tests require a local VLC installation; use focused filters while iterating, then run the appropriate full suite where the environment supports it. There is no repository-wide coverage threshold configured.

## Commit & Pull Request Guidelines

Use concise, imperative, sentence-case commit subjects, e.g. `Dispose the connection a failing pragma leaves open`. Keep commits focused. Pull requests should explain the user-visible or architectural change, link related issues/plans, list validation performed, and include screenshots for UI changes. Call out configuration, protocol, database, or security-boundary changes explicitly.
