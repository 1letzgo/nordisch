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
            }
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
        .listStyle(.sidebar)
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
            if sel.count == 1, let o = sel.first {
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
