# AGENTS.md

会译 (HuiYi): a Windows tray app that turns what the PC plays into live translated subtitles and meeting notes, keeps each meeting as a bilingual record, and speaks what you say back into the room in their language. It follows the macOS app Cicada (Apache-2.0); see NOTICE for what was ported.

## Hard rules

- API keys live only in `%APPDATA%\HuiYi\keys\<service id>.key`, DPAPI-encrypted (`AIKey`): never in settings, logs or exports.
- Every AI call goes through `AiClient` (start gate, priorities, the platform's own thinking switch from `ProviderPreset`, content in the system message). Services are OpenAI-compatible; platforms are presets in `Providers.cs`, never special cases elsewhere. Partial (draft) text is translated by Google, never the AI, at most once a second.
- Never speak untranslated text into the room (`TranslationService.LooksTranslated`). Never bundle VB-CABLE or the models; models are downloaded and checked against pinned SHA-256 (`ModelStore`).
- The live panel (实时面板) and the floating subtitles (悬浮字幕) never take focus (`WS_EX_NOACTIVATE`) and stay out of captures (`WDA_EXCLUDEFROMCAPTURE`) unless the user turns on 在屏幕共享和录屏中显示字幕窗口 (then they show 共享中可见).
- Engines in `HuiYi.Core` are UI-free and raise events on worker threads; `AppModel` hops to the UI thread.
- `session.json` stays compatible with Cicada's format (camelCase, ISO-8601 UTC dates without fractions).
- The debug log never holds transcript text, translations or the key.

## Design

会译 has its own look, deliberately unlike Cicada's: cool slate ground `#F5F6FA`, ink `#1E2235`, indigo `#4F5BD5` for actions and selection, green `#22B07D` for "on air", greys `#646B80` / `#9AA1B2`. The live panel is dark (`#1B1E2B`); the records window has a white sidebar grouped by day and notes split into cards; settings are tabbed pages; first run is a one-step-per-page wizard; the mark is a speech bubble (`Icons.cs`). Words: 发言人 (not 说话人), 实时面板, 悬浮字幕, 逐字稿, 同声传译. Colours live in `App.xaml` and `Theme.cs`. Copy is Chinese and short, at most one grey helper line. The product name is `Brand.Name` in `AppModel.cs`.

## Commands

```powershell
dotnet build HuiYi.sln
dotnet test tests/HuiYi.Tests
dotnet run --project src/HuiYi -- --listen          # or --show main|settings|setup
dotnet run --project tools/HuiYi.Cli -- loopback <audio file>
powershell -File scripts/package.ps1
```

## Verifying

Run the app and check the behaviour, not just the build. Use `huiyi-cli loopback` with a clip to drive the real capture path, read `%LOCALAPPDATA%\HuiYi\debug.log`. To check the UI without taking over the desktop, run the app with `--snapshot <folder>`: every window renders to PNG off screen (the capture-proof ones too); `--feed <audio>` listens to a file instead of the PC. VB-CABLE, a real meeting and a real voice need a person.

## Conventions

Commit messages are English and follow Conventional Commits (`feat:`, `fix:`, `docs:`, `build:`, `refactor:`, `test:`, `chore:`), one change per commit. Comments say why. Keep `README.md` in step with user-visible changes.
