import AppKit
import Foundation
import ImageIO

/// Buckets item indices by the grid cell of an anchor point (x east, y north). Queries are padded by the largest
/// extent seen on insert, so items reaching into the query rect from a neighbouring cell are found too.
struct GridIndex {
    let cell: Double
    private var cells: [Int: [Int32]] = [:]
    private(set) var pad = 0.0

    init(cell: Double) { self.cell = cell }

    private static func key(_ ix: Int, _ iz: Int) -> Int { (ix << 32) | (iz & 0xFFFF_FFFF) }

    mutating func insert(_ i: Int, at p: CGPoint, extent: Double = 0) {
        cells[Self.key(Int(floor(p.x / cell)), Int(floor(p.y / cell))), default: []].append(Int32(i))
        pad = max(pad, extent)
    }

    /// Calls `body` for every item whose anchor lies within `r` grown by the largest extent (unordered, no duplicates).
    func query(_ r: CGRect, _ body: (Int) -> Void) {
        guard !cells.isEmpty, !r.isNull else { return }
        let g = r.insetBy(dx: -pad, dy: -pad)
        let x0 = Int(floor(g.minX / cell)), x1 = Int(floor(g.maxX / cell))
        let z0 = Int(floor(g.minY / cell)), z1 = Int(floor(g.maxY / cell))
        if (x1 - x0 + 1) * (z1 - z0 + 1) > cells.count * 2 {
            // Zoomed far out: walking the occupied cells is cheaper than walking the rectangle.
            for (k, list) in cells {
                let ix = k >> 32, iz = Int(Int32(truncatingIfNeeded: k))
                guard ix >= x0, ix <= x1, iz >= z0, iz <= z1 else { continue }
                for i in list { body(Int(i)) }
            }
            return
        }
        for ix in x0...x1 {
            for iz in z0...z1 {
                guard let list = cells[Self.key(ix, iz)] else { continue }
                for i in list { body(Int(i)) }
            }
        }
    }
}

/// Top-down images of an area, rendered by SceneSnapshot.RenderThumbnails:
/// - overview `Assets/Generated/<id>/<id>_map.jpg` covering the area rectangle + AreaInfo.MapMargin (60 m), ≤ 2048 px;
/// - one image per 1 km region `Assets/Generated/<id>/regions~/r<rx>_<rz>.jpg` (1024 px, north up), region (rx, rz)
///   covering x ∈ [rx·1000, (rx+1)·1000), z ∈ [rz·1000, (rz+1)·1000) (WorldStreamer.RegionOf / RegionContent.Bounds).
/// Region images are decoded lazily in the background, downsampled to the level the zoom needs, and kept in a
/// small LRU cache. Main thread only (except the decoding itself).
final class MapImages {
    static let regionSize = 1000.0
    static let levels = [256, 512, 1024]

    struct Region: Hashable { let x: Int, z: Int }
    private struct Key: Hashable { let r: Region, level: Int }
    private struct Entry { let image: CGImage; var used: Int }

    let overview: CGImage?
    /// World rectangle of the overview image.
    let overviewRect: CGRect
    /// Regions that have an image file.
    let available: Set<Region>
    /// Bumped whenever a region image arrives (the map redraws).
    private(set) var version = 0
    var onLoad: (() -> Void)?

    private let dir: URL
    private var cache: [Key: Entry] = [:]
    private var bytes = 0
    private var clock = 0
    private var pending: [Key: Operation] = [:]
    private let queue: OperationQueue = {
        let q = OperationQueue()
        q.maxConcurrentOperationCount = 4
        q.qualityOfService = .userInitiated
        return q
    }()
    /// Decoded pixels kept around (~60 full-resolution regions).
    private let budget = 256 << 20

    init(overviewURL: URL, overviewRect: CGRect, regionsDir: URL) {
        if let src = CGImageSourceCreateWithURL(overviewURL as CFURL, nil) {
            overview = CGImageSourceCreateImageAtIndex(src, 0, [kCGImageSourceShouldCacheImmediately: true] as CFDictionary)
        } else {
            overview = nil
        }
        self.overviewRect = overviewRect
        dir = regionsDir
        var found = Set<Region>()
        for name in (try? FileManager.default.contentsOfDirectory(atPath: regionsDir.path)) ?? [] {
            // r<rx>_<rz>.jpg
            guard name.hasPrefix("r"), name.hasSuffix(".jpg") else { continue }
            let xz = name.dropFirst().dropLast(4).split(separator: "_").compactMap { Int($0) }
            if xz.count == 2 { found.insert(Region(x: xz[0], z: xz[1])) }
        }
        available = found
    }

    deinit { queue.cancelAllOperations() }

    /// Region images still being decoded.
    var isLoading: Bool { !pending.isEmpty }

    static func rect(_ r: Region) -> CGRect {
        CGRect(x: Double(r.x) * regionSize, y: Double(r.z) * regionSize, width: regionSize, height: regionSize)
    }

    /// Overview pixels per region edge (regions only help above this).
    var overviewPixelsPerRegion: Double {
        guard let overview, overviewRect.width > 0 else { return 0 }
        return Double(overview.width) / overviewRect.width * Self.regionSize
    }

    /// Existing regions overlapping `view`.
    func regions(in view: CGRect) -> [Region] {
        let x0 = Int(floor(view.minX / Self.regionSize)), x1 = Int(floor(view.maxX / Self.regionSize))
        let z0 = Int(floor(view.minY / Self.regionSize)), z1 = Int(floor(view.maxY / Self.regionSize))
        guard x1 >= x0, z1 >= z0, (x1 - x0 + 1) * (z1 - z0 + 1) <= 4096 else { return [] }
        var out: [Region] = []
        for x in x0...x1 { for z in z0...z1 where available.contains(Region(x: x, z: z)) { out.append(Region(x: x, z: z)) } }
        return out
    }

    /// Smallest level with at least `pixels` per region edge.
    static func level(for pixels: Double) -> Int { levels.first { Double($0) >= pixels } ?? levels.last! }

    /// Best image available now for each region (the wanted level, else the closest cached one) and starts loading
    /// what is missing, nearest to `center` first. Loads no longer wanted are cancelled.
    func images(for regions: [Region], level: Int, center: CGPoint) -> [(Region, CGImage)] {
        clock += 1
        var out: [(Region, CGImage)] = []
        var missing: [Region] = []
        for r in regions {
            if var e = cache[Key(r: r, level: level)] {
                e.used = clock
                cache[Key(r: r, level: level)] = e
                out.append((r, e.image))
                continue
            }
            missing.append(r)
            // Meanwhile: another level of the same region (sharper first).
            for l in Self.levels.sorted(by: { abs($0 - level) < abs($1 - level) || (abs($0 - level) == abs($1 - level) && $0 > $1) })
            where l != level {
                if var e = cache[Key(r: r, level: l)] {
                    e.used = clock
                    cache[Key(r: r, level: l)] = e
                    out.append((r, e.image))
                    break
                }
            }
        }
        let wanted = Set(missing.map { Key(r: $0, level: level) })
        for (k, op) in pending where !wanted.contains(k) {
            op.cancel()
            pending[k] = nil
        }
        let dist = { (r: Region) -> Double in
            let c = Self.rect(r)
            return hypot(c.midX - center.x, c.midY - center.y)
        }
        for r in missing.sorted(by: { dist($0) < dist($1) }) {
            let k = Key(r: r, level: level)
            guard pending[k] == nil else { continue }
            let url = dir.appendingPathComponent("r\(r.x)_\(r.z).jpg")
            let op = BlockOperation()
            op.addExecutionBlock { [weak self, weak op] in
                guard let op, !op.isCancelled else { return }
                let img = Self.decode(url, maxPixels: level)
                DispatchQueue.main.async { self?.loaded(k, img, op) }
            }
            // Nearest first: earlier operations get higher priority.
            op.queuePriority = pending.count < 4 ? .high : .normal
            pending[k] = op
            queue.addOperation(op)
        }
        return out
    }

    private func loaded(_ k: Key, _ img: CGImage?, _ op: Operation) {
        guard pending[k] === op else { return }
        pending[k] = nil
        guard let img else { return }
        cache[k] = Entry(image: img, used: clock)
        bytes += img.bytesPerRow * img.height
        evict()
        version += 1
        onLoad?()
    }

    private func evict() {
        guard bytes > budget else { return }
        for (k, e) in cache.sorted(by: { $0.value.used < $1.value.used }) {
            // Keep everything drawn in the last frame.
            guard bytes > budget, e.used < clock else { break }
            cache[k] = nil
            bytes -= e.image.bytesPerRow * e.image.height
        }
    }

    /// Decodes a JPEG downsampled to at most `maxPixels` per side (ImageIO scales in the DCT, so small levels are cheap).
    private static func decode(_ url: URL, maxPixels: Int) -> CGImage? {
        guard let src = CGImageSourceCreateWithURL(url as CFURL, [kCGImageSourceShouldCache: false] as CFDictionary) else { return nil }
        let opts: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceThumbnailMaxPixelSize: maxPixels,
            kCGImageSourceShouldCacheImmediately: true,
        ]
        return CGImageSourceCreateThumbnailAtIndex(src, 0, opts as CFDictionary)
    }
}
