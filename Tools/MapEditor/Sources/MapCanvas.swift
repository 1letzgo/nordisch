import AppKit
import SwiftUI

/// Top-down map: minimap render + LoD2 roof outlines as background, placed objects on top.
/// World coordinates: x = east, y = north (metres). View is flipped (y down).
final class MapCanvas: NSView {
    weak var model: EditorModel?
    var camera = CGPoint.zero
    var zoom: CGFloat = 1 // view points per metre
    var lastRecenter = -1
    var lastFocus = -1

    private enum Drag {
        case none
        case pan(lastView: CGPoint)
        case move(start: CGPoint, originals: [UUID: CGPoint], snapped: Bool)
        case rotate(id: UUID)
        case scale(id: UUID, startScale: Double, startDist: Double)
        case marquee(start: CGPoint, current: CGPoint, base: Set<UUID>)
    }
    private var drag = Drag.none

    private struct CacheKey: Equatable {
        var size: CGSize, camera: CGPoint, zoom: CGFloat, scale: CGFloat
        var map: Bool, buildings: Bool, bg: ObjectIdentifier?, path: ObjectIdentifier?
    }
    private var cacheKey: CacheKey?
    private var cache: CGImage?

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    /// Until the user pans/zooms, keep the whole area fitted (window size settles after launch).
    private var userNavigated = false

    override func setFrameSize(_ newSize: NSSize) {
        super.setFrameSize(newSize)
        if !userNavigated { recenter(); userNavigated = false }
    }

    // MARK: - Coordinates

    var transform: CGAffineTransform {
        CGAffineTransform(a: zoom, b: 0, c: 0, d: -zoom,
                          tx: bounds.midX - camera.x * zoom, ty: bounds.midY + camera.y * zoom)
    }

    func toView(_ p: CGPoint) -> CGPoint { p.applying(transform) }
    func toWorld(_ v: CGPoint) -> CGPoint { v.applying(transform.inverted()) }

    func recenter() {
        guard let model, bounds.width > 10 else { return }
        let r = model.areaRect
        camera = CGPoint(x: r.midX, y: r.midY)
        zoom = min(bounds.width / (r.width + 40), bounds.height / (r.height + 40))
        needsDisplay = true
    }

    func focus(on p: CGPoint) {
        userNavigated = true
        camera = p
        zoom = max(zoom, 6)
        needsDisplay = true
    }

    private func zoom(by factor: CGFloat, around v: CGPoint) {
        userNavigated = true
        let before = toWorld(v)
        zoom = min(max(zoom * factor, 0.2), 80)
        let after = toWorld(v)
        camera.x += before.x - after.x
        camera.y += before.y - after.y
        needsDisplay = true
    }

    // MARK: - Drawing

    override func draw(_ dirtyRect: NSRect) {
        guard let ctx = NSGraphicsContext.current?.cgContext, let model else { return }
        drawBackground(ctx, model)

        ctx.saveGState()
        ctx.concatenate(transform)
        let visible = toWorld(.zero), visible2 = toWorld(CGPoint(x: bounds.maxX, y: bounds.maxY))
        let view = CGRect(x: visible.x, y: visible2.y, width: visible2.x - visible.x, height: visible.y - visible2.y).insetBy(dx: -10, dy: -10)
        drawHouses(ctx, model, view)
        if model.mode == .gardens { drawGardens(ctx, model, view) }
        for o in model.objects where view.contains(CGPoint(x: o.x, y: o.z)) && model.isVisible(o) {
            let selected = model.selection.contains(o.id)
            if o.deleted {
                drawDeleted(ctx, o, selected: selected)
            } else {
                drawObject(ctx, o, selected: selected, ghost: false, faint: o.isGenerated && !model.isChanged(o))
            }
        }
        if let type = model.placing, let c = model.cursor {
            var ghost = PlacedObject(type: type.id, x: c.x, z: c.y)
            if let prev = model.objects.last(where: { $0.type == type.id && !$0.isGenerated }) { ghost.yaw = prev.yaw; ghost.scale = prev.scale }
            drawObject(ctx, ghost, selected: false, ghost: true)
        }
        ctx.restoreGState()

        if model.selection.count == 1, let o = model.selectedObjects.first, !o.deleted {
            drawHandles(ctx, o)
        }
        if case .marquee(let a, let b, _) = drag {
            let r = CGRect(x: min(a.x, b.x), y: min(a.y, b.y), width: abs(a.x - b.x), height: abs(a.y - b.y))
            ctx.setFillColor(NSColor.controlAccentColor.withAlphaComponent(0.12).cgColor)
            ctx.fill(r)
            ctx.setStrokeColor(NSColor.controlAccentColor.cgColor)
            ctx.stroke(r, width: 1)
        }
    }

    private func drawBackground(_ ctx: CGContext, _ model: EditorModel) {
        let scale = window?.backingScaleFactor ?? 2
        let key = CacheKey(size: bounds.size, camera: camera, zoom: zoom, scale: scale,
                           map: model.showMap, buildings: model.showBuildings,
                           bg: model.background.map(ObjectIdentifier.init), path: model.buildings.map(ObjectIdentifier.init))
        if key != cacheKey || cache == nil {
            cacheKey = key
            cache = renderBackground(model, scale: scale)
        }
        guard let cache else { return }
        ctx.saveGState()
        // Bitmap is y-up; undo the view flip.
        ctx.translateBy(x: 0, y: bounds.height)
        ctx.scaleBy(x: 1, y: -1)
        ctx.draw(cache, in: bounds)
        ctx.restoreGState()
    }

    private func renderBackground(_ model: EditorModel, scale: CGFloat) -> CGImage? {
        let w = Int(bounds.width * scale), h = Int(bounds.height * scale)
        guard w > 0, h > 0,
              let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue) else { return nil }
        ctx.scaleBy(x: scale, y: scale)
        ctx.setFillColor(NSColor(hex: 0x1E2328).cgColor)
        ctx.fill(CGRect(origin: .zero, size: bounds.size))
        // Same mapping as `transform`, but for a y-up bitmap.
        ctx.translateBy(x: 0, y: bounds.height)
        ctx.scaleBy(x: 1, y: -1)
        ctx.concatenate(transform)

        let area = model.areaRect
        let mapRect = model.mapRect
        ctx.interpolationQuality = zoom > 2 ? .none : .high
        if model.showMap, let bg = model.background {
            ctx.draw(bg, in: mapRect)
        } else {
            ctx.setFillColor(NSColor(hex: 0x8DBF6A).cgColor)
            ctx.fill(area)
        }
        // Outside the playable area: dim.
        ctx.setFillColor(NSColor.black.withAlphaComponent(0.45).cgColor)
        ctx.addRect(mapRect.insetBy(dx: -4000, dy: -4000))
        ctx.addRect(area)
        ctx.fillPath(using: .evenOdd)

        // 100 m chunk grid when zoomed in.
        if zoom > 1.5 {
            ctx.setStrokeColor(NSColor.white.withAlphaComponent(0.12).cgColor)
            ctx.setLineWidth(1 / zoom)
            var x = ceil(area.minX / 100) * 100
            while x <= area.maxX {
                ctx.move(to: CGPoint(x: x, y: area.minY)); ctx.addLine(to: CGPoint(x: x, y: area.maxY))
                x += 100
            }
            var y = ceil(area.minY / 100) * 100
            while y <= area.maxY {
                ctx.move(to: CGPoint(x: area.minX, y: y)); ctx.addLine(to: CGPoint(x: area.maxX, y: y))
                y += 100
            }
            ctx.strokePath()
        }
        if model.showBuildings, let path = model.buildings, zoom > 0.8 {
            ctx.addPath(path)
            ctx.setStrokeColor(NSColor.black.withAlphaComponent(model.showMap ? 0.35 : 0.6).cgColor)
            ctx.setLineWidth(1 / zoom)
            ctx.strokePath()
        }
        ctx.setStrokeColor(NSColor(hex: 0xE3A82B).cgColor)
        ctx.setLineWidth(2 / zoom)
        ctx.stroke(area)
        return ctx.makeImage()
    }

    private func shapePath(_ t: ObjectType) -> CGPath {
        switch t.shape {
        case .circle(let r): return CGPath(ellipseIn: CGRect(x: -r, y: -r, width: 2 * r, height: 2 * r), transform: nil)
        case .rect(let w, let d): return CGPath(rect: CGRect(x: -w / 2, y: -d / 2, width: w, height: d), transform: nil)
        }
    }

    /// Hedges, fences and walls (gardens mode). Context in world space.
    private func drawGardens(_ ctx: CGContext, _ model: EditorModel, _ view: CGRect) {
        ctx.setLineCap(.round)
        let minW = 2.5 / zoom
        for g in model.gardenLines {
            let r = CGRect(x: min(g.a.x, g.b.x), y: min(g.a.y, g.b.y), width: abs(g.a.x - g.b.x), height: abs(g.a.y - g.b.y))
            guard view.intersects(r.insetBy(dx: -2, dy: -2)) else { continue }
            let removed = model.gardenRemoved.contains(g.id)
            let selected = model.gardenSelection == .generated(g.id)
            ctx.move(to: g.a); ctx.addLine(to: g.b)
            if removed {
                ctx.setStrokeColor(NSColor.systemRed.withAlphaComponent(selected ? 1 : 0.7).cgColor)
                ctx.setLineWidth((selected ? 3 : 1.5) / zoom)
                ctx.setLineDash(phase: 0, lengths: [4 / zoom, 3 / zoom])
            } else {
                ctx.setStrokeColor((selected ? NSColor.controlAccentColor : g.gardenKind.color).cgColor)
                ctx.setLineWidth(max(g.gardenKind.width, minW) + (selected ? 3 / zoom : 0))
                ctx.setLineDash(phase: 0, lengths: [])
            }
            ctx.strokePath()
        }
        ctx.setLineDash(phase: 0, lengths: [])
        for d in model.drawnLines {
            let pts = d.cgPoints
            guard pts.count >= 2 else { continue }
            let selected = model.gardenSelection == .drawn(d.id)
            ctx.addLines(between: pts)
            ctx.setStrokeColor((selected ? NSColor.controlAccentColor : d.gardenKind.color).cgColor)
            ctx.setLineWidth(max(d.gardenKind.width, minW) + 2 / zoom)
            ctx.strokePath()
            ctx.setFillColor(NSColor.white.cgColor)
            for p in pts { ctx.fillEllipse(in: CGRect(x: p.x - 3 / zoom, y: p.y - 3 / zoom, width: 6 / zoom, height: 6 / zoom)) }
        }
        // Snap ring under the cursor.
        if model.drawingKind != nil, let sp = model.snapPoint {
            let r = 6 / zoom
            ctx.setStrokeColor(NSColor.controlAccentColor.cgColor)
            ctx.setLineWidth(2 / zoom)
            ctx.strokeEllipse(in: CGRect(x: sp.x - r, y: sp.y - r, width: 2 * r, height: 2 * r))
        }
        // Line being drawn, rubber band to the cursor.
        if let kind = model.drawingKind, !model.drawingPoints.isEmpty {
            var pts = model.drawingPoints
            if let c = model.cursor { pts.append(c) }
            ctx.addLines(between: pts)
            ctx.setStrokeColor(kind.color.withAlphaComponent(0.85).cgColor)
            ctx.setLineWidth(max(kind.width, minW) + 2 / zoom)
            ctx.setLineDash(phase: 0, lengths: [6 / zoom, 4 / zoom])
            ctx.strokePath()
            ctx.setLineDash(phase: 0, lengths: [])
            ctx.setFillColor(NSColor.white.cgColor)
            for p in model.drawingPoints { ctx.fillEllipse(in: CGRect(x: p.x - 3.5 / zoom, y: p.y - 3.5 / zoom, width: 7 / zoom, height: 7 / zoom)) }
        }
    }

    /// Styled houses filled with their wall colour; the selected house outlined. Context in world space.
    private func drawHouses(_ ctx: CGContext, _ model: EditorModel, _ view: CGRect) {
        for h in model.houses where model.styleByHouse[h.id] != nil && view.intersects(h.bounds) {
            let st = model.styleByHouse[h.id]!
            if st.hidden == true {
                // Removed: dark fill, red outline and cross.
                ctx.addPath(h.path)
                ctx.setFillColor(NSColor.black.withAlphaComponent(0.35).cgColor)
                ctx.fillPath()
                ctx.addPath(h.path)
                ctx.move(to: CGPoint(x: h.bounds.minX, y: h.bounds.minY)); ctx.addLine(to: CGPoint(x: h.bounds.maxX, y: h.bounds.maxY))
                ctx.move(to: CGPoint(x: h.bounds.minX, y: h.bounds.maxY)); ctx.addLine(to: CGPoint(x: h.bounds.maxX, y: h.bounds.minY))
                ctx.setStrokeColor(NSColor.systemRed.cgColor)
                ctx.setLineWidth(2 / zoom)
                ctx.strokePath()
                continue
            }
            let fill = st.wall.flatMap(NSColor.init(hexString:)) ?? NSColor.controlAccentColor.withAlphaComponent(0.35)
            ctx.addPath(h.path)
            ctx.setFillColor(fill.withAlphaComponent(0.9).cgColor)
            ctx.fillPath()
            ctx.addPath(h.path)
            ctx.setStrokeColor(NSColor.white.withAlphaComponent(0.9).cgColor)
            ctx.setLineWidth(1.5 / zoom)
            ctx.strokePath()
        }
        if let h = model.currentHouse {
            // Every part of the same building (shared address) gets a light outline, the clicked part a strong one.
            if let a = h.addr.first {
                for o in model.houses where o.id != h.id && o.addr.contains(a) && view.intersects(o.bounds) {
                    ctx.addPath(o.path)
                    ctx.setStrokeColor(NSColor.controlAccentColor.withAlphaComponent(0.6).cgColor)
                    ctx.setLineWidth(2 / zoom)
                    ctx.strokePath()
                }
            }
            ctx.addPath(h.path)
            ctx.setFillColor(NSColor.controlAccentColor.withAlphaComponent(0.18).cgColor)
            ctx.fillPath()
            ctx.addPath(h.path)
            ctx.setStrokeColor(NSColor.controlAccentColor.cgColor)
            ctx.setLineWidth(3 / zoom)
            ctx.strokePath()
        }
    }

    /// Deleted generated object: red cross (select it to restore).
    private func drawDeleted(_ ctx: CGContext, _ o: PlacedObject, selected: Bool) {
        let r = max(o.kind.radius * o.effScale, 4 / zoom)
        ctx.saveGState()
        ctx.translateBy(x: o.x, y: o.z)
        ctx.setStrokeColor(NSColor.systemRed.withAlphaComponent(selected ? 1 : 0.75).cgColor)
        ctx.setLineWidth((selected ? 3 : 2) / zoom)
        ctx.move(to: CGPoint(x: -r, y: -r)); ctx.addLine(to: CGPoint(x: r, y: r))
        ctx.move(to: CGPoint(x: -r, y: r)); ctx.addLine(to: CGPoint(x: r, y: -r))
        ctx.strokePath()
        ctx.restoreGState()
    }

    /// Context is in world space. `faint` = untouched generated object.
    private func drawObject(_ ctx: CGContext, _ o: PlacedObject, selected: Bool, ghost: Bool, faint: Bool = false) {
        let t = o.kind
        let px = t.radius * o.effScale * zoom
        ctx.saveGState()
        ctx.translateBy(x: o.x, y: o.z)
        if px < 5 {
            // Too small to see: fixed-size marker.
            let r = (faint && !selected ? 3 : 5) / zoom
            ctx.setFillColor(t.color.withAlphaComponent(ghost ? 0.5 : faint ? 0.7 : 1).cgColor)
            ctx.fillEllipse(in: CGRect(x: -r, y: -r, width: 2 * r, height: 2 * r))
            if !faint || selected {
                ctx.setStrokeColor((selected ? NSColor.controlAccentColor : NSColor.white).cgColor)
                ctx.setLineWidth((selected ? 2.5 : 1) / zoom)
                ctx.strokeEllipse(in: CGRect(x: -r, y: -r, width: 2 * r, height: 2 * r))
            }
            ctx.restoreGState()
            return
        }
        ctx.rotate(by: -o.yaw * .pi / 180)
        ctx.scaleBy(x: o.effScale, y: o.effScale)
        let lw = 1 / (zoom * o.effScale)
        ctx.addPath(shapePath(t))
        ctx.setFillColor(t.color.withAlphaComponent(ghost ? 0.45 : faint ? 0.6 : 0.9).cgColor)
        ctx.fillPath()
        // Facing marker on the local +Z (front) side.
        let front: Double
        switch t.shape {
        case .circle(let r): front = r
        case .rect(_, let d): front = d / 2
        }
        let m = min(t.radius * 0.35, 0.6)
        ctx.move(to: CGPoint(x: -m, y: front - m * 0.2))
        ctx.addLine(to: CGPoint(x: 0, y: front + m * 0.8))
        ctx.addLine(to: CGPoint(x: m, y: front - m * 0.2))
        ctx.closePath()
        ctx.setFillColor(NSColor.white.withAlphaComponent(ghost ? 0.5 : 0.9).cgColor)
        ctx.fillPath()
        ctx.addPath(shapePath(t))
        ctx.setStrokeColor((selected ? NSColor.controlAccentColor : NSColor.white.withAlphaComponent(faint ? 0.35 : 0.85)).cgColor)
        ctx.setLineWidth((selected ? 3 : 1) * lw)
        if ghost { ctx.setLineDash(phase: 0, lengths: [4 * lw, 3 * lw]) }
        ctx.strokePath()
        ctx.restoreGState()
    }

    // MARK: - Handles (view space)

    private func handlePoints(_ o: PlacedObject) -> (center: CGPoint, rotate: CGPoint, scale: CGPoint) {
        let c = toView(CGPoint(x: o.x, y: o.z))
        let rpx = max(o.kind.radius * o.effScale * Double(zoom), 8)
        let a = o.yaw * .pi / 180
        let fwd = CGPoint(x: sin(a), y: -cos(a))   // local +Z in view space
        let right = CGPoint(x: cos(a), y: sin(a))  // local +X in view space
        let rot = CGPoint(x: c.x + fwd.x * (rpx + 26), y: c.y + fwd.y * (rpx + 26))
        let d = (rpx + 12) / 2.squareRoot()
        let sc = CGPoint(x: c.x + (right.x - fwd.x) * d, y: c.y + (right.y - fwd.y) * d)
        return (c, rot, sc)
    }

    private func drawHandles(_ ctx: CGContext, _ o: PlacedObject) {
        let h = handlePoints(o)
        let accent = NSColor.controlAccentColor.cgColor
        ctx.setStrokeColor(accent)
        ctx.setLineWidth(1.5)
        ctx.move(to: h.center); ctx.addLine(to: h.rotate)
        ctx.strokePath()
        ctx.setFillColor(NSColor.white.cgColor)
        ctx.fillEllipse(in: CGRect(x: h.rotate.x - 7, y: h.rotate.y - 7, width: 14, height: 14))
        ctx.strokeEllipse(in: CGRect(x: h.rotate.x - 7, y: h.rotate.y - 7, width: 14, height: 14))
        // Circular arrow hint inside the rotate knob.
        ctx.addArc(center: h.rotate, radius: 3.5, startAngle: 0.4, endAngle: 5.6, clockwise: false)
        ctx.strokePath()
        let s = CGRect(x: h.scale.x - 6, y: h.scale.y - 6, width: 12, height: 12)
        ctx.fill(s)
        ctx.stroke(s)
    }

    private func dist(_ a: CGPoint, _ b: CGPoint) -> CGFloat { hypot(a.x - b.x, a.y - b.y) }

    /// Drawing cursor: snaps to existing hedges/fences/walls (end points first); otherwise ⇧ = 15° steps.
    private func drawTarget(_ w: CGPoint, shift: Bool, _ model: EditorModel) -> CGPoint {
        if let s = model.gardenSnap(w, tolerance: Double(10 / zoom)) { return s }
        return shift ? snapped(w, model) : w
    }

    /// ⇧ while drawing: the new segment snaps to multiples of 15° from the previous point.
    private func snapped(_ w: CGPoint, _ model: EditorModel) -> CGPoint {
        guard let last = model.drawingPoints.last else { return w }
        let dx = w.x - last.x, dy = w.y - last.y, len = hypot(dx, dy)
        let ang = (atan2(dy, dx) / (.pi / 12)).rounded() * (.pi / 12)
        return CGPoint(x: last.x + cos(ang) * len, y: last.y + sin(ang) * len)
    }

    // MARK: - Mouse

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseMoved, .mouseEnteredAndExited, .activeInKeyWindow, .inVisibleRect, .cursorUpdate],
                                       owner: self, userInfo: nil))
    }

    override func cursorUpdate(with event: NSEvent) {
        (model?.placing != nil || model?.drawingKind != nil ? NSCursor.crosshair : NSCursor.arrow).set()
    }

    override func mouseMoved(with event: NSEvent) {
        let v = convert(event.locationInWindow, from: nil)
        model?.cursor = toWorld(v)
        if let model, model.mode == .gardens, model.drawingKind != nil, let c = model.cursor {
            let snap = model.gardenSnap(c, tolerance: Double(10 / zoom))
            model.snapPoint = snap
            model.cursor = snap ?? (event.modifierFlags.contains(.shift) ? snapped(c, model) : c)
        }
        (model?.placing != nil || model?.drawingKind != nil ? NSCursor.crosshair : NSCursor.arrow).set()
        if model?.placing != nil || model?.drawingKind != nil { needsDisplay = true }
    }

    override func mouseExited(with event: NSEvent) {
        model?.cursor = nil
        needsDisplay = true
    }

    private func hitObject(_ v: CGPoint) -> PlacedObject? {
        guard let model else { return nil }
        let w = toWorld(v)
        let slack = 6 / Double(zoom)
        // Prefer new/edited objects over untouched generated ones when they overlap.
        func hit(_ o: PlacedObject) -> Bool {
            model.isVisible(o) && (o.contains(w, slack: slack) || dist(toView(CGPoint(x: o.x, y: o.z)), v) < 7)
        }
        return model.objects.last { hit($0) && (!$0.isGenerated || model.isChanged($0)) } ?? model.objects.last(where: hit)
    }

    override func mouseDown(with event: NSEvent) {
        window?.makeFirstResponder(self)
        guard let model else { return }
        let v = convert(event.locationInWindow, from: nil)
        let w = toWorld(v)
        let shift = event.modifierFlags.contains(.shift)

        if model.mode == .gardens {
            if model.drawingKind != nil {
                model.addDrawingPoint(drawTarget(w, shift: shift, model))
                model.snapPoint = nil
            } else if let hit = model.gardenHit(w, tolerance: Double(8 / zoom)) {
                model.gardenSelection = hit
            } else {
                model.gardenSelection = nil
                drag = .pan(lastView: v)
            }
            needsDisplay = true
            return
        }
        if model.mode == .houses {
            if let h = model.house(at: w) {
                model.selectedHouse = h.id
                if event.clickCount == 2 { focus(on: CGPoint(x: h.bounds.midX, y: h.bounds.midY)) }
            } else {
                model.selectedHouse = nil
                drag = .pan(lastView: v)
            }
            needsDisplay = true
            return
        }
        if let type = model.placing {
            model.place(type, at: w)
            if let o = model.selectedObjects.first {
                drag = .move(start: w, originals: [o.id: CGPoint(x: o.x, y: o.z)], snapped: true)
            }
            needsDisplay = true
            return
        }
        if model.selection.count == 1, let o = model.selectedObjects.first, !o.deleted {
            let h = handlePoints(o)
            if dist(v, h.rotate) < 10 { model.snapshot(); drag = .rotate(id: o.id); return }
            if dist(v, h.scale) < 10 {
                model.snapshot()
                drag = .scale(id: o.id, startScale: o.scale, startDist: max(dist(v, h.center), 1))
                return
            }
        }
        if let hit = hitObject(v) {
            if shift {
                if model.selection.contains(hit.id) { model.selection.remove(hit.id) } else { model.selection.insert(hit.id) }
            } else if !model.selection.contains(hit.id) {
                model.selection = [hit.id]
            }
            if event.clickCount == 2 { focus(on: CGPoint(x: hit.x, y: hit.z)) }
            var originals: [UUID: CGPoint] = [:]
            for o in model.selectedObjects { originals[o.id] = CGPoint(x: o.x, y: o.z) }
            drag = .move(start: w, originals: originals, snapped: false)
        } else if shift {
            drag = .marquee(start: v, current: v, base: model.selection)
        } else {
            model.selection = []
            drag = .pan(lastView: v)
        }
        needsDisplay = true
    }

    override func mouseDragged(with event: NSEvent) {
        guard let model else { return }
        let v = convert(event.locationInWindow, from: nil)
        let w = toWorld(v)
        model.cursor = w
        let shift = event.modifierFlags.contains(.shift)
        switch drag {
        case .none:
            break
        case .pan(let last):
            userNavigated = true
            camera.x -= (v.x - last.x) / zoom
            camera.y += (v.y - last.y) / zoom
            drag = .pan(lastView: v)
        case .move(let start, let originals, let snapped):
            if !snapped {
                model.snapshot()
                drag = .move(start: start, originals: originals, snapped: true)
            }
            var dx = w.x - start.x, dz = w.y - start.y
            if shift { dx = (dx * 2).rounded() / 2; dz = (dz * 2).rounded() / 2 }
            for (id, p) in originals { model.update(id) { $0.x = p.x + dx; $0.z = p.y + dz } }
        case .rotate(let id):
            guard let o = model.objects.first(where: { $0.id == id }) else { break }
            var yaw = atan2(w.x - o.x, w.y - o.z) * 180 / .pi
            if shift { yaw = (yaw / 15).rounded() * 15 }
            model.update(id) { $0.yaw = EditorModel.normYaw(yaw) }
        case .scale(let id, let s0, let d0):
            guard let o = model.objects.first(where: { $0.id == id }) else { break }
            var s = s0 * Double(dist(v, toView(CGPoint(x: o.x, y: o.z))) / d0)
            if shift { s = (s * 10).rounded() / 10 }
            model.update(id) { $0.scale = EditorModel.clampScale(s) }
        case .marquee(let start, _, let base):
            drag = .marquee(start: start, current: v, base: base)
            let r = CGRect(x: min(start.x, v.x), y: min(start.y, v.y), width: abs(start.x - v.x), height: abs(start.y - v.y))
            model.selection = base.union(model.objects.filter { !$0.deleted && model.isVisible($0) && r.contains(toView(CGPoint(x: $0.x, y: $0.z))) }.map(\.id))
        }
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        drag = .none
        needsDisplay = true
    }

    override func rightMouseDown(with event: NSEvent) {
        if model?.placing != nil { model?.placing = nil; needsDisplay = true; return }
        drag = .pan(lastView: convert(event.locationInWindow, from: nil))
    }
    override func rightMouseDragged(with event: NSEvent) { mouseDragged(with: event) }
    override func rightMouseUp(with event: NSEvent) { drag = .none }
    override func otherMouseDown(with event: NSEvent) { drag = .pan(lastView: convert(event.locationInWindow, from: nil)) }
    override func otherMouseDragged(with event: NSEvent) { mouseDragged(with: event) }
    override func otherMouseUp(with event: NSEvent) { drag = .none }

    override func scrollWheel(with event: NSEvent) {
        let v = convert(event.locationInWindow, from: nil)
        if event.hasPreciseScrollingDeltas && !event.modifierFlags.contains(.command) && !event.modifierFlags.contains(.option) {
            // Trackpad: two-finger scroll pans.
            userNavigated = true
            camera.x -= event.scrollingDeltaX / zoom
            camera.y += event.scrollingDeltaY / zoom
            needsDisplay = true
        } else {
            let dy = event.hasPreciseScrollingDeltas ? event.scrollingDeltaY * 0.01 : event.scrollingDeltaY * 0.1
            zoom(by: exp(dy), around: v)
        }
    }

    override func magnify(with event: NSEvent) {
        zoom(by: 1 + event.magnification, around: convert(event.locationInWindow, from: nil))
    }

    // MARK: - Keyboard

    private var keyMonitor: Any?

    /// Map shortcuts work window-wide unless a text field is being edited (SwiftUI's inspector likes to grab focus).
    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        guard keyMonitor == nil, window != nil else { return }
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            guard let self, let window = self.window, event.window === window,
                  event.modifierFlags.intersection([.command, .control, .option]).isEmpty else { return event }
            if let text = window.firstResponder as? NSTextView, text.isEditable {
                // Esc / Return leave the field so the next key goes to the map again.
                if event.keyCode == 53 { window.makeFirstResponder(self); return nil }
                return event
            }
            if window.firstResponder !== self { window.makeFirstResponder(self) }
            self.keyDown(with: event)
            return nil
        }
    }

    override func keyDown(with event: NSEvent) {
        guard let model else { return super.keyDown(with: event) }
        let shift = event.modifierFlags.contains(.shift)
        let step = shift ? 5.0 : 0.5
        switch event.keyCode {
        case 51, 117:                                             // ⌫ / ⌦
            if model.mode == .gardens {
                if model.drawingKind != nil, !model.drawingPoints.isEmpty { model.drawingPoints.removeLast() }
                else if let i = model.selectedDrawnIndex { model.deleteDrawn(model.drawnLines[i].id) }
                else if let g = model.selectedGardenLine, !model.gardenRemoved.contains(g.id) { model.toggleRemoved(g.id) }
            } else { model.deleteSelected() }
        case 53:                                                  // Esc
            if model.drawingKind != nil {
                if model.drawingPoints.isEmpty { model.startDrawing(nil) } else { model.drawingPoints = [] }
            } else if model.placing != nil { model.placing = nil } else { model.selection = []; model.selectedHouse = nil; model.gardenSelection = nil }
        case 36, 76:                                              // Return / Enter: finish the line
            model.finishDrawing()
        case 123: model.nudgeSelected(dx: -step, dz: 0)
        case 124: model.nudgeSelected(dx: step, dz: 0)
        case 125: model.nudgeSelected(dx: 0, dz: -step)
        case 126: model.nudgeSelected(dx: 0, dz: step)
        default:
            switch event.charactersIgnoringModifiers {
            case "r": model.rotateSelected(by: 15)
            case "R": model.rotateSelected(by: -15)
            case "e": model.rotateSelected(by: 90)
            case "+", "=": model.scaleSelected(by: 1.1)
            case "-": model.scaleSelected(by: 1 / 1.1)
            case "f":
                if let o = model.selectedObjects.first { focus(on: CGPoint(x: o.x, y: o.z)) } else { recenter() }
            default: super.keyDown(with: event)
            }
        }
        needsDisplay = true
    }
}

struct MapCanvasView: NSViewRepresentable {
    @ObservedObject var model: EditorModel

    func makeNSView(context: Context) -> MapCanvas {
        let v = MapCanvas()
        v.model = model
        return v
    }

    func updateNSView(_ v: MapCanvas, context: Context) {
        v.model = model
        if v.lastRecenter != model.recenterToken, v.bounds.width > 10 {
            v.lastRecenter = model.recenterToken
            v.recenter()
        }
        if v.lastFocus != model.focusToken, let p = model.focusPoint {
            v.lastFocus = model.focusToken
            v.focus(on: p)
        }
        v.needsDisplay = true
    }
}
