import Foundation

public enum Paths {
    public static var customTpws = ""
    public static var customTelegramApp = ""

    public static func apply(_ settings: MacSettings) {
        customTpws = settings.customTpwsPath
        customTelegramApp = settings.customTgAppPath
    }

    public static var support: URL {
        FileManager.default
            .urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("NetFix", isDirectory: true)
    }

    public static var bin: URL { support.appendingPathComponent("bin", isDirectory: true) }
    public static var tpws: URL { bin.appendingPathComponent("tpws") }
    public static var effectiveTpws: URL {
        if !customTpws.isEmpty, FileManager.default.isExecutableFile(atPath: customTpws) {
            return URL(fileURLWithPath: customTpws)
        }
        return tpws
    }
    public static var fallbackTpws: URL { cache.appendingPathComponent("fallback/tpws") }
    public static var tracks: URL { support.appendingPathComponent("tracks", isDirectory: true) }
    public static var tpwsHelp: URL { bin.appendingPathComponent("tpws.help") }
    public static var zapretVersion: URL { bin.appendingPathComponent("zapret.version") }
    public static var lists: URL { support.appendingPathComponent("lists", isDirectory: true) }
    public static var modsLists: URL { support.appendingPathComponent("mods/lists", isDirectory: true) }
    public static var modsHosts: URL { support.appendingPathComponent("mods/hosts", isDirectory: true) }
    public static var runtime: URL { support.appendingPathComponent("runtime", isDirectory: true) }
    public static var apps: URL { support.appendingPathComponent("Applications", isDirectory: true) }
    public static var tgVersion: URL { apps.appendingPathComponent("tgws.version") }
    public static var logs: URL { support.appendingPathComponent("logs", isDirectory: true) }
    public static var cache: URL { support.appendingPathComponent("cache", isDirectory: true) }
    public static var settings: URL { support.appendingPathComponent("settings.json") }

    public static func ensure() throws {
        let directories = [support, bin, lists, modsLists, modsHosts, runtime, apps, logs, cache, tracks, cache.appendingPathComponent("fallback", isDirectory: true)]
        for directory in directories {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        }
    }
}
