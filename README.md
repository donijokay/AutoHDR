# AutoHDR

Aplikasi system-tray untuk Windows 11 yang **menyalakan HDR otomatis** saat game fullscreen/borderless terdeteksi, lalu **mengembalikan keadaan HDR sebelumnya** saat game ditutup.

System-tray app for Windows 11 that **turns HDR ON** when a fullscreen/borderless game is detected, then **restores the previous HDR state** when the game exits.

---

## Bahasa Indonesia

### Persyaratan
- Windows 11 (disarankan; Windows 10 2004+ mungkin berjalan)
- Layar yang mendukung HDR / Advanced Color
- GPU + driver yang mendukung Windows HDR
- .NET 8 SDK (untuk build) — atau gunakan hasil `publish` self-contained

### Build
```bat
cd AutoHDR
dotnet restore
dotnet build -c Release
```

### Publish (single-file, win-x64)
Jalankan `publish.bat`, atau:
```bat
dotnet publish AutoHDR\AutoHDR.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```
Output: `publish\win-x64\AutoHDR.exe`

### Penggunaan
1. Jalankan `AutoHDR.exe` — ikon muncul di system tray (pojok kanan bawah).
2. **Enable AutoHDR** (default aktif): saat game fullscreen terdeteksi → HDR ON; saat game keluar → HDR dikembalikan *hanya jika AutoHDR yang menyalakannya*.
3. Menu tray:
   - **Enable / Disable AutoHDR** — arm/disarm
   - **Toggle HDR now** — nyala/mati HDR manual
   - **Settings** — whitelist exe, start with Windows, interval poll, cakupan fullscreen
   - **Exit**
4. Jika HDR tidak didukung, muncul balloon tip peringatan.

### Konfigurasi
File: `%AppData%\AutoHDR\config.json`  
(`Environment.SpecialFolder.ApplicationData` → biasanya `C:\Users\<user>\AppData\Roaming\AutoHDR\config.json`)

| Field | Arti |
|--------|------|
| `enabled` | AutoHDR armed saat start |
| `whitelist` | Daftar nama exe (tanpa path). **Kosong** = semua game fullscreen kecuali exclusion list. **Isi** = hanya exe tersebut |
| `extraExclusions` | Tambahan proses yang diabaikan |
| `startWithWindows` | Daftar di HKCU Run |
| `pollIntervalMs` | Interval deteksi (default 1500) |
| `fullscreenCoverageThreshold` | Proporsi monitor yang harus tertutup (default 0.95) |
| `allHdrDisplays` | `true` = semua display HDR-capable; `false` = primary saja |

Exclusion bawaan meliputi: explorer, chrome, msedge, firefox, Code, devenv, WINWORD, Discord, Slack, dll.

### Catatan
- Deteksi berbasis jendela foreground yang menutupi hampir seluruh monitor (fullscreen / borderless).
- Polling timer ~1–2 detik agar CPU rendah.
- HDR dikontrol lewat DisplayConfig API (`DisplayConfigGetDeviceInfo` / `DisplayConfigSetDeviceInfo`).

---

## English

### Requirements
- Windows 11 (recommended; Windows 10 2004+ may work)
- HDR-capable display (Advanced Color)
- GPU/driver with Windows HDR support
- .NET 8 SDK to build — or use the self-contained publish output

### Build
```bat
cd AutoHDR
dotnet restore
dotnet build -c Release
```

### Publish (single-file, win-x64)
Run `publish.bat`, or:
```bat
dotnet publish AutoHDR\AutoHDR.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```
Output: `publish\win-x64\AutoHDR.exe`

### Usage
1. Run `AutoHDR.exe` — it starts in the system tray.
2. **Enable AutoHDR** (on by default): when a fullscreen game is detected → HDR ON; when the game closes → HDR is restored **only if AutoHDR turned it on**.
3. Tray menu: Enable/Disable, Toggle HDR now, Settings, Exit.
4. Balloon tip if no HDR-capable display is found.

### Configuration
Path: `%AppData%\AutoHDR\config.json`  
(Resolved via `Environment.SpecialFolder.ApplicationData`.)

- **Empty whitelist** → any fullscreen/borderless app not on the exclusion list.
- **Non-empty whitelist** → only listed executable names (e.g. `eldenring`, `Cyberpunk2077`).
- Default exclusions include explorer, browsers, Visual Studio/Code, Office, Discord, Slack, etc.

### Technical notes
- HDR via P/Invoke: `QueryDisplayConfig`, `GetDisplayConfigBufferSizes`, `DisplayConfigGetDeviceInfo` (`GET_ADVANCED_COLOR_INFO` = 9), `DisplayConfigSetDeviceInfo` (`SET_ADVANCED_COLOR_STATE` = 10).
- Primary display preferred; optionally all HDR-capable displays.
- Low CPU: WinForms timer polling (default 1.5s).

### Linux build caveat
On Linux, `dotnet build` may fail because the **Windows targeting pack / Windows Forms workload** is not installed. That is expected. The project is valid for building on Windows 11 with the .NET 8 SDK.
