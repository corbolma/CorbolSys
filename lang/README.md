# Translations

AppSys is available in **Italian** and **English** (written in the source code).
Any other language is a plain text file in this folder, embedded into `AppSys.exe` and the installer at build time.
No programming needed: translations are very welcome as pull requests.

## Add a language

1. Copy `_template.txt` to `<code>.txt`, where `<code>` is the language code used by Windows:
   `de.txt` (German), `fr.txt` (French), `es.txt` (Spanish), `pt-BR.txt` (Portuguese, Brazil)...
2. Write the language name on the first line, in the language itself: `language = Deutsch`
3. Write each translation after ` = `:
   ```
   Show widget = Widget anzeigen
   {0} free = {0} frei
   Temperature {0:0} °C. = Temperatur {0:0} °C.
   ```
4. Run `powershell -ExecutionPolicy Bypass -File lang-check.ps1` to see what is missing, then `build.bat` and try it:
   the language appears in the menu **Lingua / Language**, and *Automatic* picks it when Windows uses that language.

## Rules

- The text on the left (English) must stay exactly as it is: it is the key.
- Keep placeholders like `{0}`, `{1}`, `{0:0}` and line breaks written as `\n`.
- A line left empty, or a missing line, is shown in English.
- Lines starting with `#` are comments. Save the file as UTF-8.
- Keep short texts short: the widget is small (e.g. `DISK` and `NET` are row labels).
- The AI assistant answers in the language of the interface, so it works in your language too.

## Keeping translations up to date

When texts change in the code, run `lang-check.ps1`: it regenerates `_template.txt` and lists, for every language,
the **missing** texts, the **obsolete** ones (no longer used) and wrong **placeholders** (in that case it exits with an error).
