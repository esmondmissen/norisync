# Third-Party Notices

NoriSync redistributes the following third-party components as part of
its plugin package. Each component is governed by its own license; this file
gathers the notices those licenses require.

Nothing in this file grants you rights over NoriSync itself — the
plugin as a whole is distributed under the GNU General Public License v2.0 or
later (see [LICENSE](LICENSE)).

---

## TagLibSharp 2.3.0

- **Upstream:** https://github.com/mono/taglib-sharp
- **NuGet:** https://www.nuget.org/packages/TagLibSharp/2.3.0
- **License:** GNU Lesser General Public License, version 2.1 or later
  (LGPL-2.1-or-later)
- **Full license text:** [licenses/TagLibSharp-COPYING.txt](licenses/TagLibSharp-COPYING.txt)

Copyright © the TagLib# authors. See the AUTHORS file in the upstream
repository for the full list of contributors.

NoriSync uses TagLibSharp as an unmodified, dynamically-loaded .NET
assembly (`TagLibSharp.dll`) to read audio file metadata. In accordance with
LGPL-2.1 §6:

- **§6(b) — shared library mechanism.** TagLibSharp is distributed as a
  separate `.dll` alongside the plugin. You may replace it with any
  interface-compatible build of TagLibSharp (for example, your own build from
  upstream source) by dropping the replacement DLL into the plugin directory;
  the plugin will continue to function without recompilation.
- **§6(d) — source availability.** The complete corresponding source code for
  TagLibSharp 2.3.0 is available from the upstream repository linked above and
  from NuGet (which ships a `.snupkg` symbol package and source link). No
  modifications have been made by the NoriSync project.

If you received this plugin without the accompanying `licenses/` directory or
are unable to obtain the TagLibSharp source through the channels above,
please open an issue on the NoriSync repository and a copy will be
provided.

---

## Microsoft.Data.Sqlite 9.0.6

- **Upstream:** https://github.com/dotnet/efcore
- **NuGet:** https://www.nuget.org/packages/Microsoft.Data.Sqlite/9.0.6
- **License:** MIT
- **Full license text:** [licenses/MicrosoftDataSqlite-LICENSE.txt](licenses/MicrosoftDataSqlite-LICENSE.txt)

Copyright © .NET Foundation and Contributors.

NoriSync uses `Microsoft.Data.Sqlite` as the managed ADO.NET provider
for its local cache database. The library is pulled in as an unmodified NuGet
package. It is part of the .NET Foundation project; the copyright line and
license text above cover every assembly in the
`Microsoft.Data.Sqlite` / `Microsoft.Data.Sqlite.Core` set.

### Transitive dependency: SQLitePCLRaw (Apache-2.0)

`Microsoft.Data.Sqlite` depends on the SQLitePCLRaw family of packages, which
provide the P/Invoke bridge to the native SQLite engine. These assemblies
ship alongside `Microsoft.Data.Sqlite.dll` in the plugin output:

- `SQLitePCLRaw.core.dll`
- `SQLitePCLRaw.provider.e_sqlite3.dll`
- `SQLitePCLRaw.batteries_v2.dll`
- `SQLitePCLRaw.nativelibrary.dll` (where present)
- `e_sqlite3.{dll,so,dylib}` — native SQLite build produced by the
  SQLitePCLRaw project

- **Upstream:** https://github.com/ericsink/SQLitePCL.raw
- **License:** Apache License, Version 2.0
- **Full license text:** [licenses/SQLitePCLRaw-LICENSE.txt](licenses/SQLitePCLRaw-LICENSE.txt)

Copyright © Eric Sink and SourceGear LLC.

No modifications have been made by NoriSync. Per Apache-2.0 §4(a),
the license text is included in the `licenses/` directory; per §4(b), no
NOTICE file is shipped by the upstream project, so none is reproduced here.

### Transitive dependency: SQLite itself (public domain)

The `e_sqlite3` native binary embeds the SQLite engine
(<https://www.sqlite.org>). SQLite is in the public domain — see
<https://www.sqlite.org/copyright.html>. No attribution is legally required,
but the project is acknowledged here in good faith.

---

## FftSharp 2.2.0

- **Upstream:** https://github.com/swharden/FftSharp
- **NuGet:** https://www.nuget.org/packages/FftSharp/2.2.0
- **License:** MIT
- **Full license text:** [licenses/FftSharp-LICENSE.txt](licenses/FftSharp-LICENSE.txt)

Copyright © 2020 Scott W Harden.

NoriSync uses `FftSharp` as an unmodified NuGet package to compute FFTs used by
the waveform analyzer. The assembly `FftSharp.dll` ships in the plugin output.

---

## MessagePack 3.1.12 (MessagePack-CSharp)

- **Upstream:** https://github.com/MessagePack-CSharp/MessagePack-CSharp
- **NuGet:** https://www.nuget.org/packages/MessagePack/3.1.12
- **Primary license:** MIT
- **Full license text:** [licenses/MessagePack-LICENSE.txt](licenses/MessagePack-LICENSE.txt)

Copyright © 2017 Yoshifumi Kawai and contributors.

NoriSync uses MessagePack-CSharp as an unmodified NuGet package to
serialize API responses when the client requests
`Accept: application/x-msgpack`. The assembly `MessagePack.dll` ships in the
plugin output.

MessagePack-CSharp's `LICENSE` file is a combined notices file — reproduced
verbatim at the path linked above — that also covers two pieces of
third-party code embedded in the assembly:

- **lz4net** (BSD 2-Clause) — Copyright © 2013-2017, Milosz Krajewski.
  Source of the LZ4 compression routines used by MessagePack's
  `LZ4MessagePackSerializer`.
- **BufferWriter.cs** (Apache-2.0) — Copyright 2019 .NET Foundation.
  A single buffer-writer primitive incorporated from the .NET Foundation.

Including [licenses/MessagePack-LICENSE.txt](licenses/MessagePack-LICENSE.txt)
satisfies the attribution requirements of all three licenses in one file,
which is how MessagePack-CSharp itself distributes them.
