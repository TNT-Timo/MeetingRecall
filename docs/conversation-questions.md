# Sprint 9 – quellengebundene Fragen zum Gespräch

## Ziel

Nach Audio, Transkript und Zusammenfassung bildet **Fragen zum Gespräch** den vierten Verarbeitungsschritt. Benutzer können freie Anschlussfragen zum aktuell ausgewählten Gespräch stellen. Audio, Transkript, Fragen und Antworten bleiben vollständig lokal.

## Festes Modell

Der Gesprächschat verwendet weiterhin **Qwen3-14B Q4_K_M**, ohne weiteren Download
oder Modellauswahl. Die bestehenden Q&A-Routingmarker halten Thinking deaktiviert.
Zusammenfassung, deren Prompts, Sampling, Worker und Oberfläche bleiben unverändert.

## Fakten- und Halluzinationsschutz

1. Passt das Transkript ins sichere Anfragebudget, bekommen Entwurf und Redaktion
   sämtliche Originalpassagen in chronologischer Reihenfolge.
2. Bei langen Aufnahmen erstellt `ContextualQuestionPipeline` einen chronologischen
   Navigationsindex aus **jedem** Abschnitt. Anfang/Ende helfen, Hauptthema und
   eingebettete Geschichten zu unterscheiden. Der Index wird hierarchisch verdichtet;
   alle Abschnittsnotizen bleiben für die semantische Auswahl verfügbar.
3. Ausgewählte Originalfenster, zeitliche Nachbarn, Anfang/Ende und eine ergänzende
   Wortsuche bilden den Antwortkontext. Notizen und Chatverlauf sind ausdrücklich
   **keine Faktenquellen**. Der Verlauf hilft beiden Durchläufen, Folgefragen aufzulösen.
4. Ein redaktioneller Modellaufruf liefert eine **korrigierte vollständige Antwort**,
   nicht bloß Ja/Nein-Verdikte. Er prüft Zuordnung, abschließende Korrekturen, Zahlen,
   Rechnungen, Negationen und Frageabsicht gegen Originale. Bei erfolgloser Auswahl
   wird vor einer endgültigen Ablehnung jeder Originalabschnitt nochmals durchsucht.
5. Quellennachweise müssen vorhandene IDs referenzieren. Die Anzeige verwendet exakte
   Originalausschnitte mit den ursprünglichen Segment-IDs und Zeitstempeln. Große
   Segmente werden Unicode-sicher geteilt, ohne den Rest zu verlieren. Wiederholungen
   werden im Chat nicht nochmals weggefiltert, da sie Korrekturen enthalten können.

Grün bedeutet: redaktionell als gestützt zurückgegeben, mit gültigen Quellen.
Orange bedeutet: ausdrücklich qualifizierte Einordnung, erzwungene Antwort oder
gültiger Entwurf, dessen Redaktion nach einer Formatwiederholung unlesbar blieb.
Eine ausdrückliche Ablehnung der Redaktion wird **nicht** als orange Antwort aus dem
widersprochenen Entwurf übernommen. Ohne brauchbare Antwort bleibt die Meldung rot.
Auch erzwungene Antworten durchlaufen die Redaktion und bleiben immer orange.

Die frühere pauschale Wortüberlappungs-/Negationsprüfung entfällt: eine sinngleiche
Antwort braucht keine identischen Wörter. Inhaltliche Prüfung ist modellbasiert,
nicht mathematisch garantiert; insbesondere ein zweiter Durchlauf desselben Modells
ist kein unabhängiger Faktenbeweis. Reale Tests mit Originalaudio/Transkript bleiben
notwendig. Die Unit-Tests prüfen Verträge und Ablauf mit simulierten Modellausgaben,
nicht die Sprachqualität von Qwen.

## Laufzeit und Regressionen

- Jede komplette Modellanfrage wird auf 14.000 UTF-8-Bytes begrenzt, mit separater
  Ausgabe-Reserve. Quellen, Notizen, Verlauf und Entwurf haben eigene Budgets.
- Der erste Chat zu einer langen Aufnahme benötigt zusätzliche Index-Aufrufe.
  Weitere Fragen nutzen den Index wieder; Antworten werden nie zwischengespeichert.
- Der flüchtige Cache hält höchstens zwei Aufnahmen. Sein Schlüssel umfasst
  Gesprächs-ID und SHA-256 des tatsächlichen Quelleninhalts einschließlich Text,
  Sprecherzuordnung und Zeiten. Änderungen erzwingen einen neuen Index.
- Abbruch und Laufzeitfehler werden weitergegeben. Unvollständige Indexläufe gelangen
  nicht in den Cache. Die existierende Chatpersistenz und UI bleiben unverändert.

Manuelle Qualitätsproben vor einem Release:

- Alibaba: acht von zehn richtig; die übernommene Antwort „Angelika Merkel“ war falsch,
  auch wenn der Moderator anschließend „Angela Merkel“ nennt.
- Parasocial: Hauptspiel versus eingebettetes Spiel; Lob für ein früheres Spiel nicht
  als Lob für Parasocial ausgeben. Endgültige 4/10 nach vorübergehender 3 erkennen.
- Folgefragen wie „Warum?“ und „War er also unzufrieden?“ müssen dieselben Originale
  verwenden, nicht die vorherige KI-Antwort zur neuen Faktenquelle machen.
- Unbekanntes (z. B. nicht genannte private Daten) bleibt unbekannt, auch bei Erzwingen.

## Oberfläche und Verhalten

- Die vierte Karte erscheint direkt nach der Zusammenfassung und ist erst mit vorhandenem Transkript aktiv.
- Antworten zeigen die exakten verwendeten Transkriptsegmente einschließlich Sprechername und Zeitstempel.
- Ein Klick auf den Zeitstempel setzt Audio und Transkript auf die passende Passage.
- Der Verlauf bleibt beim Wechsel zwischen Gesprächen während der laufenden App-Sitzung je Gespräch erhalten.
- Eine neue Transkription verwirft den bisherigen Sitzungsverlauf dieses Gesprächs automatisch.
- Der Chatverlauf wird pro Gespräch in der lokalen SQLite-Datenbank gespeichert und
  nach einem Neustart wiederhergestellt.
- Ein Rechtsklick auf eine Frage ermöglicht das Löschen der Frage samt Antwort. Ein
  Rechtsklick auf eine Antwort löscht nur diese Antwort.
- **Chat leeren** entfernt ausschließlich den gespeicherten Frageverlauf, niemals
  das Gespräch, Transkript oder Audio. Vor dem endgültigen Leeren muss der Benutzer
  den Vorgang in einem Warnungsdialog bestätigen.
- Das Kontextmenü zum Löschen einer einzelnen Frage oder Antwort kennzeichnet die
  destruktive Aktion einschließlich Symbol in der kritischen Systemfarbe Rot.
- Ändert sich das zugrunde liegende Transkript, wird ein dazu nicht mehr passender
  gespeicherter Chat anhand einer stabilen Transkript-Signatur verworfen.
- Download- und Verarbeitungsfortschritt sind abbrechbar; Downloadgeschwindigkeit wird in MB/s und Mbit/s dargestellt.

## Datenschutzgrenze

Netzwerkzugriff erfolgt ausschließlich für den einmaligen Modelldownload. Die
Inferenz läuft im isolierten lokalen Qwen-GPU-Worker. Chatverläufe werden lokal in
SQLite gespeichert, sind Bestandteil von Sicherungen und werden beim Löschen des
Gesprächs beziehungsweise beim Werkreset über die bestehenden Datenbankregeln
entfernt.
