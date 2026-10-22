# Sprint 5.5 – UI Polish

## Ziel

Die Gesprächsdetailansicht soll ruhiger, eindeutiger und direkter bedienbar sein, ohne die Metadaten unnötig zu verkleinern.

## Änderungen

- Die redundante Überschrift `Archiv` wird ausgeblendet.
- Der technische Ursprung `native Aufnahme`, `Audioimport` oder `aus Video extrahiert` wird in der Detailansicht nicht mehr angezeigt.
- Die Dauer wird eindeutig beschriftet:
  - Deutsch: `Länge: 0:20`
  - andere UI-Sprachen: `Duration: 0:20`
- Dieselbe Dauerkennzeichnung wird auch in der Gesprächsliste verwendet.
- Die dauerhafte Erfolgsmeldung `Aufnahme gespeichert` wird entfernt; laufende und fehlerrelevante Statusmeldungen bleiben erhalten.
- Neben dem Gesprächstitel steht ein dezenter Bearbeiten-Stift.
- Der Titel kann direkt geändert und dauerhaft in SQLite gespeichert werden.

## Bewusste Abgrenzung

Die automatische inhaltliche Titelvergabe folgt erst nach der lokalen Transkription. Ein manuell gesetzter Titel bleibt dabei künftig maßgeblich und darf nicht ungefragt überschrieben werden.

## Lokaler Smoke-Test

1. Ein bestehendes Gespräch öffnen.
2. Prüfen, dass `Archiv` und der technische Quelltyp nicht mehr sichtbar sind.
3. Prüfen, dass die Metadaten `Länge:` beziehungsweise `Duration:` enthalten.
4. Den Titel über den Stift ändern, die App neu starten und die Persistenz kontrollieren.
5. Eine Aufnahme speichern und prüfen, dass keine dauerhafte Meldung `Aufnahme gespeichert` stehen bleibt.
