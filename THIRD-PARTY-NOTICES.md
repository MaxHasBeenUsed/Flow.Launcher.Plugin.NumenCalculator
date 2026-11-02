# Third-Party Notices

Numen Calculator for Flow Launcher is an unofficial, personal-use plugin. It statically
links libnumen into `numen_c.dll` and embeds tables generated from GeoNames and Unicode
CLDR data. The notices below reproduce the applicable licenses and attributions.

| Component | Version / pin | Use | License |
|---|---|---|---|
| Numen (`libnumen`) | v0.8.0, commit `d7117da039f201ce0946b847e1d5c9ba31da4694` | Static library behind `numen_c.dll` | BSD 3-Clause |
| GeoNames timezone and city data | dumps used by `numen/scripts/gen-timezone-tables.py` | Generated place-name → IANA timezone and character-folding tables | CC BY 4.0 |
| Unicode CLDR region and currency data | `supplementalData.xml` (`currencyData`) as vendored by Numen | Generated region → currency table | Unicode License V3 |
| Flow Launcher plugin contract (`Flow.Launcher.Plugin` 5.3.2) | NuGet package | Compile-time API only; not redistributed | MIT |

ICU is **not** redistributed by this plugin. Date, timezone, and locale formatting use the
ICU data built into Windows through the MSVC `std::chrono` timezone database, so Windows'
own ICU notices apply to that component.

---

## 1. Numen (`libnumen`) — BSD 3-Clause License

Numen is statically linked into `numen_c.dll` and is therefore redistributed in binary
form inside `artifacts/publish/numen_c.dll`.

```text
BSD 3-Clause License

Copyright (c) 2026, Aurélien Brabant

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:

Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.

Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.

Neither the name of the copyright holder nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE
```

## 2. GeoNames timezone and city data — CC BY 4.0

The generated tables `numen/src/numen/gen/geo-tz-tables.inc` and
`numen/src/numen/gen/geo-charmap.inc`, compiled into `numen_c.dll`, are derived from the
GeoNames dumps `cities15000`, `admin1CodesASCII`, `countryInfo` and
`alternateNamesV2`, downloaded from `https://download.geonames.org/export/dump/` by
`numen/scripts/gen-timezone-tables.py`.

**Attribution.** This product includes data from GeoNames
(`https://www.geonames.org`), licensed under the Creative Commons Attribution 4.0
International License (CC BY 4.0).

**License.** CC BY 4.0 — `https://creativecommons.org/licenses/by/4.0/`. The full legal
code is published at `https://creativecommons.org/licenses/by/4.0/legalcode` and is
incorporated here by reference; its terms govern the use of this data. The license grant:

> **You are free to:**
> **Share** — copy and redistribute the material in any medium or format for any purpose,
> even commercially.
> **Adapt** — remix, transform, and build upon the material for any purpose, even
> commercially.
> The licensor cannot revoke these freedoms as long as you follow the license terms.
>
> **Under the following terms:**
> **Attribution** — You must give appropriate credit, provide a link to the license, and
> indicate if changes were made. You may do so in any reasonable manner, but not in any way
> that suggests the licensor endorses you or your use.
> **No additional restrictions** — You may not apply legal terms or technological measures
> that legally restrict others from doing anything the license permits.
>
> **Notices:**
> You do not have to comply with the license for elements of the material in the public
> domain or where your use is permitted by an applicable exception or limitation.
> No warranties are given. The license may not give you all of the permissions necessary
> for your intended use. For example, other rights such as publicity, privacy, or moral
> rights may limit how you use the material.

**Modifications.** The data is not redistributed verbatim. Only the fields needed for
place-name → IANA timezone resolution are retained: city names, ASCII forms, English and
country-language alternate names, first-level administrative divisions and countries.
Each division is mapped to the timezone covering the largest share of its population; each
country is mapped to the timezone of its capital. Names are lowercased and folded to ASCII
where possible (otherwise kept as lowercase UTF-8), names shorter than 3 bytes are dropped
unless GeoNames flags them as short names, and words used by the calculator grammar are
removed. The result is committed as generated C++ lookup tables.

**No endorsement.** GeoNames does not endorse this plugin or its use of the data.

## 3. Unicode CLDR region and currency data — Unicode License V3

The generated table `numen/src/numen/gen/region-currency-table.inc`, compiled into
`numen_c.dll`, is derived from Unicode CLDR supplemental data (`supplementalData.xml`,
`currencyData`) as vendored by Numen.

Unicode License V3 applies to that data.

```text
UNICODE LICENSE V3

COPYRIGHT AND PERMISSION NOTICE

Copyright © 1991-2026 Unicode, Inc.

NOTICE TO USER: Carefully read the following legal agreement. BY
DOWNLOADING, INSTALLING, COPYING OR OTHERWISE USING DATA FILES, AND/OR
SOFTWARE, YOU UNEQUIVOCALLY ACCEPT, AND AGREE TO BE BOUND BY, ALL OF THE
TERMS AND CONDITIONS OF THIS AGREEMENT. IF YOU DO NOT AGREE, DO NOT
DOWNLOAD, INSTALL, COPY, DISTRIBUTE OR USE THE DATA FILES OR SOFTWARE.

Permission is hereby granted, free of charge, to any person obtaining a
copy of data files and any associated documentation (the "Data Files") or
software and any associated documentation (the "Software") to deal in the
Data Files or Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, and/or sell
copies of the Data Files or Software, and to permit persons to whom the
Data Files or Software are furnished to do so, provided that either (a)
this copyright and permission notice appear with all copies of the Data
Files or Software, or (b) this copyright and permission notice appear in
associated Documentation.

THE DATA FILES AND SOFTWARE ARE PROVIDED "AS IS", WITHOUT WARRANTY OF ANY
KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT OF
THIRD PARTY RIGHTS.

IN NO EVENT SHALL THE COPYRIGHT HOLDER OR HOLDERS INCLUDED IN THIS NOTICE
BE LIABLE FOR ANY CLAIM, OR ANY SPECIAL INDIRECT OR CONSEQUENTIAL DAMAGES,
OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS,
WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION,
ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THE DATA
FILES OR SOFTWARE.

Except as contained in this notice, the name of a copyright holder shall
not be used in advertising or otherwise to promote the sale, use or other
dealings in these Data Files or Software without prior written
authorization of the copyright holder.
```

## 4. Flow Launcher plugin contract — MIT License

The plugin is compiled against the `Flow.Launcher.Plugin` 5.3.2 NuGet package. That
assembly provides the compile-time contract (`IAsyncPlugin`, `IContextMenu`,
`ISettingProvider`, `IPluginI18n`) and is **not** copied into `artifacts/publish`; Flow
Launcher supplies it at runtime.

```text
The MIT License (MIT)

Copyright (c) 2019 Flow-Launcher
Copyright (c) 2015 Wox

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## 5. ICU

ICU is not redistributed with this plugin. Numen's date, timezone, and locale handling
consumes the ICU data provided by Windows (`icu.dll` / Windows globalization data) through
the MSVC `std::chrono` timezone database. Any notices required for those Windows
components are satisfied by the Windows license; the plugin ships no ICU binaries or data
files.
