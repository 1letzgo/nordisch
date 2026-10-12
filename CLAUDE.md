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
4. **In Unity importieren** (baut alle Gebiete, Menü-Vorschaubilder, Minimap-Karten, Hauptmenü-Szene):
   ```bash
   /Applications/Unity/Hub/Editor/6000.3.26f1/Unity.app/Contents/MacOS/Unity -batchmode -quit -projectPath . -buildTarget iOS \
     -executeMethod LeGamez.Editor.OsmImport.OsmAreaImporter.ImportAllBatch -logFile /tmp/import.log
   grep "\[OSM\]" /tmp/import.log
   ```
   Ergebnis: `Assets/Scenes/Areas/<Name>.unity`, Eintrag in `Assets/Resources/AreaCatalog.asset`, Thumbnail `Resources/AreaThumbs/<id>.png`,
   Minimap `Resources/AreaMaps/<id>.png`. Die Szene steht automatisch in den Build-Settings; das Menü zeigt sie als Karte.
5. **Prüfen**: `-executeMethod LeGamez.Editor.SceneSnapshot.RenderAll` rendert Luft-/Straßen-/Wahrzeichen-Bilder nach `Builds/Snapshots/`.
   POI-Auswahl in `Assets/Generated/<id>/<id>_pois.asset` ansehen (max. 30, Hotels/Läden sind ausgefiltert).
6. **Bauen & aufs iPhone** – siehe unten.

### Was der Importer macht (`Assets/Editor/OsmImport/`)
- `OsmData` + `MiniJson`: Overpass-JSON lesen, `GeoProjection` (äquirektangulär um den Mittelpunkt, 1 Unit = 1 m, +X Ost, +Z Nord).
- `Lod2Builder`: echte Wand-/Dachflächen aus LoD2; regionaler Stil je ALKIS-Gebäudefunktion + Insel/Festland
  (Klinker rot/dunkel/gelb, weißer Putz, Pfannendach rot/anthrazit, Schornsteine). Materialcode steckt im Vertex-Alpha.
- `BuildingBuilder`: Fallback aus OSM-Grundrissen.
- `GroundBuilder`: Grund-Kacheln, Flächen (landuse/natural/leisure), Straßen-Bänder mit Mittellinie, Wasser, Hecken/Mauern/Zäune.
- `PropPlacer`: OSM-Bäume, Baumgruppen in Parks/Wäldern (mit Abstand zu Wegen), Büsche auf Dünen/Heide, Straßenlaternen.
- `PoiExtractor`: benannte Sehenswürdigkeiten → Stempelheft.
- `MeshSink`: Geometrie pro Material und 100-m-Chunk; Kollider für Gebäude, Stämme, Props
  (Stämme/Props liegen auf Layer 2, damit die Kamera sie ignoriert).
- Shader `Assets/Shaders/VertexColorLit.shader`: Vertex-Farben, Fenster/Sprossen, Klinker, Putz, Dachpfannen, Wasser.

### Schnell eine Stadt ergänzen (Kurzfassung)
```bash
# 1. Eintrag in Tools/osm/areas.json
python3 Tools/osm/fetch_area.py <id>
python3 Tools/lod2/fetch_lod2.py <id>      # nur Niedersachsen
# 2. Unity ImportAllBatch (siehe oben), Snapshots prüfen, bauen
```
Das Menü-Raster ist 2-spaltig und wächst mit; mehr als ~6 Gebiete brauchen später Scrollen.

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

## Stolperfallen
- Unity schreibt Template-Pakete (purchasing, analytics, collab-proxy, multiplayer.center) zurück in `Packages/manifest.json` → wieder entfernen.
- Unity-Batchmode stürzt gelegentlich mit Exit 139 beim Asset-Import ab → einfach erneut ausführen.
- Builds brauchen mehrere GB Platz (Xcode DerivedData in `/tmp/*-dd`); bei „Disk full“ Simulator-Artefakte löschen.
- Laufzeit-Materialien nie per `Shader.Find` erzeugen (Stripping) – über `Resources/GameMaterials.asset` referenzieren.
- Lizenzhinweise: OSM (ODbL), LGLN (CC BY 4.0), Kenney (CC0) – stehen im Menü und im HUD; bei neuen Quellen ergänzen.

## Assets & Lizenzen
- Figuren: Kenney Mini Characters (CC0), `Assets/ThirdParty/KenneyMiniCharacters`.
- Logo „Kompass-N“: Quelle `Branding/concepts/make_concepts.py` (`python3 ... --final` schreibt `Branding/logo.png`, `wordmark.png`, Lockups). Danach beide nach `Assets/Resources/Branding/` kopieren und `ProjectSetup.Run`. Schrift Rockwell Bold/Regular, Farben Navy `#1A3A4E`, Senf `#E3A82B` (`UiKit`). Altes Logo: `Branding/logo_v1.png`.
