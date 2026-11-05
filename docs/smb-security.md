# SMB security for a recordings share

How to share a Steam recording folder from Windows so the Android companion (or another PC) can read it securely.

## Signing is not encryption
Recent Windows 11 builds **require SMB signing** by default. Signing proves packets weren't altered and blocks relay attacks, but
file contents still travel readable. The login itself never sends the password in clear text (NTLMv2/Kerberos challenge-response).
**Encryption is opt-in**, per share or for the whole server. Over a VPN such as Tailscale/WireGuard the tunnel already encrypts the
traffic, so the exposure is on the local network.

## Enable encryption for one share (elevated PowerShell)
```powershell
Set-SmbShare -Name <ShareName> -EncryptData $true -Force
Get-SmbShare -Name <ShareName> | Select Name, EncryptData
# undo
Set-SmbShare -Name <ShareName> -EncryptData $false -Force
```
- Only that share changes; other shares keep their settings.
- Clients must speak SMB 3.x **and** negotiate a cipher (AES-128/256-GCM/CCM). AES is hardware-accelerated on modern PCs and phones.
- Existing client bookmarks (server, share, path, user) stay valid. Reconnect; nothing needs re-adding.

## "Access denied" after enabling encryption
With `RejectUnencryptedAccess = True` (the Windows default), a client that doesn't negotiate encryption is refused at tree
connect, but **only** on encrypted shares. Its other shares keep working, which makes it look like a permissions problem. It isn't.

Confirm in the server log (elevated):
```powershell
Get-WinEvent -LogName Microsoft-Windows-SMBServer/Operational -MaxEvents 50 | Where-Object Id -eq 1046 |
  Select TimeCreated, Message -First 3
```
Event **1046** says "the server requires encryption, but the server and client did not negotiate an encryption cipher, nor does
server allow unencrypted access", and names the client address and the share. `Microsoft-Windows-SMBServer/Connectivity` event
**4000** shows `Encryption used: false` on that client's other connections.

Several Android file managers ship SMB clients without SMB 3 encryption support. Choices:
1. **Keep encryption strict** and use clients that support it for this share (SteamPoweredPlayer's Android app does).
2. **Mixed mode:** `Set-SmbServerConfiguration -RejectUnencryptedAccess $false`. This is server-wide, but it only matters for
   encrypted shares: clients that negotiate a cipher are still always encrypted (and SMB 3.1.1 pre-authentication integrity stops
   a man-in-the-middle from downgrading them), while clients that can't encrypt get plain, signed access again.
   Weaker, but no worse than before encryption was enabled.
3. **Undo** encryption on the share.

## Who is connected, and how (elevated)
```powershell
Get-SmbSession    | Select ClientComputerName, ClientUserName, Dialect
Get-SmbConnection | Select ServerName, ShareName, Dialect, Encrypted, Signed   # run on a Windows client
```

## Read-only access for the phone
The app opens files read-only, but a share permission is the real guarantee. Optionally, create a dedicated local user, give it
**Read** share permission on the recordings share and nothing else, and use that account on the phone.
