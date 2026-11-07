# S3 `android-smb-download`: JCIFS 3 on Android, download-first with drops

Given an Android 13 phone + JCIFS 3 (read-only, encryption required) and two profiles (LAN, VPN), when downloading a 5 min range
over Wi-Fi with injected drops, then 0 corrupt chunks, ≤ 1 chunk re-fetched per drop, automatic resume and profile failover,
and the server shows an encrypted 3.1.1 session.

## Design
- `org.codelibs:jcifs:3.0.4` (the only Java SMB client with AES-256-GCM; encryption works from 3.0.4 on). Settings: SMB 3.0 …
  3.1.1, `encryptionEnabled`, `signingEnforced`, SMB2 negotiation, short connect timeout.
- jcifs 3.0.4 cannot *require* encryption (it encrypts when the server or share asks for it). The spike enforces the plan's
  "never silently downgraded" itself: after connecting it reads the session state and **refuses an unencrypted session** while
  "require encryption" is on. jcifs has no public accessor for dialect/cipher/signing and the impl classes are package-private,
  so `Smb.info` reads them by reflection (pinned version). A product build should get an upstream accessor instead.
- `Downloader`: the unit is Steam's chunk file. `<name>.part` → size check → rename; SHA-256 of every finished file is recorded
  for a corruption check against the server copy. A retry **resumes the `.part` at its length**, so a drop costs at most the
  bytes in flight. Priority = init segments, then chunks from the playhead forward; 2 workers by default. Retries back off
  0.5 … 8 s with jitter and are woken early by `ConnectivityManager` callbacks (no polling).
- Profiles: LAN first, then VPN, each gated by a 1.5 s TCP probe of :445. A byte budget (`vpn_max_mb`, default 50) caps what may
  be fetched through the fallback profile, because with Wi-Fi off the VPN rides on mobile data.
- Opens are read-only with share READ|WRITE|DELETE, so Steam keeps writing and trimming.
- Credentials: host, share and user can be set over adb; **the password is typed on the phone** and stored with a
  non-exportable Android Keystore AES-256-GCM key. Nothing about the server lives in this repo.

## Results (test phone, Android 13, Windows 11 SMB server, share with EncryptData on; 100 chunks = 5 min, 514 MB)
| run | result |
|---|---|
| probe | LAN and VPN (Tailscale): **SMB 3.1.1, encrypted AES-128-GCM, signing enforced**. jcifs 3.0.4 runs fine on ART |
| LAN | 202 files, 514 MB in 49.3 s = **10.4 MB/s**, 0 failed, 0 re-fetched |
| 2 Wi-Fi drops (20 s each) | 0 failed, **0 bytes re-fetched** (2 `.part` resumes), failover LAN→VPN in 13 s, 137 s total |
| VPN only (Wi-Fi under Tailscale) | 42 files, 106 MB in 12.5 s = 8.5 MB/s |
| integrity | SHA-256 of all 446 downloaded files == server files: **0 corrupt** |

AES-128-GCM was negotiated, not AES-256-GCM: Windows chose it; jcifs offers both.

**Bugs found on the device (fixed in code, re-run pending):**
1. The fallback byte cap was checked only when connecting, so once on the VPN the whole rest of the range (494 MB) came over the
   VPN, part of it over mobile data. Now enforced on every read; when used up, workers wait for a network change.
2. No fail-back: after Wi-Fi returned the download stayed on the VPN. Now a network callback probes the LAN profile and drops the
   fallback session when LAN is reachable; in-flight files resume their `.part` on LAN.

## Device runbook
```bash
adb install -r app/build/outputs/apk/debug/app-debug.apk
adb shell am start -S -n spp.spike.s3/.MainActivity --es mode config --es lan <LAN host> --es vpn <VPN host> --es share <share> --es user <user>
# owner: open "SPP S3 SMB" on the phone, type the password, press Save
adb shell am start -S -n spp.spike.s3/.MainActivity --es mode probe
# baseline, then with drops (Wi-Fi off 20 s twice during the run), then VPN only
adb shell am start -S -n spp.spike.s3/.MainActivity --es mode run --es session video/<bg_folder> --ei first 1 --ei count 100 --es tag lan --ez fresh true
adb shell am start -S -n spp.spike.s3/.MainActivity --es mode run --es session video/<bg_folder> --ei first 1 --ei count 100 --es tag drops --ez fresh true
#   while it runs: adb shell svc wifi disable; sleep 20; adb shell svc wifi enable
adb shell am start -S -n spp.spike.s3/.MainActivity --es mode run --es session video/<bg_folder> --ei first 1 --ei count 10 --es tag vpn --es only vpn --ez fresh true
adb pull /sdcard/Android/data/spp.spike.s3/files/results .
```
Server side (Windows, elevated): `Get-SmbSession | Select ClientComputerName, Dialect, …` and `Get-SmbConnection`/event
1046 to see the session's dialect and encryption. Corruption check: compare the recorded SHA-256 values with the server files.

Verdict: **PASS on integrity, resume and encryption**; failover policy bugs fixed, needs one re-run of the drop test.
