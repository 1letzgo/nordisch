import AppKit
import Foundation

/// Developer check without clicking through the UI:
///   "Nordisch Map Editor" -snapshot <dir> [-snapshotAreas spiekeroog,westerstede] [-repoPath <repo>]
/// opens each area, renders the map canvas offscreen (1400×900 pt, 2×) at several zoom levels around the area
/// centre to <dir>/<area>_<n>.png, logs load/draw times and quits. Nothing is saved.
@MainActor
enum Snapshot {
    static func runIfRequested(_ model: EditorModel) {
        guard let dir = UserDefaults.standard.string(forKey: "snapshot") else { return }
        let out = URL(fileURLWithPath: dir)
        try? FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)
        let ids = UserDefaults.standard.string(forKey: "snapshotAreas")?.split(separator: ",").map(String.init)
            ?? model.areas.map(\.id)
        var log = ""
        for id in ids {
            guard let a = model.areas.first(where: { $0.id == id }) else { log += "\(id): not found\n"; continue }
            model.dirty = false // never write edits from a snapshot run
            let t0 = Date()
            model.open(a)
            log += String(format: "%@: open %.2f s, %d objects, %d houses, %d garden lines, %d region images\n", id,
                          Date().timeIntervalSince(t0), model.objects.count, model.houses.count, model.gardenLines.count,
                          model.mapImages?.available.count ?? 0)
            let canvas = MapCanvas(frame: NSRect(x: 0, y: 0, width: 1400, height: 900))
            canvas.model = model
            canvas.recenter()
            let fit = canvas.zoom
            // Area centre = town centre (areas.json lat/lon); 6 pt/m shows single trees and houses.
            let views: [(String, CGFloat, CGPoint)] = [
                ("fit", fit, CGPoint(x: model.areaRect.midX, y: model.areaRect.midY)),
                ("z0.15", 0.15, .zero), ("z0.5", 0.5, .zero), ("z1.5", 1.5, .zero), ("z6", 6, .zero),
            ]
            for (n, (name, zoom, cam)) in views.enumerated() {
                canvas.zoom = zoom
                canvas.camera = cam
                _ = canvas.snapshotImage() // starts region loads
                let w0 = Date()
                while model.mapImages?.isLoading == true, Date().timeIntervalSince(w0) < 20 {
                    RunLoop.main.run(until: Date().addingTimeInterval(0.02))
                }
                let wait = Date().timeIntervalSince(w0)
                let d0 = Date()
                let img = canvas.snapshotImage()
                let draw = Date().timeIntervalSince(d0)
                // Same frame again: only the objects layer (background cached), like a mouse-move redraw.
                let r0 = Date()
                _ = canvas.snapshotImage()
                let redraw = Date().timeIntervalSince(r0)
                let file = out.appendingPathComponent("\(id)_\(n)_\(name).png")
                if let img, let dest = CGImageDestinationCreateWithURL(file as CFURL, "public.png" as CFString, 1, nil) {
                    CGImageDestinationAddImage(dest, img, nil)
                    CGImageDestinationFinalize(dest)
                }
                log += String(format: "  %@ zoom %.3f: regions %.2f s, draw %.0f ms, redraw %.0f ms\n", name, zoom, wait, draw * 1000, redraw * 1000)
            }
            // Edit round trip on an untouched generated object: change → listed as edited → undo → untouched again.
            if let i = model.genBase.indices.first(where: { !model.editedFlag[$0] }) {
                let before = model.edited.count
                let e0 = Date()
                model.selection = [model.objects[i].id]
                model.nudgeSelected(dx: 2, dz: 0)
                let afterMove = model.edited.count
                model.undo()
                let ok = afterMove == before + 1 && model.edited.count == before && !model.editedFlag[i]
                log += String(format: "  edit/undo %@ (%d → %d → %d edited) in %.0f ms\n", ok ? "ok" : "FAILED",
                              before, afterMove, model.edited.count, Date().timeIntervalSince(e0) * 1000)
                model.selection = []
            }
            // Search over the whole catalog.
            let s0 = Date()
            let hits = model.houseMatches("str")
            log += String(format: "  search 'str': %d hits in %.0f ms\n", hits.count, Date().timeIntervalSince(s0) * 1000)
        }
        model.dirty = false // never write edits from a snapshot run
        try? log.write(to: out.appendingPathComponent("snapshot.log"), atomically: true, encoding: .utf8)
        print(log)
        exit(0)
    }
}

extension MapCanvas {
    /// The canvas as the window would show it (2× backing scale).
    func snapshotImage() -> CGImage? {
        let scale: CGFloat = 2
        let w = Int(bounds.width * scale), h = Int(bounds.height * scale)
        guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue) else { return nil }
        ctx.scaleBy(x: scale, y: scale)
        ctx.translateBy(x: 0, y: bounds.height)
        ctx.scaleBy(x: 1, y: -1)
        let prev = NSGraphicsContext.current
        NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: true)
        draw(bounds)
        NSGraphicsContext.current = prev
        return ctx.makeImage()
    }
}
