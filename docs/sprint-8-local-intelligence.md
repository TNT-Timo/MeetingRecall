# Sprint 8: lokale Gesprächsintelligenz

## Ziel

Sprint 8 erzeugt aus einem vorhandenen Transkript eine strukturierte, vollständig lokale Zusammenfassung. Zusätzlich kann die Transkriptansicht der Audiowiedergabe optional live folgen.

## Zusammenfassung

Die Auswertung bleibt innerhalb der bestehenden Datenschutzgrenze:

- kein Gesprächstext wird an einen externen Dienst übertragen
- kein API-Schlüssel und kein Cloudkonto sind erforderlich
- festes Zusammenfassungsmodell: Qwen3-14B Q4_K_M (ca. 9,0 GB)
- das Modell wird beim ersten Einsatz ausdrücklich bestätigt und heruntergeladen
- lokale Ausführung über LLamaSharp/llama.cpp; nach dem Download ist keine Verbindung erforderlich
- fortsetzbarer Download mit Prozent, Geschwindigkeit, ETA, SHA-256-Prüfung und Abbruch

Die Zusammenfassung ist generativ und abstraktiv. Die dominante Sprache wird aus den tatsächlichen Transkriptabschnitten bestimmt und für Extraktion, Gesamtsynthese und Faktenprüfung verbindlich vorgegeben. Lange Transkripte werden an Segmentgrenzen aufgeteilt.

Qwen extrahiert zuerst einzelne, strukturierte Informationskandidaten. Entscheidungen, Vereinbarungen, Zahlen, Termine, Aufgaben, Status, Änderungen, Zusagen, Ergebnisse und offene Fragen besitzen dabei Vorrang vor bloßen Themenbeschreibungen. Jeder Kandidat muss einen wörtlichen, zusammenhängenden Transkriptbeleg enthalten; die Anwendung verwirft Kandidaten, deren Beleg im zugehörigen Abschnitt nicht tatsächlich vorkommt. Anschließend bewertet die Anwendung den Informationswert deterministisch und reicht höchstens die wichtigsten belegten Kandidaten mit stabilen Quellen-IDs an die Synthese weiter.

Jede Aussage der Gesamtsynthese muss ihre Quellen-IDs mitliefern. Der abschließende Prüfpass kontrolliert insbesondere Zahlen, Einheiten, Beträge, Termine, Verantwortliche, Negationen und Modalität. Vorschläge, Wünsche, Ziele, Fragen und Möglichkeiten dürfen nicht zu Entscheidungen oder Zusagen hochgestuft werden. Die Anwendung entfernt abschließend Aussagen ohne gültige Beleg-ID sowie numerische Angaben, die im referenzierten Transkriptausschnitt nicht vorkommen. Generische Aussagen bleiben erlaubt, wenn tatsächlich kein konkretes Ergebnis vorliegt oder ein Thema ausdrücklich ungeklärt blieb.

Das Ergebnis enthält:

- eine kurze Übersicht
- bis zu acht nach Informationswert priorisierte Kernpunkte
- explizit formulierte Entscheidungen
- explizit formulierte Aufgaben mit erkanntem Sprecher, sofern benannt
- einen kompakten Titelvorschlag

Entscheidungen und Aufgaben werden nur ausgegeben, wenn sie im Transkript ausdrücklich vorkommen. Bei Unterhaltungsvideos dürfen beide Abschnitte deshalb leer sein. Die Prompts verlangen bewusst eine inhaltliche Neuformulierung statt einer Auswahl und Verkettung einzelner Originalsätze.

## Transkription

Für die Transkription wird ausschließlich Whisper Large-v3 (ca. 3,1 GB) verwendet. Die Pipeline ist gegen Decoder-Schleifen gehärtet:

- vorheriger Decoderkontext wird nicht unkontrolliert in neue Fenster übernommen
- eine strengere Entropieschwelle löst bei repetitiver Ausgabe den Whisper-Fallback aus
- Stillewahrscheinlichkeit wird berücksichtigt
- ein nachgelagerter Filter erkennt ein- bis sechssegmentige Zyklen ab drei Wiederholungen
- erkannte Wiederholungssegmente werden vor Sprecheranalyse, Speicherung und Zusammenfassung verworfen

Der Filter behält den ersten Durchlauf eines erkannten Zyklus und setzt danach beim ersten neuen Inhalt fort. Normale, nichtzyklische Wiederholungen bleiben erhalten.

## Zusammenfassungsmodell

Für Zusammenfassungen wird ausschließlich Qwen3-14B Q4_K_M verwendet. Es gibt keine Modell- oder Qualitätsauswahl; dadurch verwenden neue und bestehende Installationen immer denselben lokalen Verarbeitungspfad.

## Titel

Ein Titelvorschlag wird mit der Zusammenfassung in der Gesprächssprache erzeugt. Er ersetzt den Gesprächstitel nicht automatisch. Der Benutzer kann ihn bewusst über **Als Titel übernehmen** anwenden, über **Weiteren Vorschlag** ausschließlich neue Titelkandidaten erzeugen oder über **Eigener Titel** einen freien Titel eingeben. Die erneute Titelerzeugung berechnet die bestehende Zusammenfassung nicht neu.

## Persistenz

SQLite-Schemaversion 5 ergänzt die Tabelle `meeting_summaries`. Sie speichert Übersicht, strukturierte Abschnitte, Titelvorschlag sowie Quell- und Erzeugungszeitpunkt.

- Löschen eines Gesprächs löscht die Zusammenfassung über den Fremdschlüssel mit.
- Erneute Transkription verwirft die bisherige Zusammenfassung.
- Die bestehende vollständige Sicherung erfasst die Daten automatisch als Teil der SQLite-Datenbank.
- Import und Werkseinstellungen benötigen keinen separaten Sonderpfad.

## Live-Transkript

Unter **Einstellungen → Allgemein → Live-Transkript** kann die synchrone Darstellung aktiviert werden.

Bei aktiver Option:

- wird der bereits gesprochene Anteil innerhalb des Whisper-Zeitsegments progressiv hervorgehoben
- bleibt die aktuelle Scrollposition unverändert; Audio-Wiedergabe und -Sprünge bewegen die Textansicht nie automatisch
- bleibt der Text auswählbar
- wird die normale Darstellung beim Ausschalten sofort wiederhergestellt

Whisper Large-v3 liefert im aktuellen Datenmodell verlässliche Segmentgrenzen, aber keine persistierten akustischen Wortgrenzen. Die Hervorhebung interpoliert deshalb innerhalb des echten Segmentzeitraums zeichenweise. Sie ist flüssig und deterministisch, aber keine phonemgenaue Karaoke-Ausrichtung.

Audio bewegt das Transkript unabhängig von dieser Option nie automatisch. **Zur Textpassage springen** führt den bewussten Sprung an die aktuelle Audioposition aus; ein Klick auf eine Transkriptpassage setzt umgekehrt weiterhin die Audioposition.

## Fehler und Abbruch

Die Zusammenfassung läuft asynchron und meldet Modelldownload, Modellladen, Abschnittsanalyse, Synthese, Faktenprüfung und Speicherung. Download und Inferenz können abgebrochen werden; ein unvollständiger Modelldownload wird beim nächsten Versuch fortgesetzt. Eine fehlgeschlagene oder abgebrochene Neugenerierung überschreibt keine bereits gespeicherte Zusammenfassung.

## Abgrenzung

Nicht Bestandteil von Sprint 8:

- freier Chat mit dem Gespräch
- semantische Archivsuche
- MCP-Endpunkt
- Cloudmodelle
- generative Textnachkorrektur des Whisper-Transkripts
- Cloudmodelle oder das Hochladen von Gesprächstext
