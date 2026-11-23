import Foundation

public struct ScanScore: Codable, Equatable, Identifiable {
    public var id: String
    public var ok: Int
    public var fail: Int
    public var ms: Int

    public init(id: String, ok: Int, fail: Int, ms: Int) {
        self.id = id
        self.ok = ok
        self.fail = fail
        self.ms = ms
    }
}

public enum ScanReport {
    public static func parse(_ text: String) -> (rows: [ScanScore], best: String?) {
        var rows: [ScanScore] = []
        var best: String?
        for raw in text.split(separator: "\n") {
            let parts = raw.split(separator: " ").map(String.init)
            if parts.count == 2, parts[0] == "BEST" {
                best = parts[1] == "none" ? nil : parts[1]
                continue
            }
            if parts.count == 4,
               let ok = Int(parts[1]),
               let fail = Int(parts[2]),
               let ms = Int(parts[3]) {
                rows.append(ScanScore(id: parts[0], ok: ok, fail: fail, ms: ms))
            }
        }
        return (rows, best)
    }
}
