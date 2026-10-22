# Verarbeitungswarteschlange

MeetingRecall führt rechenintensive lokale KI-Aufträge über eine gemeinsame FIFO-Warteschlange aus.

## Ziele

- Whisper- und Qwen-Worker konkurrieren nicht gleichzeitig um GPU/VRAM.
- Aufnahme, Import und Navigation bleiben während Modell-Download und Inferenz verfügbar.
- Weitere Transkriptionen, Zusammenfassungen, Titelvorschläge und Chat-Fragen können während einer laufenden Verarbeitung eingereiht werden.
- Mehrere Chat-Fragen desselben Gesprächs werden in Reihenfolge verarbeitet; der Verlauf für eine Folgefrage wird erst beim tatsächlichen Start des Jobs aufgebaut.
- Wartende und aktive Jobs können getrennt abgebrochen werden.

## Verhalten

Die Kopfzeile zeigt während der Verarbeitung einen Warteschlangenindikator. Ein Klick darauf zeigt den aktiven Job und die nächsten wartenden Jobs. Der aktive Job oder alle wartenden Jobs können dort abgebrochen werden.

Transkript-, Zusammenfassungs- und Chat-Karten zeigen zusätzlich an, wenn der ausgewählte Auftrag aktiv ist oder an welcher Position er wartet.

Die Warteschlange lebt absichtlich nur für die aktuelle App-Sitzung. Beim Schließen von MeetingRecall wird der aktive Job abgebrochen und die wartende Queue verworfen.

## Konsistenz

Destruktive Datenoperationen wie vollständige Sicherungswiederherstellung oder Werkreset starten nicht, solange Verarbeitungsjobs aktiv oder eingereiht sind. Ein Gespräch kann ebenfalls nicht endgültig gelöscht werden, solange dazu ein Job aktiv oder wartend ist.

Chat-Nachrichten ohne fertige Antwort werden nicht persistiert. Dadurch entstehen bei abgebrochenen oder noch wartenden Fragen keine halbfertigen Einträge nach einem Neustart.


## Oberflächenverhalten

Der Warteschlangenknopf bleibt auch bei leerer Queue sichtbar und wird dann bewusst grau und zurückhaltend dargestellt. Der Tooltip nennt die aktuelle Anzahl der Aufträge.

Die Warteschlange öffnet als kompakter Flyout statt als modaler Windows-Dialog. Aktive und wartende Aufträge bleiben über Auftragstyp und Gesprächstitel eindeutig erkennbar. Jede Zeile besitzt einen eigenen Abbrechen-Button. Zur besseren Lesbarkeit wechseln die Zeilen zwischen zwei sehr nah beieinanderliegenden Theme-Flächen; der aktive Auftrag erhält lediglich einen kleinen Akzentindikator.
