# Onyx

Ein minimalistisches Desktop-Overlay für Windows 11: eine **Dynamic Island** oben in der Mitte, eine eigene **schwebende Taskleiste** und dezente **Desktop-Widgets** – alles im dunklen, leicht transparenten Look.

## Download

Die neueste Version gibt es immer unter **[Releases](../../releases/latest)** → `Onyx.exe` herunterladen und starten. Keine Installation nötig.

> Für die CPU-Temperatur braucht Onyx Adminrechte und den Treiber **PawnIO**. Fehlt etwas, steht unter dem CPU-Widget „Admin nötig“ bzw. „Treiber fehlt“ – ein Klick darauf behebt es.

## Funktionen

**Dynamic Island**
- Zeigt das aktuelle Lied (Spotify, Browser, …) mit Cover, Fortschritt und Steuerung
- Klappt beim Darüberfahren weich auf: CPU, RAM, Netzwerk und Lautstärkeregler
- Zahnrad öffnet die Einstellungen direkt in der Island

**Taskleiste**
- Ersetzt die Windows-Taskleiste, schwebend und abgerundet
- Angepinnte Apps in derselben Reihenfolge wie bei Windows, mit Animationen
- Eigene Popups für Netzwerk, Lautstärke und Aktivitätszentrum (inkl. laufender Apps, die man beenden kann)
- Eigenes Startmenü mit Suche über alle installierten Apps
- Rechtsklick: Task-Manager und Taskleisten-Einstellungen (Transparenz, automatisch einklappen)
- Verschwindet in Vollbild-Spielen; mit der Windows-Taste kommt sie zurück, bis man wieder ins Spiel klickt

**Widgets**
- Download, Upload, Ping, GPU-Temperatur, CPU-Verlauf, RAM und Festplatten
- CPU-Temperatur unten rechts
- Frei anordnen über „Widgets anordnen“ in den Island-Einstellungen

**Einstellungen**
- Farben für Island, Taskleiste und Widgets
- Mit Windows starten (mit Adminrechten, damit die Temperatur funktioniert)
- Alles wird in `%APPDATA%\Onyx\settings.json` gespeichert

## Selbst bauen

Voraussetzung: [.NET 8 SDK](https://dotnet.microsoft.com/download) unter Windows.

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Die fertige Datei liegt danach in `publish/Onyx.exe`.

## Technik

C# / WPF auf .NET 8 · [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) für Temperaturen · Windows-Media-Steuerung über die System-Media-Transport-Controls

Beenden: Rechtsklick auf die Island → „Beenden“ (die normale Windows-Taskleiste kommt dann zurück).
