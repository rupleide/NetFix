import Foundation

public enum BypassController {
    public static func isRunning() -> Bool {
        let markers = [Paths.privilegedTpws.path, Paths.effectiveTpws.path]
        let lines = Shell.run("/bin/ps", ["-ax", "-o", "command="]).stdout.split(separator: "\n")
        return lines.contains { line in
            markers.contains { line.contains($0) } && !line.contains("netfix-") && !line.contains("grep")
        }
    }

    public static func launch(strategy: TpwsStrategy, blockQuic: Bool, hostlist: URL?, exclude: URL?) throws {
        let binary = Paths.effectiveTpws.path
        guard FileManager.default.fileExists(atPath: binary) else {
            throw NetFixError("tpws не установлен")
        }
        let (plistURL, pfURL) = try writeRuntime(strategy: strategy, blockQuic: blockQuic, hostlist: hostlist, exclude: exclude, directory: Paths.runtime)
        try PrivilegedRunner.run(script: BypassScripts.start, arguments: [plistURL.path, pfURL.path, binary])
    }

    public static func shutdown() throws {
        try PrivilegedRunner.run(script: BypassScripts.stop)
    }

    public static func scan(strategies: [TpwsStrategy], blockQuic: Bool, hostlist: URL?, exclude: URL?) throws -> String {
        let binary = Paths.effectiveTpws.path
        guard FileManager.default.fileExists(atPath: binary) else {
            throw NetFixError("tpws не установлен")
        }
        try Paths.ensure()
        let root = Paths.runtime.appendingPathComponent("scan")
        try? FileManager.default.removeItem(at: root)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let pf = BypassBlueprint.pfRules(port: BypassBlueprint.port, blockQuic: blockQuic)
        let pfURL = Paths.runtime.appendingPathComponent("netfix.pf")
        try pf.write(to: pfURL, atomically: true, encoding: .utf8)
        let help = (try? String(contentsOf: Paths.tpwsHelp, encoding: .utf8)) ?? ""
        for strategy in strategies {
            let args = TpwsArgs.resolve(strategy.args, help: help, hostlist: hostlist?.path, exclude: exclude?.path)
            let plist = BypassBlueprint.daemonPlist(binary: binary, arguments: args)
            let directory = root.appendingPathComponent(strategy.id)
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            try plist.write(to: directory.appendingPathComponent("daemon.plist"), atomically: true, encoding: .utf8)
        }
        let results = Paths.runtime.appendingPathComponent("scan-results.txt")
        try Data().write(to: results, options: .atomic)
        try PrivilegedRunner.run(
            script: BypassScripts.scan,
            arguments: [root.path, results.path, pfURL.path, binary]
        )
        return (try? String(contentsOf: results, encoding: .utf8)) ?? ""
    }

    private static func writeRuntime(
        strategy: TpwsStrategy,
        blockQuic: Bool,
        hostlist: URL?,
        exclude: URL?,
        directory: URL
    ) throws -> (URL, URL) {
        try Paths.ensure()
        let help = (try? String(contentsOf: Paths.tpwsHelp, encoding: .utf8)) ?? ""
        let args = TpwsArgs.resolve(strategy.args, help: help, hostlist: hostlist?.path, exclude: exclude?.path)
        let plist = BypassBlueprint.daemonPlist(binary: Paths.effectiveTpws.path, arguments: args)
        let pf = BypassBlueprint.pfRules(port: BypassBlueprint.port, blockQuic: blockQuic)
        let plistURL = directory.appendingPathComponent("com.netfix.tpws.plist")
        let pfURL = directory.appendingPathComponent("netfix.pf")
        try plist.write(to: plistURL, atomically: true, encoding: .utf8)
        try pf.write(to: pfURL, atomically: true, encoding: .utf8)
        return (plistURL, pfURL)
    }
}
