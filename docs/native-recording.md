# Native Aufnahme

Sprint 5 zeichnet Mikrofon und Windows-Desktopaudio vollständig lokal auf.

## Pipeline

1. Der Benutzer wählt ein aktives Mikrofon und eine aktive Windows-Audioausgabe.
2. `WasapiCapture` schreibt die Mikrofonspur nach `microphone.wav`.
3. `WasapiLoopbackCapture` schreibt die ausgewählte Audioausgabe nach `desktop.wav`.
4. Der Zeitpunkt des ersten Audiopakets jeder Quelle wird erfasst, damit der Mix kleine Startunterschiede durch Stille ausgleichen kann.
5. Beide Quellen werden lokal auf 48 kHz Stereo normalisiert und zu `primary.wav` gemischt.
6. Gespräch, Hauptspur und vorhandene Rohspuren werden in einer SQLite-Transaktion gespeichert.

## Dateistruktur

```text
Meetings/<conversation-id>/
├── .recording-session.json   # nur während einer laufenden oder unterbrochenen Aufnahme
└── Audio/
    ├── microphone.wav        # optionale Mikrofon-Rohspur
    ├── desktop.wav           # optionale Desktop-Rohspur
    └── primary.wav           # sichtbare, gemischte Hauptspur
```

Bei erfolgreichem Abschluss wird der Sitzungsmarker entfernt. Beim bewussten Verwerfen wird der komplette Meetingordner gelöscht.

## Datenschutz

Die Aufnahme, Mischung und Speicherung erfolgen lokal. Es werden keine Audioinhalte an einen Server übertragen.

## Abgrenzung

Die Desktopspur ist ein WASAPI-Loopback der ausgewählten Windows-Audioausgabe. Sie enthält daher alle Töne, die über dieses Gerät ausgegeben werden. Eine isolierte Aufnahme nur eines einzelnen Teams-, Discord- oder Browserprozesses ist noch nicht enthalten.

## Lokaler Smoke-Test

1. `feature/sprint-5-native-recording` auschecken und die App mit `F5` starten.
2. **Neue Aufnahme** wählen.
3. Mikrofon und Audioausgabe prüfen und die Aufnahme starten.
4. Während der Aufnahme sprechen und zusätzlich ein kurzes Windows-Audiosignal abspielen.
5. **Aufnahme stoppen** wählen und speichern.
6. Prüfen, ob das Gespräch im Archiv erscheint und `primary.wav` abspielbar ist.
7. Im Datenordner prüfen, ob `primary.wav`, `microphone.wav` und `desktop.wav` vorhanden sind.
8. Eine zweite kurze Aufnahme starten und über **Verwerfen** abbrechen; dafür darf kein Gespräch bestehen bleiben.
