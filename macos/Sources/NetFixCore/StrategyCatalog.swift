import Foundation

public struct TpwsStrategy: Identifiable, Equatable {
    public var id: String
    public var title: String
    public var detail: String
    public var args: [String]

    public init(id: String, title: String, detail: String, args: [String]) {
        self.id = id
        self.title = title
        self.detail = detail
        self.args = args
    }
}

public enum StrategyCatalog {
    public static let all: [TpwsStrategy] = [
        TpwsStrategy(
            id: "split-1",
            title: "Split 1",
            detail: "Разрез TCP на первом байте и disorder. Базовый вариант.",
            args: ["--split-pos=1", "--disorder"]
        ),
        TpwsStrategy(
            id: "split-midsld",
            title: "Split SLD",
            detail: "Разрез в середине домена SNI. Часто подходит для YouTube и Discord.",
            args: ["--split-pos=1,midsld", "--disorder"]
        ),
        TpwsStrategy(
            id: "oob",
            title: "OOB",
            detail: "Out-of-band байт вместе с разрезом.",
            args: ["--split-pos=1", "--oob", "--disorder"]
        ),
        TpwsStrategy(
            id: "tlsrec",
            title: "TLS record",
            detail: "Отдельная TLS-запись на SNI.",
            args: ["--split-pos=1", "--tlsrec=sni", "--disorder"]
        ),
        TpwsStrategy(
            id: "hostcase",
            title: "Host case",
            detail: "Нестандартный регистр заголовка Host. Помогает части HTTP-сайтов.",
            args: ["--split-pos=method+2", "--hostcase", "--methodeol"]
        ),
        TpwsStrategy(
            id: "combo",
            title: "Combo",
            detail: "Разрез SLD, OOB и отдельная TLS-запись.",
            args: ["--split-pos=1,midsld", "--disorder", "--oob", "--tlsrec=sni"]
        )
    ]

    public static func byId(_ id: String) -> TpwsStrategy {
        all.first { $0.id == id } ?? all[1]
    }
}

public enum TpwsArgs {
    public static func resolve(_ strategyArgs: [String], help: String, hostlist: String?, exclude: String?) -> [String] {
        var args = base(port: BypassBlueprint.port) + normalized(strategyArgs, help: help)
        if let hostlist, !hostlist.isEmpty {
            args.append("--hostlist=\(hostlist)")
        }
        if let exclude, !exclude.isEmpty, FileManager.default.fileExists(atPath: exclude) {
            args.append("--hostlist-exclude=\(exclude)")
        }
        return filter(args, help: help)
    }

    public static func base(port: Int) -> [String] {
        ["--user=root", "--port=\(port)", "--bind-addr=127.0.0.1"]
    }

    public static func normalized(_ args: [String], help: String) -> [String] {
        guard !help.isEmpty else { return args }
        let hasSplitTLS = help.contains("--split-tls")
        let hasTLSRec = help.contains("--tlsrec")
        return args.map { arg in
            if hasSplitTLS && !hasTLSRec {
                return arg.replacingOccurrences(of: "--tlsrec=", with: "--split-tls=")
            }
            if hasTLSRec && !hasSplitTLS {
                return arg.replacingOccurrences(of: "--split-tls=", with: "--tlsrec=")
            }
            return arg
        }
    }

    public static func filter(_ args: [String], help: String) -> [String] {
        guard help.contains("--port") || help.contains("port=") else { return args }
        return args.filter { arg in
            guard let name = arg.split(separator: "=", maxSplits: 1).first else { return true }
            let flag = String(name)
            guard flag.hasPrefix("--") else { return true }
            return help.contains(flag)
        }
    }
}
