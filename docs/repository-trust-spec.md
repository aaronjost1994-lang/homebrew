# Repository and trust specification (simplified TUF, D6)

## Layout

```
<repository>/
  root.json                 newest root (envelope)
  root/1.json, root/2.json  every root version (rotation history)
  timestamp.json            binds index.json by SHA-256 and length (≤ 7 days)
  index.json                sparse index — the ONLY resolver input (≤ 30 days)
  manifests/<id>/<version>.json   every historical manifest, forever
  artifacts/…               anywhere; manifests name them by URL + SHA-256
```

Repositories must keep every historical manifest published so locked installs
keep working. There is no built-in public repository in v1.

## Envelope

```json
{ "payload": "<base64 of the exact UTF-8 JSON bytes>",
  "payloadType": "application/vnd.winbrew.<role>+json;version=1",
  "signatures": [ { "keyid": "<hex>", "sig": "<base64>" } ] }
```

Signatures cover the exact payload bytes (no canonicalization).
`payloadType` is `…root…`, `…timestamp…`, `…index…`, or `…releases…` for the
self-update feed.

## Keys and signatures

- ECDSA P-256 with SHA-256; signatures are IEEE P1363 `r‖s`, **exactly 64
  bytes**, base64.
- Keys are DER SubjectPublicKeyInfo (base64) in `root.json`. Before import,
  WinBrew parses the SPKI with `System.Formats.Asn1` and requires algorithm
  `1.2.840.10045.2.1`, parameters exactly the named curve
  `1.2.840.10045.3.1.7` (explicit parameters are rejected) and an
  uncompressed 65-byte point; after import `KeySize == 256`.
- Key ID (= fingerprint) = lowercase hex SHA-256 of the DER SPKI; it must
  match the key's entry name.
- An envelope with two signatures for one key ID is rejected; each distinct
  key of the role counts once toward its threshold; keys outside the role
  never count.

## Roles

| File | Signed by | Contents | Max lifetime |
|---|---|---|---|
| `root.json` | root keys (threshold) | `version`, `expires`, `keys`, `roles.{root,timestamp,index}.{threshold,keyids}` | 365 days |
| `timestamp.json` | timestamp key(s) | `version`, `expires`, `meta["index.json"].{sha256,length}` | 7 days |
| `index.json` | index key(s) | `version`, `expires`, `packages.<id>.{displayName,description,versions.<v>.{sha256,size,architectures[],dependencies[]}}` | 30 days |

There is no `latestVersion`: "latest" depends on architecture and prerelease
rules and is computed locally.

## Client verification

1. Root: bootstrap (`repo add`): `root.json` signed by a threshold of its own
   keys, not expired; fingerprints approved by the user (`--trust-root`,
   every value a root key, together reaching the threshold). Rotation
   (`update`): fetch `root/<N+1>.json` until 404; each must have version N+1
   and be signed by a threshold of root N **and** of itself. The final root
   must be valid now.
2. Timestamp: signed by the timestamp role; version ≥ the accepted one
   (replay); not expired (freeze); within 7 days.
3. Index: exactly `length` bytes (enforced while streaming, also against
   `trust.maxMetadataBytes`) with the timestamp's SHA-256; signed by the index
   role; version ≥ accepted (rollback); not expired; within 30 days.
4. Manifest: SHA-256 and size from the index entry; its `dependencies` and
   artifact `architectures` must equal the entry.
5. Artifact: SHA-256 and size from the manifest.

Expiry checks allow `trust.maxClockSkewSeconds` (default 300). The accepted
root, every role's high-water mark and the catalog rows are written in ONE
SQLite transaction.

## Producing a repository

`scripts/new-fixture-repo.ps1` generates a signed repository with fresh test
keys (TEST ONLY). Production keys must be generated and stored offline
(`release-checklist.md`). A publisher rotates keys by publishing
`root/<N+1>.json` signed by the old and the new root keys, then updates
`root.json`.
