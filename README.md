# LogPulse

[![CI](https://github.com/Samot2003/logpulse/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Samot2003/logpulse/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Self-hosted server monitoring in C#/.NET 8: a lightweight agent collects metrics and logs, an ASP.NET Core API stores them in SQL Server, and a real-time dashboard shows them.

> Work in progress. See [docs/ROADMAP.md](docs/ROADMAP.md) for what is done and what comes next.

## Build and test

Requires the .NET 8 SDK. Integration tests also need Docker (they start SQL Server 2022 with Testcontainers).

```bash
dotnet restore LogPulse.sln --locked-mode
dotnet build LogPulse.sln
dotnet test LogPulse.sln --filter "Category!=Integration"   # unit tests
dotnet test LogPulse.sln --filter "Category=Integration"    # integration tests (Docker)
```

## License

[MIT](LICENSE)
