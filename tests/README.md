# Pure networking regression tests

These tests exercise synchronization primitives that do not depend on ONI or Unity assemblies. They are intentionally isolated from the repository-wide `Directory.Build.props` and `Directory.Build.targets`, so they can run before the licensed game assemblies are installed.

Per this fork's build policy, run them only on 8ka:

```bash
dotnet run --project tests/ONI_Together.Core.Tests/ONI_Together.Core.Tests.csproj
```

A zero exit code and the final `RESULT ... 0 failed` line are both required. These tests do not replace the full U59 build or dual-instance in-game verification.
