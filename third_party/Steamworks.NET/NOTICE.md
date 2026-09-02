# Steamworks.NET binding

- Upstream: https://github.com/rlabrecque/Steamworks.NET
- Pinned commit: `fecff53` (`Update to Steamworks 1.65`)
- Upstream SDK compatibility: Steamworks SDK 1.65
- Local native SDK used by this project: Steamworks SDK 1.65
- Build: `Standalone2.0/Steamworks.NET.Standard.csproj`, configuration `Windows`, platform `x64`,
  managed `PlatformTarget=AnyCPU` (native calls remain Windows x64 via `STEAMWORKS_X64`)

`Steamworks.NET.dll` is redistributed under the MIT license in `LICENSE.txt`.
`steam_api64.dll` remains Valve's Steamworks redistributable and must be refreshed from
the configured official SDK when the SDK version changes.
