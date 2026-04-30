# OmniPlay Windows Third-Party Notices

This file is shipped with the Windows build for quick reference.
It is a convenience notice and does not replace the original upstream license texts or service terms.

## Runtime components

| Component | Version | License / Terms | Purpose | Upstream |
| --- | --- | --- | --- | --- |
| Avalonia | 11.2.0 | MIT | Desktop UI framework | https://avaloniaui.net |
| Avalonia.Controls.DataGrid | 11.2.0 | MIT | Data grid controls | https://avaloniaui.net |
| Avalonia.Desktop | 11.2.0 | MIT | Desktop host integration | https://avaloniaui.net |
| Avalonia.Themes.Fluent | 11.2.0 | MIT | Fluent theme resources | https://avaloniaui.net |
| CommunityToolkit.Mvvm | 8.4.0 | MIT | MVVM toolkit | https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/ |
| Dapper | 2.1.66 | Apache-2.0 | Data access | https://github.com/DapperLib/Dapper |
| Microsoft.Data.Sqlite | 9.0.4 | MIT | SQLite provider | https://learn.microsoft.com/dotnet/standard/data/sqlite/ |
| Microsoft.Extensions.DependencyInjection | 9.0.4 | MIT | Dependency injection | https://github.com/dotnet/runtime |
| Microsoft.Extensions.Logging | 9.0.4 | MIT | Logging abstractions | https://github.com/dotnet/runtime |
| Microsoft.Extensions.Logging.Abstractions | 9.0.4 | MIT | Logging abstractions | https://github.com/dotnet/runtime |
| Microsoft.Extensions.Options | 9.0.4 | MIT | Options infrastructure | https://github.com/dotnet/runtime |
| Microsoft.Extensions.Primitives | 9.0.4 | MIT | Shared primitives | https://github.com/dotnet/runtime |
| MicroCom.Runtime | bundled via Avalonia | MIT | COM interop runtime | https://github.com/AvaloniaUI/Avalonia |
| Serilog | 4.2.0 | Apache-2.0 | Application logging | https://serilog.net |
| SkiaSharp | bundled via Avalonia | MIT | 2D graphics rendering | https://github.com/mono/SkiaSharp |
| HarfBuzzSharp | bundled via Avalonia | MIT | Text shaping | https://github.com/mono/SkiaSharp |
| SQLitePCLRaw | bundled via Microsoft.Data.Sqlite | Apache-2.0 | SQLite native provider loading | https://github.com/ericsink/SQLitePCL.raw |
| SQLite | bundled via SQLitePCLRaw native provider | Public domain / blessing | Embedded SQL database engine | https://www.sqlite.org |
| Tmds.DBus.Protocol | 0.21.3 | MIT | Desktop platform integration dependency | https://github.com/tmds/Tmds.DBus |

## Playback-related native binaries

| Component | Bundled file | License / Terms | Notes |
| --- | --- | --- | --- |
| mpv / libmpv | `Native/mpv/libmpv-2.dll` | See upstream distribution license | This repository currently bundles the binary but does not include the upstream full license text beside the DLL. Verify the exact build provenance and downstream obligations before public release. |
| Microsoft D3D Compiler | `Native/mpv/d3dcompiler_43.dll` | Microsoft redistribution terms | Keep this file subject to Microsoft's original redistribution terms. |

## Service attributions

| Service | Terms | Purpose | URL |
| --- | --- | --- | --- |
| The Movie Database (TMDB) | API Terms of Use / Attribution requirements | Metadata, posters and credits | https://www.themoviedb.org/api-terms-of-use |

This product uses the TMDB API but is not endorsed or certified by TMDB.

## Test-only dependencies

These packages are used for development or testing and are not part of the shipped desktop runtime:

| Component | Version | License | Upstream |
| --- | --- | --- | --- |
| coverlet.collector | 6.0.4 | MIT | https://github.com/coverlet-coverage/coverlet |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT | https://github.com/microsoft/vstest |
| xUnit | 2.9.3 | Apache-2.0 | https://xunit.net |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 | https://xunit.net |

## Notes

- For authoritative terms, always refer to the upstream project or service site.
- If the bundled `libmpv-2.dll` is replaced, update this file at the same time.
- If distribution requirements change, add the upstream license texts or source offer materials required by those components.
