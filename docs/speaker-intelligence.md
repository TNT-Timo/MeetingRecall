# Sprint 7: lokale Sprecherintelligenz

## Ziel

Sprint 7 trennt Sprecher innerhalb eines Transkripts, ordnet jedem Sprecher ein globales lokales Profil zu und erkennt benannte Stimmen in späteren Gesprächen wieder.

## Verarbeitung

1. Der normale Speech-to-Text-Ablauf startet nach dem Audioimport und normalisiert das Audio auf 16 kHz Mono.
2. Whisper Large-v3 erzeugt Textsegmente mit Zeitbereichen.
3. Pyannote segmentiert die Sprecherwechsel; NVIDIA NeMo TitaNet-Large erzeugt 192-dimensionale Stimm-Embeddings.
4. Für stabile Profile werden ausschließlich überlappungsfreie Sprachanteile verwendet, in mehrere Fenster geteilt, L2-normalisiert und per Konsens zusammengeführt.
5. Segmente werden innerhalb des Gesprächs zu Stimmen gruppiert und benannte lokale Stimmabdrücke mit einem konservativen Cosinus-Abgleich zugeordnet.
6. Sprecherzuordnungen, Profilfarben und modellversionierte Stimmabdrücke werden in SQLite gespeichert.

Gesprächsaudio und daraus abgeleitete Stimmmerkmale verlassen den Rechner nicht. Es gibt keine Sprecher-API und keinen Cloud-Abgleich.

## Personenverwaltung

Unter **Einstellungen → Personen** können alle erkannten Personen verwaltet werden:

- neutralen Namen durch einen echten Namen ersetzen
- eine gedämpfte, transkriptgeeignete Profilfarbe auswählen
- Namen oder Farbe später ändern
- ein Profil einschließlich seines lokalen Stimmabdrucks löschen

Beim Löschen bleiben Gespräch und Transkript erhalten. Alle betroffenen Segmente werden transaktional einem neuen anonymen Profil wie `Person 0002` zugeordnet. Die gelöschte Stimme wird in neuen Gesprächen nicht mehr automatisch erkannt.

## Transkript und Audio

- Jede Passage zeigt Sprechername, Farbe und Startzeit.
- Ein Klick auf die Passage oder ihren Zeitstempel setzt den Audioplayer an den Segmentanfang.
- Die Audiowiedergabe scrollt das Transkript nicht automatisch.
- Nur **Zur Textpassage** scrollt bewusst zur Passage an der aktuellen Wiedergabeposition.

## Modellentscheidung

MeetingRecall unterstützt ausschließlich Whisper Large-v3 (`ggml-large-v3.bin`) für Speech-to-Text und NVIDIA NeMo TitaNet-Large (`nemo-en-titanet-large.onnx`) für Sprecher-Embeddings. Das frühere ERes2Net-Modell wird nach der Installation von TitaNet-Large aus dem verwalteten Modellordner entfernt und nicht mehr verwendet.

Gespeicherte Stimmabdrücke tragen die Kennung ihres Embedding-Modells. Dadurch werden bestehende ERes2Net-Vektoren beim Modellwechsel nicht mit TitaNet-Vektoren verglichen, selbst wenn beide zufällig dieselbe Vektorlänge besitzen. Im neuronalen Ablauf gibt es keinen Rückfall auf den früheren akustischen Personenabgleich; ohne belastbares TitaNet-Embedding bleibt eine Stimme anonym.

## Qualitätshinweis

Der Stimmenabgleich ist bewusst vollständig lokal und benötigt keinen zusätzlichen Cloud- oder Kontodienst. Akustisch sehr ähnliche Stimmen, starke Hintergrundgeräusche, kurze Einwürfe und überlappende Sprache können eine manuelle Korrektur erfordern. Benutzerseitige Namen und Farben bleiben dabei maßgeblich.


## Qualitätskorrektur in Sprint 8

Die Sprecherzuordnung wird nicht mehr für jedes Whisper-Segment isoliert entschieden.
Stattdessen entsteht zunächst eine vorläufige Gruppierung über das gesamte Gespräch,
die anschließend ähnliche Cluster zusammenführt und kurze unsichere Passagen
konsolidiert.

Für die gesprächsübergreifende Wiedererkennung gelten bewusst strengere Regeln:

- ein bekannter Name benötigt mindestens zwei belastbare Samples und mehrere Sekunden Sprache
- der beste Treffer muss nicht nur einen strikten Grenzwert erfüllen, sondern auch klar besser als der zweitbeste bekannte Stimmabdruck sein
- kurze Passagen, Flüstern, Lachen und Stimmverstellungen erzeugen nicht automatisch eine neue dauerhafte Person
- bei Unsicherheit bleibt ein neutrales Personenprofil erhalten, statt einen bekannten Namen zu raten
- automatisch zugeordnete Passagen werden nicht mehr in den gespeicherten Stimmabdruck einer benannten Person zurückgemischt

Der letzte Punkt verhindert Profil-Drift: Eine einzelne Fehlzuordnung kann den
Stimmabdruck einer Person nicht mehr schrittweise verunreinigen und dadurch weitere
Fehlzuordnungen in späteren Gesprächen auslösen.

TitaNet-Large verwendet mehrere saubere Sprachfenster je erkannter Stimme. Überlappende
Passagen anderer Sprecher werden vor der Embedding-Bildung entfernt; ungültige, stille
oder untereinander stark abweichende Fenster fließen nicht in den gespeicherten
Stimmabdruck ein. Für bekannte Namen gilt zusätzlich der von NeMo vorgesehene
Cosinus-Grenzwert samt Abstand zum zweitbesten Profil. Bei Unsicherheit bleibt die
Zuordnung anonym.

Bei langen Gesprächen stammen diese Fenster verteilt aus dem gesamten zeitlichen
Verlauf statt nur aus den ersten Sprachpassagen. Lange, stabile Fenster erhalten beim
Konsens ein höheres Gewicht als kurze Restfenster. An nahezu gleich großen
Sprecherüberlappungen wird zusätzlich die am Segmentmittelpunkt aktive Stimme
bevorzugt; dadurch entscheidet nicht mehr die zufällige interne Clusternummer über
knappe Sprecherwechsel.


## Weitere Stabilisierung in Sprint 8

Gesprächscluster besitzen zusätzlich zum laufenden Mittelpunkt einen festen Anker aus dem ersten belastbaren Sprachsample. Eine neue Passage muss sowohl zum Mittelpunkt als auch zu diesem Anker passen. Dadurch kann ein Cluster nicht mehr schrittweise über Zwischenstimmen wandern und am Ende fast das gesamte Gespräch einer einzigen Person zuordnen. Die nachgelagerte Zusammenführung verwendet einen nochmals strengeren Grenzwert.

Über **Personen → Alle löschen** können alle gespeicherten Namen und Stimmabdrücke gemeinsam entfernt werden. Die Aktion benötigt eine ausdrückliche Bestätigung. Vorhandene Transkripte bleiben erhalten und bekommen neutrale Ersatzprofile, damit keine Gesprächsdaten gelöscht werden.
