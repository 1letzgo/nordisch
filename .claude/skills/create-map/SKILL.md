---
name: create-map
description: Add a new town/area ("Karte") to Nordisch – define it in areas.json, fetch OSM + LGLN geodata (LoD2, DGM1, DOM1 trees, DOP20 aerial, ALKIS parcels for garden hedges/fences), import into Unity, check snapshots, build to the iPhone. Use when the user wants another town, island or district in the game, or wants to enlarge/move an existing map.
---

# /create-map – neue Karte für Nordisch

Ziel: aus einem Ortsnamen eine spielbare Karte machen. Pipeline **Offline-Daten → Unity-Importer → Szene**;
Details zu jedem Baustein stehen in `CLAUDE.md` (Abschnitt „Wie eine Karte entsteht“) – bei Abweichungen gilt der Code.

## 0. Klären (nur was fehlt, nicht alles abfragen)

- **Ort und Mittelpunkt**: Marktplatz/Altstadt/Dorfkern. Wenn der Nutzer nur den Ort nennt: per Nominatim geocoden
  (`https://nominatim.openstreetmap.org/search?format=json&q=<Ort>, Niedersachsen` mit eigenem User-Agent) und den
  Kern selbst wählen (Kirche/Markt), Ergebnis kurz nennen.
- **Größe**: Standard quadratisch `size_m` 1000. Ganze Dörfer/Inseln 1500–2000. Ausgreifende Gebiete rechteckig mit
  `"extent": {"west","east","south","north"}` (Meter vom Mittelpunkt; `size_m` = größte Kantenlänge). Grenzen an
  Straßenenden festmachen: Endpunkt per Overpass/Nominatim holen und in lokale Meter umrechnen
  (x = (lon−lon0)·111320·cos(lat0), z = (lat−lat0)·110574).
- **Insel?** → `"ground": "sand"`, `"vehicles": ["bicycle", "carriage"]` (autofrei, Menü-Badge „Insel“).
  Festland → `"ground": "grass"`, `"vehicles": ["bicycle", "car"]`, optional `car_spots` (lat/lon, note).
- **Bundesland**: LGLN-Daten (LoD2, DGM1, DOM1, DOP20, ALKIS-Flurstücke) gibt es nur für **Niedersachsen**. Bremen/NRW/… → nur OSM
  (Häuser aus OSM extrudiert, flaches Gelände, OSM-Bäume) – dem Nutzer vorher sagen.
- Größere Karten = mehr Bäume/Häuser = schwerer fürs iPhone; Westerstede (3,2 × 1,7 km, ~11 000 Häuser, ~13 000 Bäume)
  ist das bisherige Maximum.

## 1. Gebiet eintragen

`Tools/osm/areas.json` → neues Objekt in `areas` (id = kleingeschrieben, ohne Umlaute):
```json
{ "id": "jever", "name": "Jever", "lat": 53.5745, "lon": 7.8990, "size_m": 1000,
  "vehicles": ["bicycle", "car"], "ground": "grass" }
```
Bestehende Karte vergrößern/verschieben: nur `extent`/`size_m`/Mittelpunkt ändern und alle Schritte ab 2 neu laufen
lassen. Gespeicherte Spielerposition verfällt dabei automatisch (Schlüssel enthält Größe + Zentrum).

## 2. Daten holen (Reihenfolge wichtig: Bäume brauchen LoD2 als Gebäudemaske)

```bash
python3 Tools/osm/fetch_area.py <id>     # OSM → Data/<id>.osm.json   (Overpass 504? einfach nochmal)
python3 Tools/lod2/fetch_lod2.py <id>    # LoD2-Häuser → Data/<id>.lod2.json
python3 Tools/dgm/fetch_dgm.py <id>      # Gelände DGM1 → Data/<id>.dgm.bin
python3 Tools/dgm/fetch_trees.py <id>    # Bäume DOM1−DGM1 → Data/<id>.trees.json
python3 Tools/dop/fetch_dop.py <id>      # Luftbild DOP20 RGBI → Data/<id>.dop.png/.json (braucht rasterio)
python3 Tools/alkis/fetch_parcels.py <id> # Flurstücke (INSPIRE-WFS) → Data/<id>.parcels.json → Gartenzäune/-grenzen
python3 Tools/osm/fetch_traffic.py <id>  # Ampeln, Übergänge, Stopp/Vorfahrt, Verkehrszeichen (OSM-Knoten) → Data/<id>.traffic.json
# nach dem ersten Unity-Import (braucht Assets/Generated/<id>/<id>_buildings.json als Maske), dann nochmal importieren:
python3 Tools/dgm/fetch_hedges.py <id>   # echte Hecken bDOM20−DGM1 + DOP-NIR (aktuell, 0,4 m) → Data/<id>.hedges.json (~4 min)
```
Kontrolle der Ausgaben: Anzahl Gebäude/Kacheln/Bäume/Flurstücke plausibel (Flurstücke: Stadt ~700 m² Median)? 0 Kacheln = falsche Koordinaten oder außerhalb
Niedersachsens. `rasterio` fehlt → `pip3 install --user rasterio`.

## 3. In Unity importieren

Unity-Editor muss **geschlossen** sein (sonst Projekt-Lock) – nie selbst beenden, den Nutzer bitten.
```bash
/Applications/Unity/Hub/Editor/6000.3.26f1/Unity.app/Contents/MacOS/Unity -batchmode -quit -projectPath . -buildTarget iOS \
  -executeMethod LeGamez.Editor.OsmImport.OsmAreaImporter.ImportAllBatch -logFile /tmp/import.log
grep -E "error CS|\[OSM\]|\[Trees\]" /tmp/import.log
```
- Exit 139 = bekannter Absturz beim Asset-Import → einfach erneut starten.
- Die `[OSM]`-Zeile muss `terrain DGM1`, `aerial DOP20`, `trees LiDAR` zeigen (sonst fehlt die jeweilige Datei)
  und `gardens N plots/M measured hedges/…` mit N, M > 0 (sonst fehlt `parcels.json` bzw. `hedges.json`).
- `traffic …`: in Städten > 0 Ampelkreuzungen/Schilder (sonst fehlt `traffic.json`); Inseln haben fast nur Straßenschilder.
- Unity meldet „No valid Unity Editor license found“ → Nutzer muss sich im Unity Hub neu anmelden.
- Nur ein Gebiet: `-executeMethod LeGamez.Editor.OsmImport.OsmAreaImporter.ImportAreaBatch -area <id>`
  (Menü-Szene/Katalog werden dann trotzdem aktualisiert).
- Danach `git diff Packages/manifest.json` – Unity schreibt gern Template-Pakete zurück → wieder entfernen.

## 4. Prüfen (Snapshots ansehen, nicht nur Exit-Code)

```bash
...Unity -batchmode -quit -projectPath . -buildTarget iOS -executeMethod LeGamez.Editor.SceneSnapshot.RenderAll -logFile /tmp/snap.log
```
Bilder in `Builds/Snapshots/<id>_{top,aerial,street,landmark}.png` mit Read ansehen. Checkliste:
- Draufsicht deckt das gewünschte Gebiet (Straßenenden, Ortskern mittig/wie gewünscht)?
- Dachfarben plausibel gegen das echte Luftbild (`Data/<id>.dop.png` RGB-Vorschau erzeugen und vergleichen).
- Keine Bäume in Häusern/auf Fahrbahnen, keine schwebenden/versunkenen Häuser am Hang.
- Gärten: Hecken/Zäune an Grundstücksgrenzen, Einfahrten (gepflastert im Luftbild) frei, nichts auf Straßen oder
  durch Häuser. Hecken-Erkennung prüfen: Overlay der `hedges.json`-Linien auf `Data/<id>.dop.png` (rot) ansehen –
  rot = Grenzhecken (sollen durchgängig auf den Grundstücksgrenzen liegen), gelb = freie Hecken außerhalb der Gärten.
- `facade N windows/…` in der `[OSM]`-Zeile > 0 (sonst fehlen LoD2-Häuser). Nahdetails (Rahmen, Rinnen, Ortgang) sieht man
  erst zur Laufzeit; für Testbilder `FacadeDetails.Refresh(kameraPosition, 1000)` vor dem Rendern aufrufen.
- Verkehr: Ampeln/Schilder am rechten Fahrbahnrand, nicht auf der Fahrbahn; Schrift lesbar und nicht durch Hecken/Häuser
  sichtbar (Shader `LeGamez/SignText`); keine Schilderwälder (Einbahn-/Tempo-Schilder nur an echten Übergängen).
- Spawnpunkt/Startfahrrad nicht in Brunnen/Wasser; Menü-Thumbnail (`Assets/Generated/<id>/<id>_thumb.png`) ok.
- POIs: `Assets/Generated/<id>/<id>_pois.asset` (max. 30) sinnvoll?
Fehler → im Importer beheben (gilt dann für alle Karten), nicht per Hand in der Szene.

## 5. Aufs iPhone

Die App enthält keine Karten: Auf dem Gerät erscheint eine neue Karte erst nach `publish_maps.sh <id>` (Abschnitt 6).
Vorher im Unity-Editor (Play im MainMenu) oder über die Snapshots prüfen.

```bash
...Unity -batchmode -quit -projectPath . -buildTarget iOS -executeMethod LeGamez.Editor.BuildScript.BuildIOS -logFile /tmp/build.log
cd Builds/iOS && xcodebuild -project Unity-iPhone.xcodeproj -scheme Unity-iPhone -configuration Release \
  -destination "id=00008150-000215380CE2401C" -derivedDataPath /tmp/Unity-iPhone-dd2 -allowProvisioningUpdates build
xcrun devicectl device install app --device 466CD8EB-A8DD-5BF2-BD23-5CF7F213964F /tmp/Unity-iPhone-dd2/Build/Products/Release-iphoneos/Nordisch.app
xcrun devicectl device process launch --device 466CD8EB-A8DD-5BF2-BD23-5CF7F213964F de.letzgo.legamez
```
Eigener `-derivedDataPath` (dd2): „database is locked“ = paralleler Build einer anderen Sitzung; dann wird sonst
still die alte App installiert. WLAN reicht („available (paired)“). Launch-Fehler „Locked“ = iPhone gesperrt, App ist trotzdem installiert.
Builds brauchen mehrere GB – bei „Disk full“ nur eigene Simulator-/DerivedData-Artefakte löschen.

## 6. Abschluss

- Commit: `Tools/osm/areas.json`, `Data/<id>.*`, `Assets/Generated/<id>/`, `Assets/Scenes/Areas/<Name>.unity`,
  `Assets/Generated/AreaCatalog.asset`, `MainMenu.unity`.
- In die App bringen: `Tools/release/publish_maps.sh <id>` (Unity zu; baut iOS + Android-Bundle, lädt in die GitHub-Release
  `maps`, aktualisiert `maps.json`). Erst `--dry-run`, Upload nur nach Rückfrage beim Nutzer (öffentlich).
  Nur eigene Änderungen stagen, wenn parallel eine andere Sitzung im Repo arbeitet.
- Dem Nutzer berichten: Gebiet/Größe, Häuser (LoD2 + OSM-only), Bäume, Datenstand (Befliegungsjahr), Auffälligkeiten.
- Neue Erkenntnisse/Stolperfallen in `CLAUDE.md` und hier ergänzen.
- Quellenangaben stehen automatisch im Spiel (OSM ODbL; LGLN CC BY 4.0 – gilt auch für die ALKIS-Flurstücke). Neue Datenquelle → Attribution in
  `OsmAreaImporter` (`info.attribution`) und Menü ergänzen.
