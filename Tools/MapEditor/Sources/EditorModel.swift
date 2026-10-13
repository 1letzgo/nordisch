import AppKit
import Foundation

@MainActor
final class EditorModel: ObservableObject {
    @Published var repo: URL?
    @Published var areas: [Area] = []
    @Published var area: Area?
    @Published var objects: [PlacedObject] = []
    /// Original placement of generated objects, by genId.
    private(set) var originals: [String: PlacedObject] = [:]
    @Published var showGenerated = true
    @Published var selection: Set<UUID> = []
    /// Palette item armed for placing (click on the map drops one).
    @Published var placing: ObjectType?
    @Published var dirty = false
    @Published var background: CGImage?
    @Published var buildings: CGPath?
    @Published var showBuildings = true
    @Published var showMap = true
    @Published var status = ""
    @Published var unityBusy = false
    @Published var unityLog = ""
    @Published var cursor: CGPoint?
    /// Bumped to make the map recentre on the area.
    @Published var recenterToken = 0
    /// Bumped (with `focusPoint`) to zoom the map onto a point.
    @Published var focusToken = 0
    var focusPoint: CGPoint?

    func focus(_ o: PlacedObject) {
        selection = [o.id]
        focusPoint = CGPoint(x: o.x, y: o.z)
        focusToken += 1
    }

    private struct UndoState { var objects: [PlacedObject]; var styles: [HouseStyle] }
    private var undoStack: [UndoState] = []
    private var redoStack: [UndoState] = []
    private var undoState: UndoState { UndoState(objects: objects, styles: houseStyles) }

    // MARK: - Houses

    enum Mode: String { case objects, houses }
    @Published var mode: Mode = .objects { didSet { if mode != oldValue { placing = nil; selection = []; selectedHouse = nil } } }
    @Published var houses: [House] = [] { didSet { rebuildStyleCache() } }
    /// Entries of Data/<area>.buildings.json (also hand-written ones, kept as they are).
    @Published var houseStyles: [HouseStyle] = [] { didSet { rebuildStyleCache() } }
    /// House id -> its style, for drawing (styles are few, houses many).
    private(set) var styleByHouse: [String: HouseStyle] = [:]

    private func rebuildStyleCache() {
        var m: [String: HouseStyle] = [:]
        if !houseStyles.isEmpty {
            for h in houses { if let st = style(h) { m[h.id] = st } }
        }
        styleByHouse = m
    }
    @Published var selectedHouse: String?
    @Published var houseQuery = ""

    var currentHouse: House? { selectedHouse.flatMap { id in houses.first { $0.id == id } } }

    /// Index of the style entry for a house: id match first, then any of its addresses.
    func styleIndex(_ h: House) -> Int? {
        if let i = houseStyles.firstIndex(where: { $0.id == h.id }) { return i }
        let addrs = Set(h.addr.map(HouseCatalog.normalize))
        return houseStyles.firstIndex { $0.address.map { addrs.contains(HouseCatalog.normalize($0)) } ?? false }
    }

    func style(_ h: House) -> HouseStyle? { styleIndex(h).map { houseStyles[$0] } }

    /// Changes the style of a house – keyed by its address (covers all parts of the building) or, without one, its id.
    func updateStyle(_ h: House, _ change: (inout HouseStyle) -> Void) {
        snapshot()
        if let i = styleIndex(h) {
            change(&houseStyles[i])
            if houseStyles[i].isEmpty { houseStyles.remove(at: i) }
        } else {
            var st = h.addr.first.map { HouseStyle(address: $0) } ?? HouseStyle(id: h.id)
            change(&st)
            if !st.isEmpty { houseStyles.append(st) }
        }
    }

    func resetStyle(_ h: House) {
        guard let i = styleIndex(h) else { return }
        snapshot()
        houseStyles.remove(at: i)
    }

    /// Houses whose address contains the query (case-insensitive), at most 40.
    func houseMatches(_ q: String) -> [House] {
        let n = HouseCatalog.normalize(q)
        guard n.count >= 2 else { return [] }
        var seen = Set<String>()
        var out: [House] = []
        for h in houses where h.addr.contains(where: { HouseCatalog.normalize($0).contains(n) }) {
            // One row per address (LoD2 buildings come in several parts).
            if seen.insert(h.title).inserted { out.append(h) }
        }
        return out.sorted { $0.title.localizedStandardCompare($1.title) == .orderedAscending }.prefix(40).map { $0 }
    }

    func focusHouse(_ h: House) {
        mode = .houses
        selectedHouse = h.id
        focusPoint = CGPoint(x: h.bounds.midX, y: h.bounds.midY)
        focusToken += 1
    }

    /// Smallest house containing the point.
    func house(at p: CGPoint) -> House? {
        houses.filter { $0.contains(p) }.min { $0.area < $1.area }
    }

    func reloadHouses() {
        guard let a = area else { return }
        houses = repo.map { HouseCatalog.load($0.appendingPathComponent("Assets/Generated/\(a.id)/\(a.id)_buildings.json")) } ?? []
    }

    private func stylesURL(_ a: Area) -> URL? { repo?.appendingPathComponent("Data/\(a.id).buildings.json") }

    var areaRect: CGRect { area?.rect ?? CGRect(x: -500, y: -500, width: 1000, height: 1000) }
    /// Region of the minimap PNG: area rectangle + 60 m margin (SceneSnapshot / AreaInfo.MapMargin).
    var mapRect: CGRect { areaRect.insetBy(dx: -60, dy: -60) }

    init() {
        repo = Self.findRepo()
        reloadAreas()
    }

    // MARK: - Project / areas

    static func findRepo() -> URL? {
        let fm = FileManager.default
        func isRepo(_ u: URL) -> Bool { fm.fileExists(atPath: u.appendingPathComponent("Tools/osm/areas.json").path) }
        if let saved = UserDefaults.standard.string(forKey: "repoPath"), isRepo(URL(fileURLWithPath: saved)) {
            return URL(fileURLWithPath: saved)
        }
        var u = Bundle.main.bundleURL
        for _ in 0..<6 {
            u.deleteLastPathComponent()
            if isRepo(u) { return u }
        }
        let guess = fm.homeDirectoryForCurrentUser.appendingPathComponent("Code/LeGamez")
        return isRepo(guess) ? guess : nil
    }

    func chooseRepo() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.message = "LeGamez-Projektordner wählen (enthält Tools/osm/areas.json)"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        guard FileManager.default.fileExists(atPath: url.appendingPathComponent("Tools/osm/areas.json").path) else {
            status = "Kein LeGamez-Projekt: Tools/osm/areas.json fehlt"
            return
        }
        UserDefaults.standard.set(url.path, forKey: "repoPath")
        repo = url
        reloadAreas()
    }

    func reloadAreas() {
        guard let repo else { status = "Projektordner nicht gefunden – Datei › Projektordner wählen…"; return }
        struct AreasFile: Decodable { let areas: [Area] }
        do {
            let data = try Data(contentsOf: repo.appendingPathComponent("Tools/osm/areas.json"))
            areas = try JSONDecoder().decode(AreasFile.self, from: data).areas
                .filter { FileManager.default.fileExists(atPath: repo.appendingPathComponent("Data/\($0.id).osm.json").path) }
            let last = UserDefaults.standard.string(forKey: "lastArea")
            if let a = areas.first(where: { $0.id == last }) ?? areas.first { open(a) }
        } catch {
            status = "areas.json: \(error.localizedDescription)"
        }
    }

    private func editsURL(_ a: Area) -> URL? { repo?.appendingPathComponent("Data/\(a.id).edits.json") }

    func open(_ a: Area) {
        if dirty, area != nil { save() }
        area = a
        UserDefaults.standard.set(a.id, forKey: "lastArea")
        selection = []
        placing = nil
        undoStack = []
        redoStack = []
        selectedHouse = nil
        reloadHouses()
        houseStyles = []
        if let url = stylesURL(a), let data = try? Data(contentsOf: url) {
            houseStyles = (try? JSONDecoder().decode(HouseStylesFile.self, from: data).buildings) ?? []
        }
        var list: [PlacedObject] = []
        originals = [:]
        var genCount = 0
        if let repo, let data = try? Data(contentsOf: repo.appendingPathComponent("Assets/Generated/\(a.id)/\(a.id)_objects.json")),
           let gen = try? JSONDecoder().decode(GeneratedFile.self, from: data) {
            for r in gen.objects {
                var o = PlacedObject(type: r.type, x: r.x, z: r.z)
                o.yaw = r.yaw
                o.seed = 0
                o.genId = r.id
                o.baseSize = r.size
                originals[r.id] = o
                list.append(o)
            }
            genCount = gen.objects.count
        }
        var missing = 0
        var rematched = 0
        if let url = editsURL(a), let data = try? Data(contentsOf: url) {
            do {
                let edits = try JSONDecoder().decode(EditsFile.self, from: data)
                var index: [String: Int] = [:]
                for (i, o) in list.enumerated() { index[o.genId!] = i }
                let changes = edits.changes ?? [:]
                for (id, c) in changes {
                    // Placement tweaks in the importer can shift an object slightly (new id) – same rule as
                    // EditsPlacer.TryRematch: same type within 1 m whose own id has no change.
                    guard let i = index[id] ?? Self.rematch(id, in: list, taken: changes) else { missing += 1; continue }
                    if list[i].genId != id { rematched += 1 }
                    if let v = c.x { list[i].x = v }
                    if let v = c.z { list[i].z = v }
                    if let v = c.yaw { list[i].yaw = v }
                    if let v = c.scale { list[i].scale = v }
                    list[i].deleted = c.deleted ?? false
                }
                list += edits.objects
            } catch { status = "\(a.id).edits.json unlesbar: \(error.localizedDescription)" }
        }
        objects = list
        dirty = rematched > 0 // re-save under the new ids
        loadBackground()
        recenterToken += 1
        status = "\(a.name): \(genCount) erzeugte, \(objects.count - genCount) neue Objekte"
            + (genCount == 0 ? " – noch nicht importiert? (In Unity übernehmen)" : "")
            + (rematched > 0 ? " · \(rematched) Änderungen neu zugeordnet (Objekt leicht verschoben)" : "")
            + (missing > 0 ? " · \(missing) Änderungen passen nicht mehr (OSM-Daten geändert?)" : "")
    }

    /// Index of the generated object of the same type nearest to the position encoded in `id` ("type@x10,z10"), within 1 m.
    static func rematch(_ id: String, in list: [PlacedObject], taken: [String: EditsFile.Change]) -> Int? {
        let parts = id.split(separator: "@")
        guard parts.count == 2 else { return nil }
        let type = String(parts[0])
        let xz = parts[1].split(separator: "#")[0].split(separator: ",").compactMap { Double($0) }
        guard xz.count == 2 else { return nil }
        let x = xz[0] / 10, z = xz[1] / 10
        var best: (Int, Double)?
        for (i, o) in list.enumerated() where o.type == type && taken[o.genId ?? ""] == nil {
            let d = hypot(o.x - x, o.z - z)
            if d <= 1, d < (best?.1 ?? .infinity) { best = (i, d) }
        }
        return best?.0
    }

    func loadBackground() {
        guard let repo, let a = area else { return }
        let png = repo.appendingPathComponent("Assets/Resources/AreaMaps/\(a.id).png")
        if let src = CGImageSourceCreateWithURL(png as CFURL, [kCGImageSourceShouldCache: false] as CFDictionary) {
            background = CGImageSourceCreateImageAtIndex(src, 0, nil)
        } else {
            background = nil
        }
        buildings = nil
        if !houses.isEmpty {
            let path = CGMutablePath()
            for h in houses { path.addPath(h.path) }
            buildings = path
            return
        }
        let lod2 = repo.appendingPathComponent("Data/\(a.id).lod2.json")
        let id = a.id
        Task.detached(priority: .userInitiated) {
            let path = Self.loadBuildings(lod2)
            await MainActor.run {
                if self.area?.id == id { self.buildings = path }
            }
        }
    }

    /// All LoD2 roof/wall rings projected onto the ground (x east, y north).
    nonisolated static func loadBuildings(_ url: URL) -> CGPath? {
        guard let data = try? Data(contentsOf: url),
              let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let list = root["buildings"] as? [[String: Any]] else { return nil }
        let path = CGMutablePath()
        for b in list {
            guard let surfaces = b["s"] as? [[String: Any]] else { continue }
            for s in surfaces where (s["t"] as? String) == "R" {
                guard let ring = s["r"] as? [[Double]], ring.count > 2 else { continue }
                path.addLines(between: ring.map { CGPoint(x: $0[0], y: $0[2]) })
                path.closeSubpath()
            }
        }
        return path
    }

    // MARK: - Editing

    var selectedObjects: [PlacedObject] { objects.filter { selection.contains($0.id) } }

    /// Call before every user change (also marks the document dirty).
    func snapshot() {
        dirty = true
        undoStack.append(undoState)
        if undoStack.count > 200 { undoStack.removeFirst() }
        redoStack = []
    }

    var canUndo: Bool { !undoStack.isEmpty }
    var canRedo: Bool { !redoStack.isEmpty }

    func undo() {
        guard let last = undoStack.popLast() else { return }
        redoStack.append(undoState)
        objects = last.objects
        houseStyles = last.styles
        dirty = true
        selection = selection.filter { id in objects.contains { $0.id == id } }
    }

    func redo() {
        guard let next = redoStack.popLast() else { return }
        undoStack.append(undoState)
        objects = next.objects
        houseStyles = next.styles
        dirty = true
    }

    func place(_ type: ObjectType, at p: CGPoint) {
        guard area != nil, areaRect.contains(p) else {
            status = "Außerhalb des Gebiets"
            return
        }
        snapshot()
        var o = PlacedObject(type: type.id, x: p.x, z: p.y)
        // Keep rotation/size of the last object of this type – handy for rows of benches.
        if let prev = objects.last(where: { $0.type == type.id && !$0.isGenerated }) { o.yaw = prev.yaw; o.scale = prev.scale }
        objects.append(o)
        selection = [o.id]
    }

    func update(_ id: UUID, _ change: (inout PlacedObject) -> Void) {
        guard let i = objects.firstIndex(where: { $0.id == id }) else { return }
        change(&objects[i])
    }

    func updateSelected(_ change: (inout PlacedObject) -> Void) {
        for i in objects.indices where selection.contains(objects[i].id) { change(&objects[i]) }
    }

    func deleteSelected() {
        guard !selection.isEmpty else { return }
        snapshot()
        // Generated objects are only marked (the importer skips them); new ones disappear.
        for i in objects.indices where selection.contains(objects[i].id) { objects[i].deleted = true }
        objects.removeAll { $0.deleted && !$0.isGenerated }
        selection = []
    }

    func duplicateSelected() {
        guard !selection.isEmpty else { return }
        snapshot()
        var copies: [PlacedObject] = []
        for o in selectedObjects {
            var c = PlacedObject(type: o.type, x: o.x, z: o.z)
            c.yaw = o.yaw
            c.scale = o.effScale
            c.x += 2
            c.z -= 2
            copies.append(c)
        }
        objects += copies
        selection = Set(copies.map(\.id))
    }

    func rotateSelected(by deg: Double) {
        guard !selection.isEmpty else { return }
        snapshot()
        updateSelected { $0.yaw = Self.normYaw($0.yaw + deg) }
    }

    func scaleSelected(by f: Double) {
        guard !selection.isEmpty else { return }
        snapshot()
        updateSelected { $0.scale = Self.clampScale($0.scale * f) }
    }

    func nudgeSelected(dx: Double, dz: Double) {
        guard !selection.isEmpty else { return }
        snapshot()
        updateSelected { $0.x += dx; $0.z += dz }
    }

    func selectAll() { selection = Set(objects.filter(isVisible).map(\.id)) }

    func isVisible(_ o: PlacedObject) -> Bool { showGenerated || !o.isGenerated || isChanged(o) }

    /// Generated object that differs from what the importer placed.
    func isChanged(_ o: PlacedObject) -> Bool {
        guard let id = o.genId, let orig = originals[id] else { return false }
        return o.deleted || abs(o.x - orig.x) > 0.005 || abs(o.z - orig.z) > 0.005
            || abs(o.yaw - orig.yaw) > 0.05 || abs(o.scale - 1) > 0.005
    }

    func restoreSelected() {
        guard !selection.isEmpty else { return }
        snapshot()
        for i in objects.indices where selection.contains(objects[i].id) {
            if let id = objects[i].genId, let orig = originals[id] { objects[i] = orig.with(id: objects[i].id) }
        }
    }

    func undeleteSelected() {
        guard !selection.isEmpty else { return }
        snapshot()
        updateSelected { $0.deleted = false }
    }

    static func normYaw(_ y: Double) -> Double {
        var v = y.truncatingRemainder(dividingBy: 360)
        if v < 0 { v += 360 }
        return v
    }

    static func clampScale(_ s: Double) -> Double { min(max(s, 0.2), 8) }

    // MARK: - Save / Unity

    @discardableResult
    func save() -> Bool {
        guard let a = area, let url = editsURL(a) else { return false }
        // Divide by the inverse step so 2.53 stays 2.53 in the JSON (not 2.5300000000000002).
        func r(_ v: Double, _ step: Double) -> Double { (v / step).rounded() / (1 / step).rounded() }
        let rounded = objects.filter { !$0.isGenerated }.map { o -> PlacedObject in
            var c = o
            c.x = r(o.x, 0.01); c.z = r(o.z, 0.01); c.yaw = r(o.yaw, 0.1); c.scale = r(o.scale, 0.01)
            return c
        }
        var changes: [String: EditsFile.Change] = [:]
        for o in objects where isChanged(o) {
            changes[o.genId!] = o.deleted
                ? EditsFile.Change(deleted: true)
                : EditsFile.Change(x: r(o.x, 0.01), z: r(o.z, 0.01), yaw: r(o.yaw, 0.1), scale: r(o.scale, 0.01))
        }
        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        do {
            if rounded.isEmpty && changes.isEmpty {
                try? FileManager.default.removeItem(at: url)
            } else {
                try enc.encode(EditsFile(area: a.id, objects: rounded, changes: changes)).write(to: url, options: .atomic)
            }
            if let surl = stylesURL(a) {
                if houseStyles.isEmpty { try? FileManager.default.removeItem(at: surl) }
                else { try enc.encode(HouseStylesFile(buildings: houseStyles)).write(to: surl, options: .atomic) }
            }
            dirty = false
            status = "Gespeichert: \(rounded.count) neue, \(changes.count) geänderte Objekte, \(houseStyles.count) angepasste Häuser"
            return true
        } catch {
            status = "Speichern fehlgeschlagen: \(error.localizedDescription)"
            return false
        }
    }

    var unityExecutable: URL? {
        guard let repo,
              let txt = try? String(contentsOf: repo.appendingPathComponent("ProjectSettings/ProjectVersion.txt"), encoding: .utf8),
              let line = txt.split(separator: "\n").first(where: { $0.hasPrefix("m_EditorVersion:") }) else { return nil }
        let version = line.replacingOccurrences(of: "m_EditorVersion:", with: "").trimmingCharacters(in: .whitespaces)
        let url = URL(fileURLWithPath: "/Applications/Unity/Hub/Editor/\(version)/Unity.app/Contents/MacOS/Unity")
        return FileManager.default.isExecutableFile(atPath: url.path) ? url : nil
    }

    /// True while any Unity (editor or batchmode) has this project open.
    func unityHasProjectOpen() -> Bool {
        guard let repo else { return false }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/pgrep")
        p.arguments = ["-f", "Unity.app/Contents/MacOS/Unity.*-projectPath \(repo.path)"]
        p.standardOutput = FileHandle.nullDevice
        try? p.run()
        p.waitUntilExit()
        return p.terminationStatus == 0
    }

    /// Saves and re-imports the current area in Unity batchmode (scene, minimap, thumbnails).
    func importInUnity() {
        guard let a = area, let repo, !unityBusy else { return }
        guard save() else { return }
        guard let unity = unityExecutable else { status = "Unity-Version aus ProjectVersion.txt nicht installiert"; return }
        if unityHasProjectOpen() {
            status = "Unity hat das Projekt schon offen – Editor schließen (oder dort LeGamez › Import Area… nutzen)"
            return
        }
        unityBusy = true
        unityLog = ""
        status = "Unity importiert \(a.name)… (dauert ein paar Minuten)"
        let log = FileManager.default.temporaryDirectory.appendingPathComponent("nordisch-mapeditor-import.log")
        let p = Process()
        p.executableURL = unity
        p.currentDirectoryURL = repo
        p.arguments = ["-batchmode", "-quit", "-projectPath", repo.path, "-buildTarget", "iOS",
                       "-executeMethod", "LeGamez.Editor.OsmImport.OsmAreaImporter.ImportAreaBatch",
                       "-area", a.id, "-logFile", log.path]
        p.terminationHandler = { proc in
            let code = proc.terminationStatus
            let text = (try? String(contentsOf: log, encoding: .utf8)) ?? ""
            let lines = text.split(separator: "\n").filter {
                $0.contains("[OSM]") || $0.contains("error CS") || $0.contains("Exception")
            }.prefix(20).joined(separator: "\n")
            Task { @MainActor in
                self.unityBusy = false
                self.unityLog = lines
                if code == 0 {
                    self.status = "Import fertig – Szene \(a.name).unity aktualisiert"
                    if self.area?.id == a.id { self.reloadHouses(); self.loadBackground() }
                } else {
                    self.status = code == 139 ? "Unity abgestürzt (139) – einfach nochmal starten" : "Unity-Import fehlgeschlagen (Exit \(code)), Log: \(log.path)"
                }
            }
        }
        do { try p.run() } catch {
            unityBusy = false
            status = "Unity startet nicht: \(error.localizedDescription)"
        }
    }

    func openSceneInUnity() {
        guard let a = area, let repo, let unity = unityExecutable else { return }
        if dirty { save() }
        let scene = repo.appendingPathComponent("Assets/Scenes/Areas/\(a.name).unity")
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        p.arguments = ["-a", unity.deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().path,
                       "--args", "-projectPath", repo.path, "-buildTarget", "iOS", "-openfile", scene.path]
        try? p.run()
    }
}
