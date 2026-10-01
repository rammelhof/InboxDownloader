# InboxDownloader

> **Note:** This project was built with the help of AI (a coding agent
> in the terminal). Review the code before relying on it.

A Windows-only .NET 10 console application with a TUI for downloading mails from an IMAP
inbox into a target folder. The target is either a **local folder** or a **Nextcloud
server** (via WebDAV). Mails are sorted into subfolders according to a mapping file
(subfolder path = address criteria) that lives inside the target - for a Nextcloud
target the mapping file is read from the destination root folder on the server.

The app is designed for **shared mailboxes**: the read state of mails is deliberately
not relied upon (other users may have read a mail), and an **archive folder is
mandatory** so that processed mails are reliably moved out of the source folder.

For each mail the app stores:

- a **text file** with the readable content (header block + message body; `text/plain`
  is preferred, HTML bodies are converted to plain text) — the original mail is *not* saved
- all **attachments**, next to the text file

All files of one mail share the same name prefix (sent date + message id + subject),
so they group/sort together:

```
200000\20260105-1200_post-6@test.local_Zustellung + Beleg.txt
200000\20260105-1200_post-6@test.local_Zustellung + Beleg_beleg.csv
```

Mail access uses [MailKit](https://www.nuget.org/packages/MailKit) (MIT license, safe for
commercial use).

## Requirements

- Windows (x64)
- .NET SDK 10 (`dotnet run`) or a published single-file exe

## Build / run

```
dotnet run
```

Publish a self-contained exe:

```
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

### Command line

```
InboxDownloader.exe [service] [--test] [--config <path>]
```

- `service` – run as a (Windows) service instead of the TUI.
- `--test` – test the IMAP connection and list all folders (recursive, incl.
  special-use flags and message counts of the configured source/archive folder),
  then exit. Exit code `0` = success, `1` = failure (useful for diagnostics and
  scripts). The same test is available interactively via TUI option 5.
- `--config <path>` – use a different config file than
  `%PROGRAMDATA%\InboxDownloader\config.json`.

## TUI

| Key | Action                                              |
|-----|-----------------------------------------------------|
| 1   | Configure IMAP account (server, port, SSL, user, password, folder, archive) |
| 2   | Configure target (local folder / Nextcloud) |
| 3   | Show current configuration                          |
| 4   | Download mails (scans the whole inbox)              |
| 5   | Test connection (lists all folders, recursive)   |
| 6   | Configure service settings (poll interval, monitoring push URL) |
| 7   | Start service in this console (Ctrl+C to stop)      |
| 0   | Exit                                                |

## Configuration

Stored as JSON in `%PROGRAMDATA%\InboxDownloader\config.json`
(e.g. `C:\ProgramData\InboxDownloader\config.json`). A machine-wide location, so the
same config is used by the TUI and by a service running under **any** account.
A different file can be used with `--config <path>`.

> Note: creating `C:\ProgramData\InboxDownloader` needs write access to `C:\ProgramData`
> (usually administrator rights) - run the TUI elevated on first start, or use
> `--config` with a writable location. If a config from the old location
> (`%APPDATA%\InboxDownloader\config.json`) is found, the app prints a hint to move it.

```json
{
  "account": {
    "name": "My IMAP Account",
    "server": "imap.example.com",
    "port": 993,
    "useSsl": true,
    "acceptUntrustedCertificates": false,
    "userName": "me@example.com",
    "password": "secret",
    "folderName": "INBOX",
    "lastSyncUtc": null,
    "archiveFolder": "Archive",
    "archiveMaxAgeDays": 72
  },
  "target": {
    "mode": "nextcloud",
    "path": "C:\\Users\\me\\Documents\\Mail",
    "mappingFileName": "mapping.ini",
    "unmappedSubfolder": "Unsorted",
    "fileExtension": ".txt",
    "webDavBaseUrl": "https://cloud.example.com",
    "webDavUser": "me",
    "webDavPassword": "secret",
    "acceptUntrustedCertificates": false,
    "destinationRoot": "Mails"
  },
  "service": {
    "pollIntervalMinutes": 15,
    "pushUrl": "http://mon.lan:3001/api/push/TOKEN?status={status}&msg={msg}&msgcount={msgcount}&ping={ping}",
    "pushIntervalMinutes": 15
  }
}
```

> Passwords (IMAP, WebDAV) and the push URL (usually contains a token) are stored
> **in plain text** in this file. The default location is machine-wide, so lock the
> ACL down so only the accounts that need the file can read it.
>
> Config files written by older versions (with a separate `sync` section) are
> migrated automatically: a `nextcloud` sync destination becomes a `nextcloud` target.
>
> `account.archiveFolder` is **required** - if it is missing, the app refuses to run
> (and a service reports `down`).

### Target

`mode` selects where the processed mail files are stored:

| mode        | Meaning |
|-------------|---------|
| `local`     | Files are stored in the local folder `path`. The mapping file lives in this folder (`path/mapping.ini`) |
| `nextcloud` | Files are stored directly on a Nextcloud server via WebDAV. `webDavBaseUrl` = the Nextcloud URL (e.g. `https://cloud.example.com`), `destinationRoot` = folder **relative to the user's files folder** (e.g. `Mails` → `files/me/Mails/...`). The WebDAV endpoint `remote.php/dav/files/<user>/` is derived automatically. The mapping file is read from the destination root folder on the server (`files/<user>/Mails/mapping.ini`) |

The relative subfolder structure (mapped subfolders) is preserved below the target
root. Files that already exist in the target are never overwritten (Nextcloud:
`HEAD` check, including the `X-OC-Mtime` header on upload; local: file check).

`unmappedSubfolder` (default `Unsorted`) is the subfolder for mails that match no
mapping criterion. **If it is set to empty, such mails are not downloaded at all** -
they are also not archived and not reported as leftovers (a way to say "mails that
are not for us are simply ignored", e.g. in a shared mailbox).

## Mapping file

The target must contain the mapping file (default name `mapping.ini`; for a Nextcloud
target it is read via WebDAV from the destination root folder). Format: one
`subfolder-path=addresses` pair per line (case-insensitive addresses; lines starting
with `;` or `#` are comments). Subfolders can be nested using `\` or `/`:

```ini
; shared mailbox: only mails addressed to belege@ or erin@ are "ours" -
; this default applies to every line without its own to: criterion
to=belege@example.com;erin@example.com

; mails from office@ OR post@ go to 200000 (several addresses = alternatives)
200000=office@example.com;post@example.com
; nested subfolder: 200000\er - a line with its own to: criterion
; overrides the global "to=" line
200000\er=to:erin@example.com
; match on recipient instead of sender (To or Cc)
300000\belege=to:belege@example.com
; combine criteria: from AND to must both match
200000=from:post@example.com;to:belege@example.com
```

- The right side has one or more `;`-separated addresses. A bare address (or one with
  the `from:` prefix) matches the **From** header; `to:address` matches the **To** or
  **Cc** headers. Addresses of the same field are **alternatives (any of them may
  match)**; a line that has both `from:` and `to:` addresses requires **both** (the
  mail must be from one of the from-addresses *and* addressed to one of the
to-addresses). At least one address is required.
- A line `to=address;...` (left side is the keyword `to`, not a subfolder) sets a
  **global default "to:" criterion** that is applied to every mapping line that has no
  `to:` criterion of its own. A line with its own `to:` addresses (like
  `200000\er=to:erin@example.com`) **overrides** the global default.
- A subfolder may be mapped by several lines / addresses.
- If one mail matches several subfolders, the files are stored in each.
- Mails that match no criterion are stored in the *unmapped subfolder*
  (default `Unsorted`) **and are not archived** - see below. If the unmapped
  subfolder is set to empty, they are not downloaded at all (and not reported as
  leftovers either).
- Invalid path values (`..`, absolute paths, forbidden characters) are ignored.

### What happens to processed mails

- A mail that matches at least one criterion is marked **read** (`\Seen`) after being
  saved and **moved to the archive folder** (IMAP `MOVE`, or copy + delete on servers
  without `MOVE`). This is reliable for shared mailboxes even if other users have
  already read the mail - the whole folder is scanned on every run (see below), so
  nothing depends on the read state.
- A mail that matches no criterion is (by default) saved to the unmapped subfolder
  but **deliberately not archived** - it stays in the source folder. It is reported as
  a *leftover mail* in the TUI summary and - in service mode - makes the monitoring
  push report `down` with a message like `2 mail(s) in INBOX match no mapping
  criteria`. The leftover check looks at **all** mails currently in the source folder,
  so it does not depend on the read state either. If the unmapped subfolder is set to
  empty, such mails are not downloaded and not reported (they are simply ignored).
- Fix (leftover mails): add a mapping for the mail's sender/recipient (it is picked up
  on the next poll), or delete the mail from the source folder.

### Archive folder and age cleanup

- `account.archiveFolder` (**required**): the folder processed mails are moved to
  after being saved. The folder (and any missing parents) is created automatically.
  If it is missing/empty, the app refuses to run and the service reports `down` with
  the error message - this is intentional, because without an archive the app cannot
  reliably tell processed mails apart in a shared mailbox.
- `account.archiveMaxAgeDays` (0 = keep forever): after each run, mails in the archive
  folder **older** than this many days are deleted (e.g. `72` for 3 days).

## Stored files

Per mail, inside the mapped subfolder (local or on the Nextcloud server):

| File | Content |
|------|---------|
| `yyyyMMdd-HHmm_<message-id>_<subject><ext>` (default `.txt`) | Readable text: `Subject/From/To/Cc/Date` header block, then the body (plain text, or HTML converted to plain text) |
| `yyyyMMdd-HHmm_<message-id>_<subject>_<attachment name>` | Each attachment, byte-for-byte as received |

The leading `yyyyMMdd-HHmm` is the mail's **sent** date and time (minute precision);
the message id keeps the prefix unique. The shared prefix keeps all files of a mail
together. Existing files are never overwritten.

## Inbox scanning

Every run scans the **entire source folder** - there is no incremental/date-based mode.
This is deliberate and cheap: processed mails are moved to the (mandatory) archive
folder, so the source folder only ever contains *unprocessed* mails. A full scan is
therefore reliable in every case - new mails, mails that matched no criterion yet
(re-scanned until they are mapped or deleted), and shared mailboxes where the read
state cannot be relied upon. `lastSyncUtc` is updated after every successful run but
is informational only (shown in the TUI). Re-downloads are prevented by the
file-existence check, so running the download repeatedly is always safe.

## Windows service mode

The app can run as a Windows service that polls the inbox in the background
(TUI option 6 configures it):

```
InboxDownloader.exe service
```

### What it does

- Every **poll interval** (configurable, default 15 min): one full inbox scan
  (same logic as TUI option 4; `lastSyncUtc` is persisted after each successful run).
- Every **push interval** (configurable, default 15 min): if a **monitoring push URL**
  is set, the state of the *last run* is reported to the monitor (e.g. Uptime Kuma):
  - `status=up` + short summary (e.g. `1 message(s) processed, 1 new file(s)`) on success
  - `status=down` + message when mails remain in the source folder that match no
    mapping criterion (e.g. `2 mail(s) in INBOX match no mapping criteria`)
  - `status=down` + the error message on failure (IMAP/WebDAV problems, missing archive
    folder, ...)
  The push URL may use the following placeholders, which are substituted **where they
  occur** - the rest of the URL (including the query string) is left unchanged, so a
  plain URL (e.g. a Kuma push URL) works as-is:

  | placeholder  | Meaning                                                        |
  |--------------|----------------------------------------------------------------|
  | `{status}`   | `up` or `down`                                                 |
  | `{msg}`      | the (URL encoded) status message of the last run               |
  | `{msgcount}` | message count of the inbox when the run started                |
  | `{ping}`     | seconds the whole run took                                     |

  Example: `http://mon.lan:3001/api/push/TOKEN?status={status}&msg={msg}&msgcount={msgcount}&ping={ping}`
- The config file is re-read before every step: changes made with the TUI (new password,
  intervals, push URL) apply without a service restart.
- Log files: one per day, `service_yyyyMMdd.log` in the config file's directory
  (default `%PROGRAMDATA%\InboxDownloader\`; with `--config` the logs are stored
  next to that config file). Log files older than 14 days are deleted automatically.

### Installation

1. Configure everything with the TUI first (this creates and fills `config.json`).
2. Publish a single-file exe:
   ```
   dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
   ```
3. Create the service (PowerShell, elevated):
   ```powershell
   New-Service -Name InboxDownloader -DisplayName "InboxDownloader" `
     -BinaryPathName '"C:\Tools\InboxDownloader\InboxDownloader.exe" service' -StartupType Automatic
   ```
4. `Start-Service InboxDownloader`

The service can run as **any** service account (your user, LocalSystem, Network
Service, ...) - the default config location `C:\ProgramData\InboxDownloader\config.json`
is visible to all of them. Two things to consider:

- **File access**: lock the ACL of the config file down (it contains plain-text
  passwords), and a local target folder must be writable by the service account
  (a Nextcloud target has no local folder access requirements). With a custom config
  location (`--config <path>`), use e.g.
  ```powershell
  New-Service -Name InboxDownloader -DisplayName "InboxDownloader" `
    -BinaryPathName '"C:\Tools\InboxDownloader\InboxDownloader.exe" service --config D:\data\config.json' `
    -StartupType Automatic
  ```

Test the service in a console without installing it: TUI option 7, or
`dotnet run -- service` (Ctrl+C to stop).

## Test helpers

`tools/FakeImapServer` – minimal multi-folder IMAP server for testing. Serves all
`*.eml` files in a directory as `INBOX` and keeps all other folders (e.g. the archive)
in memory for the lifetime of the process (any user/password unless the optional
third argument is given):

```
dotnet run --project tools/FakeImapServer -- C:\path\to\eml\folder 1143 [user:password]
```

With `user:password`, the server rejects `LOGIN` attempts with wrong credentials.
A file named `*.read.eml` is seeded as already read (\Seen) - useful to simulate a
shared mailbox where another user read a mail first.

`tools/FakeWebDavServer` – minimal WebDAV server (OPTIONS/PROPFIND/MKCOL/PUT/GET/HEAD/DELETE)
backed by a local directory, emulating the Nextcloud `remote.php/dav/files/<user>/`
layout (everything below the user root maps into the storage root directory):

```
dotnet run --project tools/FakeWebDavServer -- C:\path\to\storage 1144 [user:password]
```

`tools/FakeKumaServer` – minimal Uptime Kuma push endpoint (logs the requests):

```
dotnet run --project tools/FakeKumaServer -- 1301
```
