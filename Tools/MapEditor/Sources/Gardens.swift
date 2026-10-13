import AppKit
import Foundation

/// Hedge / fence / wall types – names match GardenBuilder.KindOf in the importer.
enum GardenKind: String, CaseIterable, Identifiable {
    case hedgeLow, hedgeTall, picket, privacy, mesh, wall

    var id: String { rawValue }

    var name: String {
        switch self {
        case .hedgeLow: return "Hecke niedrig"
        case .hedgeTall: return "Hecke hoch"
        case .picket: return "Jägerzaun"
        case .privacy: return "Sichtschutz (Holz)"
        case .mesh: return "Stabmattenzaun"
        case .wall: return "Klinkermauer"
        }
    }

    var symbol: String {
        switch self {
        case .hedgeLow, .hedgeTall: return "leaf"
        case .picket: return "line.diagonal"
        case .privacy: return "rectangle.split.3x1"
        case .mesh: return "squareshape.split.3x3"
        case .wall: return "square.stack.3d.up"
        }
    }

    var color: NSColor {
        switch self {
        case .hedgeLow: return NSColor(hex: 0x6E9A55)
        case .hedgeTall: return NSColor(hex: 0x3F6E36)
        case .picket: return NSColor(hex: 0xC9A27A)
        case .privacy: return NSColor(hex: 0x96704A)
        case .mesh: return NSColor(hex: 0x34503A)
        case .wall: return NSColor(hex: 0x964634)
        }
    }

    var isHedge: Bool { self == .hedgeLow || self == .hedgeTall }
    var defaultHeight: Double { self == .hedgeLow ? 1.0 : 1.8 }
    /// Drawn width on the map (metres).
    var width: Double { isHedge ? (self == .hedgeTall ? 0.8 : 0.6) : self == .wall ? 0.35 : 0.15 }

    /// Catalog kinds: "hedge" = measured hedge.
    static func of(_ s: String) -> GardenKind {
        s == "hedge" ? .hedgeTall : GardenKind(rawValue: s) ?? .hedgeTall
    }
}

/// A line the importer built (Assets/Generated/<area>/<area>_gardens.json).
struct GardenLine: Identifiable {
    let id: String
    let kind: String      // hedge (measured), hedgeLow, hedgeTall, picket, privacy, mesh, wall
    let a: CGPoint
    let b: CGPoint
    let h: Double

    var gardenKind: GardenKind { GardenKind.of(kind) }
    var length: Double { hypot(b.x - a.x, b.y - a.y) }
    var mid: CGPoint { CGPoint(x: (a.x + b.x) / 2, y: (a.y + b.y) / 2) }
    var title: String { kind == "hedge" ? "Hecke (gemessen)" : gardenKind.name + " (erzeugt)" }
}

/// A line drawn in the editor.
struct DrawnLine: Codable, Identifiable, Equatable {
    var id = UUID()
    var kind: String
    var points: [[Double]]
    var height: Double?

    enum CodingKeys: String, CodingKey { case kind, points, height }

    init(kind: String, points: [[Double]], height: Double? = nil) {
        self.kind = kind
        self.points = points
        self.height = height
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        kind = try c.decode(String.self, forKey: .kind)
        points = try c.decode([[Double]].self, forKey: .points)
        height = try c.decodeIfPresent(Double.self, forKey: .height)
    }

    var cgPoints: [CGPoint] { points.compactMap { $0.count == 2 ? CGPoint(x: $0[0], y: $0[1]) : nil } }
    var gardenKind: GardenKind { GardenKind.of(kind) }
    var length: Double {
        let p = cgPoints
        return zip(p, p.dropFirst()).reduce(0) { $0 + hypot($1.1.x - $1.0.x, $1.1.y - $1.0.y) }
    }
}

/// Data/<area>.garden.json (read by Assets/Editor/OsmImport/GardenBuilder.cs).
struct GardenEditsFile: Codable {
    var removed: [String]
    var lines: [DrawnLine]
}

enum GardenSelection: Equatable {
    case generated(String)
    case drawn(UUID)
}

enum GardenCatalog {
    struct Raw: Decodable {
        struct L: Decodable { let id: String; let kind: String; let a: [Double]; let b: [Double]; let h: Double }
        let lines: [L]
    }

    static func load(_ url: URL) -> [GardenLine] {
        guard let data = try? Data(contentsOf: url), let raw = try? JSONDecoder().decode(Raw.self, from: data) else { return [] }
        return raw.lines.map { GardenLine(id: $0.id, kind: $0.kind, a: CGPoint(x: $0.a[0], y: $0.a[1]), b: CGPoint(x: $0.b[0], y: $0.b[1]), h: $0.h) }
    }
}

/// Distance from p to segment a–b.
func segmentDistance(_ p: CGPoint, _ a: CGPoint, _ b: CGPoint) -> Double {
    let abx = b.x - a.x, aby = b.y - a.y
    let l2 = abx * abx + aby * aby
    let t = l2 < 1e-9 ? 0 : max(0, min(1, ((p.x - a.x) * abx + (p.y - a.y) * aby) / l2))
    return hypot(p.x - (a.x + abx * t), p.y - (a.y + aby * t))
}
