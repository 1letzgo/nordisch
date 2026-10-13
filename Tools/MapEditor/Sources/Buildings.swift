import AppKit
import Foundation

/// A house from Assets/Generated/<area>/<area>_buildings.json (written by the importer).
struct House: Identifiable {
    let id: String          // LoD2 gml id or "osm:<way id>"
    let src: String         // "lod2" | "osm"
    let addr: [String]
    let ring: [CGPoint]     // footprint, x east / y north
    let floors: Int         // storeys up to the eaves as built (measured / estimated)
    let path: CGPath
    let bounds: CGRect
    let area: Double

    var title: String { addr.first ?? (src == "osm" ? "OSM-Gebäude" : "Gebäude ohne Adresse") }
    var sourceName: String { src == "lod2" ? "LGLN LoD2 (amtlich)" : "OpenStreetMap" }

    func contains(_ p: CGPoint) -> Bool { bounds.contains(p) && path.contains(p) }
}

/// One entry of Data/<area>.buildings.json (read by Assets/Editor/OsmImport/BuildingOverrides.cs).
struct HouseStyle: Codable, Equatable {
    var id: String?
    var address: String?
    var wall: String?
    var brick: Bool?
    var roof: String?
    var gableWindows: Int?
    var gableWindowFloor: Int?
    var windows: Bool?        // false = no facade windows
    var hidden: Bool?         // house removed
    var door: String?         // nil = automatic, "none", "N"/"E"/"S"/"W"
    var doorColor: String?
    var roofShape: String?    // OSM houses: gabled / hipped / flat
    var chimney: Bool?
    var floors: Int?          // storeys up to the eaves, nil = as built

    var isEmpty: Bool {
        wall == nil && roof == nil && (gableWindows ?? 0) == 0 && windows == nil && hidden != true
            && door == nil && doorColor == nil && roofShape == nil && chimney == nil && floors == nil
    }
}

struct HouseStylesFile: Codable {
    var buildings: [HouseStyle]
}

enum HouseCatalog {
    struct Raw: Decodable {
        struct B: Decodable { let id: String; let src: String; let addr: [String]; let floors: Int?; let ring: [[Double]] }
        let buildings: [B]
    }

    static func load(_ url: URL) -> [House] {
        guard let data = try? Data(contentsOf: url), let raw = try? JSONDecoder().decode(Raw.self, from: data) else { return [] }
        return raw.buildings.compactMap { b in
            let pts = b.ring.compactMap { $0.count == 2 ? CGPoint(x: $0[0], y: $0[1]) : nil }
            guard pts.count >= 3 else { return nil }
            let path = CGMutablePath()
            path.addLines(between: pts)
            path.closeSubpath()
            var a = 0.0
            for i in pts.indices {
                let p = pts[i], q = pts[(i + 1) % pts.count]
                a += p.x * q.y - q.x * p.y
            }
            return House(id: b.id, src: b.src, addr: b.addr, ring: pts, floors: b.floors ?? 0, path: path, bounds: path.boundingBoxOfPath, area: abs(a) / 2)
        }
    }

    static func normalize(_ s: String) -> String {
        s.lowercased().split(separator: " ").joined(separator: " ")
    }
}

/// Swatches matching the importer's regional palette.
enum Palette {
    static let walls: [(String, String)] = [
        ("Klinker rot", "#9E4834"), ("Klinker dunkel", "#703A2E"), ("Klinker gelb", "#CDAA76"),
        ("Putz weiß", "#EEEAE2"), ("Putz creme", "#E8DCBE"), ("Putz grau", "#C4C4BE"), ("Putz ocker", "#DEC48C"),
    ]
    static let doors: [(String, String)] = [
        ("Friesengrün", "#264E3C"), ("Navy", "#1E344E"), ("Ochsenblut", "#702622"), ("Weiß", "#E8E6DE"),
        ("Holz", "#6E482C"), ("Graublau", "#4E6270"),
    ]
    static let roofs: [(String, String)] = [
        ("Pfanne rot", "#963A2A"), ("Anthrazit", "#3E3E42"), ("Braun", "#704232"), ("Grau", "#7B7F86"),
    ]
}

extension NSColor {
    convenience init?(hexString: String) {
        var s = hexString.trimmingCharacters(in: .whitespaces)
        if s.hasPrefix("#") { s.removeFirst() }
        guard s.count == 6, let v = Int(s, radix: 16) else { return nil }
        self.init(hex: v)
    }

    var hexString: String {
        let c = usingColorSpace(.sRGB) ?? self
        return String(format: "#%02X%02X%02X", Int(round(c.redComponent * 255)), Int(round(c.greenComponent * 255)), Int(round(c.blueComponent * 255)))
    }
}
