# Swifter Browser

**Swifter** is a production-grade, feature-packed web browser for Windows (x64) written in
C# / .NET 8 on WinForms + `Microsoft.Web.WebView2`, with an IDM-class segmented download
engine, a streaming media sniffer, an ad/tracker shield engine, SQLite FTS5 history and a
fully custom `swifter://` internal page platform.

```
Swifter/
├── Swifter.csproj
├── Program.cs                     # entry point, WebView2 environment, service locator
├── Assets/                        # logo.png + app.ico (embedded resources)
├── Core/
│   ├── BrowserForm.cs             # window chrome, omnibox, badges, PiP, reader, find, certs
│   ├── TabManager.cs              # tab strip, drag reorder, pin, groups, audio, previews
│   ├── Config/                    # settings model + JSON store (%AppData%\Swifter)
│   ├── Storage/                   # SQLite FTS5 history, bookmarks, session recovery
│   ├── Engine/                    # downloads, media sniffer, shields, scripts, reader, memory
│   └── Protocols/                 # swifter:// scheme handler, page renderer, internal pages
├── build-exe.ps1                  # single-file self-contained publisher
└── .github/workflows/build-release.yml
```

## Building

Requirements: Windows 10/11 x64, .NET SDK 8, Microsoft Edge WebView2 Evergreen runtime.

```powershell
dotnet build -c Release            # compile check
.\build-exe.ps1                    # -> .\dist\Swifter.exe (single file, self contained)
```

The GitHub Action `build-release.yml` publishes `Swifter.exe` on every `v*` tag and attaches
it to a GitHub release.

## Feature map

| Area | Where | Highlights |
| --- | --- | --- |
| Window & tabs | `Core/BrowserForm.cs`, `Core/TabManager.cs` | borderless DWM window (dark/light, mica/acrylic, rounded corners), owner-drawn tab strip with drag reorder, pinning, colour groups, mute indicator, hover thumbnails, memory-saver badges |
| Omnibox | `BrowserForm.cs` | URL vs search auto-detect, 6 engines, inline suggestions from history + bookmarks + engine suggest APIs, padlock / `SWIFTER` protocol badges, TLS certificate viewer over `SslStream` |
| Downloads | `Engine/SegmentedDownloader.cs`, `Engine/DownloadQueueManager.cs` | 1-32 parallel HTTP Range streams, per-segment `.swpart` resume files, stitching + SHA-256, single-stream fallback, HLS (.m3u8/master) parallel segment download, global token-bucket throttle, scheduler, queue persistence, clipboard link sniffer (`WM_CLIPBOARDUPDATE`) |
| Media sniffer | `Engine/MediaSniffer.cs` | network + response + DOM probing (video/audio/source, performance entries, MutationObserver) for MP4/WebM/MKV/MP3/FLAC/HLS/DASH, one-click queue from `swifter://downloads` |
| Shields | `Engine/ShieldEngine.cs` | EasyList/uBlock subset parser (`||host^`, `|anchor`, `^`, `*`, `/regex/`, `$third-party`, `$domain=`, `@@` exceptions, hosts-file lines), per-site overrides, live stats, DoH resolver (Cloudflare/Google/Quad9/custom), canvas/WebGL/audio fingerprint noise, WebRTC relay-only ICE, HTTPS upgrade with 307 redirects, third-party cookie stripping, DNT header |
| History | `Storage/HistoryDatabase.cs` | SQLite WAL + FTS5 index over url/title/description, visit duration analytics, daily/hourly SVG charts, domain breakdown, CSV/JSON export, domain purge |
| Bookmarks | `Storage/BookmarksDatabase.cs` | hierarchical JSON store, tags, favourites, bookmark-bar sync, drag & drop, Netscape HTML import/export (Chrome/Firefox/Edge/Brave compatible) |
| Sessions | `Storage/SessionManager.cs` | 10 s autosave, crash detection via clean-exit flag, restore prompt, pinned/group/mute state persisted |
| Memory saver | `Engine/MemorySaver.cs` | freezes background tabs after 30 min (`TrySuspendAsync`), discards after 90 min, wakes on activation |
| Reader mode | `Engine/ReaderModeExtractor.cs` | Readability-style in-page extraction + typography view (theme, font, width) |
| User scripts | `Engine/ScriptInjector.cs` | Greasemonkey-style JS/CSS injection with domain wildcards and document-start/end timing |
| PiP | `BrowserForm.cs` (`PiPForm`) | direct-stream detachment for sniffed media URLs, otherwise 11 fps canvas frame mirroring into a borderless top-most window |
| Internal pages | `Core/Protocols/InternalPages/*` | `swifter://newtab`, `/downloads`, `/history`, `/bookmarks`, `/shields`, `/settings` served through `WebResourceRequested` with a JSON message bridge (`window.Swifter.call`) |

## Notes

* All inline HTML/CSS/JS uses C# 11 raw string literals (`"""`), never verbatim `@""`
  strings, so quote escaping can never break the build.
* Configuration, history, bookmarks, sessions, download queue and shield statistics live
  under `%AppData%\Swifter\`.
* `swifter://` is registered with `CoreWebView2EnvironmentOptions.CustomSchemeRegistrations`
  (authority + secure); if a runtime ever rejects the registration the browser falls back
  to `NavigateToString` rendering automatically.
