# Third-party notices

RemoteHub itself is licensed under the GNU AGPL-3.0 (see LICENSE). The components below ship with it under their own
licenses.

## Virtual Display Driver (RemoteHub Agent, optional)

The RemoteHub Agent ships the signed x64 driver files of the Virtual Display Driver (version 24.12.24) unchanged, in
`third-party\VirtualDisplayDriver\24.12.24` – installed only with the agent's virtual display option.
Source: https://github.com/VirtualDrivers/Virtual-Display-Driver

```
MIT License

Copyright (c) 2024 Virtual Display

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## SSH.NET (RemoteHub, network shares over SFTP)

SSH.NET 2026.0.0 – Copyright © Renci 2010-2026. MIT License (text as above).
Source: https://github.com/sshnet/SSH.NET

## FluentFTP (RemoteHub, network shares over FTP/FTPS)

FluentFTP 55.1.0 – Copyright (c) 2015 Robin Rodricks and FluentFTP Contributors. MIT License (text as above).
Source: https://github.com/robinrodricks/FluentFTP

## SMBLibrary (RemoteHub, network shares over SMB with a second account)

SMBLibrary 1.5.8.1 – Copyright © Tal Aloni 2014-2026. GNU Lesser General Public License v3.0 or later
(https://www.gnu.org/licenses/lgpl-3.0.html). Used unchanged as a separate library (`SMBLibrary.dll` next to
`RemoteHub.exe`); it can be replaced by a compatible build of the library.
Source: https://github.com/TalAloni/SMBLibrary

## AvalonEdit (RemoteHub, script blocks in notes)

AvalonEdit 6.3.1.120 – Copyright © 2000-2025 AlphaSierraPapa for the SharpDevelop Team. MIT License (text as above).
Source: https://github.com/icsharpcode/AvalonEdit

## Markdig (RemoteHub, notes stored as Markdown)

Markdig 0.44.0 (the signed build `Markdig.Signed`, which the PowerShell SDK also uses) – Copyright (c) 2018-2019,
Alexandre Mutel. All rights reserved. BSD 2-Clause License:

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the
following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of conditions and the following
   disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the
   following disclaimer in the documentation and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
Source: https://github.com/xoofx/markdig

## WPF UI (RemoteHub, the Fluent design of every window)

WPF UI 4.3.0 (`WPF-UI`) – Copyright (C) 2021-2026 Leszek Pomianowski and WPF UI Contributors. MIT License (text as above).
Source: https://github.com/lepoco/wpfui

## CommunityToolkit.Mvvm (RemoteHub, view models)

CommunityToolkit.Mvvm 8.4.2 – (c) .NET Foundation and Contributors. All rights reserved. MIT License (text as above).
Source: https://github.com/CommunityToolkit/dotnet

## Microsoft.Data.Sqlite (RemoteHub, the local database)

Microsoft.Data.Sqlite.Core 10.0.12 – © Microsoft Corporation. All rights reserved. MIT License (text as above).
Source: https://github.com/dotnet/efcore

## SQLite3 Multiple Ciphers and SQLite (RemoteHub, the encrypted local database)

SQLite3MC.PCLRaw.bundle, SQLite3MC.PCLRaw.provider and SQLite3MC.PCLRaw.lib 2.4.0 – Copyright (c) 2023-2026 Ulrich
Telle, et al. MIT License (text as above). They contain SQLite, which is in the public domain
(https://www.sqlite.org/copyright.html).
Source: https://github.com/utelle/SQLite3MultipleCiphers-NuGet

## SQLitePCLRaw (RemoteHub, the local database)

SQLitePCLRaw.core 3.0.2 – Copyright 2014-2025 SourceGear, LLC. Apache License 2.0
(https://www.apache.org/licenses/LICENSE-2.0).
Source: https://github.com/ericsink/SQLitePCL.raw

## PowerShell SDK (RemoteHub, PowerShell remoting and the console)

Microsoft.PowerShell.SDK 7.6.6 – © Microsoft Corporation. All rights reserved. MIT License (text as above). The
libraries it brings with it are listed in PowerShell's own notices:
https://github.com/PowerShell/PowerShell/blob/master/ThirdPartyNotices.txt
Source: https://github.com/PowerShell/PowerShell

## Microsoft.Extensions.Hosting.WindowsServices (RemoteHub Agent, running as a Windows service)

Microsoft.Extensions.Hosting.WindowsServices 10.0.13 – © Microsoft Corporation. All rights reserved. MIT License (text
as above).
Source: https://github.com/dotnet/runtime
