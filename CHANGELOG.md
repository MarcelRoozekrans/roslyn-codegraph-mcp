# Changelog

## [1.3.0](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.2.1...v1.3.0) (2026-04-27)


### Features

* add find_tests_for_symbol MCP tool (xUnit/NUnit/MSTest) ([c5252a2](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/c5252a2f7940b072493e17d64946938f4b5792d9))
* add FindTestsForSymbolLogic (direct mode) ([1f8cf9a](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/1f8cf9ad03b56161a3f78528a910fcf988f775a8))
* add TestAttributeRecognizer for xUnit/NUnit/MSTest ([d65a763](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/d65a76384d5b2847c8e2bbefa4fd69996820ff73))
* add TestProjectDetector via package-ref pattern scan ([4199cf6](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/4199cf685386c2f28e49dc4fc02be66b7c19ba25))
* add transitive mode to FindTestsForSymbolLogic ([41db467](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/41db467c6c166447235237bb14d648aa5c66b400))
* register find_tests_for_symbol MCP tool ([e15213d](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/e15213d7f9ad1a5ae6a36241478ef2ffa4bc8de7))

## [1.2.1](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.2.0...v1.2.1) (2026-04-26)


### Bug Fixes

* enable manifest mode for release-please and sync version files to 1.2.0 ([03d3a8f](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/03d3a8f96d32117eee29f826ed3949cfaf31a6f1))
* enable release-please manifest mode + sync version files to 1.2.0 ([b159d05](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/b159d0517c4ffa60042575d6c761e315e0650eed))

## [1.2.0](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.1.7...v1.2.0) (2026-04-25)


### Features

* external-assembly analysis — Phase 1 (Tier 1 + inspect_external_assembly) ([#96](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/96)) ([52b229b](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/52b229baf9e1585d61d2903c3f8b4ceca713b19f))
* external-assembly analysis — Phase 2 (Tier-2 references) ([#98](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/98)) ([a078981](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/a07898114d8e65dd91f312a04de9bccf17d8c215))
* external-assembly analysis — Phase 3 (peek_il) ([#100](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/100)) ([175d9aa](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/175d9aa85ea1f0c352f06d6114a511490051fcaa))


### Performance Improvements

* fix find_callers and find_attribute_usages regressions, add missing benchmarks ([8422cb9](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/8422cb91eb977fd63b4574a54110fd984e597a5f))
* skip FindImplementationForInterfaceMember when method names differ ([7d137a3](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/7d137a34a9cf8ad1b8e7046846800a6a903b5b3e))
* use default ToDisplayString() in FindAttributeUsagesLogic.BuildResults ([f64dee4](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/f64dee4513009a3162af546c05aa4ef6591cc8d3))

## [1.1.7](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.1.6...v1.1.7) (2026-03-30)


### Bug Fixes

* set server.json version to match release-please manifest ([6f35202](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/6f35202b7f2e7d37fb1e10f387239b8f91e0aaa6))
* set server.json version to match release-please manifest ([7898409](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/789840973ed3de57fe12e6be2a313b0f99f4b5da))

## [1.1.6](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.1.5...v1.1.6) (2026-03-30)


### Bug Fixes

* reset server.json version for release-please management ([0dad708](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/0dad708f1efd42f9f5e78f3b41c9f1827b1a81b6))
* sync server.json version with latest NuGet release ([55b80a8](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/55b80a85982ee13e8a86ceb447d327515d0acd6a))

## [1.1.5](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.1.4...v1.1.5) (2026-03-29)


### Bug Fixes

* include server.json in release-please version bumps ([3812bb1](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/3812bb1c2eef691423c258bd5bb6de112c050f0c))
* include server.json in release-please version bumps ([5598f53](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/5598f5394a4fa7512f2a2303f63b7181bc20eb97))

## [1.1.4](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.1.3...v1.1.4) (2026-03-29)


### Bug Fixes

* restore test fixtures in release workflows ([6924b03](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/6924b0355eefde8f79ffc635e94aafd24f5a1bf0))
* restore test fixtures in release workflows ([ba8e53c](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/ba8e53c0e3ea294909a0533597bff23be00bf5e8))

## [1.1.3](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.1.2...v1.1.3) (2026-03-29)


### Bug Fixes

* add missing DI package reference to test fixture TestLib2 ([33f9279](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/33f92793632257e9648dbd182bc9511f1ca86671))
* add missing DI package reference to test fixture TestLib2 ([9940c1c](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/9940c1c175f685a0b2aa94603947abfd4b23ef3c))

## [1.1.2](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.1.1...v1.1.2) (2026-03-28)


### Bug Fixes

* add mcp-name to README for MCP registry ownership verification ([679c4e6](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/679c4e69f5911af5c5ce547acb4c9a3eebccf38e))
* add mcp-name to README for MCP registry ownership verification ([cb4acdc](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/cb4acdcf296d3f1a0c433e9e7583092c1ede372d))

## [1.1.1](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.1.0...v1.1.1) (2026-03-28)


### Bug Fixes

* correct MCP registry name casing ([7770bbe](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/7770bbe191ce7a30edf17a5055d36b9c1b510d5a))
* correct MCP registry name casing ([ad84450](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/ad844503043fda4a77af9b92c4eab61e3412f28e))

## [1.1.0](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.0.15...v1.1.0) (2026-03-24)


### Features

* add load_solution and unload_solution (fixed) ([745925f](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/745925f10ba251663578e52a490f79fbe190daae))


### Bug Fixes

* address PR [#37](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/37) review feedback for load/unload solution ([cfd0039](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/cfd0039e3f8bc7a916db6161ee6cdbe7040fb02b))
* correct dotnet-version format (10.0.x not net10.0) ([0bb9c27](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/0bb9c27452c2e6c7450b48f1d4654e2cd4db7a96))
* restore test fixture solution before build in CI ([8b155d6](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/8b155d65251ab48f2a5ae36656ed2402c9d6d03e))
