import Foundation

public struct UserStrategy: Codable, Identifiable, Equatable {
    public var id: String
    public var title: String
    public var args: String

    public init(id: String, title: String, args: String) {
        self.id = id
        self.title = title
        self.args = args
    }
}

public enum StrategyLibrary {
    public static func parseArgs(_ text: String) -> [String] {
        text.split(whereSeparator: \.isWhitespace).map(String.init).filter { !$0.isEmpty }
    }

    public static func all(custom: [UserStrategy], titles: [String: String]) -> [TpwsStrategy] {
        let builtin = StrategyCatalog.all.map { titled($0, titles: titles) }
        let extra = custom.compactMap { strategy -> TpwsStrategy? in
            let args = parseArgs(strategy.args)
            guard !args.isEmpty else { return nil }
            let title = titles[strategy.id] ?? strategy.title
            return TpwsStrategy(id: strategy.id, title: title, detail: strategy.args, args: args)
        }
        return builtin + extra
    }

    public static func resolve(id: String, custom: [UserStrategy], titles: [String: String]) -> TpwsStrategy {
        all(custom: custom, titles: titles).first { $0.id == id } ?? titled(StrategyCatalog.byId(id), titles: titles)
    }

    public static func title(id: String, custom: [UserStrategy], titles: [String: String]) -> String {
        resolve(id: id, custom: custom, titles: titles).title
    }

    public static func isNewer(_ remote: String, than local: String) -> Bool {
        let left = parts(remote)
        let right = parts(local)
        let count = max(left.count, right.count)
        for index in 0..<count {
            let a = index < left.count ? left[index] : 0
            let b = index < right.count ? right[index] : 0
            if a != b { return a > b }
        }
        return false
    }

    private static func titled(_ strategy: TpwsStrategy, titles: [String: String]) -> TpwsStrategy {
        guard let title = titles[strategy.id], !title.isEmpty else { return strategy }
        return TpwsStrategy(id: strategy.id, title: title, detail: strategy.detail, args: strategy.args)
    }

    private static func parts(_ version: String) -> [Int] {
        version
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .trimmingCharacters(in: CharacterSet(charactersIn: "vV"))
            .split { !$0.isNumber }
            .compactMap { Int($0) }
    }
}
