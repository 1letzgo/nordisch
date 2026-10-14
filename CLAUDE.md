# Nordisch (Repo: LeGamez)

iOS-3rd-Person-Spiel (Unity 6.3 LTS, URP) – man läuft durch echte Orte im Nordwesten, gebaut aus offenen Geodaten.
App-Name „Nordisch“, Bundle-ID `de.letzgo.legamez`, Team `699G2QY959`.

## Wie eine Karte (ein Gebiet) entsteht

Pipeline: **Offline-Daten → Unity-Importer → Gebietsdaten + Kacheln → Download-Pakete**. Die App baut jedes Gebiet zur Laufzeit
in der einen Welt-Szene auf (Plan „Eine Welt“: `Docs/one-world.md`).

1. **Gebiet definieren** – `Tools/osm/areas.json`
   ```json
   { "id": "jever", "name": "Jever", "lat": 53.5745, "lon": 7.8990, "size_m": 1000,
     "vehicles": ["bicycle", "car"], "ground": "grass" }
   ```
   - `lat/lon` = Mittelpunkt (Marktplatz/Altstadt wählen), `size_m` = Kantenlänge (1000; Inseln 1000–1200).
   - Inseln: `"ground": "sand"`, `"vehicles": ["bicycle", "carriage"]` (autofrei). Das Badge „Insel“ im Menü kommt von `carriage`.
   - Mehrere Startpunkte (optional): `"starts": [{"name": "Westerstede"}, {"name": "Oldenburg", "lat": 53.1395, "lon": 8.2145}]`
     – ohne lat/lon = Mittelpunkt, optional `yaw`; ohne Liste = ein Start mit Gebietsnamen. Siehe „Startpunkte“.
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
   Fehlende Kacheln: Inseln (`ground: sand`) → −2,5 m NHN (Meeresboden, siehe `CoastBuilder`), Festland → Median.
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
   **Bodenbedeckung** je Geländedreieck (Rasen/Pflaster/Sand/Erde; unter LiDAR-Kronen entscheidet die Umgebung; im offenen Land geglättet Wiese/Acker, s. 3e).
   Mit Luftbild entfallen die flächigen OSM-Flächen residential/commercial/retail/industrial, damit Gärten/Höfe sichtbar werden.
3e. **Große Gebiete (ganze Gemeinde) – Maske** (`Tools/osm/make_mask.py <id>`, vor allen anderen Fetchern):
   `"mask"` im Gebiet (`boundary` = Gemeindename, `admin_level`, `settlement_buffer`, `road_buffer`, `roads` …) →
   `Data/<id>.mask.png/.json` (10 m/px, oben = Norden; 255 Siedlung, 192 Straßenkorridor, 32 Wald außerhalb, 0 außen).
   Siedlung = OSM-landuse residential/commercial/industrial/retail + Gebäudecluster (Einzelhöfe nicht), gepuffert, plus
   Korridore entlang der Verbindungsstraßen, auf die Gemeindegrenze geschnitten; `include` = Rechtecke, die immer drin
   sind (Westerstede: die alte Stadtkarte, damit Parks/Wälder im Kern bleiben). Fetcher holen LoD2/Bäume/Hecken/Flurstücke
   nur innerhalb (`Tools/osm/area_mask.py`, STAC-Suchen blättern), OSM in 3-km-Kacheln nur wo Maske (Wasser/Bahn überall).
   DGM (`"dgm_step": 5`, gzip „DGMQ“ uint16-cm) und DOP (`"dop_step": 2`, außerhalb 16-m-Blockmittel) decken das ganze Rechteck.
   Importer: `AreaMask.Filter` wirft beim Laden alles außerhalb raus (Knoten, Häuser per Schwerpunkt, Linien werden an der
   Maske geschnitten; Seen/Teiche, Flüsse, Kanäle + Bahn bleiben, Bäche/Gräben nur innen), auch LoD2/LiDAR/Flurstücke/
   Hecken/Verkehrsknoten; Straßenlaternen nur in Siedlungen (Landstraßen sind unbeleuchtet). `CoarseTerrain` vereinfacht
   das Höhenfeld außerhalb der 40-m-Detailblöcke (40-m-Raster geglättet, bis 160 m zusammengelegt, Samples durch die
   Blockdreiecke ersetzt → Mesh = Feld, keine Risse) und baut dort nur große Dreiecke; `GroundBuilder.BuildBase` überspringt diese Blöcke.
   Farben: Klassenfeld auf 20-m-Knoten (Wiese/Acker aus `AerialImage.FieldScore`, Wald/See aus Maske, 2× 3×3-Mehrheit),
   einheitliche Blöcke bleiben ganz (Vertex-Farbe = gemessener Ton je Feld), an Klassengrenzen werden die 20-m-Dreiecke
   entlang der interpolierten Grenze geschnitten (Marching Triangles) → glatte Feldränder statt Treppen. Wälder ≤ 100 m
   vom Detailgelände bekommen lockere Instanz-Bäume (Rand dicht; eigener Zufall, keine Editor-Objekte).
   **Offenes Land** (alle Gebiete außer Inseln, `AerialImage.SetBuiltUp`): Zellen ≥ 30 m von Gebäuden und außerhalb
   bebauter landuse/Parkplätze nehmen auch im Detailgelände Wiese/Acker (`Cover.Field`) aus derselben geglätteten Karte
   (10 m, 3× [1 2 1]-Blur, Ton = Helligkeit) – keine falschen „Pflaster“-Flecken auf Äckern, Feldformen laufen über den Maskenrand
   (gilt auch ohne Maske, z. B. Felder am Rand kleiner Karten; CoarseTerrain/Waldbäume nur mit Maske). Ansichten einzelner Stellen ohne
   alle Kacheln zu laden: `-executeMethod LeGamez.Editor.AreaViewSnapshot.Render -area <id> -views "name:x,z,yaw,höhe,neigung;…"`.
3f. **Autobahn + zweite Stadt im selben Gebiet** (Westerstede: A28 bis Oldenburg, Gebiet bis 20,4 km Ost / 14,2 km Süd;
   Gebiete haben je ein eigenes Koordinatensystem, deshalb ein Gebiet statt zwei verbundener Karten). In `"mask"`:
   `"motorway": {"refs": ["A 28"], "clip": [x0,x1,z0,z1], "corridor": 45, "landscape": 220, "strip": 25}` → Autobahn
   (+ Rampen nahe dran) als **176** = Autobahnkorridor (feines Gelände, Straßen, Bäume, keine Häuser/Gärten/Möbel), Straßen,
   die ihn kreuzen/anbinden, als 25-m-Streifen derselben Stufe, beidseitig **160** = Landschaftsband (grobes Gelände, aber
   Straßen, Wasser, Zäune, LiDAR-Bäume); `"extra": [[x0,x1,z0,z1], …]` = Siedlungsrechtecke außerhalb der Gemeinde
   (Oldenburg: Dreieck Oldenburg-West → Haarentor → Innenstadt, 3,4 km²). Stufen: ≥ 128 Straßen/Bäume (`AreaMask.Inside`),
   ≥ 176 feines Gelände (`FineTerrain`, DOP in voller Auflösung), ≥ 192 Häuser/LoD2/Flurstücke/Hecken/Flächen/Knoten
   (`AreaMask.Detail`, `area_mask.AreaMask.load(id, detail=True)`). **Gebiet vergrößern ohne alles neu zu holen**: alte
   Maske nach `Data/<id>.old.mask.png/.json` kopieren, `make_mask.py <id> --keep-old` (alte Pixel bleiben exakt →
   Objekt-IDs/Edits stabil), `fetch_area.py <id> --add` (nur 3-km-Kacheln mit neuen Maskenpixeln, alte Elemente bleiben
   unverändert), `fetch_dgm.py/fetch_dop.py <id> --reuse` (vorhandene Raster übernehmen, nur fehlende Kacheln laden);
   LoD2/Bäume/Flurstücke/Hecken über ein temporäres Gebiet `<id>_add` mit Maske = nur neue Teile holen und hinten anhängen
   (Reihenfolge der alten Einträge bleibt → gleiche Zufallszüge). Importer: `BridgeBuilder` (Brücken mit Lücke darunter
   ≥ 1,8 m, über Straßen/Wegen/Bahn ≥ 1 m: Fahrbahnplatte zwischen den Endhöhen, Kappen, Brüstung, Pfeiler wo unten nichts fährt; Kollider, nicht
   drapiert – das DGM hat keine Brücken), `MotorwayBuilder` (Schutzplanken beidseitig mit
   Lücken an Rampen), `TrafficBuilder.Motorway` (blaue Vorwegweiser 500 m vor jeder Ausfahrt mit Nummer/Name/`destination`
   der Rampe, „Ausfahrt“-Schild; `motorway_junction` aus traffic.json). Rampen haben Rang 3 (Vorfahrt achten am Ende),
   an Autobahn-Ein-/Ausfädelungen keine Schilder. Straßenindex: Autobahn heißt nach `ref` („A 28“), Rampen
   „Anschlussstelle“; `StreetIndex.fast` markiert sie → Auto bis 130 km/h (`Car.MotorwaySpeed`, sonst 90).
   Schutzplanken: sichtbar = verzinkter W-Holm 0,44–0,75 m mit Pfosten alle 3–4 m (Bucket `Rails`, Trees-Material,
   ohne Kollider, ~4 Dreiecke/m); was das Auto hält = unsichtbare, geschlossene Wand 1,4 m hoch, 20 cm dick (Bucket
   `Blockers` → Kachel-Part mit `WorldTile.FlagNoRender` = nur MeshCollider, kein Renderer; **ab Kartenformat 8**,
   ältere Apps würden die Wand zeichnen). Entlang verketteter Fahrbahnen/Rampen (Gehrung, keine Keillücken), Höhe =
   höchster Punkt zwischen Mittellinie und Planke + Holm/Wand (Dämme, Widerlager bis 12 m tiefer). Lücken nur, wo eine
   *anschließende* Fahrbahn (gemeinsamer Punkt, Rampen-Ketten) nahe kommt – nie für die Gegenfahrbahn (Mittelplanke),
   Parallelstraßen oder Brücken darüber. Zusätzlich öffnet jede andere Autobahn-/Rampenfahrbahn **ohne gemeinsamen
   Knoten**, deren Band (+0,4 m) die Planke überdeckt (Einfädelkeile als eigene Ways, zwei Rampen nebeneinander), und
   jede **ebenerdige Brückenplatte** daneben (Oberfläche < 2,4 m über dem Boden an der Planke; nicht die eigene
   Fortsetzung, `Continues`) – sonst stand die Wand in deren Spur. Gegenfahrbahn (auch Rampen, `AgainstChain`) so nah,
   dass die Mittelplanken in der anderen Spur stünden → Planke rückt in die Mitte zwischen beide Achsen (Linie alle 8 m
   verdichtet). **Rastplätze**: endet eine Rampe nur an `service`-Wegen (≤ 3 m), laufen ihre Planken entlang der
   geradesten Zufahrt bis 150 m weiter (Übergang 5,8 → 2,8 m Abstand), abzweigende Service-Wege (keine Parkgassen)
   ≤ 250 m bekommen eigene Planken; an Parkflächen (`amenity=parking`) offen (`RestAreaRun/RestAreaBranches`).
   Brückenbrüstungen haben oben drauf ebenfalls eine unsichtbare 1,4-m-Wand (`BridgeBuilder`, Bucket `Blockers`) –
   die 1,1-m-Brüstung wurde von der Auto-Kapsel überklettert. Wand ≫ Radius der Auto-Kapsel (0,95 m): bei 1,05 m kletterte der
   CharacterController noch an einzelnen Stellen darüber; im Auto `stepOffset` ≤ 0,25 m, `slopeLimit` 35°.
   **Kartenende** (Fahrbahn verlässt den gebauten Teil, an Ende *und* Anfang einer Kette, nur die erste Sperre von innen,
   `MotorwayBuilder.DeadEnds`): Absperrschranke Z. 600 rot-weiß von Planke zu Planke mit gelben Warnleuchten, Leitbaken
   Z. 605 an beiden Rändern auf den letzten 40 m, unsichtbare Wand dahinter; gelbe Tafel „Ende des Kartenbereichs“ über
   der Schranke und 400 m vorher (`TrafficBuilder.MapEndSigns`).
   `BridgeBuilder` nimmt als Endhöhe den höchsten Boden ≤ 25 m vor dem Brückenende (das DGM hat dort oft eine Grube)
   und füllt die Grube im Fahrbahnstreifen auf (vor `BuildBase`, nach allen Brücken, nicht auf anderen Fahrbahnen –
   Brückenplatten zählen nicht); aneinanderstoßende Brücken (Viadukte aus mehreren Ways) bekommen am Stoß dieselbe
   Höhe, mindestens die Gerade zwischen den beiden fernen Enden (sonst fanden beide Widerlagersuchen das Tal darunter:
   22-%-Senke im Viadukt am Dreieck Oldenburg-West, Autos flogen über die Brüstung); wo der drapierte Anschluss trotzdem höher/tiefer liegt, läuft die Platte auf ~1:12 (≥ 4 m, kurze Endfelder
   ganz) weich auf die Straße (`Deck.HeightAt`, Log „eased onto the road“). Brüstungen mit geschlossenen Enden. Prüfen: `-executeMethod LeGamez.Editor.MotorwayDriveTest.Run -area <id> [-fresh] [-trace x,z]`
   (Auto-Kapsel fährt von jedem 150. m jeder Autobahn-Fahrbahn 180 m geradeaus mit ±10/25°; `-fresh` baut Planken
   neu ohne Re-Import, mit dem Gelände wie importiert). Endet die Kapsel abseits der Fahrbahn innerhalb eines
   `highway=rest_area/services`-Polygons, zählt das als „inside a rest area“, nicht als Ausbruch. Westerstede: 1451 von
   1452 an der Planke, 1 auf dem Rastplatz (Parkgasse), 0 Ausbrüche (vorher 1448 + 4 „Ausbrüche“ – alle über die
   Ausfahrt in einen Rastplatz und hinter dem Rampenende ins Gebüsch). `"key_size"` (areas.json) hält `AreaData.sizeMeters` und damit
   `AreaInfo.MemoryKey` beim Vergrößern um denselben Mittelpunkt stabil (gespeicherte Positionen/eigene Startpunkte
   bleiben gültig). HUD-Titel = nächster Startpunkt (Westerstede/Oldenburg), Fahrradplätze bis 80.
   **Glatte Fahrbahnen** (`RoadProfile`, nach `BridgeBuilder.Prepare`, vor `EaseEnds`/`BuildBase`): drapierte Straßen
   folgten jeder DGM-Delle (Gräben, Rauschen, Gruben an Brückenenden – auf der A28 „Schlaglöcher“). Jetzt bekommt jede
   Straße ein Längsprofil (Ways zu Ketten, Median → 2× gleitendes Mittel; Fenster Autobahn 100 m, Bundes-/Landstraße
   30–50 m, Wohnstraße 20 m, Wege 10 m mit max. ±0,5 m – Dünenwege bleiben natürlich; Autobahn ≤ 5 % Steigung), das ins
   Höhenfeld geschrieben wird: quer eben (mind. 5 m ab Mitte), seitlich weich auslaufend; große Unterschiede neben der
   Fahrbahn (Rampen kreuzender Brücken, Gräben, Höfe) bleiben. Anker: gemeinsame Knoten mit schon profilierten Straßen
   (Rang: Autobahn → … → Fußweg) und freie Brückenenden (Fahrbahn trifft die Platte; dahinter eine Zelle Widerlager).
   **Ganze Zellen**: Das Gelände besteht aus 5-m-Zellen; eine Zelle, deren Ecken auf verschiedenen Höhen liegen, kippt
   als steile Fläche durch die Fahrbahn („Stufe/Loch“ quer über die A28). Deshalb setzt `RoadProfile.Write` bei Fahrbahnen
   (Rang ≤ 5) **alle Ecken jeder Zelle, die das Band (+ 0,5 m) berührt**, hart auf das Profil (auch steile Böschungskanten;
   an Brückenenden die Zellen bis eine halbe Diagonale unter die Platte = Widerlager, nie über der Plattenoberfläche).
   Autobahn/Rampen: eine Probe unter dem eigenen Band schlägt eine, die die andere Straße nur als Zellecke braucht.
   Geht unter einem Brückenende ein Weg/eine Straße durch (≥ 1 m tiefer), teilen sich dessen Zellen und die der Zufahrt
   Ecken → `BridgeBuilder.Extension` verlängert die Platte **entlang der Zufahrt** (Polylinie, ≤ 30 m), bis keine Zelle am
   Ende mehr an eine Zelle des Wegs darunter grenzt (`SharesCell`); nie über einen Knoten hinaus, an dem eine andere
   Fahrbahn ebenerdig anschließt. `RoadProfile` kürzt die Zufahrt um genau diese Länge. Unter Brücken schreibt nur die
   Straße darunter; Meer/Hafen, CoarseTerrain-Blöcke tabu.
   Kollider: Die Zufahrt fährt auf dem Gelände (das Band liegt 10 cm drüber, nur gezeichnet), die Platte auf ihrer
   Fahrbahn → an jedem freien Plattenende eine unsichtbare 2-m-Rampe (`EndWedge`, Bucket `Blockers`) statt 10-cm-Kante.
   Brüstungen weichen anderen Platten aus (`SideEdge`, Log „parapet … in the median/open towards“): stünde die
   Brüstung auf der Fahrbahn einer anderen Brücke gleicher Höhe, steht sie bei Gegenrichtung mittig (Mittelwand zweier
   Richtungsfahrbahn-Brücken, OSM-Achsen oft nur 9 m auseinander), bei gleicher Richtung (Rampenbrücke fädelt ein/aus)
   entfällt sie, wo deren Platte dahinter weitergeht. Platten in Stücken ≤ 6 m.
   Prüfen: `-executeMethod LeGamez.Editor.RoadStepCheck.Run -area <id> [-classes motorway,motorway_link|all] [-min 0.05]`
   lädt die Kacheln entlang der Straßen, fährt Mitte und ±halbe Breite alle 0,5 m auf den Kollidern (Gelände, Platten,
   Blockers) ab und meldet senkrechte Stufen (> 4 cm auf 5 cm), Knicke/Abbrüche (Steigung auf 1 m weicht > 12 % vom
   Mittel ab) und Löcher → `Builds/RoadSteps/<id>.csv` + `-views`-Liste für `AreaViewSnapshot`; `-at x,z;…` druckt
   Profile/Plattenenden an einer Stelle. Westerstede Autobahn+Rampen: 84 Stellen → 19 (Rest ≤ 0,55 m auf Rampen an Dreieck
   Oldenburg-West/Wechloy: Rampe und Fahrbahn in derselben 5-m-Zelle auf verschiedener Höhe), alle Straßen 272 → 79;
   „WALL in lane“ = `Blockers`-Wand in einer Spur; Sperren ≤ 6 m neben einer `MotorwayBuilder.DeadEnds`-Stelle zählen
   extra als „map-end“. `-fresh` ersetzt die Blockers der Kacheln durch frisch gebaute (Planken „rail“, Brücken „deck“,
   ohne Re-Import), `-railDebug "x,z;…"` loggt Ketten/Nachbarfahrbahnen dort (`[RailDebug]`), `-walkAt x,z` jede Probe
   im Umkreis 10 m (`[Walk]`, mit Treffern). Westerstede: Wände in Spuren 13 → 1 (+ 3 echte Kartenende-Sperren; Rest =
   `EndWedge` der kurzen Rampenbrücke 23704443 bei (17593, −12469) neben der 0,51-m-Stufe), Stufen > 5 cm 74 → 52; der
   „1,42-m-Abbruch“ bei (17731, −11708) war die Brüstung der Rampenbrücke 135459833 in der Fahrbahn → `SideEdge` prüft
   jetzt auch Fahrbahnen am Boden, wenn der Boden *unter der Brüstung* auf Plattenhöhe liegt (±0,3 m).
   Offen: Rampe/Fahrbahn in derselben Zelle auf verschiedener Höhe (Wechloy (17586, −12473) 0,51 m, (17751, −11326)
   0,55 m) – braucht gemeinsame Profile überlappender Fahrbahnen ohne gemeinsamen Knoten in `RoadProfile`.
   Log „road roughness“ (Abweichung von der 5-m-Sehne je Klasse vorher → nachher), Profile in
   `Builds/RoadProfile/<id>_roads.csv`, Plot `python3 Tools/dgm/plot_road_profile.py <id>`; nur das Gelände prüfen
   (ohne Import, ~1 min): `-executeMethod LeGamez.Editor.OsmImport.OsmAreaImporter.RoadProfileBatch -area <id>`.
   `AreaViewSnapshot` setzt die Kamera auf dieses fertige Gelände (`OsmAreaImporter.PrepareTerrain`).
4. **In Unity importieren** (baut alle Gebiete, Menü-Vorschaubilder, Minimap-Karten, Hauptmenü-Szene):
   ```bash
   /Applications/Unity/Hub/Editor/6000.3.26f1/Unity.app/Contents/MacOS/Unity -batchmode -quit -projectPath . -buildTarget iOS \
     -executeMethod LeGamez.Editor.OsmImport.OsmAreaImporter.ImportAllBatch -logFile /tmp/import.log
   grep "\[OSM\]" /tmp/import.log
   ```
   Ergebnis: `Assets/Generated/<id>/<id>_area.asset` (`World/AreaData`: Spawn, Grenzen, POIs, Straßen, Ampeln, Schildertexte,
   Fassaden-Records, Kachelliste, Übersichtskarte), Kacheln `Generated/<id>/tiles/`, Eintrag in `Assets/Generated/AreaCatalog.asset`,
   Thumbnail `Generated/<id>/<id>_thumb.png`, Kartenbilder (siehe unten) `Generated/<id>/regions~/r<rx>_<rz>.jpg` +
   Übersicht `Generated/<id>/<id>_map.jpg`. Es gibt keine Gebietsszenen mehr:
   `Assets/Scenes/World.unity` (Licht, Himmel, `World/AreaLoader`) baut das Gebiet aus `AreaData` auf; Materialien/Shader
   liegen in der App (`GameMaterials.worldMaterials`, `signTextMaterial`, vom Importer gesetzt). Editor-Werkzeuge öffnen ein
   Gebiet mit `Editor/AreaPreview.Open` (ungespeicherte Szene, alle Kacheln). Im Editor zeigt das Menü das Gebiet sofort als Karte;
   in die App kommt sie erst per **Veröffentlichen** (Schritt 7). Die Build-Settings enthalten nur `MainMenu`.
   **Inkrementell** (`OsmImport/TileBuild`): Ein erneuter Import lässt alle Builder laufen (gleiche Zufallsfolge → gleiche
   Objekt-IDs, Kataloge vollständig), drapiert/vernetzt/schreibt aber nur Kacheln, deren **Signatur** sich geändert hat
   (Hash der Kachelgeometrie vor dem Drapieren + Meer + Instanzen + Gelände + `TileBuild.Version`; Liste in
   `Generated/<id>/build~/tiles.txt`, nicht im Repo). Regionsbilder/Kartenbilder nur, wo sich Kacheln geändert haben
   (`build~/maps-dirty.txt`). Westerstede: voll ~8 min (vorher ~42 min), ein Haus/Objekt geändert ~2,5 min (2 Kacheln).
   `TileBuild.Version` erhöhen, wenn sich Code *nach* der Signatur ändert (TerrainDrape, `MeshSink.ToMesh`, Part-Regeln in
   `WriteTiles`); `-full` (ImportAreaBatch) oder `build~/` löschen baut alles. Asset-Schreiben gebündelt
   (`StartAssetEditing`, 1000 Kacheln je Refresh, Speichern erst nach `StopAssetEditing` – sonst fehlen die Meshes in den
   Dateien); ein Refresh je Kachel war ~95 % der alten Importzeit. `[Import]`-Zeilen im Log = Zeiten je Phase
   (`ImportProfile`); `-executeMethod LeGamez.Editor.TileDigest.Run -area <id> -out <datei>` = Inhalts-Hash je Kachel
   (zwei Importe exakt vergleichen).
5. **Prüfen**: `-executeMethod LeGamez.Editor.SceneSnapshot.RenderAll` rendert Luft-/Straßen-/Wahrzeichen-Bilder nach `Builds/Snapshots/`.
   POI-Auswahl in `Assets/Generated/<id>/<id>_pois.asset` ansehen (max. 30, Hotels/Läden sind ausgefiltert).
6. **Bauen & aufs iPhone** – siehe unten.
7. **Veröffentlichen**: `Tools/release/publish_maps.sh [<id> …]` – siehe „Karten-Downloads“.

### Karten-Downloads (App enthält keine Karten, lädt nie ganze Karten)
- **Format 9** (= 6 + Instanzen in den Kacheln (7) + reine Kollisionsteile `WorldTile.FlagNoRender` (8) + gestückeltes World-Pack in 4×4-km-Dateien (9); `World/MapFormat`, dieselben Dateien für iOS und Android, unabhängig von der Unity-Version): je Gebiet
  `area.bin` (AreaData-JSON + kleine Übersichtskarte + **Index des World-Packs**, ~0,2–1 MB) und `world.bin` (alle
  Einträge hintereinander: `t/<x>_<z>` = 200-m-Kachel (Deflate; Geometrie + Abschnitt „INS1“ mit Baum-/Busch-/Laternen-Instanzen), `f/<rx>_<rz>` = Fassaden-Records der 1-km-Region,
  `m/<rx>_<rz>` = Minimap-JPEG der Region). Die App holt **einzelne Einträge per HTTP-Range** (`Core/PackTileSource`,
  GitHub-Release-Assets liefern 206) und cached sie unter ihrem Inhalts-Hash (`maps/<id>/cache/<entry>-<hash>.bin`).
  Export: `Editor/MapExport` (`-executeMethod LeGamez.Editor.MapExport.ExportBatch [-areas a,b]`) → `Builds/Maps/<id>/`
  + `Builds/Maps/index.json`. Vorher: Format 5 = eine Datei pro Region, Formate 1–4 = AssetBundles.
  **Inkrementeller Export**: `Builds/Maps/<id>/export.json` merkt sich je Eintrag die Quelle (Kachel-Signatur aus
  `build~/tiles.txt`, Hash der Fassaden-Records/des Regionsbilds) und wo die Bytes liegen; Unverändertes wird aus den alten
  Dateien kopiert (Westerstede 21 s statt 111 s). Prüfen wie die App: `-executeMethod LeGamez.Editor.MapExport.VerifyBatch`.
  **Gestückeltes Pack** (ab `MapFormat.ChunkedPackFormat` = 9, greift sobald `MapStore.Format` ≥ 9): statt `world.bin`
  Dateien je 4 × 4 km (`MapFormat.ChunkFile`, im Index `region = "pack"`): `world-<cx>_<cz>.bin` = Kacheln + Fassaden,
  `world-<cx>_<cz>-maps.bin` = Regionsbilder (werden um jede geänderte Kachel neu gerendert und sind nie bytegleich –
  eigene kleine Dateien, damit sie keine Geometrie-Stücke mitziehen). `area.bin` enthält `pack2.idx` (Datei je Eintrag);
  `PackTileSource` holt jeden Eintrag aus seiner Datei. Westerstede: 126 Dateien; ein Haus + eine Bank geändert →
  1 Geometrie-Stück (40 MB) + 4 Bild-Stücke (je ~1,6 MB) + `area.bin` = 48 MB statt 421 MB Upload (`publish_maps.py`
  vergleicht SHA-256 je Datei, braucht keine Änderung). Format-7-Dateien (`pack.idx`) bleiben lesbar.
- **Starten**: Startpunkt antippen (`MapStore.Start`) → nur `area.bin` holen/auffrischen, Welt-Szene, Ladebildschirm
  wartet auf die Kacheln um den Start (~1–3 MB). Weitere Kacheln, sobald der Streamer sie braucht, plus Vorladen bis 1,1 km (in Fahrt ab 10 m/s um den Punkt 15 s voraus, alle 0,1 s, bis 6 Anfragen;
  der Streamer sortiert ab 5 m/s nach Abstand zu einem Punkt 4 s voraus). Fahrzeuge bremsen auf 5 m/s, solange die Kachel
  unter oder vor ihnen (3 s) noch nicht in der Szene ist (`WorldStreamer.Ready`, `VehicleSteering.speedLimit`).
  Cache = LRU (`MapStore.CacheLimit` 1 GB). Offline: gecachte Kacheln, fehlende werden nach 20 s erneut versucht.
- **Startpunkte im Katalog**: `index.json` je Karte `starts[]` = {name, x, z, yaw, thumb (lokaler Pfad, leer = Kartenbild)}
  (`MapStore.Entry.starts`, eine Menükarte je Start; fehlt das Feld → ein Start mit Kartennamen) und `start[]` = Regionen
  ≤ 1,2 km um **alle** Startpunkte. Kartenbilder: `Generated/<id>/<id>_thumb.png` (erster Start) und `<id>_thumb_<k>.png`
  (`SceneSnapshot.RenderThumbnails`, gleiche Ansicht um den Start). `publish_maps.py` muss `starts` in den Katalog übernehmen
  und die Start-Bilder hochladen (`thumb` → URL), sonst zeigt die App nur eine Karte pro Gebiet.
- `Tools/release/publish_maps.sh [--dry-run] [--drop-old] [<id> …]` exportiert und lädt **nur geänderte Dateien** (SHA-256)
  hoch (bei Format 6/7: `area.bin` + `world.bin` je Gebiet, ab 8: `area.bin` + geänderte `world-<cx>_<cz>.bin`). Katalog `https://github.com/1letzgo/nordisch/releases/download/maps/maps.json` (Pre-Release `maps`, nie „latest“ →
  stört den APK-Updater nicht): je Karte Name/Lage/Fahrzeuge/Thumbnail-URL und `builds[]` = {platform ("any"), format,
  unity (leer), version (yyyyMMddHHmm), url, size, sha256, files[] = {name, region, url, size, sha256}, start[] =
  Regionen ≤ 1,2 km um den Spawn}. Dateien liegen inhaltsadressiert (`<id>-f5-<name>-<sha12>.bin`) in der Pre-Release
  **`maps-<id>`** (Asset-Limit pro Release), Katalog + Thumbnails in `maps`. Unveränderte Regionen behalten ihre URL →
  kein Upload, kein erneuter Download. Nicht mehr referenzierte Assets werden gelöscht. Gebiete, die nicht mehr in
  `areas.json` stehen (z. B. `oldenburg`, jetzt Startpunkt in Westerstede), fliegen ganz aus dem Katalog.
- **Nur Benötigtes auf dem Gerät**: „Laden“ holt `area.bin` + Startregionen; alle anderen Regionen lädt
  `Core/RegionTileSource` bei Bedarf (Kachel gebraucht oder Spieler ≤ 1,6 km entfernt, eine Region nach der anderen).
  Kacheln werden im Hintergrund-Thread gelesen/entpackt, auf dem Hauptthread zu Meshes (1 Kachel pro Frame).
  Regionen = LRU-Cache (`MapStore.CacheLimit` 1 GB, `usage.json`), beim nächsten Start gekürzt. Offline: fehlende Kacheln
  werden nach 10 s erneut versucht; Spawn/Beamen warten nicht auf sie. Updates holen nur Dateien, die auf dem Gerät liegen
  und deren SHA-256 sich geändert hat.
- App: `Core/MapStore` – Katalog einmal pro App-Start laden (Kopie für offline), Download → Größe + SHA-256 prüfen →
  `persistentDataPath/maps/<id>/` + `info.json` (iOS: kein iCloud-Backup). Spielen = `area.bin` lesen
  (`MapFormat.ReadArea`), `AreaLoader.Pending` = AreaData, Welt-Szene (Build-Index 1) laden. Im Editor listet `MapStore`
  die lokalen AreaData-Assets, Kacheln kommen direkt aus `Assets/Generated`.
- **Update-Logik**: Ein Build passt, wenn `MapStore.Format` gleich ist (Plattform "any"). Neuerer passender Build →
  „Update“ (im WLAN automatisch, sonst Tipp auf das Pill; die alte Version bleibt bis zum Tausch spielbar).
  Installierter Build passt nicht mehr (App-Update) → „Update nötig“. Kein passender Build: nur neuere Formate →
  „Neue App-Version nötig“, nur ältere → „Karte wird aktualisiert“.
- Testen ohne GitHub: `maps/catalog-url.txt` im App-Datenordner (Android: `/storage/emulated/0/Android/data/de.letzgo.legamez/files/`)
  enthält eine andere Katalog-URL, z. B. `file://…/catalog.json` mit `file://`-Datei-URLs (per `adb push`).
- `MapStore.Format` erhöhen, wenn Karten etwas brauchen, das ältere Apps nicht haben (Änderungen an MapFormat,
  `MapFormat.AreaJson`, AreaData/WorldTile-Feldern …). **Reihenfolge bei neuem Format: erst Karten veröffentlichen, dann
  App releasen** (ohne `--drop-old`, damit alte Apps ihre Builds behalten).
- Stripping: Engine-Code-Stripping sieht nur Menü + Welt-Szene → `Assets/link.xml` (TextMesh, Collider, Light,
  Assembly-CSharp). Fog-Varianten: Grafik-Einstellung „Fog Modes“ = Custom.

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
- `GroundBuilder`: Grund-Kacheln, Flächen (landuse/natural/leisure), Straßen-Bänder, Wasser, Hecken/Mauern/Zäune.
  Geländeblöcke werden bis 40 m zusammengefasst, wenn jede Dreieckshälfte (Diagonale x0z0–x1z1) eben ist (±2 cm) und eine Bodenbedeckung hat.
- `MarkingBuilder`: **Fahrbahnmarkierungen nach Regeln (RMS)**, alle an einer Stelle, nach `MotorwayBuilder`. Ways derselben
  Klasse werden über Knoten mit genau zwei Enden zu Ketten verbunden (Strichphase läuft durch – kein Neustart an Way-Grenzen,
  Kacheln, Brücken); jeder Way liefert seinen Querschnitt, Linien gleicher Lage gehen an Way-Grenzen nahtlos (Gehrung) über.
  Autobahn (+ einbahnige trunk): Fahrbahnbegrenzung 0,30 m links und rechts vor dem Standstreifen (2,3 m), Leitlinien 6/12 m
  0,15 m aus `lanes`; Ein-/Ausfädelstreifen (`lanes` = n+1, letzte Spur slight_right/merge_to_left bzw. `placement=right_of:1`):
  Breitstrich 6/6 m 0,30 m zum Zusatzstreifen, durchgehende Begrenzung außen. Wo eine Rampe abgeht/einmündet, wird die rechte
  Begrenzung zum Breitstrich, solange sie in der Rampe liegt; Rampenlinien beginnen erst außerhalb der Hauptfahrstreifen
  (Spitze), dazwischen Sperrfläche (45°-Schraffur 0,30 m alle 5 m) bis die Fahrbahnen auseinander sind. Rampen: 0,30 m beidseitig.
  Zweistreifige Straßen: Leitlinie Mitte nur primary…tertiary (unclassified mit lanes ≥ 2), außerorts 4/8 m, innerorts 3/6 m,
  durchgezogen bei overtaking=no oder ≥ 4 Spuren; vor höherrangigen Straßen Warnlinie (6/3 bzw. 3/1,5) und dann
  Fahrstreifenbegrenzung (30 m bzw. 15 m). Randlinien 0,15 m nur außerorts (maxspeed > 50, sonst Luftbild „offenes Land“).
  Wohn-/Erschließungsstraßen, service, Wege: keine Markierung. Knoten: Linien enden im markierten Bereich einer anderen
  Fahrbahn; Rang entscheidet (Nebenstraße endet an der Hauptstraße, deren Mittel-/Spurlinien laufen durch, ihre Randlinie
  öffnet nur auf der Seite der Einmündung; gleicher Rang unterbricht sich gegenseitig); verschiedene Ebenen (bridge/layer)
  ohne gemeinsamen Knoten stören sich nicht. Brücken: dieselben Linien auf der Plattenhöhe (auch über die Verlängerung auf die
  Zufahrt), sonst drapiert. Kein Zugriff auf den gemeinsamen `rng`. Prüfen ohne Import (~1 min):
  `-executeMethod LeGamez.Editor.OsmImport.MarkingBuilder.DebugBatch -area <id> [-at x,z]` (Statistik; mit `-at` Zustände
  jeder Linie der Ways ≤ 40 m: `Normal@s Hidden@s(by chain …)`). Westerstede: 176 Ketten, 335 km Farbe, 33 Sperrflächen.
- `CoastBuilder` (nur Inseln mit DGM + Luftbild): Meer/Watt bei **Niedrigwasser wie im Luftbild**. Wasser = DOP-NIR < 50 und < 0,5 m NHN
  oder keine DGM-Kachel (3×3-Mehrheit); Meeresspiegel = Median-DGM der Wasserpixel (≈ −1,5…−1,7 m NHN). `TerrainField.Remap`:
  Wasser → flacher Grund 0,8 m unter dem Spiegel (watbar), Watt (< 1,2 m NHN) auf ein 40-m-Gitter geglättet (an den Geländeblöcken
  ausgerichtet → 2 Dreiecke je 40 m), Land ≥ Spiegel + 0,15. `BuildSea` legt nach dem Draping ebene Wasser-Quads (Quadtree bis 80 m)
  wo Gelände unter dem Spiegel liegt, plus 600 m offenes Meer rund ums Gebiet (Horizont). Bodenbedeckung nach Höhe + DOP
  (`Classify`, Mehrheit über 15 m): Watt/nasser Sand, Salzwiese, Queller, Sand, Dünengras, Gebüsch, Rasen. OSM-natural-/Wiesenflächen und
  Tide-Wasser entfallen dort, OSM-Wasser in Meeresfarbe; Zufallsbüsche nur auf Gebüsch/einem Drittel Dünengras (gleiche Zufallszüge).
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
  **Bäume, Büsche, Laternen sind GPU-Instanzen** (keine Kachelgeometrie): `MeshSink.AddInstance` → `WorldTile.instances`
  (Prototyp, Position, Yaw, Skalierung xyz, Farbe). Prototypen erzeugt die App selbst (`World/InstancePrototypes`, beim
  ersten Gebrauch, deterministisch): Stamm (r 1, Länge 1), 4 Laubkronen + 2 hohe mit Aufsatz (Radius 1, halbe Höhe 0,9;
  Instanz-Skalierung = Kronenradius / halbe Höhe ÷ 0,9), 2 Nadelkronen (3 Kegel, Radius/Höhe 1), 3 Büsche, Laterne.
  Ein Baum = Stamm + Krone. Farbe → Tint = Instanzfarbe ÷ Referenzfarbe des Prototyps (`_InstanceTint`).
  Gelände: `TerrainDrape.Lift(instances)`. Edits verschieben/drehen/skalieren Instanzen wie Geometrie (`EditsPlacer.AppendAll`).
  Formänderungen an Prototypen wirken in allen Karten ohne Neuexport.
  Pflanzen-Builder ziehen aus dem gemeinsamen `rng` genau so viele Zahlen wie früher (`Fork`), sonst verschieben sich IDs/Edits.
- Nahdetails an LoD2-Häusern (`FacadeRecords` im Importer → `Assets/Generated/<id>/<id>_facade.bytes`, nicht im Repo):
  je Shader-Fenster (gleiches Raster wie `FacadeMask`) 3D-Rahmen mit Sprossen, Fensterbank, bei Klinker Rollschicht-Sturz;
  Ladenfenster mit dunklem Rahmen; Haustür mit Vordach + Stufe; Dachrinne + Fallrohr an Traufkanten; weiße Ortgangbretter
  an Giebelkanten (Dachkanten, die nur eine Dachfläche hat); Firstziegel; Sockelband. Gebaut wird erst zur Laufzeit
  (`World/FacadeDetails`): 32-m-Zellen im Umkreis von ~95 m um die Kamera, max. 3 Zellen pro Tick, weiter weg wieder
  verworfen – fern bleiben die Shader-Fenster. Records kommen pro Region: `WorldStreamer.Regions` hält den
  `RegionContent` jeder Region mit mindestens einer Kachel in der Szene (Gerät: Abschnitt der Regionsdatei,
  `RegionTileSource.LoadContent`; Editor: `_facade.bytes` aufgeteilt + `regions~`-Bild); kommt/geht eine Region,
  werden die berührten Zellen neu gebaut. Datensatz: 10 floats (kind, x, y, z, along x/z, a, b, c, RGB);
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
- `MeshSink`: Geometrie pro Material und 100-m-Chunk (+ Instanzliste); Kollider für Gebäude, Props (Stämme/Laternen: Kapseln)
  (Stämme/Props liegen auf Layer 2, damit die Kamera sie ignoriert).
- **Nachladen (Streaming)**: Welt-Meshes stecken nur in Kacheln. `OsmAreaImporter.WriteTiles` sortiert die Chunks in
  200-m-Kacheln (`World/WorldTile`, Meshes als Sub-Assets, `Assets/Generated/<id>/tiles/`, nicht im Repo); Meshes ohne
  Kollider sind nicht CPU-lesbar (halber Speicher). `World/WorldStreamer` (vom AreaLoader erzeugt) lädt Kacheln ≤ 760 m um die
  Kamera (nächste zuerst, max. 3 parallel, 1 pro Frame instanziiert) und gibt sie ab 960 m wieder frei.
  `GameBootstrap` wartet mit Ladebildschirm, bis der Start-Umkreis (220 m) steht; Beamen (`FullMap`) lädt das Ziel hinter
  dem Blitz vor. Editor-Werkzeuge, die die ganze Welt brauchen (`SceneSnapshot`), rufen `LoadAllInEditor()` auf.
  `-executeMethod LeGamez.Editor.MeshStats.Print` zeigt Dreiecke je Materialgruppe und Gebiet (+ Instanzen je Prototyp).
- **Instanzen zeichnen** (`World/InstancedProps`, je Kachel vom Streamer angelegt): `Graphics.RenderMeshInstanced` pro
  Prototyp (≤ 1023 je Aufruf) in `LateUpdate`, Unity cullt per Kachel-/Prototyp-Bounds (Kamera + Schatten), Schatten an.
  Kollision: CapsuleCollider je Stamm/Laterne auf einem Kind-Objekt der Kachel (Layer 2, kein Mesh-Cooking).
  Außerhalb des Play-Modes (Snapshots, Thumbnails, Minimap – `camera.Render` sieht keine Instanced-Draws) werden die
  Instanzen zu normalen Meshes gebacken (gleiche Formen/Farben). Materialien brauchen `enableInstancing` (sonst strippt
  der Build die INSTANCING_ON-Varianten; `EnsureMaterials` setzt es).
- Shader `Assets/Shaders/VertexColorLit.shader`: Vertex-Farben, Fenster/Sprossen (Laibungsschatten, Gardinen), Klinker, Putz,
  Sockel, Traufband, Dachpfannen, Wasser, großflächige Farbvariation auf Boden/Flachdächern. Bordsteinbänder: `GroundBuilder.Kerbs`
  (nicht an Autobahn/Rampen: Standstreifen bleibt Asphalt bis zum Bankett). Bankett/Mittelstreifen/Zwickel bis 9 m
  neben Autobahn-/Rampenbändern = Rasen (`AerialImage` „verge“; der echte breitere Asphalt im Luftbild wurde sonst als
  heller Acker/Pflaster-Streifen gezeichnet).
  `multi_compile_instancing` in allen Pässen; `_InstanceTint` nur in der Instancing-Variante (normale Meshes unverändert).
- Himmel `Assets/Shaders/Sky.shader` (Verlauf, Sonne, ziehende Wolken); Licht/Nebel in `OsmAreaImporter.AddLighting`,
  HDR + Bloom/Farbkorrektur in `ProjectSetup` (`ConfigureUrp`, `CreatePostProfile`).

### Map-Editor (macOS-App, `Tools/MapEditor`)
Draufsicht je Gebiet (Unity-Renders + Hausumrisse). Neue Objekte setzen, außerdem alle vom Importer erzeugten
(Bäume, Büsche, Laternen, Bänke, Kübel, Brunnen, Café-Tische, Denkmäler …) verschieben/drehen/skalieren/löschen.
- Hintergrund (`MapImages.swift`): Übersicht `Assets/Generated/<id>/<id>_map.jpg` (Gebiet + 60 m Rand), ab mittlerem Zoom
  die 1-km-Regionsbilder `regions~/r<rx>_<rz>.jpg` (Region rx = x ∈ [rx·1000, (rx+1)·1000), Norden oben; gitignored,
  entstehen bei `RenderThumbnails`/Import) – nur sichtbare, im Hintergrund dekodiert in 256/512/1024 px, LRU-Cache ~256 MB.
- Große Gebiete (Westerstede ~93 000 Objekte, ~37 000 Häuser): Gitter-Indizes (`GridIndex`, 100 m) für Objekte/Häuser,
  unveränderte erzeugte Objekte als Punkte (< 0,25 pt/m ausgeblendet), Hausumrisse ab 0,6 pt/m, Garten-Linien ab 0,35 pt/m;
  Undo speichert nur Abweichungen vom Import. Prüfen ohne UI: App mit
  `-snapshot <dir> -snapshotAreas spiekeroog,westerstede [-repoPath <repo>]` starten → PNGs je Zoomstufe + Zeiten in `snapshot.log`.
- Speichert nach `Data/<id>.edits.json`: `objects` = neue Objekte, `changes` = Overrides erzeugter Objekte per ID
  (`<typ>@<x·10>,<z·10>`, Liste in `Assets/Generated/<id>/<id>_objects.json`, schreibt der Importer).
- Importer: `EditsPlacer` – alle Placer laufen über `EditsPlacer.Emit` (baut auch gelöschte, damit die Zufallsfolge
  und damit alle anderen Objekte gleich bleiben). Neue Objekttypen: in `FurniturePlacer/PropPlacer.BuildSingle` und `ObjectType.all` (Swift).
- „In Unity übernehmen“ = `-executeMethod LeGamez.Editor.OsmImport.OsmAreaImporter.ImportAreaBatch -area <id>` (Unity-Editor muss zu sein;
  inkrementell: nur geänderte Kacheln + Regionsbilder, Westerstede ~2,5 min).
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
- **Startpunkte eines Gebiets** (areas.json `starts`, z. B. Westerstede + Oldenburg in einem Gebiet): Importer sucht je
  Start einen begehbaren Punkt ≤ 200 m (`FindSpawn`) + Fahrradplatz → `AreaData.starts` (name, position, yaw, bikeSpot;
  `spawn`/`spawnBikeSpot` = erster Start für alte Apps) → `MapFormat.AreaJson.starts` (kein Formatwechsel: alte Apps
  ignorieren es, Dateien ohne Liste → `AreaData.StartList()` = Spawn). Menü + „Andere Orte“: eine Karte/Zeile je Start
  (`StartPoints.Default(areaId, k, name)`, nicht gespeichert). `GameBootstrap`: gewählter Start setzt dort ab – die letzte
  Position (`PositionMemory`) gewinnt nur, wenn sie näher an diesem als an jedem anderen Start liegt (Gebiete mit einem
  Start: wie bisher „weiter, wo ich war“); frischer Start = zu Fuß. Startrad am Start, der dem Spieler am nächsten ist.
  Start im aktuellen Gebiet („Andere Orte“, auch eigene Startpunkte) = direkt hinbeamen (`FullMap.BeamTo`), kein Neuladen.
- **Eigene Startpunkte** (`Core/StartPoints`, `persistentDataPath/startpoints.json`): „Startpunkt merken“ in der großen
  Karte speichert Gebiet + Position + Blickrichtung (Name = nächste Straße). Erscheinen im Menü („Wo startest du?“,
  scrollbar) und in „Andere Orte“; `MapStore.Start(map, point)` → `StartPoints.Pending` → `GameBootstrap` setzt dort ab
  (gewinnt immer gegen die letzte Position).
- `UI/PlaceSwitcher` („Andere Orte“ in der großen Karte): alle Gebiete mit Status; Tipp = hinbeamen (fehlt das Gebiet,
  erst Start-Download). `MapStore.Play` lädt dafür die Welt-Szene mit dem neuen Gebiet neu und gibt das alte erst danach
  frei; jeder `WorldStreamer` übernimmt seine eigene Kachelquelle (`WorldStreamer.Source` → Instanz).
- `UI/FullMap`: Tipp auf Minikarte → große Karte, Beamen. Zoom bis 6× per Zwei-Finger-Pinch, Mausrad oder ＋/−, Ziehen verschiebt;
  Marker behalten ihre Bildschirmgröße. Kartentextur `AreaInfo.mapTexture` = Übersicht (`Generated/<id>/<id>_map.jpg`,
  ≤ 1 px/m, max. 2048 px); stark gezoomt liegen die scharfen Regionsbilder geladener Regionen darüber. `UI/Navigation`:
  Minikarte = Regionsbilder um den Spieler (`UI/RegionMaps`: bei Bedarf dekodiert, max. 1 pro Frame, ~8 im Speicher)
  über der Übersicht. Erzeugt von `SceneSnapshot.RenderThumbnails` (Teil des Imports; einzeln:
  `-executeMethod LeGamez.Editor.SceneSnapshot.RenderThumbnailsBatch -area <id>`): je Region orthografisch genau ihr
  km² (lädt nur die Kacheln drumherum, `WorldStreamer.KeepInEditor`), Übersicht daraus heruntergerechnet.

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
- **Versionierung**: `Tools/release/version.json` = {"version": "1.0", "build": N}. Version von Hand (1.0 → 1.1 …),
  Build-Nummer läuft für iOS und Android gemeinsam hoch (`python3 Tools/release/bump_build.py`). Jeder Build
  (`BuildScript`) setzt daraus iOS `1.0 (N)`, Android versionName `1.0.N` und versionCode `1000000 + N` (über den alten
  Datumscodes ~93 600) – nur für die Dauer des Builds, ProjectSettings bleibt unverändert. `ITSAppUsesNonExemptEncryption`
  setzt `Editor/IosPlistPatch` automatisch.
- Android: `Tools/release/release_android.sh` (Unity zu, versionierte Dateien sauber) → erhöht die Build-Nummer, committet
  `version.json` („Release 1.0.N“), signiert mit `~/.config/nordisch/release.keystore`
  (Passwort in `release.env`, **nicht im Repo – sichern, sonst lassen sich Updates nicht mehr über alte Versionen installieren**),
  Tag `v1.0.N` + GitHub-Release mit `Nordisch-1.0.N.apk`. `--dry-run` = nur bauen (ohne Hochzählen).
  Alte Datums-Releases (v2026.10.13.x) sehen 1.0.N im In-App-Updater nicht als neuer an – sie laufen ab und verweisen dann auf die Releases-Seite.
- Release-APKs laufen **15 Tage** nach dem Build ab: `BuildScript.BuildAndroidRelease` bettet `Resources/BuildInfo.txt`
  (nur während des Builds) ein, `Core/BuildExpiry` + Sperrbildschirm im Hauptmenü mit Link auf das neueste Release.
  Normale Builds (BuildAndroid/BuildIOS) laufen nie ab.
- Karten: `Tools/release/publish_maps.sh` (siehe „Karten-Downloads“), unabhängig vom App-Release.
- iOS/TestFlight: erst `bump_build.py` (+ Commit), dann `BuildIOS`, dann Skill `/testflight-build` auf `Builds/iOS`
  (Version/Build stehen schon richtig in der Info.plist, nichts von Hand ändern). Wird Android im selben Zug released,
  dieselbe Build-Nummer verwenden (also `release_android.sh` zuerst, dann ohne erneutes Hochzählen `BuildIOS`).
- Generierte Kacheln (`Assets/Generated/*/tiles/`) sind nicht im Repo – nach dem Klonen `ImportAllBatch`.
- Große Raster `Data/*.dop.png`, `Data/*.dgm.bin` und die LoD2-Gebäude `Data/*.lod2.json` (Westerstede > 70 MB) sind nicht im Repo (ganze Inseln/Gemeinden > 100 MB) – nach dem Klonen
  je Gebiet `Tools/dop/fetch_dop.py <id>`, `Tools/dgm/fetch_dgm.py <id>` und `Tools/lod2/fetch_lod2.py <id>` laufen lassen
  (die übrigen JSON-Daten sind versioniert).

## Stolperfallen
- Unity schreibt Template-Pakete (purchasing, analytics, collab-proxy, multiplayer.center) zurück in `Packages/manifest.json` → wieder entfernen.
- Unity-Batchmode stürzt gelegentlich mit Exit 139 beim Asset-Import ab → einfach erneut ausführen.
- Builds brauchen mehrere GB Platz (Xcode DerivedData in `/tmp/*-dd`); bei „Disk full“ Simulator-Artefakte löschen.
- Laufzeit-Materialien nie per `Shader.Find` erzeugen (Stripping) – über `Resources/GameMaterials.asset` referenzieren.
- Lizenzhinweise: OSM (ODbL), LGLN (CC BY 4.0), Kenney (CC0) – stehen im Menü und im HUD; bei neuen Quellen ergänzen.

## Assets & Lizenzen
- Figuren: Kenney Mini Characters (CC0), `Assets/ThirdParty/KenneyMiniCharacters`.
- Logo „Kompass-N“: Quelle `Branding/concepts/make_concepts.py` (`python3 ... --final` schreibt `Branding/logo.png`, `wordmark.png`, Lockups). Danach beide nach `Assets/Resources/Branding/` kopieren und `ProjectSetup.Run`. Schrift Rockwell Bold/Regular, Farben Navy `#1A3A4E`, Senf `#E3A82B` (`UiKit`). Altes Logo: `Branding/logo_v1.png`.
