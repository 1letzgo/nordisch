import AppKit
import Foundation

@MainActor
final class EditorModel: ObservableObject {
    @Published var repo: URL?
    @Published var areas: [Area] = []
    @Published var area: Area?
    /// Generated objects first (same order as `genBase`), then new ones. Not @Published: big areas have ~100 000
    /// objects, and a plain didSet lets `objects[i].x = …` change the array in place instead of copying it.
    var objects: [PlacedObject] = [] {
        didSet {
            objectWillChange.send()
            derivedValid = false
        }
    }
    /// Generated objects as imported (the first `genBase.count` entries of `objects` correspond 1:1).
    private(set) var genBase: [PlacedObject] = []
    /// Untouched generated objects by imported position, for drawing and hit tests (cell 100 m).
    private(set) var genGrid = GridIndex(cell: 100)
    @Published var showGenerated = true
    @Published var selection: Set<UUID> = []
    /// Palette item armed for placing (click on the map drops one).
    @Published var placing: ObjectType?
    @Published var dirty = false
    /// Overview + region images (top-down renders from Unity).
    @Published var mapImages: MapImages?
    @Published var buildings: CGPath?
    @Published var showBuildings = true
    @Published var showMap = true
    @Published var status = ""
    @Published var unityBusy = false
    @Published var unityLog = ""
    /// Mouse position in world metres – separate object so moving the mouse doesn't re-render the whole UI.
    let pointer = PointerState()
    var cursor: CGPoint? {
        get { pointer.cursor }
        set { pointer.cursor = newValue }
    }
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

    // MARK: - Derived object data (rebuilt lazily after changes)

    private var derivedValid = false
    private var _edited: [Int] = []
    private var _editedFlag: [Bool] = []
    private var _indexById: [UUID: Int] = [:]

    private func validateDerived() {
        guard !derivedValid else { return }
        derivedValid = true
        var list: [Int] = []
        var flags = [Bool](repeating: false, count: genBase.count)
        for i in objects.indices {
            if i < genBase.count {
                guard Self.differs(objects[i], genBase[i]) else { continue }
                flags[i] = true
            }
            list.append(i)
        }
        _edited = list
        _editedFlag = flags
        if _indexById.count != objects.count || objects.last.map({ _indexById[$0.id] != objects.count - 1 }) == true {
            rebuildIndex()
        }
    }

    private func rebuildIndex() {
        var m = [UUID: Int](minimumCapacity: objects.count)
        for (i, o) in objects.enumerated() { m[o.id] = i }
        _indexById = m
    }

    /// Indices (into `objects`) of new objects and of generated ones that differ from the import, in order.
    var edited: [Int] { validateDerived(); return _edited }
    /// Per generated index: changed (drawn from `edited`, not from `genGrid`).
    var editedFlag: [Bool] { validateDerived(); return _editedFlag }

    func index(of id: UUID) -> Int? {
        validateDerived()
        if let i = _indexById[id], i < objects.count, objects[i].id == id { return i }
        rebuildIndex()
        return _indexById[id]
    }

    private static func differs(_ o: PlacedObject, _ base: PlacedObject) -> Bool {
        o.deleted != base.deleted || abs(o.x - base.x) > 0.005 || abs(o.z - base.z) > 0.005
            || abs(o.yaw - base.yaw) > 0.05 || abs(o.scale - base.scale) > 0.005
    }

    /// Undo keeps only what differs from the import (a full copy of ~100 000 objects per step would cost gigabytes).
    private struct UndoState {
        var changed: [Int: PlacedObject]; var added: [PlacedObject]
        var styles: [HouseStyle]; var removed: Set<String>; var drawn: [DrawnLine]
    }
    private var undoStack: [UndoState] = []
    private var redoStack: [UndoState] = []
    private var undoState: UndoState {
        var changed: [Int: PlacedObject] = [:]
        for i in edited where i < genBase.count { changed[i] = objects[i] }
        return UndoState(changed: changed, added: Array(objects[min(genBase.count, objects.count)...]),
                         styles: houseStyles, removed: gardenRemoved, drawn: drawnLines)
    }

    private func restore(_ st: UndoState) {
        var list = genBase
        for (i, o) in st.changed { list[i] = o }
        objects = list + st.added
        houseStyles = st.styles
        gardenRemoved = st.removed
        drawnLines = st.drawn
    }

    // MARK: - Gardens (hedges, fences, walls)

    @Published var gardenLines: [GardenLine] = []
    /// Ids of generated lines removed in the editor.
    @Published var gardenRemoved: Set<String> = []
    @Published var drawnLines: [DrawnLine] = []
    @Published var gardenSelection: GardenSelection?
    /// Armed drawing tool and the points placed so far.
    @Published var drawingKind: GardenKind?
    @Published var drawingPoints: [CGPoint] = []
    var lastDrawHeight: [GardenKind: Double] = [:]

    private func gardenURL(_ a: Area) -> URL? { repo?.appendingPathComponent("Data/\(a.id).garden.json") }

    func reloadGardens() {
        guard let a = area else { return }
        gardenLines = repo.map { GardenCatalog.load($0.appendingPathComponent("Assets/Generated/\(a.id)/\(a.id)_gardens.json")) } ?? []
    }

    /// Loads Data/<area>.garden.json; returns true if removed ids were re-matched to slightly moved lines.
    private func loadGardenEdits(_ a: Area) -> Bool {
        gardenRemoved = []
        drawnLines = []
        guard let url = gardenURL(a), let data = try? Data(contentsOf: url),
              let file = try? JSONDecoder().decode(GardenEditsFile.self, from: data) else { return false }
        drawnLines = file.lines
        let ids = Set(gardenLines.map(\.id))
        var rematched = false
        for id in file.removed {
            if ids.contains(id) || gardenLines.isEmpty { gardenRemoved.insert(id); continue }
            // Same rule as GardenBuilder.Record: same kind within 1.5 m.
            let parts = id.split(separator: "@")
            let xz = parts.count == 2 ? parts[1].split(separator: "#")[0].split(separator: ",").compactMap { Double($0) } : []
            guard xz.count == 2 else { continue }
            let p = CGPoint(x: xz[0] / 10, y: xz[1] / 10)
            if let near = gardenLines.filter({ $0.kind == String(parts[0]) && !gardenRemoved.contains($0.id) })
                .min(by: { hypot($0.mid.x - p.x, $0.mid.y - p.y) < hypot($1.mid.x - p.x, $1.mid.y - p.y) }),
               hypot(near.mid.x - p.x, near.mid.y - p.y) < 1.5 {
                gardenRemoved.insert(near.id)
                rematched = true
            } else {
                gardenRemoved.insert(id) // keep it (maybe the line comes back after a re-import)
            }
        }
        return rematched
    }

    var selectedGardenLine: GardenLine? {
        if case .generated(let id)? = gardenSelection { return gardenLines.first { $0.id == id } }
        return nil
    }

    var selectedDrawnIndex: Int? {
        if case .drawn(let id)? = gardenSelection { return drawnLines.firstIndex { $0.id == id } }
        return nil
    }

    func toggleRemoved(_ id: String) {
        snapshot()
        if gardenRemoved.contains(id) { gardenRemoved.remove(id) } else { gardenRemoved.insert(id) }
    }

    func startDrawing(_ kind: GardenKind?) {
        drawingKind = kind
        drawingPoints = []
        if kind != nil { gardenSelection = nil }
    }

    /// Where the drawing cursor snaps to (shown as a ring); nil = free.
    var snapPoint: CGPoint? // not published: the map redraws itself on mouse moves

    /// One click = start, second click = end: every drawn line is a single element (extend it by snapping to its end).
    func addDrawingPoint(_ p: CGPoint) {
        guard drawingKind != nil else { return }
        if let last = drawingPoints.last, hypot(p.x - last.x, p.y - last.y) < 0.3 { return } // double click
        drawingPoints.append(p)
        if drawingPoints.count >= 2 { finishDrawing() }
    }

    /// Snap target near `p`: an end point of an existing hedge/fence/wall (preferred) or the nearest point on one.
    func gardenSnap(_ p: CGPoint, tolerance: Double) -> CGPoint? {
        var bestEnd: (CGPoint, Double)?
        var bestOn: (CGPoint, Double)?
        func consider(_ a: CGPoint, _ b: CGPoint) {
            for e in [a, b] {
                let d = hypot(e.x - p.x, e.y - p.y)
                if d < tolerance, d < (bestEnd?.1 ?? .infinity) { bestEnd = (e, d) }
            }
            let abx = b.x - a.x, aby = b.y - a.y, l2 = abx * abx + aby * aby
            guard l2 > 1e-9 else { return }
            let t = max(0, min(1, ((p.x - a.x) * abx + (p.y - a.y) * aby) / l2))
            let q = CGPoint(x: a.x + abx * t, y: a.y + aby * t)
            let d = hypot(q.x - p.x, q.y - p.y)
            if d < tolerance, d < (bestOn?.1 ?? .infinity) { bestOn = (q, d) }
        }
        for g in gardenLines where !gardenRemoved.contains(g.id) {
            guard abs(g.mid.x - p.x) < g.length / 2 + tolerance + 1, abs(g.mid.y - p.y) < g.length / 2 + tolerance + 1 else { continue }
            consider(g.a, g.b)
        }
        for d in drawnLines {
            let pts = d.cgPoints
            for (a, b) in zip(pts, pts.dropFirst()) { consider(a, b) }
        }
        return bestEnd?.0 ?? bestOn?.0
    }

    /// Finishes the current line (≥ 2 points) and keeps the tool armed for the next one.
    func finishDrawing() {
        guard let kind = drawingKind else { return }
        if drawingPoints.count >= 2 {
            snapshot()
            let r = { (v: Double) in (v * 100).rounded() / 100 }
            var line = DrawnLine(kind: kind.rawValue, points: drawingPoints.map { [r($0.x), r($0.y)] })
            if kind.isHedge { line.height = lastDrawHeight[kind] ?? kind.defaultHeight }
            drawnLines.append(line)
            gardenSelection = .drawn(line.id)
        }
        drawingPoints = []
    }

    func deleteDrawn(_ id: UUID) {
        snapshot()
        drawnLines.removeAll { $0.id == id }
        if gardenSelection == .drawn(id) { gardenSelection = nil }
    }

    func updateDrawn(_ id: UUID, _ change: (inout DrawnLine) -> Void) {
        guard let i = drawnLines.firstIndex(where: { $0.id == id }) else { return }
        change(&drawnLines[i])
        if let h = drawnLines[i].height { lastDrawHeight[drawnLines[i].gardenKind] = h }
    }

    /// Nearest garden line within `tolerance` metres: drawn lines first, then generated ones.
    func gardenHit(_ p: CGPoint, tolerance: Double) -> GardenSelection? {
        var best: (GardenSelection, Double)?
        for d in drawnLines {
            let pts = d.cgPoints
            for (a, b) in zip(pts, pts.dropFirst()) {
                let dist = segmentDistance(p, a, b)
                if dist < tolerance, dist < (best?.1 ?? .infinity) { best = (.drawn(d.id), dist) }
            }
        }
        if best != nil { return best?.0 }
        for g in gardenLines {
            guard abs(g.mid.x - p.x) < g.length / 2 + tolerance + 1, abs(g.mid.y - p.y) < g.length / 2 + tolerance + 1 else { continue }
            let dist = segmentDistance(p, g.a, g.b)
            if dist < tolerance, dist < (best?.1 ?? .infinity) { best = (.generated(g.id), dist) }
        }
        return best?.0
    }

    func focusGarden(_ sel: GardenSelection) {
        mode = .gardens
        gardenSelection = sel
        var p: CGPoint?
        switch sel {
        case .generated(let id): p = gardenLines.first { $0.id == id }?.mid
        case .drawn(let id): p = drawnLines.first { $0.id == id }?.cgPoints.first
        }
        if let p { focusPoint = p; focusToken += 1 }
    }

    // MARK: - Houses

    enum Mode: String { case objects, houses, gardens }
    @Published var mode: Mode = .objects {
        didSet { if mode != oldValue { placing = nil; selection = []; selectedHouse = nil; gardenSelection = nil; drawingKind = nil; drawingPoints = [] } }
    }
    @Published var houses: [House] = [] { didSet { rebuildHouseIndex(); rebuildStyleCache() } }
    /// Entries of Data/<area>.buildings.json (also hand-written ones, kept as they are).
    @Published var houseStyles: [HouseStyle] = [] { didSet { rebuildStyleCache() } }
    /// House id -> its style, for drawing (styles are few, houses many).
    private(set) var styleByHouse: [String: HouseStyle] = [:]
    /// Indices of houses with a style.
    private(set) var styledHouses: [Int] = []
    /// First style entry per house id / normalised address (houseStyles order wins, like the importer).
    private var styleIdx: [String: Int] = [:]
    private var styleAddrIdx: [String: Int] = [:]
    private(set) var houseById: [String: Int] = [:]
    /// Normalised address -> house indices (a building can have several parts).
    private(set) var housesByAddr: [String: [Int]] = [:]
    /// Houses by footprint centre (cell 100 m).
    private(set) var houseGrid = GridIndex(cell: 100)

    private func rebuildHouseIndex() {
        houseById = [:]
        housesByAddr = [:]
        houseGrid = GridIndex(cell: 100)
        for (i, h) in houses.enumerated() {
            houseById[h.id] = i
            for a in Set(h.norm) { housesByAddr[a, default: []].append(i) }
            houseGrid.insert(i, at: CGPoint(x: h.bounds.midX, y: h.bounds.midY), extent: max(h.bounds.width, h.bounds.height) / 2)
        }
        matchCache = [:]
    }

    private func rebuildStyleCache() {
        styleIdx = [:]
        styleAddrIdx = [:]
        for (i, st) in houseStyles.enumerated() {
            if let id = st.id, styleIdx[id] == nil { styleIdx[id] = i }
            if let a = st.address.map(HouseCatalog.normalize), styleAddrIdx[a] == nil { styleAddrIdx[a] = i }
        }
        var candidates = Set<Int>()
        for id in styleIdx.keys { if let i = houseById[id] { candidates.insert(i) } }
        for a in styleAddrIdx.keys { for i in housesByAddr[a] ?? [] { candidates.insert(i) } }
        var m: [String: HouseStyle] = [:]
        for i in candidates { if let st = style(houses[i]) { m[houses[i].id] = st } }
        styleByHouse = m
        styledHouses = candidates.sorted()
    }

    /// Houses a style entry applies to (by id, else by address).
    func houses(for st: HouseStyle) -> [House] {
        if let id = st.id, let i = houseById[id] { return [houses[i]] }
        return (st.address.map { housesByAddr[HouseCatalog.normalize($0)] ?? [] } ?? []).map { houses[$0] }
    }
    @Published var selectedHouse: String?
    @Published var houseQuery = ""

    var currentHouse: House? { selectedHouse.flatMap { houseById[$0] }.map { houses[$0] } }

    /// Index of the style entry for a house: id match first, then any of its addresses.
    func styleIndex(_ h: House) -> Int? {
        if let i = styleIdx[h.id] { return i }
        return h.norm.compactMap { styleAddrIdx[$0] }.min()
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

    private var matchCache: [String: [House]] = [:]

    /// Houses whose address contains the query (case-insensitive), at most 40.
    func houseMatches(_ q: String) -> [House] {
        let n = HouseCatalog.normalize(q)
        guard n.count >= 2 else { return [] }
        if let hit = matchCache[n] { return hit }
        var seen = Set<String>()
        var out: [House] = []
        for h in houses where h.norm.contains(where: { $0.contains(n) }) {
            // One row per address (LoD2 buildings come in several parts).
            if seen.insert(h.title).inserted { out.append(h) }
        }
        let result = out.sorted { $0.title.localizedStandardCompare($1.title) == .orderedAscending }.prefix(40).map { $0 }
        if matchCache.count > 64 { matchCache = [:] }
        matchCache[n] = result
        return result
    }

    func focusHouse(_ h: House) {
        mode = .houses
        selectedHouse = h.id
        focusPoint = CGPoint(x: h.bounds.midX, y: h.bounds.midY)
        focusToken += 1
    }

    /// Smallest house containing the point.
    func house(at p: CGPoint) -> House? {
        var best: House?
        houseGrid.query(CGRect(origin: p, size: .zero)) { i in
            let h = houses[i]
            if h.contains(p), h.area < (best?.area ?? .infinity) { best = h }
        }
        return best
    }

    func reloadHouses() {
        guard let a = area else { return }
        houses = repo.map { HouseCatalog.load($0.appendingPathComponent("Assets/Generated/\(a.id)/\(a.id)_buildings.json")) } ?? []
    }

    private func stylesURL(_ a: Area) -> URL? { repo?.appendingPathComponent("Data/\(a.id).buildings.json") }

    var areaRect: CGRect { area?.rect ?? CGRect(x: -500, y: -500, width: 1000, height: 1000) }
    /// Region of the overview image <id>_map.jpg: area rectangle + 60 m margin (SceneSnapshot / AreaInfo.MapMargin).
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
        reloadGardens()
        gardenSelection = nil
        drawingKind = nil
        drawingPoints = []
        let gardenRematched = loadGardenEdits(a)
        houseStyles = []
        if let url = stylesURL(a), let data = try? Data(contentsOf: url) {
            houseStyles = (try? JSONDecoder().decode(HouseStylesFile.self, from: data).buildings) ?? []
        }
        var list: [PlacedObject] = []
        var grid = GridIndex(cell: 100)
        var genCount = 0
        if let repo, let data = try? Data(contentsOf: repo.appendingPathComponent("Assets/Generated/\(a.id)/\(a.id)_objects.json")),
           let gen = try? JSONDecoder().decode(GeneratedFile.self, from: data) {
            for r in gen.objects {
                var o = PlacedObject(type: r.type, x: r.x, z: r.z)
                o.yaw = r.yaw
                o.seed = 0
                o.genId = r.id
                o.baseSize = r.size
                grid.insert(list.count, at: CGPoint(x: r.x, y: r.z), extent: o.kind.radius * r.size)
                list.append(o)
            }
            genCount = gen.objects.count
        }
        let base = list
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
        genBase = base
        genGrid = grid
        objects = list
        dirty = rematched > 0 || gardenRematched // re-save under the new ids
        status = "\(a.name): \(genCount) erzeugte, \(objects.count - genCount) neue Objekte, \(houses.count) Häuser"
            + (genCount == 0 ? " – noch nicht importiert? (In Unity übernehmen)" : "")
            + (rematched > 0 ? " · \(rematched) Änderungen neu zugeordnet (Objekt leicht verschoben)" : "")
            + (missing > 0 ? " · \(missing) Änderungen passen nicht mehr (OSM-Daten geändert?)" : "")
        loadBackground()
        recenterToken += 1
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
        let dir = repo.appendingPathComponent("Assets/Generated/\(a.id)")
        let images = MapImages(overviewURL: dir.appendingPathComponent("\(a.id)_map.jpg"), overviewRect: mapRect,
                               regionsDir: dir.appendingPathComponent("regions~"))
        mapImages = images
        if images.overview == nil {
            status += " · Kein Kartenbild (\(a.id)_map.jpg) – einmal „In Unity übernehmen“"
        } else if images.available.isEmpty {
            status += " · Keine Regionsbilder (regions~) – nur Übersicht"
        }
        // Catalog houses are drawn per visible cell; the LoD2 file is only the fallback without a catalog.
        buildings = nil
        if !houses.isEmpty { return }
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

    /// Indices of the selected objects, in list order.
    var selectedIndices: [Int] { selection.compactMap(index(of:)).sorted() }
    var selectedObjects: [PlacedObject] { selectedIndices.map { objects[$0] } }

    /// Last new (hand-placed) object of a type – new objects keep its rotation/size.
    func lastNew(of type: String) -> PlacedObject? {
        objects[min(genBase.count, objects.count)...].last { $0.type == type }
    }

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
        restore(last)
        dirty = true
        selection = selection.filter { index(of: $0) != nil }
    }

    func redo() {
        guard let next = redoStack.popLast() else { return }
        undoStack.append(undoState)
        restore(next)
        dirty = true
        selection = selection.filter { index(of: $0) != nil }
    }

    func place(_ type: ObjectType, at p: CGPoint) {
        guard area != nil, areaRect.contains(p) else {
            status = "Außerhalb des Gebiets"
            return
        }
        snapshot()
        var o = PlacedObject(type: type.id, x: p.x, z: p.y)
        // Keep rotation/size of the last object of this type – handy for rows of benches.
        if let prev = lastNew(of: type.id) { o.yaw = prev.yaw; o.scale = prev.scale }
        objects.append(o)
        selection = [o.id]
    }

    func update(_ id: UUID, _ change: (inout PlacedObject) -> Void) {
        guard let i = index(of: id) else { return }
        change(&objects[i])
    }

    func updateSelected(_ change: (inout PlacedObject) -> Void) {
        for i in selectedIndices { change(&objects[i]) }
    }

    func deleteSelected() {
        guard !selection.isEmpty else { return }
        snapshot()
        // Generated objects are only marked (the importer skips them); new ones disappear.
        for i in selectedIndices { objects[i].deleted = true }
        if objects[genBase.count...].contains(where: \.deleted) { objects.removeAll { $0.deleted && !$0.isGenerated } }
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
        guard o.isGenerated, let i = index(of: o.id), i < genBase.count else { return false }
        return editedFlag[i]
    }

    func restoreSelected() {
        guard !selection.isEmpty else { return }
        snapshot()
        for i in selectedIndices where i < genBase.count { objects[i] = genBase[i] }
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
        for i in edited where i < genBase.count {
            let o = objects[i]
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
            if let gurl = gardenURL(a) {
                if gardenRemoved.isEmpty && drawnLines.isEmpty { try? FileManager.default.removeItem(at: gurl) }
                else { try enc.encode(GardenEditsFile(removed: gardenRemoved.sorted(), lines: drawnLines)).write(to: gurl, options: .atomic) }
            }
            dirty = false
            status = "Gespeichert: \(rounded.count) neue, \(changes.count) geänderte Objekte, \(houseStyles.count) angepasste Häuser, "
                + "\(gardenRemoved.count) entfernte / \(drawnLines.count) gezeichnete Zäune & Hecken"
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
                    if self.area?.id == a.id { self.reloadHouses(); self.reloadGardens(); self.loadBackground() }
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
