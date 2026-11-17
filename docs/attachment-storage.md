# Attachment storage

Attachment bytes (PLAN2 §9.1) live behind one interface, `IAttachmentFileStore`
(`src/Chaos.Application/Attachments/AttachmentFileStore.cs`). Nothing else in the app knows
where the bytes are: commands, backup, restore, export, purge and the RPC service only call
the interface. Today the one provider is `local`, a folder. This page says how to add an
S3-compatible provider, and how to move existing files to it.

## The contract

- A blob is addressed only by the lowercase hex SHA-256 of its bytes. No file name, path or
  URL crosses the interface. `AttachmentHash.IsValid` decides what an address is; a provider
  must treat anything else as "not there".
- Blobs are immutable. Putting identical bytes twice keeps one copy (`AlreadyPresent`).
- `PutAsync` hashes while it writes, refuses more than `maxBytes` and a wrong
  `expectedSha256` (both as `AppException` validation errors), and makes the blob visible only
  when it is complete.
- `OpenReadAsync(hash, offset)` returns a stream or `null`; `GetInfoAsync`, `ExistsAsync` and
  `DeleteAsync` never throw for a missing blob; `ListAsync` enumerates every blob.

## Layout of the local provider

`<data root>/attachments/<first 2 hex>/<sha256>`, written through
`<data root>/attachments/.partial/`. Backups keep their copies in one shared store of the same
kind, `<data root>/backups/attachments/<hash>`; each backup's `.db.json` lists the hashes it
needs, retention removes a copy only when no remaining backup lists it.

## Adding an S3-compatible provider

1. Implement `IAttachmentFileStore`, for example `S3AttachmentStore(client, bucket, prefix)`:
   - `PutAsync`: stream to a temporary key while hashing (multipart for large files), compare
     the hash, then copy to `<prefix>/<hash[..2]>/<hash>` (or `HEAD` first and skip when it
     exists). Delete the temporary key on failure.
   - `OpenReadAsync`: `GET` with `Range: bytes=<offset>-`; a 404 returns `null`.
   - `GetInfoAsync` / `ExistsAsync`: `HEAD`. `DeleteAsync`: `DELETE`. `ListAsync`: paged
     `ListObjectsV2` over the prefix.
2. Run `AttachmentFileStoreContract.RunAsync(store)` (test project,
   `tests/Chaos.Infrastructure.Tests/Attachments`) against an empty bucket. It is the same
   check the local store passes.
3. Register the provider name in `AttachmentFileStoreFactory.Create` (the one place a provider
   is chosen) and read the choice from settings in `Program.cs`.
4. Move existing files with `AttachmentStoreMigrator.CopyAllAsync(local, remote, maxBytes,
   progress, ct)`. It re-hashes every blob as it is written (a damaged source blob is refused,
   never copied), skips blobs the target already has, only reads the source, and can be run
   again or stopped at any point. When `Complete` is true, switch the provider and, once you are
   satisfied, delete the old folder.

Because the database holds only hashes, no rows change when the provider changes. The sync
work in P16 builds on the same interface (server blob storage is `IBlobStore` there; the
S3 client choice is recorded when that package starts).

## What is and is not covered today

- Backups, restore, JSON archive export and import, Markdown export, purge and "delete all
  local data" all go through the interface.
- The shared backup store uses the same interface, so it moves the same way.
- Thumbnails of large images and audio/video durations are not produced yet: no imaging or
  media library is pinned. Image sizes are read from PNG, JPEG, GIF and WebP headers.
