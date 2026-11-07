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
| 🏝️ **Dynamic Island** | Eine Kerbe am oberen Rand mit Uhrzeit und Musik. Beim Darüberfahren öffnet sich ein großes Panel mit Musik, Werkzeugen, Steuerung und Mitteilungen. |
| 📌 **Dock** | Die Taskleiste zeigt nur noch Start und deine Apps, alles andere ist in der Island. |
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

Island aufklappen → **Zahnrad** → **Allgemein** → **„Mit Windows starten“** einschalten.
Läuft Onyx dabei als Admin, startet es später automatisch mit Adminrechten (ohne Nachfrage), damit die Temperatur funktioniert.

### ⬆️ Updates

Onyx prüft selbst, ob es eine neue Version gibt. Dann wird das **Zahnrad in der Island blau**: Zahnrad → **Allgemein** → **„Aktualisieren“** klicken, den Rest erledigt Onyx. Die Datei wird vor dem Austausch geprüft, und die Einstellungen bleiben erhalten.

---

## 🧭 Bedienung

### Dynamic Island
| Aktion | Ergebnis |
|---|---|
| Maus darüber | Panel öffnet sich: **Start** (Musik, Lautstärke, Werkzeuge), **Steuerung** (Kacheln, Hintergrund-Apps), **Mitteilungen** (Benachrichtigungen, laufende Apps, Kalender) |
| Werkzeuge | Screenshot (Bereich oder ganz), Text vom Bildschirm, QR-Code lesen, Pipette, Aufnahme |
| Caps Lock, Lautstärketasten | Kurzer Hinweis in der Kerbe |
| Klick auf das Cover | Play / Pause |
| **Zahnrad** | Öffnet das Einstellungsfenster |
| Rechtsklick | Einstellungen und **Beenden** |

### Taskleiste
| Aktion | Ergebnis |
|---|---|
| **Windows-Taste** / Start-Logo | Eigenes Startmenü mit Suche über alle Apps |
| Klick auf App | Öffnen bzw. in den Vordergrund holen |
| Rechtsklick auf App | Neues Fenster / Fenster schließen |
| Rechtsklick auf freie Stelle | **Task-Manager** und **Einstellungen** |
| WLAN- / Lautstärke-Symbol | Eigene Popups |
| Pfeil neben dem Netzwerk-Symbol | Fenster mit den Symbolen der Hintergrund-Apps (Steam, Discord, …) |
| **Uhr** | Aktivitätszentrum: Schnellaktionen, Benachrichtigungen, laufende Apps beenden, Kalender |

**In Vollbild-Spielen** verschwindet die Taskleiste automatisch. Mit der **Windows-Taste** kommt sie zurück, bis du wieder ins Spiel klickst.

### Widgets anordnen
Einstellungen → **Widgets** → **„Anordnen“** → Widgets mit der Maus verschieben → **„Fertig“**.
Die Positionen bleiben auch nach einem Neustart gespeichert.

### Design
Island → Zahnrad öffnet das Einstellungsfenster. Alles wirkt sofort:

- **Allgemein:** Themes, Akzentfarbe, Schrift, Animationstempo, Rand (auch als Lichtlauf), Glas-Körnung
- **Taskleiste:** Form (Leiste, Dock, Drei Inseln), einfarbige App-Symbole, App-Anzeige, Uhr-Stil, Größen
- **Island:** Form (schwebend, angedockt, Kapsel), Bewegung, Größe, Ecken, Deckkraft
- **Widgets:** Linie, Balken oder Ringe, als Block oder einzeln platzierbar
- **Tageszeit:** Themes wechseln automatisch um 7 und 19 Uhr
- **Eigene Themes:** speichern und als `.onyxtheme`-Datei teilen

### Effekte
- **Musik-Glow:** Die Island leuchtet im Takt der Musik, wahlweise in der Farbe des Covers.
- **Hitze-Alarm:** Die Island wird rot, wenn CPU oder GPU die eingestellte Temperatur erreichen.
- **Profile:** Eine App bekommt ihr eigenes Theme, das nur gilt, solange sie im Vordergrund ist.

---

## ❓ Häufige Fragen

<details>
<summary><b>Wie beende ich Onyx?</b></summary>

Rechtsklick auf die Island → **Beenden** (oder Zahnrad → Allgemein → „Beenden“). Die normale Windows-Taskleiste kommt dann sofort zurück.
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
