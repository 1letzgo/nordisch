# Eine Welt – Startpunkt wählen, Regionen bei Bedarf laden

Vereinbart am 13.10.2026. Ziel: kein Kartenmenü mehr. Der Spieler wählt einen **Startpunkt** (einen je Gebiet) und kann sich
von dort überall hinbeamen, wo es Daten gibt. Auf dem Gerät liegt nur, was gebraucht wird; neue Gebiete erscheinen ohne
App-Update.

## Begriffe
- **Gebiet** (`areas.json`): wie bisher – eigenes lokales Koordinatensystem (Mittelpunkt lat/lon, 1 m = 1 Unit),
  Maske/Ausdehnung, Edits, Map-Editor. Gebiete grenzen nicht aneinander.
- **Kachel** (200 m, `WorldTile`): Streaming-Einheit im Speicher (gibt es schon, `WorldStreamer`).
- **Region** (1000 m = 5 × 5 Kacheln): **Download-Einheit**. Schlüssel `<gebiet>/<rx>_<rz>` mit `rx = floor(x / 1000)`.

## Daten
- `world.json` (Release `maps`, klein, wird bei jedem Start geladen und gecacht):
  `{schema, format, unity-Builds, areas: [{id, name, lat, lon, bounds, vehicles, start: {x,y,z,yaw}, thumb, area: Datei,
  regions: [{key, url, size, sha256, version}] }]}`. Basis-URL steht drin → Umzug zu Cloudflare R2 ohne App-Update.
- **Gebietspaket** `<id>.area.bundle` (pro Plattform): Gebietsdaten ohne Geometrie – AreaInfo-Werte, Minimap-Textur,
  POIs, Straßenindex, Ampeln/Schilder, Fahrrad-/Autoplätze, Attribution. Ein paar MB.
- **Regionspaket** `<id>-<rx>_<rz>.bundle`: die 25 `WorldTile`-Assets + `RegionData` (Fassaden-Records, Schildertexte
  der Region). 1–20 MB.
- Hosting: ein GitHub-Release pro Gebiet (`maps-<id>`, Asset-Limit je Release), `world.json` im Release `maps`.

## App
- **Eine Welt-Szene** im Build (Licht, Himmel, Nebel, Spieler, HUD). Kein Szenen-Bundle mehr pro Gebiet; `AreaInfo` wird
  zur Laufzeit aus dem Gebietspaket erzeugt.
- **RegionStore** (ersetzt MapStore-Download): lädt Regionen im Umkreis ~1,5 km um den Spieler, Vorladen in
  Bewegungsrichtung, max. 2 parallel; Prüfsumme, LZ4-Umpacken wie heute. LRU-Cache mit Obergrenze (z. B. 1 GB, in den
  Einstellungen), nie die Regionen ums aktuelle Ziel. Offline: nur gecachte Regionen; Rand = unsichtbare Wand.
- **WorldStreamer** fragt Kacheln beim RegionStore an; fehlt die Region, wartet die Kachel (Ladehinweis im HUD, wenn der
  Spieler schneller ist als der Download).
- **Beamen** innerhalb eines Gebiets wie heute (Ziel-Regionen zuerst laden). **Zwischen Gebieten**: Welt-Karte →
  Gebiet wählen → alles entladen, neues Gebiet in den Ursprung (lokale Koordinaten), Start-Regionen laden, absetzen.
- **Menü**: Figur + Liste der Startpunkte (Thumbnail, Name, „geladen“/Größe des Start-Downloads). Letzte Position pro
  Gebiet bleibt (PositionMemory).
- **Updates**: Region mit neuer Version → beim nächsten Betreten neu laden (alte bleibt bis dahin nutzbar).

## Importer / Tools
- `WriteTiles` bleibt; `MapBundles` baut statt Szene+Kacheln: Gebietspaket + Regionspakete.
- Was heute als GameObjects in der Gebietsszene landet (TrafficLights, SignTexts, FacadeDetails-Daten, SpawnPoint,
  AreaInfo) wird zu Daten-Assets (`AreaData`, `RegionData`).
- `publish_maps.py`: lädt nur Regionen mit geändertem CRC hoch, schreibt `world.json`.
- Editor-Play: Welt-Szene liest `AreaData`/Kacheln direkt aus `Assets/Generated` (kein Download).

## Reihenfolge
1. AreaData/RegionData statt Szeneninhalt, Welt-Szene, Editor-Play.
2. Regionspakete + `world.json` + Publisher.
3. RegionStore (Download, Cache, Offline) + WorldStreamer-Anbindung.
4. Menü (Startpunkte) + Welt-Karte + Beamen zwischen Gebieten.
5. Instancing für Bäume/Props (kleinere Regionen).

Format-Nummer: `MapStore.Format` → 3. Alte Apps behalten ihre Format-2-Pakete, bis sie aktualisiert werden.
