# NUT-Client
# NUT Multi-UPS Redundanz-Monitor für Windows

Ein leichtgewichtiger System-Tray-Client für **Network UPS Tools (NUT)**, speziell entwickelt für Umgebungen mit mehreren USV-Quellen (Dual-UPS-Redundanz). 

Das Tool überwacht mehrere NUT-Server bzw. USV-Instanzen parallel, wertet den Status (`OB` = On Battery, Ladezustand in Prozent) aus und steuert zuverlässig kritische Alarm- oder Shutdown-Szenarien bei Redundanzverlust.

## Funktionen
- **Multi-USV-Überwachung:** Überwacht mehrere USVen gleichzeitig (z. B. in einer redundanten Stromversorgungsumgebung).
- **Intelligente Auswertung:** 
  - *Szenario A:* Alle USVen antworten, alle laufen auf Batterie und mindestens eine unterschreitet den kritischen Schwellenwert.
  - *Szenario B (Redundanzverlust):* Eine USV fällt aus (Kommunikationsfehler), während die verbleibende USV auf Batterie läuft und kritisch wird.
- **System-Tray-Integration:** Läuft unauffällig im Hintergrund mit visuellem Status.
- **Automatisierter Notfall-Shutdown:** Löst bei kritischem Zustand automatisch den Windows-Herunterfahrvorgang mit individuellem Grund aus.

## Voraussetzungen
- Ein laufender NUT-Server (`upsd`), der erreichbar ist.
- .NET Framework / .NET Runtime (je nach verwendeter Version).

## Verwendung
1. Lade die aktuelle `.exe` aus den [Releases](../../releases) herunter.
2. Konfiguriere die Verbindung zu deinem NUT-Server.
3. Starte das Tool – es platziert sich im System-Tray.

## Lizenz
Open Source / MIT License 
