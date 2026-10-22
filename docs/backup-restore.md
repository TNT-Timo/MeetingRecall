# Vollständige Sicherung, Wiederherstellung und Reset

Sprint 6.5 macht den gesamten lokalen MeetingRecall-Zustand portabel. Die Funktion befindet sich hinter dem Zahnrad oben rechts unter **Daten und Sicherung**.

## Vollständige Sicherung

Eine `.mrbackup`-Datei enthält:

- SQLite-Datenbank inklusive Gesprächen, Gruppen, Sprecherprofilen und Transkriptrevisionen
- sämtliche Meeting-Audiodateien und Rohspuren
- lokale Whisper- und spätere KI-Modelle
- Einstellungen und zukünftige lokale Indizes
- Exporte, soweit sie nicht selbst `.mrbackup`-Dateien sind
- Logs
- beliebige zukünftige persistente Unterordner des MeetingRecall-Datenverzeichnisses

Nicht enthalten sind:

- der komplette `Temp`-Ordner
- laufende `.download`- und `.partial`-Dateien
- SQLite-WAL-, SHM- und Journaldateien
- andere `.mrbackup`-Dateien im Exportordner

Vor dem Verpacken wird die SQLite-Datenbank mit der nativen Backup-API konsistent kopiert. Jede Datei erhält einen SHA-256-Prüfwert. Das fertig geschriebene Archiv wird vollständig in einen separaten Ordner extrahiert und validiert, bevor es als erfolgreicher Export gilt.

## Verschlüsselung

Verschlüsselte Sicherungen verwenden AES-256 im ZIP-Container. Die Oberfläche aktiviert den Passwortschutz standardmäßig und verlangt mindestens acht Zeichen. MeetingRecall speichert das Passwort nicht.

Ein unverschlüsselter Export ist nach einer ausdrücklichen Warnung möglich. Jeder mit Zugriff auf diese Datei kann die enthaltenen Gespräche, Audiodateien und Transkripte lesen oder abspielen.

## Wiederherstellung

Vor einer Wiederherstellung werden Manifest, Formatversion, Datenbankschema, Dateigrößen, SHA-256-Prüfsummen und SQLite-Integrität geprüft. Erst nach erfolgreicher Prüfung wird der aktuelle Datenordner atomar gegen den validierten Staging-Ordner ausgetauscht.

Wenn bereits lokale Daten, Modelle, Einstellungen, Exporte oder Logs vorhanden sind, verlangt die Oberfläche eine ausdrückliche Bestätigung, dass der gesamte aktuelle Zustand ersetzt wird. Ein leerer Grundzustand benötigt diese zusätzliche Überschreibwarnung nicht.

Schlägt der Austausch fehl, wird der vorherige Datenordner automatisch aus dem Rollback-Ordner wiederhergestellt.

## Werkseinstellungen

**MeetingRecall vollständig zurücksetzen** entfernt alle persistenten Daten und erzeugt anschließend den leeren Standardspeicher mit einer frisch migrierten SQLite-Datenbank. Der Vorgang erfordert die exakte Eingabe:

```text
DELETE ALL
```

Die Bestätigungsphrase lautet unabhängig von der gewählten Anwendungssprache immer
exakt `DELETE ALL`.

Der AppData-Wurzelordner selbst bleibt dabei bestehen. MeetingRecall verschiebt nur
seine Inhalte in einen temporären Rollback-Bereich, validiert zuerst die neue leere
Datenbank und entfernt anschließend den alten Zustand. Das vermeidet
Berechtigungsfehler durch die Windows-/MSIX-Dateivirtualisierung; schlägt bereits das
Verschieben eines Inhalts fehl, werden zuvor verschobene Inhalte zurückgelegt, ohne
den noch aktiven Datenordner zu löschen.

Aufnahme, Import, Transkription, Sicherung, Wiederherstellung und Reset dürfen nicht gleichzeitig laufen.

## Lokale Abnahme

1. Ein Gespräch mit Transkript und ein installiertes Whisper-Modell bereithalten.
2. Über das Zahnrad eine verschlüsselte Sicherung exportieren.
3. Prüfen, dass genau eine `.mrbackup`-Datei entsteht.
4. Einen zusätzlichen lokalen Testinhalt erzeugen.
5. Die Sicherung importieren und das Ersetzen bestätigen.
6. Prüfen, dass Gespräch, Audio, Transkript und Modell dem Sicherungszeitpunkt entsprechen.
7. App schließen und erneut starten; Zustand muss erhalten bleiben.
8. Werkseinstellungen öffnen, einen falschen Bestätigungstext testen und danach exakt `DELETE ALL` eingeben.
9. Prüfen, dass Archiv, Modelle und Datenordner leer beziehungsweise neu angelegt sind.
