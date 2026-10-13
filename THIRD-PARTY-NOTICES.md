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

FluentFTP 55.0.0 – Copyright (c) 2015 Robin Rodricks and FluentFTP Contributors. MIT License (text as above).
Source: https://github.com/robinrodricks/FluentFTP

## SMBLibrary (RemoteHub, network shares over SMB with a second account)

SMBLibrary 1.5.8.1 – Copyright © Tal Aloni 2014-2026. GNU Lesser General Public License v3.0 or later
(https://www.gnu.org/licenses/lgpl-3.0.html). Used unchanged as a separate library (`SMBLibrary.dll` next to
`RemoteHub.exe`); it can be replaced by a compatible build of the library.
Source: https://github.com/TalAloni/SMBLibrary
