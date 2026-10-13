# Nordisch (Repo: LeGamez)

iOS-3rd-Person-Spiel (Unity 6.3 LTS, URP) – man läuft durch echte Orte im Nordwesten, gebaut aus offenen Geodaten.
App-Name „Nordisch“, Bundle-ID `de.letzgo.legamez`, Team `699G2QY959`.

## Wie eine Karte (ein Gebiet) entsteht

Pipeline: **Offline-Daten → Unity-Importer → Szene**. Zur Laufzeit wird nichts aus dem Netz geladen.

1. **Gebiet definieren** – `Tools/osm/areas.json`
   ```json
   { "id": "jever", "name": "Jever", "lat": 53.5745, "lon": 7.8990, "size_m": 1000,
     "vehicles": ["bicycle", "car"], "ground": "grass" }
   ```
   - `lat/lon` = Mittelpunkt (Marktplatz/Altstadt wählen), `size_m` = Kantenlänge (1000; Inseln 1000–1200).
   - Inseln: `"ground": "sand"`, `"vehicles": ["bicycle", "carriage"]` (autofrei). Das Badge „Insel“ im Menü kommt von `carriage`.
2. **OSM-Daten holen** (Straßen, Flächen, Wasser, Bäume, Barrieren, POIs):
   ```bash
   python3 Tools/osm/fetch_area.py jever
   ```
   → `Data/jever.osm.json`. Overpass liefert oft 504 – das Skript rotiert über mehrere Mirrors; ggf. einfach erneut starten.
3. **LoD2-Gebäude holen** (nur Niedersachsen, LGLN OpenGeoData, CC BY 4.0):
   ```bash
   python3 Tools/lod2/fetch_lod2.py jever
   ```
   → `Data/jever.lod2.json`. Kacheln (1×1 km CityGML) werden über die STAC-API `https://lod.stac.lgln.niedersachsen.de` gefunden,
   gestreamt (nichts Rohes auf Platte), UTM32 → lokale Meter umgerechnet und auf das Gebiet zugeschnitten.
   Ohne diese Datei fällt der Importer auf OSM-Extrusion zurück (`BuildingBuilder`, geschätzte Höhen/Dächer).
   Andere Bundesländer haben eigene LoD2-Portale (NRW, Bayern, HH …) – dafür bräuchte es einen weiteren Fetcher mit gleichem JSON-Format.
3b. **Gelände holen** (nur Niedersachsen, LGLN DGM1, 1-m-Raster, CC BY 4.0):
   ```bash
   python3 Tools/dgm/fetch_dgm.py jever
   ```
   → `Data/jever.dgm.bin` (2-m-Höhenraster im lokalen Rahmen, STAC `https://dgm.stac.lgln.niedersachsen.de`, neuester Befliegungsjahrgang).
   Ohne Datei bleibt die Welt flach. Der Importer baut daraus einen facettierten Boden (5-m-Zellen, `TerrainField`),
   schneidet Straßen/Flächen/Wasser entlang dieser Zellen und legt sie auf (`TerrainDrape.Drape`), hebt Bäume/Props an
   (`Lift`) und stellt Gebäude auf den tiefsten Punkt ihres Grundrisses.
3c. **Bäume aus Laserscan** (nur Niedersachsen, LGLN DOM1 − DGM1, CC BY 4.0; braucht vorher `<id>.lod2.json` als Gebäudemaske):
   ```bash
   python3 Tools/dgm/fetch_trees.py jever
   ```
   → `Data/jever.trees.json` (`[x, z, Höhe, Kronenradius]`). nDOM = Oberfläche − Gelände, Gebäude (+1,5 m) ausmaskiert,
   Kronenspitzen ≥ 3,5 m als lokale Maxima, Mindestabstand 3 m. Mit Datei ersetzt sie OSM-Bäume und Zufallsbäume in
   Parks/Wäldern; Treffer > 32 m (Kirchtürme, Masten) verwirft der Importer. Ohne Datei: OSM-Bäume wie bisher.
3d. **Luftbild holen** (nur Niedersachsen, LGLN DOP20 RGB + Infrarot, CC BY 4.0; `pip3 install --user rasterio`):
   ```bash
   python3 Tools/dop/fetch_dop.py jever
   ```
   → `Data/jever.dop.png` (1 m/Pixel im lokalen Rahmen, Alpha = Infrarot, oben = Norden) + `jever.dop.json` (Lage, Datum).
   Liest nur eine Übersichtsstufe der COG-Kacheln (schnell). Der Importer (`AerialImage`) nutzt es für
   **Dachfarben** (Median der sonnigen Dachpixel, Bäume per NDVI raus, auf Palette gezogen: Ziegelrot, Anthrazit,
   Braun, Moos, Solar, Flachdach Bitumen/Kies/grün; `buildings.json`-Overrides und OSM `roof:colour` gehen vor) und
   **Bodenbedeckung** je Geländedreieck (Rasen/Pflaster/Sand/Erde; unter LiDAR-Kronen entscheidet die Umgebung).
   Mit Luftbild entfallen die flächigen OSM-Flächen residential/commercial/retail/industrial, damit Gärten/Höfe sichtbar werden.
4. **In Unity importieren** (baut alle Gebiete, Menü-Vorschaubilder, Minimap-Karten, Hauptmenü-Szene):
   ```bash
   /Applications/Unity/Hub/Editor/6000.3.26f1/Unity.app/Contents/MacOS/Unity -batchmode -quit -projectPath . -buildTarget iOS \
     -executeMethod LeGamez.Editor.OsmImport.OsmAreaImporter.ImportAllBatch -logFile /tmp/import.log
   grep "\[OSM\]" /tmp/import.log
   ```
   Ergebnis: `Assets/Scenes/Areas/<Name>.unity`, Eintrag in `Assets/Generated/AreaCatalog.asset`, Thumbnail `Generated/<id>/<id>_thumb.png`,
   Minimap `Generated/<id>/<id>_map.png` (hängt an `AreaInfo.mapTexture`). Im Editor zeigt das Menü die Szene sofort als Karte;
   in die App kommt sie erst per **Veröffentlichen** (Schritt 7). Die Build-Settings enthalten nur `MainMenu`.
5. **Prüfen**: `-executeMethod LeGamez.Editor.SceneSnapshot.RenderAll` rendert Luft-/Straßen-/Wahrzeichen-Bilder nach `Builds/Snapshots/`.
   POI-Auswahl in `Assets/Generated/<id>/<id>_pois.asset` ansehen (max. 30, Hotels/Läden sind ausgefiltert).
6. **Bauen & aufs iPhone** – siehe unten.
7. **Veröffentlichen**: `Tools/release/publish_maps.sh [<id> …]` – siehe „Karten-Downloads“.

### Karten-Downloads (App enthält keine Karten)
- Jede Stadt = ein AssetBundle pro Plattform (Szene + Meshes + POIs + Straßen + Minimap + Materialien/Shader), gebaut von
  `Editor/MapBundles` (`-buildTarget iOS|Android -executeMethod LeGamez.Editor.MapBundles.BuildBatch [-areas a,b]`)
  nach `Builds/Maps/<platform>/<id>.bundle` (LZMA) + `index.json`.
- `Tools/release/publish_maps.sh [--dry-run] [--drop-old] [<id> …]` baut iOS + Android und lädt **nur geänderte** Karten
  (Inhalts-CRC) in die GitHub-**Pre-Release `maps`** (nie „latest“ → stört den APK-Updater nicht). Katalog
  `https://github.com/1letzgo/nordisch/releases/download/maps/maps.json`: je Karte Name/Lage/Fahrzeuge/Thumbnail-URL und
  `builds[]` = {platform, format, unity, version (yyyyMMddHHmm), url, size, sha256, crc}. Alte, nicht mehr referenzierte Assets werden gelöscht.
- App: `Core/MapStore` – Katalog einmal pro App-Start laden (Kopie für offline), Download → Größe + SHA-256 prüfen →
  `RecompressAssetBundleAsync` nach LZ4 → `persistentDataPath/maps/<id>/map.bundle` + `info.json` (iOS: kein iCloud-Backup).
  Spielen = Bundle laden + Szene daraus; zurück im Menü wird es entladen. Im Editor listet `MapStore` die lokalen Szenen.
- **Update-Logik**: Ein Build passt nur bei gleicher Plattform, `MapStore.Format` und Unity-Version. Neuerer passender Build
  → „Update“ (im WLAN automatisch, sonst Tipp auf das Pill; die alte Version bleibt bis zum Tausch spielbar).
  Installierter Build passt nicht mehr (App-Update) → „Update nötig“. Kein passender Build → „Neue App-Version nötig“.
- `MapStore.Format` erhöhen, wenn Kartenszenen etwas brauchen, das ältere Apps nicht haben (neues Skript in Szenen,
  geänderte serialisierte Felder von AreaInfo/TrafficLights/FacadeDetails/PoiDatabase/StreetIndex …).
  **Reihenfolge bei neuem Format oder Unity-Update: erst Karten veröffentlichen, dann App releasen** (ohne `--drop-old`,
  damit alte Apps ihre Builds behalten).
- Stripping: Engine-Code-Stripping sieht nur das Menü → `Assets/link.xml` (TextMesh, Collider, Light, Assembly-CSharp) und
  `BuildScript` übergibt das Bundle-Manifest der letzten Kartenbuilds. Fog-Varianten: Grafik-Einstellung „Fog Modes“ = Custom
  (`MapBundles` setzt das), sonst fehlt in Kartenszenen der Nebel.

### Was der Importer macht (`Assets/Editor/OsmImport/`)
- `OsmData` + `MiniJson`: Overpass-JSON lesen, `GeoProjection` (äquirektangulär um den Mittelpunkt, 1 Unit = 1 m, +X Ost, +Z Nord).
- `Lod2Builder`: echte Wand-/Dachflächen aus LoD2; regionaler Stil je ALKIS-Gebäudefunktion + Insel/Festland
  (Klinker rot/dunkel/gelb, weißer Putz, Pfannendach rot/anthrazit, Schornsteine). Materialcode steckt im Vertex-Alpha.
- `BuildingBuilder`: Fallback aus OSM-Grundrissen. Geschätzte Wandhöhen werden auf ganze Geschosse gerundet (3 m + 0,3 m Sockel),
  damit jedes Obergeschoss eine volle Fensterreihe bekommt. Auch mit LoD2 aktiv für OSM-Häuser, die LoD2 (noch) nicht kennt
  (Neubaugebiete; Test `Lod2Builder.Covers`), mit eigenem Zufallsgenerator, damit die Objekt-IDs stabil bleiben.
- `BuildingOverrides`: Einzelhäuser anpassen über `Data/<id>.buildings.json` (`address` oder `id` = LoD2-gml-id bzw.
  `osm:<way>`; `wall`, `brick`, `roof`, `gableWindows`, `gableWindowFloor`, `windows`, `hidden`, `door` = street/none/N/E/S/W,
  `doorColor`, `roofShape` (nur OSM), `chimney`, `floors` = Geschosse bis Traufe; LoD2-Wände werden gestreckt). Gilt für LoD2- und OSM-Häuser; eine Adresse
  trifft alle Gebäudeteile. Adressen: LoD2 = amtliche ALKIS-Adressen (fetch_lod2.py liest `bldg:address`), OSM = addr:*.
  Der Importer schreibt alle Häuser (id, Adressen, Grundriss) nach `Assets/Generated/<id>/<id>_buildings.json`;
  der Map-Editor hat dafür den Modus „Häuser“ (Adresssuche, Anklicken, Farbe/Dach/Giebelfenster).
  Nicht passende Einträge → Warnung im Import-Log.
  Fenster halten ≥ 0,6 m Abstand zum Dach (`BuildingBuilder.RoofGap`, Shader-Regel `head + 0.6` in VertexColorLit, Fensterbänke gleich).
- `GroundBuilder`: Grund-Kacheln, Flächen (landuse/natural/leisure), Straßen-Bänder mit Mittellinie, Wasser, Hecken/Mauern/Zäune.
- `GardenBuilder`: Gartengrenzen aus ALKIS-Flurstücken (`Data/<id>.parcels.json`, `Tools/alkis/fetch_parcels.py`, INSPIRE-WFS
  LGLN). Wohngrundstücke (120–2500 m², adressiertes Haus, nicht Gewerbe/Schule/Park): Straßenseite niedrige Hecke /
  Jägerzaun / Klinkermäuerchen / offen mit Lücken für Weg + Einfahrt; Nachbar-/Rückgrenzen hohe Hecke / Holz-Sichtschutz /
  Stabmatte (im Vorgarten im niedrigen Vorgartenstil). Stil pro Flurstück fest (Hash der Flurstücksnummer); Häuser auf der
  Grenze, Straßen (+1 m) und gepflasterte Flächen im Luftbild (`AerialImage.IsPaved` = Einfahrten/Wege) unterbrechen.
  Echte Hecken: `Tools/dgm/fetch_hedges.py` → `Data/<id>.hedges.json` aus **bDOM20** (bildbasiertes Oberflächenmodell,
  gleiche Befliegung wie DOP, also aktuell; STAC `bdom.stac.lgln.niedersachsen.de`) − DGM1, 0,4 m Raster, grün per DOP-NIR.
  Typ 1 = Grenzhecke: jede Gartengrenze wird abgetastet (±1,2 m Streifen, Lücken ≤ 2 m geschlossen) → durchgängig,
  exakt auf der Grenze. Typ 0 = frei (nur außerhalb der Gärten, RANSAC, ≥ 6 m). Mit diesen Daten erfindet der
  Importer keine Hecken mehr (generierte Grenzen nur Zaun/Mauer/offen).
  Hecken kürzen sich an Straßen/Gehwegen (Abstand 0,5 m + halbe Breite). Map-Editor-Modus „Gärten“: jede gebaute Linie
  hat eine ID `<art>@<x·10>,<z·10>` (Mitte) in `Assets/Generated/<id>/<id>_gardens.json`; `Data/<id>.garden.json`
  = `{"removed": [ids], "lines": [{"kind","points","height"}]}` (entfernen ±1,5 m tolerant, gezeichnete Linien werden gebaut).
- `TrafficBuilder`: Verkehr aus `Data/<id>.traffic.json` (`Tools/osm/fetch_traffic.py`, OSM-Knoten: Ampeln, Übergänge,
  stop/give_way, traffic_sign; getrennt von osm.json, damit Objekt-IDs stabil bleiben) + aus dem Straßennetz abgeleitet.
  Kreuzungen = gemeinsame Way-Punkte mit ≥ 3 Armen. Ampelknoten ≤ 28 m → Ampelkreuzung (Kopf je Zufahrt rechts, Haltlinie,
  Fußgängerfurten; Gruppe 0/1 nach Achse), sonst Fußgängerampel. Lampen animiert zur Laufzeit (`World/TrafficLights`,
  ein Mesh, Vertex-Farben, Phasen rot/rot-gelb/grün/gelb). Zebrastreifen + Z. 350 nur bei zebra/markings=yes, `dashes` = Furt.
  Vorfahrt: Nebenstraße (Rang kleiner, Hauptstraße ≥ tertiary) → Vorfahrt achten + Wartelinie, Hauptstraße → Z. 306;
  gleichrangig = rechts vor links (keine Schilder). Tempo-/Zone-30-Schilder wo maxspeed wechselt (nicht beim Abbiegen in
  größere Straßen, nicht `DE:urban`), Einbahn: Pfeil am Anfang, Z. 267 nur am echten Ende (nicht bei Hauptstraßen ≥ secondary),
  Straßennamen-Schilder an Kreuzungen benannter Straßen, kartierte Schilder (Ortsschild, 101, 239, 242.1 …).
  Schrift = TextMesh (eingebauter Font) auf Layer 8 mit Shader `LeGamez/SignText` (Tiefentest, keine Rückseite;
  `SignTextFont` hält die Font-Textur aktuell), Material `Assets/Generated/Materials/SignText.mat`.
- `PropPlacer`: LiDAR-Bäume (`<id>.trees.json`, Höhe/Krone echt) bzw. ohne Datei OSM-Bäume, Baumgruppen in Parks/Wäldern (mit Abstand zu Wegen), Büsche auf Dünen/Heide, Straßenlaternen.
  Kronen = facettierter Hauptklumpen + Ring kleinerer Klumpen bis zum gemessenen Radius (~40–100 Dreiecke), Nadelbäume 3 Kegel.
  Pflanzen-Builder ziehen aus dem gemeinsamen `rng` genau so viele Zahlen wie früher (`Fork`), sonst verschieben sich IDs/Edits.
- Nahdetails an LoD2-Häusern (`FacadeRecords` im Importer → `Assets/Generated/<id>/<id>_facade.bytes`, nicht im Repo):
  je Shader-Fenster (gleiches Raster wie `FacadeMask`) 3D-Rahmen mit Sprossen, Fensterbank, bei Klinker Rollschicht-Sturz;
  Ladenfenster mit dunklem Rahmen; Haustür mit Vordach + Stufe; Dachrinne + Fallrohr an Traufkanten; weiße Ortgangbretter
  an Giebelkanten (Dachkanten, die nur eine Dachfläche hat); Firstziegel; Sockelband. Gebaut wird erst zur Laufzeit
  (`World/FacadeDetails`): 32-m-Zellen im Umkreis von ~95 m um die Kamera, max. 3 Zellen pro Tick, weiter weg wieder
  verworfen – fern bleiben die Shader-Fenster. Datensatz: 10 floats (kind, x, y, z, along x/z, a, b, c, RGB);
  Außennormale = (−along.z, along.x). Neue Teile: Kind-Konstante + Builder in `FacadeDetails`, Erfassen in `Lod2Builder`.
- `CommercialBuilder` (+ `CommercialBuilder.Retail.cs`): Gewerbeimmobilien erkennbar. **Tankstellen** (`amenity=fuel`, Knoten
  oder Fläche): Tankdach = LoD2-„Gebäude“ mit ALKIS-Funktion `31001_2130` (Tankstelle) oder `51009_1610` (Überdachung),
  flach, ≤ 9 m, ≤ 45 m von der Tankstelle – `Lod2Builder` ruft `ClaimLod2` und baut dann keinen Kasten. Der Shop hat oft
  denselben Code: Ein OSM-Gebäude ≠ `building=roof` darüber oder eine Adresse hält ihn als normales Haus. Ohne LoD2-Dach:
  OSM `building=roof`, sonst generiertes 14×9-m-Dach auf gepflastertem Vorplatz (`AerialImage.IsPaved`). Dach mit Blende
  in Markenfarben + Markenname (`PickBrand`: Esso, Shell, Aral, JET, star/Orlen, Q1, SCORE, Total, AVIA, Westfalen,
  Raiffeisen, HEM, eni, bft, Hoyer, team; sonst weiß/blau mit Name), Säulen, Zapfinseln mit Säulen/Rammschutz/Lampen,
  Preismast an der Straße (außerhalb Fahrbahn/Einfahrten/Gehwegen/Gebäuden, ≥ 3 m vom Dach). **Supermärkte**
  (`shop=supermarket`, `building=supermarket`): größtes LoD2-Teil darunter, Wand Richtung Parkplätze (flächengewichtet,
  keine Brandwand) bekommt Glasfront mit Schiebetür, Vordach und Markenschild; Parkplätze ≤ 40 m bekommen
  Stellplatzlinien entlang der Fahrgassen (`highway=service`), sonst Reihen an den Längsseiten. Texte über
  `TrafficBuilder.AddText` (TextMesh, Layer 8). Kein Zugriff auf den gemeinsamen `rng` (Objekt-IDs bleiben stabil).
  Prüfen: `-executeMethod LeGamez.Editor.CommercialSnapshot.ImportAndRender -area <id>` → `Builds/Snapshots/<id>_fuel_*`/`_market_*`.
- `PoiExtractor`: benannte Sehenswürdigkeiten → Punkte/Namen auf Mini- und Großkarte (Beam-Ziele). Kein Stempelheft mehr.
- Straßenindex (`StreetIndex`, `<id>_streets.asset`): benannte OSM-Wege + Plätze als Segmente → HUD zeigt unter dem
  Ortsnamen die aktuelle Straße (`UI/StreetDisplay`, nächste Straße ≤ 30 m, 4 m Trägheit gegen Flackern).
- `MeshSink`: Geometrie pro Material und 100-m-Chunk; Kollider für Gebäude, Stämme, Props
  (Stämme/Props liegen auf Layer 2, damit die Kamera sie ignoriert).
- Shader `Assets/Shaders/VertexColorLit.shader`: Vertex-Farben, Fenster/Sprossen (Laibungsschatten, Gardinen), Klinker, Putz,
  Sockel, Traufband, Dachpfannen, Wasser, großflächige Farbvariation auf Boden/Flachdächern. Bordsteinbänder: `GroundBuilder.Kerbs`.
- Himmel `Assets/Shaders/Sky.shader` (Verlauf, Sonne, ziehende Wolken); Licht/Nebel in `OsmAreaImporter.AddLighting`,
  HDR + Bloom/Farbkorrektur in `ProjectSetup` (`ConfigureUrp`, `CreatePostProfile`).

### Map-Editor (macOS-App, `Tools/MapEditor`)
Draufsicht je Gebiet (Minimap + LoD2-Dachumrisse). Neue Objekte setzen, außerdem alle vom Importer erzeugten
(Bäume, Büsche, Laternen, Bänke, Kübel, Brunnen, Café-Tische, Denkmäler …) verschieben/drehen/skalieren/löschen.
- Speichert nach `Data/<id>.edits.json`: `objects` = neue Objekte, `changes` = Overrides erzeugter Objekte per ID
  (`<typ>@<x·10>,<z·10>`, Liste in `Assets/Generated/<id>/<id>_objects.json`, schreibt der Importer).
- Importer: `EditsPlacer` – alle Placer laufen über `EditsPlacer.Emit` (baut auch gelöschte, damit die Zufallsfolge
  und damit alle anderen Objekte gleich bleiben). Neue Objekttypen: in `FurniturePlacer/PropPlacer.BuildSingle` und `ObjectType.all` (Swift).
- „In Unity übernehmen“ = `-executeMethod LeGamez.Editor.OsmImport.OsmAreaImporter.ImportAreaBatch -area <id>` (Unity-Editor muss zu sein).
- Bauen/Installieren:
  ```bash
  cd Tools/MapEditor && xcodegen generate && xcodebuild -project NordischMapEditor.xcodeproj -scheme NordischMapEditor \
    -configuration Release -derivedDataPath /tmp/NordischMapEditor-dd build && \
    rm -rf "/Applications/Nordisch Map Editor.app" && cp -R "/tmp/NordischMapEditor-dd/Build/Products/Release/Nordisch Map Editor.app" /Applications/
  ```

### Schnell eine Stadt ergänzen (Kurzfassung)
Skill: **`/create-map`** (`.claude/skills/create-map/SKILL.md`) führt durch den ganzen Ablauf inkl. Prüf-Checkliste.
```bash
# 1. Eintrag in Tools/osm/areas.json
python3 Tools/osm/fetch_area.py <id>
python3 Tools/osm/fetch_traffic.py <id>    # Ampeln/Schilder (OSM)
python3 Tools/lod2/fetch_lod2.py <id>      # nur Niedersachsen
python3 Tools/dgm/fetch_dgm.py <id>        # Gelände, nur Niedersachsen
python3 Tools/dgm/fetch_trees.py <id>      # Bäume aus DOM1, nach lod2
python3 Tools/dop/fetch_dop.py <id>        # Luftbild: Dachfarben + Bodenbedeckung
# 2. Unity ImportAllBatch (siehe oben), Snapshots prüfen, bauen
```
Das Menü-Raster ist 2-spaltig und wächst mit; mehr als ~6 Gebiete brauchen später Scrollen.

### Spielsysteme (Runtime, `Assets/Scripts/`)
- `Vehicles/`: Fahrräder an OSM-Fahrradständern (`AreaInfo.bikeSpots`, beim Import gegen Brunnen/Bänke/Wasser/Gebäude geprüft)
  plus eines am Startpunkt (`spawnBikeSpot`). Rennen-Knopf = zweiter Gang auf dem Rad.
- `Core/PositionMemory`: letzte Position, Blickrichtung und Fahrstatus pro Gebiet (PlayerPrefs; Schlüssel enthält Größe+Zentrum,
  wird also bei Gebietsänderung verworfen). Figurenwahl: `PlayerAvatar.SelectedIndex`.
- `UI/FullMap`: Tipp auf Minikarte → große Karte, Beamen. Zoom bis 6× per Zwei-Finger-Pinch, Mausrad oder ＋/−, Ziehen verschiebt;
  Marker behalten ihre Bildschirmgröße. Kartentextur `AreaInfo.mapTexture` (`Generated/<id>/<id>_map.png`) ~2 px/m (max. 4096). `UI/Navigation`: Minikarte.

## Bauen
- Projekt-Setup (Player-Settings, URP, Materialien, Figuren, Logo/Icon): `-executeMethod LeGamez.Editor.ProjectSetup.Run`
- Xcode-Export Gerät: `-executeMethod LeGamez.Editor.BuildScript.BuildIOS` → `Builds/iOS`
- Xcode-Export Simulator (arm64): `-executeMethod LeGamez.Editor.BuildScript.BuildIOSSimulator` → `Builds/iOS-Sim`
- Gerät (WLAN genügt, Status „available (paired)“):
  ```bash
  cd Builds/iOS && xcodebuild -project Unity-iPhone.xcodeproj -scheme Unity-iPhone -configuration Release \
    -destination "id=00008150-000215380CE2401C" -derivedDataPath /tmp/Unity-iPhone-dd -allowProvisioningUpdates build
  xcrun devicectl device install app --device 466CD8EB-A8DD-5BF2-BD23-5CF7F213964F /tmp/Unity-iPhone-dd/Build/Products/Release-iphoneos/Nordisch.app
  xcrun devicectl device process launch --device 466CD8EB-A8DD-5BF2-BD23-5CF7F213964F de.letzgo.legamez
  ```

### Android
- Unity-Module `android`, `android-sdk-ndk-tools`, `android-open-jdk` (über Unity Hub installiert). ARM64, IL2CPP, Vulkan + GLES3, minSdk 26,
  gleiche Bundle-ID. Einstellungen: `ProjectSetup.ConfigureAndroid` (läuft auch vor jedem Android-Build).
- APK: `-buildTarget Android -executeMethod LeGamez.Editor.BuildScript.BuildAndroid` → `Builds/Android/Nordisch.apk` (ohne Karten – die kommen per Download).
- Emulator (AVD `nordisch`, Pixel 7, Android 36.1 Play-Image, GPU host):
  ```bash
  ~/Library/Android/sdk/emulator/emulator -avd nordisch -no-snapshot-save -no-boot-anim &
  ~/Library/Android/sdk/platform-tools/adb install -r Builds/Android/Nordisch.apk
  ~/Library/Android/sdk/platform-tools/adb shell am start -n de.letzgo.legamez/com.unity3d.player.UnityPlayerGameActivity
  ```
  Querformat testen: `adb emu sensor set acceleration 9.81:0:0` (zurück: `0:9.81:0`). Logs: `adb logcat -s Unity`.
- Eigenheiten: Unitys Auto-Rotation bleibt auf Android im Start-Format hängen → `Core/AppOrientation` dreht selbst nach
  `Input.deviceOrientation` (respektiert Rotationssperre). Back-Taste = Escape (Predictive Back aus): schließt große Karte,
  sonst zurück ins Menü, im Menü App in den Hintergrund. `Assets/link.xml` hält Collider-Klassen fürs Engine-Stripping (`CreatePrimitive`) und alles, was Kartenszenen brauchen.

## Release (GitHub, öffentlich: https://github.com/1letzgo/nordisch)
- Android: `Tools/release/release_android.sh` (Unity zu, versionierte Dateien sauber) → Version = Datum (`2026.10.13`, `.2` …),
  versionCode = 10-Minuten-Schritte seit 2025, signiert mit `~/.config/nordisch/release.keystore`
  (Passwort in `release.env`, **nicht im Repo – sichern, sonst lassen sich Updates nicht mehr über alte Versionen installieren**),
  Tag `v<version>` + GitHub-Release mit `Nordisch-<version>.apk`. `--dry-run` = nur bauen.
- Release-APKs laufen **15 Tage** nach dem Build ab: `BuildScript.BuildAndroidRelease` bettet `Resources/BuildInfo.txt`
  (nur während des Builds) ein, `Core/BuildExpiry` + Sperrbildschirm im Hauptmenü mit Link auf das neueste Release.
  Normale Builds (BuildAndroid/BuildIOS) laufen nie ab.
- Karten: `Tools/release/publish_maps.sh` (siehe „Karten-Downloads“), unabhängig vom App-Release.
- iOS/TestFlight: Skill `/testflight-build` auf `Builds/iOS` nach `BuildIOS`; vorher in `Builds/iOS/Info.plist`
  `CFBundleShortVersionString` = Release-Version, `CFBundleVersion` = versionCode, `ITSAppUsesNonExemptEncryption` = false.
- Generierte Meshes (`Assets/Generated/*/*_meshes.asset`) sind nicht im Repo (>100 MB) – nach dem Klonen `ImportAllBatch`.

## Stolperfallen
- Unity schreibt Template-Pakete (purchasing, analytics, collab-proxy, multiplayer.center) zurück in `Packages/manifest.json` → wieder entfernen.
- Unity-Batchmode stürzt gelegentlich mit Exit 139 beim Asset-Import ab → einfach erneut ausführen.
- Builds brauchen mehrere GB Platz (Xcode DerivedData in `/tmp/*-dd`); bei „Disk full“ Simulator-Artefakte löschen.
- Laufzeit-Materialien nie per `Shader.Find` erzeugen (Stripping) – über `Resources/GameMaterials.asset` referenzieren.
- Lizenzhinweise: OSM (ODbL), LGLN (CC BY 4.0), Kenney (CC0) – stehen im Menü und im HUD; bei neuen Quellen ergänzen.

## Assets & Lizenzen
- Figuren: Kenney Mini Characters (CC0), `Assets/ThirdParty/KenneyMiniCharacters`.
- Logo „Kompass-N“: Quelle `Branding/concepts/make_concepts.py` (`python3 ... --final` schreibt `Branding/logo.png`, `wordmark.png`, Lockups). Danach beide nach `Assets/Resources/Branding/` kopieren und `ProjectSetup.Run`. Schrift Rockwell Bold/Regular, Farben Navy `#1A3A4E`, Senf `#E3A82B` (`UiKit`). Altes Logo: `Branding/logo_v1.png`.
