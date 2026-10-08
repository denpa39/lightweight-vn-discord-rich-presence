# Cover hosting

Cloudflare Worker + D1 on **Workers Free ($0)**. No R2, billing activation, or paid image transformations. No packages in the service.

## Deploy (maintainer only)

Check **Workers plans → Free → Current plan** in Cloudflare first. This service uses all ten Free D1 database slots (one budget, nine image stores).

From this folder:

```powershell
npx --yes wrangler@4.119.0 login
./deploy.ps1
```

The script creates or migrates the databases and deploys. Legacy JPEG URLs keep working. Account-specific IDs stay in ignored `.wrangler/deploy.json`, not the source template. It never enables billing or changes your plan.

Build the app with the resulting HTTPS Worker URL, ending in `/upload`:

```powershell
dotnet publish ../VnPresence.csproj -c Release -p:CoverUploadEndpoint=https://YOUR-WORKER.workers.dev/upload -o ../dist
```

Without this property, editing and saving images works; uploading stays disabled. Users need no Cloudflare account or keys.

## Limits

- 512×512 PNG, up to 1.25 MiB, preserving transparency. Older apps can still upload baseline JPEGs up to 256 KiB. Only the finished image is sent; no paths, profile, or source URL.
- Public immutable image URLs. Matching bytes reuse the same URL.
- 10 attempts/minute per IP (Cloudflare's limiter is per location).
- 1,000 new uploads/day shared across the service; 300 MB of images per image store (2.7 GB total). Base64/index overhead stays under D1's 500 MB/database cap. Failed uploads may consume a daily reservation.
- The app reports active cover use daily; uncached image requests and reuploads also renew use. Images never expire just because they are old. Only when a new upload exceeds capacity are covers evicted by oldest last use, then fewest recorded usage days. Only enough covers to make room are removed atomically. No scheduled deletion. Existing covers start with a fresh last-use timestamp on migration. Older app versions cannot reliably report use through Discord's cache. Deleted URLs return 404; reupload to restore them.
- **Keep Workers Free.** Requests/queries stop at the platform's free limits; they do not trigger a paid upgrade. Upgrading the account yourself changes that billing protection. Limits are shared with any other apps in your account.
- Community covers are listed by public VNDB game ID (up to 40 per game). Uploads send that ID with finished pixels; existing covers join the gallery when the updated app uses them. Selecting one reuses its URL without storing another image. Matching uploads still deduplicate across games. These are anonymous uploads, not verified artwork. Canceling after uploading does not delete the hosted image. To remove an image, delete its hash row from the relevant image database's `covers` table; its storage is reclaimed automatically. Deleting covers cannot reset daily request, query, or upload limits.

Anonymous uploads are public. Review usage and remove reported images through D1. Do not expose account credentials in the app or repository.

[Workers Free limits](https://developers.cloudflare.com/workers/platform/limits/), [D1 Free pricing and enforcement](https://developers.cloudflare.com/d1/platform/pricing/).

## Check

```powershell
node --test worker.test.mjs
```
