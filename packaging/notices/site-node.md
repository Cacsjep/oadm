# Third-party notices of the OADM Site Node

The OADM Site Node is licensed under Apache-2.0 (see `LICENSE`). This is the hand-written part of its notices
(`manage package site-node`, tool `tools/Oadm.Notices --product site-node`): components with notice obligations beyond
their NuGet package license. The node contains no FFmpeg and no other LGPL part.

## Inter font (app user interface)

- The Inter typeface (`Avalonia.Fonts.Inter` package, MIT for the package code), embedded in the app.
  The font files are licensed under the SIL Open Font License 1.1 (full text in section 3 of the generated notices),
  Copyright (c) 2016 The Inter Project Authors, https://github.com/rsms/inter.

## .NET runtime and ASP.NET Core (service and app)

- The service and the app are self-contained: they contain the .NET runtime (`Microsoft.NETCore.App`) and the service
  also ASP.NET Core (`Microsoft.AspNetCore.App`), MIT License, Copyright (c) .NET Foundation and Contributors,
  https://github.com/dotnet/runtime, https://github.com/dotnet/aspnetcore. Their third-party notices
  (`THIRD-PARTY-NOTICES.TXT` of the runtime packs) are reproduced in section 4 of the generated notices.
