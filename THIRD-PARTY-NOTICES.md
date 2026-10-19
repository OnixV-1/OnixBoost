# Third-party components

## WinDivert

- Source: https://reqrypt.org/windivert.html  
- License: [GNU Lesser General Public License v3.0](https://www.gnu.org/licenses/lgpl-3.0.html)  
- Used for: interception and filtering of network packets for the Discord profile (via winws)  
- Files in distribution: `WinDivert.dll`, `WinDivert64.sys` (shipped beside winws)

WinDivert is copyright of its authors (Reqrypt). OnixBoost links to / redistributes the binary components as permitted by LGPLv3. The WinDivert source is available from the upstream project.

## winws (Flowseal / zapret stack)

- Related project: https://github.com/Flowseal/zapret-discord-youtube  
- Used for: Windows packet desync profiles consumed by the Discord card  
- Strategy lists may be refreshed from upstream Flowseal GitHub Releases

## .NET / Avalonia

- [.NET](https://dotnet.microsoft.com/) — Microsoft licenses for the runtime  
- [Avalonia UI](https://avaloniaui.net/) — Avalonia license (MIT) for the UI framework  

Full dependency licenses are embedded in published package metadata / NuGet license files where applicable.
