import AppKit
import SwiftUI

@main
struct MapEditorApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var delegate
    @StateObject private var model = EditorModel()

    var body: some Scene {
        Window("Nordisch Map Editor", id: "main") {
            ContentView(model: model)
                .frame(minWidth: 1000, minHeight: 640)
                .onAppear { delegate.model = model }
        }
        .commands {
            CommandGroup(replacing: .newItem) {
                Button("Projektordner wählen…") { model.chooseRepo() }
            }
            CommandGroup(replacing: .saveItem) {
                Button("Sichern") { model.save() }.keyboardShortcut("s")
                Button("In Unity übernehmen") { model.importInUnity() }.keyboardShortcut("i", modifiers: [.command, .shift])
                    .disabled(model.unityBusy)
            }
            CommandGroup(replacing: .undoRedo) {
                Button("Widerrufen") { model.undo() }.keyboardShortcut("z").disabled(!model.canUndo)
                Button("Wiederholen") { model.redo() }.keyboardShortcut("z", modifiers: [.command, .shift]).disabled(!model.canRedo)
            }
            CommandGroup(after: .pasteboard) {
                Button("Duplizieren") { model.duplicateSelected() }.keyboardShortcut("d")
                Button("Alle Objekte auswählen") { model.selectAll() }.keyboardShortcut("a", modifiers: [.command, .shift])
            }
            CommandMenu("Objekt") {
                Button("15° drehen") { model.rotateSelected(by: 15) }.keyboardShortcut("]")
                Button("−15° drehen") { model.rotateSelected(by: -15) }.keyboardShortcut("[")
                Button("Größer") { model.scaleSelected(by: 1.1) }.keyboardShortcut("+")
                Button("Kleiner") { model.scaleSelected(by: 1 / 1.1) }.keyboardShortcut("-")
            }
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    weak var model: EditorModel?

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }

    @MainActor
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        if let model, model.dirty { model.save() }
        return .terminateNow
    }
}

struct ContentView: View {
    @ObservedObject var model: EditorModel

    var body: some View {
        NavigationSplitView {
            Sidebar(model: model)
                .navigationSplitViewColumnWidth(min: 220, ideal: 240, max: 320)
        } detail: {
            VStack(spacing: 0) {
                MapCanvasView(model: model)
                Divider()
                StatusBar(model: model)
            }
            .inspector(isPresented: .constant(true)) {
                Inspector(model: model)
                    .inspectorColumnWidth(min: 240, ideal: 260, max: 340)
            }
        }
        .navigationTitle(model.area.map { "\($0.name)\(model.dirty ? " – bearbeitet" : "")" } ?? "Nordisch Map Editor")
        .toolbar {
            ToolbarItemGroup {
                Toggle(isOn: $model.showMap) { Label("Karte", systemImage: "map") }
                    .help("Minimap-Bild als Hintergrund")
                Toggle(isOn: $model.showBuildings) { Label("Gebäude", systemImage: "building.2") }
                    .help("LoD2-Dachumrisse")
                Toggle(isOn: $model.showGenerated) { Label("Erzeugte Objekte", systemImage: "tree") }
                    .help("Vom Importer gesetzte Bäume, Laternen, Bänke … zeigen (geänderte bleiben immer sichtbar)")
                Button { model.recenterToken += 1 } label: { Label("Ganzes Gebiet", systemImage: "arrow.up.left.and.down.right.magnifyingglass") }
                    .help("Ganzes Gebiet zeigen")
                Button { model.save() } label: { Label("Sichern", systemImage: "square.and.arrow.down") }
                    .disabled(!model.dirty)
                    .help("Nach Data/<id>.edits.json sichern (⌘S)")
                Button { model.importInUnity() } label: {
                    if model.unityBusy { ProgressView().controlSize(.small) } else { Label("In Unity übernehmen", systemImage: "arrow.triangle.2.circlepath") }
                }
                .disabled(model.unityBusy)
                .help("Sichern und Gebiet in Unity (Batchmode) neu importieren – Unity-Editor muss geschlossen sein")
                Button { model.openSceneInUnity() } label: { Label("In Unity öffnen", systemImage: "cube") }
                    .help("Szene im Unity-Editor öffnen")
            }
        }
    }
}

struct Sidebar: View {
    @ObservedObject var model: EditorModel

    var body: some View {
        List {
            Section("Gebiet") {
                Picker("Gebiet", selection: Binding(get: { model.area }, set: { if let a = $0 { model.open(a) } })) {
                    ForEach(model.areas) { Text($0.name).tag(Optional($0)) }
                }
                .labelsHidden()
                Picker("Bearbeiten", selection: $model.mode) {
                    Text("Objekte").tag(EditorModel.Mode.objects)
                    Text("Häuser").tag(EditorModel.Mode.houses)
                    Text("Gärten").tag(EditorModel.Mode.gardens)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
            }
            if model.mode == .gardens {
                GardenSidebar(model: model)
            } else if model.mode == .houses {
                HouseSidebar(model: model)
            } else {
                objectSections
            }
        }
        .listStyle(.sidebar)
    }

    @ViewBuilder
    private var objectSections: some View {
            Section("Objekt setzen") {
                ForEach(ObjectType.all) { t in
                    Button {
                        model.placing = model.placing == t ? nil : t
                    } label: {
                        HStack {
                            Image(systemName: t.symbol)
                                .foregroundStyle(Color(nsColor: t.color))
                                .frame(width: 22)
                            Text(t.name)
                            Spacer()
                        }
                        .contentShape(Rectangle())
                        .padding(.vertical, 2)
                    }
                    .buttonStyle(.plain)
                    .listRowBackground(model.placing == t ? Color.accentColor.opacity(0.25) : nil)
                }
                Text(model.placing == nil ? "Typ wählen, dann in die Karte klicken." : "Klicken = setzen · Esc/Rechtsklick = fertig")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            let edited = model.objects.filter { !$0.isGenerated || model.isChanged($0) }
            Section("Neu & geändert (\(edited.count))") {
                ForEach(edited) { o in
                    Button { model.focus(o) } label: {
                        HStack {
                            Image(systemName: o.deleted ? "xmark" : o.kind.symbol)
                                .foregroundStyle(o.deleted ? Color.red : Color.primary)
                                .frame(width: 22)
                            Text(o.kind.name)
                            if o.isGenerated {
                                Text(o.deleted ? "gelöscht" : "geändert").font(.caption).foregroundStyle(.secondary)
                            }
                            Spacer()
                            Text(String(format: "%.0f / %.0f", o.x, o.z)).font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                        }
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .listRowBackground(model.selection.contains(o.id) ? Color.accentColor.opacity(0.25) : nil)
                }
            }
    }
}

struct HouseSidebar: View {
    @ObservedObject var model: EditorModel

    var body: some View {
        Section("Adresse suchen") {
            TextField("z. B. Alte Weide 4", text: $model.houseQuery)
                .textFieldStyle(.roundedBorder)
            ForEach(model.houseMatches(model.houseQuery)) { h in row(h) }
            if model.houseQuery.count >= 2 && model.houseMatches(model.houseQuery).isEmpty {
                Text("Keine Adresse gefunden").font(.caption).foregroundStyle(.secondary)
            }
            if model.houses.isEmpty {
                Text("Keine Gebäudeliste – einmal „In Unity übernehmen“.").font(.caption).foregroundStyle(.secondary)
            } else {
                Text("Oder ein Haus in der Karte anklicken.").font(.caption).foregroundStyle(.secondary)
            }
        }
        Section("Angepasste Häuser (\(model.houseStyles.count))") {
            ForEach(Array(model.houseStyles.enumerated()), id: \.offset) { _, st in
                let h = model.houses.first { h in
                    h.id == st.id || st.address.map { a in h.addr.contains { HouseCatalog.normalize($0) == HouseCatalog.normalize(a) } } == true
                }
                Button { if let h { model.focusHouse(h) } } label: {
                    HStack {
                        Circle().fill(Color(nsColor: st.wall.flatMap(NSColor.init(hexString:)) ?? .tertiaryLabelColor)).frame(width: 12, height: 12)
                        Text(st.address ?? st.id ?? "?")
                        Spacer()
                        if h == nil { Image(systemName: "exclamationmark.triangle").foregroundStyle(.orange).help("Kein Haus mit dieser Adresse/ID im Gebiet") }
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
        }
    }

    private func row(_ h: House) -> some View {
        Button { model.focusHouse(h) } label: {
            HStack {
                Image(systemName: "house").frame(width: 22)
                Text(h.title)
                Spacer()
                if model.styleByHouse[h.id] != nil { Image(systemName: "paintbrush").foregroundStyle(.secondary) }
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .listRowBackground(model.selectedHouse == h.id ? Color.accentColor.opacity(0.25) : nil)
    }
}

struct HouseInspector: View {
    @ObservedObject var model: EditorModel
    let house: House

    var body: some View {
        let st = model.style(house) ?? HouseStyle()
        Section(house.title) {
            if house.addr.count > 1 { LabeledContent("Adressen", value: house.addr.joined(separator: ", ")) }
            LabeledContent("Quelle", value: house.sourceName)
            LabeledContent("Grundfläche", value: String(format: "%.0f m²", house.area))
            if house.addr.isEmpty {
                Text("Ohne Adresse – Änderung gilt nur für dieses Gebäudeteil.").font(.caption).foregroundStyle(.secondary)
            } else if model.houses.filter({ $0.addr.contains(house.addr[0]) }).count > 1 {
                Text("Gilt für alle Teile mit dieser Adresse.").font(.caption).foregroundStyle(.secondary)
            }
        }
        Section("Gebäude") {
            Toggle("Haus entfernen", isOn: Binding(get: { st.hidden == true }, set: { on in
                model.updateStyle(house) { $0.hidden = on ? true : nil }
            }))
            if st.hidden == true {
                Text("Wird beim Import nicht gebaut (z. B. abgerissen oder doppelt). Andere Einstellungen bleiben gespeichert.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        if st.hidden != true { details(st) }
        if model.style(house) != nil {
            Section {
                Button("Haus zurücksetzen", role: .destructive) { model.resetStyle(house) }
                Text("Änderungen erscheinen nach „In Unity übernehmen“ im Spiel.").font(.caption).foregroundStyle(.secondary)
            }
        }
    }

    @ViewBuilder
    private func details(_ st: HouseStyle) -> some View {
        Section("Geschosse") {
            let auto = max(house.floors, 1)
            Stepper(value: Binding(
                get: { st.floors ?? auto },
                set: { v in model.updateStyle(house) { $0.floors = v == auto && $0.floors == nil ? nil : v } }), in: 1...10) {
                HStack {
                    Text("Geschosse bis Traufe")
                    Spacer()
                    Text(st.floors.map { "\($0)" } ?? "\(auto) (automatisch)").foregroundStyle(st.floors == nil ? .secondary : .primary)
                }
            }
            if st.floors != nil {
                Button("Wie gemessen (\(auto))") { model.updateStyle(house) { $0.floors = nil } }
            }
            Text(house.src == "lod2" ? "Wände werden gestreckt, das Dach wandert mit." : "Je Geschoss 3 m, Dach obendrauf.")
                .font(.caption).foregroundStyle(.secondary)
        }
        Section("Wand") {
            Toggle("Fassadenfenster", isOn: Binding(get: { st.windows != false }, set: { on in
                model.updateStyle(house) { $0.windows = on ? nil : false }
            }))
            Toggle("Eigene Wandfarbe", isOn: Binding(get: { st.wall != nil }, set: { on in
                model.updateStyle(house) { $0.wall = on ? Palette.walls[0].1 : nil; $0.brick = on ? true : nil }
            }))
            if let wall = st.wall {
                ColorPicker("Farbe", selection: Binding(
                    get: { Color(nsColor: NSColor(hexString: wall) ?? .gray) },
                    set: { c in model.updateStyle(house) { $0.wall = NSColor(c).hexString } }), supportsOpacity: false)
                swatches(Palette.walls, selected: wall) { hex, name in
                    model.updateStyle(house) { $0.wall = hex; $0.brick = name.hasPrefix("Klinker") }
                }
                Toggle("Klinker (Mauerwerk)", isOn: Binding(get: { st.brick ?? false }, set: { b in model.updateStyle(house) { $0.brick = b } }))
            }
        }
        Section("Haustür") {
            Picker("Tür", selection: Binding(get: { st.door ?? "auto" }, set: { v in
                model.updateStyle(house) { $0.door = v == "auto" ? nil : v }
            })) {
                Text("Automatisch (Straßenseite)").tag("auto")
                Text("Keine Tür").tag("none")
                Text("Nordseite").tag("N"); Text("Ostseite").tag("E"); Text("Südseite").tag("S"); Text("Westseite").tag("W")
            }
            if st.door != "none" {
                Toggle("Eigene Türfarbe", isOn: Binding(get: { st.doorColor != nil }, set: { on in
                    model.updateStyle(house) { $0.doorColor = on ? Palette.doors[0].1 : nil }
                }))
                if let dc = st.doorColor {
                    ColorPicker("Farbe", selection: Binding(
                        get: { Color(nsColor: NSColor(hexString: dc) ?? .gray) },
                        set: { c in model.updateStyle(house) { $0.doorColor = NSColor(c).hexString } }), supportsOpacity: false)
                    swatches(Palette.doors, selected: dc) { hex, _ in model.updateStyle(house) { $0.doorColor = hex } }
                }
            }
        }
        Section("Dach") {
            if house.src == "osm" {
                Picker("Dachform", selection: Binding(get: { st.roofShape ?? "auto" }, set: { v in
                    model.updateStyle(house) { $0.roofShape = v == "auto" ? nil : v }
                })) {
                    Text("Automatisch").tag("auto"); Text("Satteldach").tag("gabled"); Text("Walmdach").tag("hipped"); Text("Flachdach").tag("flat")
                }
            } else {
                Text("Dachform aus der amtlichen 3D-Vermessung (LGLN).").font(.caption).foregroundStyle(.secondary)
            }
            Picker("Schornstein", selection: Binding(get: { st.chimney.map { $0 ? "yes" : "no" } ?? "auto" }, set: { v in
                model.updateStyle(house) { $0.chimney = v == "auto" ? nil : v == "yes" }
            })) {
                Text(house.src == "osm" ? "Automatisch (keiner)" : "Automatisch").tag("auto"); Text("Ja").tag("yes"); Text("Nein").tag("no")
            }
            Toggle("Eigene Dachfarbe", isOn: Binding(get: { st.roof != nil }, set: { on in
                model.updateStyle(house) { $0.roof = on ? Palette.roofs[0].1 : nil }
            }))
            if let roof = st.roof {
                ColorPicker("Farbe", selection: Binding(
                    get: { Color(nsColor: NSColor(hexString: roof) ?? .gray) },
                    set: { c in model.updateStyle(house) { $0.roof = NSColor(c).hexString } }), supportsOpacity: false)
                swatches(Palette.roofs, selected: roof) { hex, _ in model.updateStyle(house) { $0.roof = hex } }
            }
        }
        Section("Giebelfenster") {
            Stepper("Anzahl je Giebel: \(st.gableWindows ?? 0)", value: Binding(
                get: { st.gableWindows ?? 0 },
                set: { v in model.updateStyle(house) { $0.gableWindows = v > 0 ? v : nil; if v > 0 && $0.gableWindowFloor == nil { $0.gableWindowFloor = 1 } } }), in: 0...4)
            if (st.gableWindows ?? 0) > 0 {
                Picker("Geschoss", selection: Binding(
                    get: { st.gableWindowFloor ?? 1 },
                    set: { v in model.updateStyle(house) { $0.gableWindowFloor = v } })) {
                    Text("EG").tag(0); Text("1. OG").tag(1); Text("2. OG").tag(2); Text("3. OG").tag(3)
                }
            }
            Text("Mittig auf beiden Stirnseiten; Fenster, die nicht in die Wand passen, entfallen.").font(.caption).foregroundStyle(.secondary)
        }
    }

    private func swatches(_ items: [(String, String)], selected: String, pick: @escaping (String, String) -> Void) -> some View {
        HStack(spacing: 6) {
            ForEach(items, id: \.1) { name, hex in
                Button { pick(hex, name) } label: {
                    RoundedRectangle(cornerRadius: 4)
                        .fill(Color(nsColor: NSColor(hexString: hex) ?? .gray))
                        .frame(width: 22, height: 22)
                        .overlay(RoundedRectangle(cornerRadius: 4).stroke(selected.uppercased() == hex ? Color.accentColor : Color.secondary.opacity(0.4), lineWidth: selected.uppercased() == hex ? 2.5 : 1))
                }
                .buttonStyle(.plain)
                .help(name)
            }
        }
    }
}

struct StatusBar: View {
    @ObservedObject var model: EditorModel

    var body: some View {
        HStack {
            Text(model.status).lineLimit(1).truncationMode(.middle)
            Spacer()
            if let c = model.cursor {
                Text(String(format: "x %.1f m  z %.1f m", c.x, c.y)).monospacedDigit()
            }
        }
        .font(.caption)
        .foregroundStyle(.secondary)
        .padding(.horizontal, 10)
        .padding(.vertical, 5)
        .background(.bar)
    }
}

struct Inspector: View {
    @ObservedObject var model: EditorModel

    var body: some View {
        Form {
            let sel = model.selectedObjects
            if model.mode == .gardens {
                GardenInspector(model: model)
            } else if model.mode == .houses {
                if let h = model.currentHouse {
                    HouseInspector(model: model, house: h)
                } else {
                    Section("Kein Haus ausgewählt") {
                        Text("Haus in der Karte anklicken oder links nach Adresse suchen.").foregroundStyle(.secondary)
                    }
                }
            } else if sel.count == 1, let o = sel.first {
                single(o)
            } else if sel.count > 1 {
                Section("\(sel.count) Objekte") {
                    HStack {
                        Button("↺ 15°") { model.rotateSelected(by: -15) }
                        Button("↻ 15°") { model.rotateSelected(by: 15) }
                    }
                    HStack {
                        Button("Kleiner") { model.scaleSelected(by: 1 / 1.1) }
                        Button("Größer") { model.scaleSelected(by: 1.1) }
                    }
                    Button("Duplizieren") { model.duplicateSelected() }
                    Button("Löschen", role: .destructive) { model.deleteSelected() }
                }
            } else {
                Section("Nichts ausgewählt") {
                    Text("Objekt anklicken zum Auswählen.\n⇧-Klick/⇧-Ziehen: Mehrfachauswahl.").foregroundStyle(.secondary)
                }
            }
            Section("Bedienung") {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Ziehen: verschieben (⇧ = 0,5-m-Raster)")
                    Text("Runder Griff: drehen (⇧ = 15°)")
                    Text("Eckiger Griff: Größe (⇧ = 0,1er-Schritte)")
                    Text("R / ⇧R: ±15°, E: 90°, + / −: Größe")
                    Text("Pfeiltasten: 0,5 m (⇧ 5 m), ⌫: löschen")
                    Text("⌘D duplizieren, ⌘Z widerrufen, F: fokussieren")
                    Text("Leere Fläche ziehen / 2 Finger: Karte schieben")
                    Text("Scrollrad / Pinch: Zoom")
                }
                .font(.caption)
                .foregroundStyle(.secondary)
            }
            if !model.unityLog.isEmpty {
                Section("Unity") {
                    Text(model.unityLog).font(.caption.monospaced()).textSelection(.enabled)
                }
            }
        }
        .formStyle(.grouped)
    }

    @ViewBuilder
    private func single(_ o: PlacedObject) -> some View {
        if o.deleted {
            Section(o.kind.name + " (gelöscht)") {
                Text("Automatisch erzeugtes Objekt – wird beim Import weggelassen.").foregroundStyle(.secondary)
                Button("Wiederherstellen") { model.undeleteSelected() }
            }
        } else {
            editor(o)
        }
    }

    @ViewBuilder
    private func editor(_ o: PlacedObject) -> some View {
        Section(o.kind.name) {
            if o.isGenerated {
                LabeledContent("Herkunft", value: model.isChanged(o) ? "erzeugt, geändert" : "automatisch erzeugt")
            } else {
                Picker("Typ", selection: Binding(get: { o.type }, set: { v in model.snapshot(); model.update(o.id) { $0.type = v } })) {
                    ForEach(ObjectType.all) { Label($0.name, systemImage: $0.symbol).tag($0.id) }
                }
            }
            NumberField(label: "X (Ost)", unit: "m", value: o.x) { v in model.snapshot(); model.update(o.id) { $0.x = v } }
            NumberField(label: "Z (Nord)", unit: "m", value: o.z) { v in model.snapshot(); model.update(o.id) { $0.z = v } }
        }
        Section("Drehung") {
            Slider(value: Binding(get: { o.yaw }, set: { v in model.update(o.id) { $0.yaw = v } }), in: 0...360, step: 1) {
                EmptyView()
            } onEditingChanged: { if $0 { model.snapshot() } }
            NumberField(label: "Winkel", unit: "°", value: o.yaw) { v in model.snapshot(); model.update(o.id) { $0.yaw = EditorModel.normYaw(v) } }
            HStack {
                ForEach([0.0, 90, 180, 270], id: \.self) { a in
                    Button("\(Int(a))°") { model.snapshot(); model.update(o.id) { $0.yaw = a } }
                }
            }
        }
        Section("Größe") {
            Slider(value: Binding(get: { log2(o.scale) }, set: { v in model.update(o.id) { $0.scale = EditorModel.clampScale(pow(2, v)) } }), in: -2...3) {
                EmptyView()
            } onEditingChanged: { if $0 { model.snapshot() } }
            NumberField(label: "Faktor", unit: "×", value: o.scale) { v in model.snapshot(); model.update(o.id) { $0.scale = EditorModel.clampScale(v) } }
            Button("Originalgröße (1×)") { model.snapshot(); model.update(o.id) { $0.scale = 1 } }
        }
        Section {
            if o.isGenerated {
                if model.isChanged(o) { Button("Original wiederherstellen") { model.restoreSelected() } }
            } else {
                Button("Andere Variante") { model.snapshot(); model.update(o.id) { $0.seed = Int.random(in: 0..<100_000) } }
                    .help("Zufallsvariante (Baumform, Farbtöne, Schirmfarbe …)")
            }
            Button("Duplizieren") { model.duplicateSelected() }
            Button("Löschen", role: .destructive) { model.deleteSelected() }
        }
    }
}

/// Text field that commits a Double on Return / focus loss.
struct NumberField: View {
    let label: String
    let unit: String
    let value: Double
    let commit: (Double) -> Void
    @State private var text = ""
    @FocusState private var focused: Bool

    var body: some View {
        HStack {
            Text(label)
            Spacer()
            TextField(label, text: $text)
                .labelsHidden()
                .multilineTextAlignment(.trailing)
                .frame(width: 80)
                .focused($focused)
                .onSubmit(apply)
                .onChange(of: focused) { _, f in if !f { apply() } }
            Text(unit).foregroundStyle(.secondary).frame(width: 14, alignment: .leading)
        }
        .onAppear { text = format(value) }
        .onChange(of: value) { _, v in if !focused { text = format(v) } }
    }

    private func format(_ v: Double) -> String { String(format: "%.2f", v) }

    private func apply() {
        guard let v = Double(text.replacingOccurrences(of: ",", with: ".")), abs(v - value) > 0.0001 else {
            text = format(value)
            return
        }
        commit(v)
    }
}


struct GardenSidebar: View {
    @ObservedObject var model: EditorModel

    var body: some View {
        Section("Zeichnen") {
            ForEach(GardenKind.allCases) { k in
                Button {
                    model.startDrawing(model.drawingKind == k ? nil : k)
                } label: {
                    HStack {
                        RoundedRectangle(cornerRadius: 2).fill(Color(nsColor: k.color)).frame(width: 18, height: k.isHedge ? 8 : 4)
                            .frame(width: 22)
                        Text(k.name)
                        Spacer()
                    }
                    .contentShape(Rectangle())
                    .padding(.vertical, 2)
                }
                .buttonStyle(.plain)
                .listRowBackground(model.drawingKind == k ? Color.accentColor.opacity(0.25) : nil)
            }
            Text(model.drawingKind == nil
                 ? "Art wählen, dann Punkte in die Karte klicken."
                 : "1. Klick = Anfang, 2. Klick = Ende (fertig). Rastet an vorhandenen Hecken/Zäunen ein (Kreis) – zum Verlängern am Ende ansetzen. ⇧ = 15°-Raster · Esc = abbrechen")
                .font(.caption).foregroundStyle(.secondary)
        }
        Section("Gezeichnet (\(model.drawnLines.count))") {
            ForEach(model.drawnLines) { d in
                Button { model.focusGarden(.drawn(d.id)) } label: {
                    HStack {
                        RoundedRectangle(cornerRadius: 2).fill(Color(nsColor: d.gardenKind.color)).frame(width: 18, height: 6).frame(width: 22)
                        Text(d.gardenKind.name)
                        Spacer()
                        Text(String(format: "%.0f m", d.length)).font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .listRowBackground(model.gardenSelection == .drawn(d.id) ? Color.accentColor.opacity(0.25) : nil)
            }
        }
        Section("Entfernt (\(model.gardenRemoved.count))") {
            ForEach(model.gardenRemoved.sorted(), id: \.self) { id in
                let g = model.gardenLines.first { $0.id == id }
                Button { if g != nil { model.focusGarden(.generated(id)) } } label: {
                    HStack {
                        Image(systemName: "xmark").foregroundStyle(.red).frame(width: 22)
                        Text(g?.title ?? id).lineLimit(1)
                        Spacer()
                        if let g { Text(String(format: "%.0f m", g.length)).font(.caption.monospacedDigit()).foregroundStyle(.secondary) }
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .listRowBackground(model.gardenSelection == .generated(id) ? Color.accentColor.opacity(0.25) : nil)
            }
            if model.gardenLines.isEmpty {
                Text("Keine Liste – einmal „In Unity übernehmen“.").font(.caption).foregroundStyle(.secondary)
            }
        }
    }
}

struct GardenInspector: View {
    @ObservedObject var model: EditorModel

    var body: some View {
        if let g = model.selectedGardenLine {
            let removed = model.gardenRemoved.contains(g.id)
            Section(g.title) {
                LabeledContent("Länge", value: String(format: "%.1f m", g.length))
                LabeledContent("Höhe", value: String(format: "%.1f m", g.h))
                if removed {
                    Text("Wird beim Import nicht gebaut.").font(.caption).foregroundStyle(.secondary)
                    Button("Wiederherstellen") { model.toggleRemoved(g.id) }
                } else {
                    Button("Entfernen", role: .destructive) { model.toggleRemoved(g.id) }
                    Text("Tipp: ⌫ entfernt die ausgewählte Linie.").font(.caption).foregroundStyle(.secondary)
                }
            }
        } else if let i = model.selectedDrawnIndex {
            let d = model.drawnLines[i]
            Section("Gezeichnet") {
                Picker("Art", selection: Binding(get: { d.gardenKind }, set: { k in
                    model.snapshot()
                    model.updateDrawn(d.id) { $0.kind = k.rawValue; $0.height = k.isHedge ? ($0.height ?? k.defaultHeight) : nil }
                })) {
                    ForEach(GardenKind.allCases) { Text($0.name).tag($0) }
                }
                if d.gardenKind.isHedge {
                    Slider(value: Binding(get: { d.height ?? d.gardenKind.defaultHeight },
                                          set: { v in model.updateDrawn(d.id) { $0.height = (v * 10).rounded() / 10 } }), in: 0.5...4) {
                        Text("Höhe")
                    } onEditingChanged: { if $0 { model.snapshot() } }
                    LabeledContent("Höhe", value: String(format: "%.1f m", d.height ?? d.gardenKind.defaultHeight))
                }
                LabeledContent("Länge", value: String(format: "%.1f m (%d Punkte)", d.length, d.points.count))
                Button("Löschen", role: .destructive) { model.deleteDrawn(d.id) }
            }
        } else {
            Section("Nichts ausgewählt") {
                Text("Hecke, Zaun oder Mauer in der Karte anklicken – oder links eine Art wählen und zeichnen.")
                    .foregroundStyle(.secondary)
            }
        }
        Section("Legende") {
            ForEach(GardenKind.allCases) { k in
                HStack {
                    RoundedRectangle(cornerRadius: 2).fill(Color(nsColor: k.color)).frame(width: 18, height: 6)
                    Text(k.name).font(.caption)
                }
            }
            HStack {
                RoundedRectangle(cornerRadius: 2).stroke(Color.red, style: StrokeStyle(lineWidth: 1.5, dash: [4, 3])).frame(width: 18, height: 6)
                Text("entfernt").font(.caption)
            }
            Text("Gemessene Hecken (Luftbild-Höhenmodell) erscheinen dunkelgrün wie „Hecke hoch“.").font(.caption).foregroundStyle(.secondary)
        }
    }
}
