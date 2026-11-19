# Source-linked wireless diagnostics tests

This fixture compiles the production RF hub/transport/protocol against explicit fake native collaborators. It starts no Nexus host, provider, discovery worker or device session. `SLV3_FAKE_NATIVE` enables the source-owned test cases; never use this fixture as a production assembly.

From the `nexus-service` checkout, with its documented .NET SDK/dependencies:

```powershell
dotnet test tests/Nexus.Service.Slv3Diagnostics.Tests/Nexus.Service.Slv3Diagnostics.Tests.csproj -c Release
```

For coverage, pass `--settings tests/Nexus.Service.Slv3Diagnostics.Tests/coverage.runsettings --collect "XPlat Code Coverage"`. Keep output isolated with `--artifacts-path` or explicit per-project output/intermediate paths when other builds are active. Tests use fake packets, private configuration and no live USB calls. Native device behavior, packet freshness and the vendor busy-status contract remain separate acceptance questions.
