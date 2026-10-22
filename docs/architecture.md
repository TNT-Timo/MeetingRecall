# Softwarearchitektur

## 1. Ziel

Die Solution trennt Benutzeroberfläche, Produktlogik und technische Implementierungen. Dadurch können Audio-, Speicher- und KI-Komponenten später ersetzt oder erweitert werden, ohne die gesamte Anwendung umzubauen.

## 2. Projektverantwortung

### MeetingRecall.Desktop

- WinUI-3-Fenster, Seiten und Controls
- ViewModels und UI-Zustand
- Navigation und Benutzerinteraktion
- Windows-spezifische Desktopintegration
- Composition Root für Dependency Injection und Anwendungsstart

Die Desktop-Schicht enthält keine Datenbank-, Transkriptions- oder Modellimplementierungen.

### MeetingRecall.Core

- Domänenobjekte wie Gespräch, Person, Gruppe und Transkriptsegment
- fachliche Regeln und Validierung
- Schnittstellen für Speicher, Audio, Suche und lokale KI
- keine Abhängigkeit von WinUI, SQLite, FFmpeg oder konkreten KI-Modellen

### MeetingRecall.Infrastructure

- lokale Dateispeicherung und AppData-Pfade
- SQLite-Zugriff, Schema-Migrationen und Repositories
- Einstellungen und Updateprüfung
- spätere Adapter für Audio, Transkription, Sprechererkennung und lokale Modelle

Infrastructure darf von Core abhängen, Core jedoch niemals von Infrastructure.

### MeetingRecall.Tests

- Tests für Domänenregeln
- isolierte Integrationstests für Infrastrukturadapter
- Regressionstests für kritische Datenschutz- und Löschregeln

## 3. Abhängigkeitsregeln

```text
MeetingRecall.Desktop
  -> MeetingRecall.Core
  -> MeetingRecall.Infrastructure

MeetingRecall.Infrastructure
  -> MeetingRecall.Core

MeetingRecall.Core
  -> keine anderen MeetingRecall-Projekte
```

Zyklische Projektabhängigkeiten sind nicht zulässig.

## 4. UI-Regeln

- Die normale Oberfläche zeigt nur häufig benötigte Funktionen.
- Erweiterte und diagnostische Optionen liegen in einer zweiten Ebene.
- Gruppen bleiben optional, sind in der Hauptnavigation jedoch dauerhaft sichtbar.
- Primäre Aktionen werden klar hervorgehoben; technische Details bleiben dezent.
- Hell und Dunkel folgen standardmäßig der Windows-Einstellung.
- Neue Funktionen werden über Commands und ViewModels angebunden, nicht über umfangreiche Logik in Code-behind-Dateien.

## 5. Datenschutz- und Netzwerkgrenze

Meetingdaten, Audio, Transkripte, Zusammenfassungen, Embeddings und Chatverläufe dürfen nicht an externe Dienste übertragen werden.

Die einzige zulässige Internetfunktion ist ein isolierter Update-Dienst, der ausschließlich Versions- und Paketinformationen abruft. Andere Komponenten erhalten keinen allgemeinen Netzwerkzugriff.

## 6. Persistenzregeln

- Strukturierte Daten liegen in einer lokalen SQLite-Datei unter `%LOCALAPPDATA%\MeetingRecall\Database`.
- Audio, Modelle, Logs, temporäre Dateien und Exporte bleiben normale Dateien in getrennten Unterordnern.
- Core-Entitäten kennen SQLite nicht und werden über Repository-Schnittstellen gespeichert.
- Schemaänderungen erfolgen ausschließlich über versionierte, transaktionale Migrationen.
- Eine Conversation wird zusammen mit Gruppenbeziehungen, Sprecherbeziehungen, Transkriptsegmenten und Revisionen atomar geschrieben.
- Repository-Implementierungen dürfen keine Netzwerkverbindungen öffnen.
- Tests verwenden pro Testlauf einen isolierten temporären AppData-Stamm.

## 7. Aktueller Umfang

### Sprint 1

- native WinUI-App-Shell
- benannte Anwendung und angepasstes Paketmanifest
- dauerhaft sichtbare Gruppenfunktion
- globale Suche mit exakt/inhaltlich-Umschaltung
- Startaktionen für Aufnahme und Import als UI-Platzhalter
- Einstellungsnavigation als UI-Platzhalter

### Sprint 2

- persistence-unabhängiges Domänenmodell
- Gespräche, Gruppen, Sprecher und Transkriptsegmente
- unveränderlicher Originaltext und vollständige Bearbeitungshistorie
- automatisierte Domänentests

### Sprint 3

- lokale AppData-Verzeichnisstruktur
- SQLite-Schema und initiale Migration
- Conversation-, Speaker- und Group-Repositories
- Dependency-Injection-Registrierung
- Datenbankinitialisierung beim App-Start
- isolierte Persistenz-Integrationstests

Noch nicht enthalten sind funktionale Aufnahme, Import, Audioverarbeitung, Transkription, KI, Suche oder produktive Navigation.
