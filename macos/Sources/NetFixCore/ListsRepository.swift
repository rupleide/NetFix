import Foundation

public enum ListsRepository {
    private static let sources: [(name: String, url: String)] = [
        ("list-general.txt", "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/lists/list-general.txt"),
        ("list-google.txt", "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/lists/list-google.txt"),
        ("list-exclude.txt", "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/lists/list-exclude.txt")
    ]

    public static let fallbackGeneral = """
    youtube.com
    youtu.be
    googlevideo.com
    ytimg.com
    ggpht.com
    discord.com
    discord.gg
    discordapp.com
    discord.media
    gateway.discord.gg
    telegram.org
    t.me
    web.telegram.org
    cdn-telegram.org
    x.com
    twitter.com
    instagram.com
    facebook.com
    chatgpt.com
    openai.com
    claude.ai
    anthropic.com
    gemini.google.com
    """

    public static func ensureFresh(log: (String) -> Void) async {
        try? Paths.ensure()
        for source in sources {
            let destination = Paths.lists.appendingPathComponent(source.name)
            if !isStale(destination) { continue }
            guard let url = URL(string: source.url) else { continue }
            do {
                log("Скачиваю \(source.name)…")
                try await HTTPClient.download(url, to: destination)
            } catch {
                log("Не удалось обновить \(source.name)")
            }
        }
        let general = Paths.lists.appendingPathComponent("list-general.txt")
        if !FileManager.default.fileExists(atPath: general.path) {
            try? fallbackGeneral.write(to: general, atomically: true, encoding: .utf8)
        }
    }

    public static func materialize(useHostlist: Bool, activeMods: [String]) throws -> (host: URL?, exclude: URL?) {
        guard useHostlist else { return (nil, nil) }
        try Paths.ensure()
        let general = readLines(Paths.lists.appendingPathComponent("list-general.txt"))
        let google = readLines(Paths.lists.appendingPathComponent("list-google.txt"))
        let mods = activeMods.map { ModStore.read(directory: Paths.modsLists, name: $0) }.flatMap(lines)
        let combined = unique(general + google + mods)
        guard !combined.isEmpty else { return (nil, nil) }
        let host = Paths.runtime.appendingPathComponent("hostlist.txt")
        try combined.joined(separator: "\n").appending("\n").write(to: host, atomically: true, encoding: .utf8)

        let excluded = unique(readLines(Paths.lists.appendingPathComponent("list-exclude.txt")))
        if excluded.isEmpty { return (host, nil) }
        let exclude = Paths.runtime.appendingPathComponent("exclude.txt")
        try excluded.joined(separator: "\n").appending("\n").write(to: exclude, atomically: true, encoding: .utf8)
        return (host, exclude)
    }

    private static func isStale(_ url: URL) -> Bool {
        guard let values = try? url.resourceValues(forKeys: [.contentModificationDateKey]),
              let date = values.contentModificationDate else {
            return true
        }
        return Date().timeIntervalSince(date) > 12 * 60 * 60
    }

    private static func readLines(_ url: URL) -> [String] {
        guard let text = try? String(contentsOf: url, encoding: .utf8) else { return [] }
        return lines(text)
    }

    private static func lines(_ text: String) -> [String] {
        text.split(whereSeparator: \.isNewline)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty && !$0.hasPrefix("#") }
    }

    private static func unique(_ items: [String]) -> [String] {
        var seen = Set<String>()
        return items.filter { seen.insert($0).inserted }
    }
}
