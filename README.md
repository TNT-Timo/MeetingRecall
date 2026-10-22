# MeetingRecall

MeetingRecall ist eine lokale Windows-Desktopanwendung für die Aufzeichnung, Transkription, Zusammenfassung und quellengebundene Beantwortung von Fragen zu Gesprächen und Meetings.

## Projektstatus

- Sprint 1–3: WinUI-App-Shell, Domänenmodell und lokale SQLite-Persistenz
- Sprint 4–5.5: Audioimport, Player, native Aufnahme und UI-Polish
- Sprint 6: vollständig lokale Transkription mit Whisper Large-v3
- Sprint 6.5: vollständige Sicherung, Wiederherstellung und Reset
- Sprint 7: lokale Sprechertrennung und gesprächsübergreifende Stimmerkennung mit TitaNet-Large, Profilfarben und Personenverwaltung
- Sprint 8: feste lokale Verarbeitung mit Whisper Large-v3 und Qwen3-14B, lokale Zusammenfassungen mit Faktenprüfung, loop-resistente Transkription, Titelvorschläge und optionales Live-Transkript
- Sprint 9: lokaler Mini-Chat für explizite Fragen zum ausgewählten Gespräch; Antworten verwenden ausschließlich geprüfte Transkriptquellen mit anklickbaren Zeitstempeln
- Verarbeitungswarteschlange: Whisper- und Qwen-Aufträge laufen FIFO nacheinander, während Aufnahme, Import, Navigation und das Einreihen weiterer Aufträge verfügbar bleiben

Die inhaltliche Archivsuche und der lokale MCP-Endpunkt folgen in späteren Sprints.

## Projekte

- `MeetingRecall.Desktop` – WinUI-3-Oberfläche und Desktop-Integration
- `MeetingRecall.Core` – Domänenmodelle, Regeln und Schnittstellen
- `MeetingRecall.Infrastructure` – lokale technische Implementierungen
- `MeetingRecall.Tests` – automatisierte Unit- und Integrationstests

Abhängigkeitsrichtung:

```text
Desktop -> Core
Desktop -> Infrastructure -> Core
Tests -> Core + Infrastructure
```

## Voraussetzungen

- Windows 10 oder Windows 11
- Visual Studio mit der Workload **WinUI-Anwendungsentwicklung**
- .NET 8 SDK

## Starten

1. `MeetingRecall.slnx` in Visual Studio öffnen.
2. `MeetingRecall.Desktop` als Startprojekt auswählen.
3. Plattform `x64` auswählen.
4. Mit `F5` oder `Ctrl+F5` starten.

Beim ersten Start wird unter `%LOCALAPPDATA%\MeetingRecall` automatisch der lokale Arbeitsbereich einschließlich `Database\MeetingRecall.db` erstellt.

## Tests

Im Visual Studio Test Explorer `Run All` ausführen. Die Persistenztests verwenden eigene temporäre Verzeichnisse und verändern keine produktiven MeetingRecall-Daten.

## Datenschutzgrenze

Gesprächsinhalte werden lokal verarbeitet und gespeichert. Die einzige geplante Netzwerkfunktion ist eine isolierte Prüfung auf verfügbare Programmupdates.
