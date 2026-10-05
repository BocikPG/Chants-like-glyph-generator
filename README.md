# GlyphGen - changes bundle

Drop-in files, organised by project (folder = namespace). Copy each folder over the matching
project in your solution; every file here is either new or replaces your copy.

| Folder | What's in it |
|---|---|
| `GlyphGen.Core/Config` | config classes, loader, structure + value validators, JSON converters, schema generator |
| `GlyphGen.Core/Generation` | chain/glyph/language generation, placement, combination, quality gate, spacing |
| `GlyphGen.Core/Rng` | `DeterministicRandom` (comment fixes only) |
| `GlyphGen.Rendering` | `LanguageRenderer` |
| `GlyphGen.Rendering/Curves` | `CurvePlanner`, `GlyphCurvePlanner` |
| `GlyphGen.Cli` | `Program.cs`, sample configs, `config.schema.json` |
| `tests/GlyphGen.Tests` | dependency-free test project (see docs) |
| `docs` | `GlyphGen-Config-Guide.md` - every option, validation rules, tooling |
| `config.schema.json` | also kept at the root: the schema test looks for it above the tests folder |

Things to do on your side:

1. **Optional, for schema hover text** - in the Core `.csproj`:
   `<GenerateDocumentationFile>true</GenerateDocumentationFile>` and `<NoWarn>$(NoWarn);CS1591</NoWarn>`.
2. **Tests** - adjust the two `ProjectReference` paths in `tests/GlyphGen.Tests/GlyphGen.Tests.csproj`, then
   `dotnet run --project tests/GlyphGen.Tests`. The first run records two snapshot files in
   `tests/GlyphGen.Tests/Snapshots/`; commit them - they depend on your own seed code.
3. **Interface changes** to be aware of: `IGlyphGenerator.GenerateGlyph` and `ICombinationResolver.TryCombine`
   gained an optional trailing `int variation = 0`; `RootConfig.Grid` is gone (each language has its own);
   `GlyphUsageTracker` moved to its own file. Anything else implementing those interfaces (mocks) needs updating.
4. Regenerate the schema after adding options: `GlyphGen.Cli --schema config.schema.json`.
