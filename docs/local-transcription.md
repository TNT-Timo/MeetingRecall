# Sprint 6 – lokale Transkription

Sprint 6 ergänzt MeetingRecall um eine vollständig lokale Speech-to-Text-Pipeline auf Basis von `whisper.cpp` über die .NET-Bindings von Whisper.net.

## Ablauf

1. Die primäre Audiospur eines Gesprächs wird geladen.
2. MeetingRecall erzeugt temporär eine 16-kHz-Mono-WAV-Datei.
3. Beim ersten Einsatz wird das mehrsprachige Whisper-Large-v3-Modell in `Models/Whisper/ggml-large-v3.bin` heruntergeladen.
4. Whisper verarbeitet die WAV-Datei lokal und liefert Textsegmente mit Start- und Endzeit.
5. Die Segmente, die erkannte Sprache und der Verarbeitungsstatus werden in SQLite gespeichert.
6. Zeitstempel im Transkript springen direkt zur passenden Position im Audioplayer.

Audio und Transkript werden nicht an OpenAI oder einen anderen Cloud-Dienst gesendet. Nur der einmalige Modell-Download benötigt eine Internetverbindung.

## Hardwarebeschleunigung

MeetingRecall wählt die Hardware automatisch. Die Transkription versucht nacheinander CUDA 13, CUDA 12 und Vulkan. Erst wenn alle GPU-Wege nicht verfügbar sind oder fehlschlagen, wechselt sie auf CPU; dort verwendet sie alle für den Prozess verfügbaren logischen Prozessoren.

Jeder GPU-Versuch läuft in einem eigenen isolierten Worker-Prozess. Ein Fehler oder nativer Absturz in einer GPU-Runtime beendet deshalb weder die Desktop-App noch verhindert er den Versuch mit der nächsten GPU-Runtime. Nach Abschluss zeigt die Statuszeile den tatsächlich verwendeten Modus an, zum Beispiel `Cuda · GPU`, `Vulkan · GPU` oder `CPU-Fallback · 24 Threads`.

## Bedienung

- Gespräch auswählen.
- Im Transkriptbereich **Transkript erstellen** wählen.
- Den einmaligen Modelldownload bestätigen.
- Während Download und Verarbeitung zeigt die App den aktuellen Schritt an.
- Die Transkription kann abgebrochen werden; das Gespräch bleibt erhalten.
- Ein bestehendes automatisch erzeugtes Transkript kann bewusst neu erstellt werden.

## Aktueller Stand

- Sprechertrennung und Stimmenabgleich mit TitaNet-Large sind als Teil desselben lokalen Speech-to-Text-Ablaufs integriert.
- Automatische Zusammenfassung und inhaltliche Titel werden lokal mit Qwen3-14B erzeugt.
- Die CPU-Laufzeit setzt auf Windows 11 eine aktuelle Visual-C++-Laufzeit voraus.
- Formate, die Windows Media Foundation nicht dekodieren kann, benötigen später den geplanten FFmpeg-Fallback.

## Lokaler Smoke-Test

1. `feature/sprint-6-local-transcription` auschecken.
2. Lösung neu bauen und alle Tests ausführen.
3. Eine kurze deutsche WAV-, MP3- oder M4A-Datei öffnen.
4. **Transkript erstellen** wählen und den Modelldownload bestätigen.
5. Prüfen, dass Segmente mit Zeitstempeln erscheinen.
6. Einen Zeitstempel anklicken und kontrollieren, dass der Player an die richtige Position springt.
7. App neu starten und prüfen, dass das Transkript weiterhin vorhanden ist.
8. Eine laufende zweite Transkription abbrechen und prüfen, dass Gespräch und Audio unverändert bleiben.
