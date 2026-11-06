<div align="center">

<img src="docs/logo.png" width="128" alt="Onyx Logo"/>

# Onyx

**Dynamic Island, schwebende Taskleiste und Desktop-Widgets für Windows 11**

Dunkel, minimalistisch, leicht transparent – ein Desktop, der sich wie aus einem Guss anfühlt.

[![Download](https://img.shields.io/github/v/release/xjulu8x/Onyx?label=Download&style=for-the-badge&color=2B8CFF)](https://github.com/xjulu8x/Onyx/releases/latest)
![Windows 11](https://img.shields.io/badge/Windows-11-0B1A36?style=for-the-badge&logo=windows11&logoColor=white)
![.NET 8](https://img.shields.io/badge/.NET-8-1E1F23?style=for-the-badge&logo=dotnet&logoColor=white)

[**⬇ Neueste Version herunterladen**](https://github.com/xjulu8x/Onyx/releases/latest)

</div>

---

## ✨ Was ist Onyx?

Onyx legt sich über deinen Windows-Desktop und ersetzt die Taskleiste durch ein eigenes, ruhiges Design:

| | |
|---|---|
| 🏝️ **Dynamic Island** | Oben in der Mitte – zeigt das aktuelle Lied, klappt beim Darüberfahren auf und zeigt CPU, RAM, Netzwerk und Lautstärke. |
| 📌 **Eigene Taskleiste** | Schwebend und abgerundet, mit deinen angepinnten Apps, eigenem Startmenü und eigenen Popups. |
| 📊 **Desktop-Widgets** | Netzwerk, GPU- und CPU-Temperatur, CPU-Verlauf, RAM und Festplatten – frei anordnen. |
| 🎨 **Anpassbar** | Farben, Transparenz, automatisches Einklappen, Autostart. |

---

## 🚀 Installation

1. **[Onyx.exe herunterladen](https://github.com/xjulu8x/Onyx/releases/latest)** (unter „Assets“).
2. Die Datei an einen festen Ort legen, z. B. `Dokumente\Onyx\Onyx.exe`.
3. **Doppelklick** auf `Onyx.exe` – fertig. Keine Installation, kein .NET nötig.

> **Windows SmartScreen** kann beim ersten Start warnen („Der Computer wurde durch Windows geschützt“), weil die App nicht signiert ist.
> Klicke auf **„Weitere Informationen“ → „Trotzdem ausführen“**.

### 🌡️ CPU-Temperatur aktivieren

Für die CPU-Temperatur braucht Onyx **Adminrechte** und den Treiber **PawnIO**. Unter dem CPU-Widget unten rechts steht, was fehlt:

- **„Admin nötig · klicken“** → Klick startet Onyx mit Adminrechten neu.
- **„Treiber fehlt · klicken“** → Klick installiert PawnIO automatisch (über winget).

Danach erscheint die Temperatur von selbst.

### 🔁 Mit Windows starten

Island aufklappen → **Zahnrad** → **„Mit Windows starten“** einschalten.
Läuft Onyx dabei als Admin, startet es später automatisch mit Adminrechten (ohne Nachfrage), damit die Temperatur funktioniert.

### ⬆️ Updates

Onyx prüft selbst, ob es eine neue Version gibt. Dann wird das **Zahnrad in der Island blau**: Zahnrad → **„Update auf x.y.z“** klicken, den Rest erledigt Onyx. Die Datei wird vor dem Austausch geprüft, und die Einstellungen bleiben erhalten.

---

## 🧭 Bedienung

### Dynamic Island
| Aktion | Ergebnis |
|---|---|
| Maus darüber | Island klappt auf (Player, CPU, RAM, Netzwerk, Lautstärke) |
| Klick auf das Cover | Play / Pause |
| **Zahnrad** | Einstellungen in der Island |
| Rechtsklick | Schnellmenü inkl. **Beenden** |

### Taskleiste
| Aktion | Ergebnis |
|---|---|
| **Windows-Taste** / Start-Logo | Eigenes Startmenü mit Suche über alle Apps |
| Klick auf App | Öffnen bzw. in den Vordergrund holen |
| Rechtsklick auf App | Neues Fenster / Fenster schließen |
| Rechtsklick auf freie Stelle | **Task-Manager** und **Taskleisteneinstellungen** |
| WLAN- / Lautstärke-Symbol | Eigene Popups |
| Symbole von Hintergrund-Apps | Links- und Rechtsklick wie bei Windows (Steam, Discord, …) |
| **Aktivitätszentrum** | Schnellaktionen, Benachrichtigungen, laufende Apps beenden, Kalender |

**In Vollbild-Spielen** verschwindet die Taskleiste automatisch. Mit der **Windows-Taste** kommt sie zurück, bis du wieder ins Spiel klickst.

### Taskleisteneinstellungen
Rechtsklick auf die Taskleiste → **Taskleisteneinstellungen**:
- **Transparenz** – Regler von undurchsichtig bis sehr durchsichtig
- **Automatisch einklappen** – Leiste erscheint erst, wenn die Maus den unteren Rand berührt

### Widgets anordnen
Island → Zahnrad → **„Widgets anordnen“** → Widgets mit der Maus verschieben → **„Fertig“**.
Die Positionen bleiben auch nach einem Neustart gespeichert.

### Farben
Island → Zahnrad → unten **Island / Leiste / Widgets** wählen und eine Farbe anklicken.

### Design
Island → Zahnrad → **„Design“**: Themes per Klick, Akzentfarbe, Schrift, Animationstempo sowie Größe, Ecken und Deckkraft von Taskleiste, Island und Widgets. Alles wirkt sofort.

---

## ❓ Häufige Fragen

<details>
<summary><b>Wie beende ich Onyx?</b></summary>

Rechtsklick auf die Island → **Beenden** (oder Zahnrad → „Beenden“). Die normale Windows-Taskleiste kommt dann sofort zurück.
</details>

<details>
<summary><b>Die Windows-Taskleiste ist nach einem Absturz weg</b></summary>

Onyx einfach erneut starten und über „Beenden“ schließen – oder im Task-Manager „Windows-Explorer“ neu starten.
</details>

<details>
<summary><b>Wo werden die Einstellungen gespeichert?</b></summary>

In `%APPDATA%\Onyx\settings.json`. Zum Zurücksetzen Onyx beenden und die Datei löschen.
</details>

<details>
<summary><b>Wie deinstalliere ich Onyx?</b></summary>

1. In den Einstellungen „Mit Windows starten“ ausschalten.
2. Onyx beenden.
3. `Onyx.exe` und den Ordner `%APPDATA%\Onyx` löschen.
</details>

---

## 🛠️ Selbst bauen

Voraussetzung: [.NET 8 SDK](https://dotnet.microsoft.com/download) unter Windows.

```bash
git clone https://github.com/xjulu8x/Onyx.git
cd Onyx
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Die fertige Datei liegt danach in `publish/Onyx.exe`.

**Technik:** C# · WPF · .NET 8 · [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (Temperaturen) · Windows Media Controls (Musik)
