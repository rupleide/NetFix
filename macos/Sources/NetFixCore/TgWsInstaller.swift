import Foundation

public enum TgWsInstaller {
    public static var isInstalled: Bool { appURL() != nil }

    public static var installedVersion: String {
        (try? String(contentsOf: Paths.tgVersion, encoding: .utf8))?
            .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
    }

    public static func ensure(force: Bool, log: @escaping (String) -> Void) async throws {
        try Paths.ensure()
        let release = try? await fetchRelease()
        if let release, isInstalled, !force, release.tag == installedVersion {
            log("TG WS Proxy \(release.tag) уже установлен")
            return
        }
        if release == nil, isInstalled, !force {
            log("GitHub недоступен. Оставляю установленный TG WS Proxy.")
            return
        }
        if release == nil, let cached = newestCacheImage() {
            log("GitHub недоступен. Ставлю TG WS Proxy из последней скачанной копии.")
            try mountAndCopy(dmg: cached)
            return
        }
        guard let release else {
            throw NetFixError("Не удалось скачать TG WS Proxy, локальной копии тоже нет")
        }
        guard let asset = release.assets.first(where: {
            $0.name.lowercased().contains("macos") && $0.name.lowercased().hasSuffix(".dmg")
        }), let url = URL(string: asset.browser_download_url) else {
            throw NetFixError("В релизе TG WS Proxy нет сборки для macOS")
        }

        log("Скачиваю TG WS Proxy \(release.tag)…")
        let archive = Paths.cache.appendingPathComponent(asset.name)
        try await HTTPClient.download(url, to: archive)
        log("Устанавливаю TG WS Proxy…")
        try mountAndCopy(dmg: archive)
        try release.tag.write(to: Paths.tgVersion, atomically: true, encoding: .utf8)
        log("TG WS Proxy \(release.tag) установлен")
    }

    public static func launch() throws {
        guard let app = appURL() else {
            throw NetFixError("TG WS Proxy не установлен")
        }
        _ = Shell.run("/usr/bin/xattr", ["-dr", "com.apple.quarantine", app.path])
        let result = Shell.run("/usr/bin/open", [app.path])
        if !result.ok {
            throw NetFixError("Не удалось открыть TG WS Proxy. Разрешите приложение в Системных настройках → Конфиденциальность и безопасность. \(result.stderr)")
        }
    }

    public static func proxyHint() -> String? {
        let base = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support")
        let roots = ["TgWsProxy", "TG WS Proxy", "tg-ws-proxy", "tgwsproxy"]
        for name in roots {
            let directory = base.appendingPathComponent(name)
            guard let enumerator = FileManager.default.enumerator(at: directory, includingPropertiesForKeys: nil) else {
                continue
            }
            for case let url as URL in enumerator {
                let leaf = url.lastPathComponent.lowercased()
                guard leaf.hasSuffix(".json"), leaf.contains("config") || leaf.contains("setting") else { continue }
                if let hint = hint(from: url) { return hint }
            }
        }
        return nil
    }

    public static func appURL() -> URL? {
        if !Paths.customTelegramApp.isEmpty {
            let custom = URL(fileURLWithPath: Paths.customTelegramApp)
            if FileManager.default.fileExists(atPath: custom.path) { return custom }
        }
        let files = (try? FileManager.default.contentsOfDirectory(at: Paths.apps, includingPropertiesForKeys: nil)) ?? []
        return files.first { $0.pathExtension == "app" }
    }

    private static func newestCacheImage() -> URL? {
        let files = (try? FileManager.default.contentsOfDirectory(at: Paths.cache, includingPropertiesForKeys: nil)) ?? []
        return files
            .filter { $0.pathExtension.lowercased() == "dmg" }
            .sorted { $0.lastPathComponent > $1.lastPathComponent }
            .first
    }

    private static func mountAndCopy(dmg: URL) throws {
        let fm = FileManager.default
        let mount = fm.temporaryDirectory.appendingPathComponent("netfix-dmg-\(UUID().uuidString)", isDirectory: true)
        try fm.createDirectory(at: mount, withIntermediateDirectories: true)
        defer {
            _ = Shell.run("/usr/bin/hdiutil", ["detach", mount.path, "-quiet"])
            try? fm.removeItem(at: mount)
        }
        let attach = Shell.run("/usr/bin/hdiutil", ["attach", "-nobrowse", "-readonly", "-mountpoint", mount.path, dmg.path])
        if !attach.ok {
            throw NetFixError("Не удалось открыть образ TG WS Proxy. \(attach.stderr)")
        }
        guard let enumerator = fm.enumerator(at: mount, includingPropertiesForKeys: nil) else {
            throw NetFixError("Образ TG WS Proxy пуст")
        }
        var app: URL?
        for case let url as URL in enumerator where url.pathExtension == "app" {
            app = url
            break
        }
        guard let app else { throw NetFixError("В образе нет приложения TG WS Proxy") }
        let destination = Paths.apps.appendingPathComponent(app.lastPathComponent)
        try? fm.removeItem(at: destination)
        try fm.copyItem(at: app, to: destination)
        _ = Shell.run("/usr/bin/xattr", ["-dr", "com.apple.quarantine", destination.path])
    }

    private static func hint(from url: URL) -> String? {
        guard let data = try? Data(contentsOf: url),
              let object = try? JSONSerialization.jsonObject(with: data) else {
            return nil
        }
        var port: String?
        var secret: String?
        func walk(_ value: Any) {
            if let dictionary = value as? [String: Any] {
                for (key, item) in dictionary {
                    let name = key.lowercased()
                    if secret == nil, name.contains("secret"), let text = item as? String, !text.isEmpty {
                        secret = text
                    }
                    if port == nil, name == "port" || name.contains("proxy_port") || name == "mtproto_port" {
                        if let number = item as? Int { port = String(number) }
                        if let text = item as? String, !text.isEmpty { port = text }
                    }
                    walk(item)
                }
            } else if let list = value as? [Any] {
                list.forEach(walk)
            }
        }
        walk(object)
        guard port != nil || secret != nil else { return nil }
        var parts = ["Telegram → Настройки → Продвинутые → Тип соединения → Прокси → MTProto, сервер 127.0.0.1"]
        if let port { parts.append("порт \(port)") }
        if let secret { parts.append("секрет \(secret)") }
        return parts.joined(separator: ", ")
    }

    private struct Release: Decodable {
        let tag: String
        let assets: [Asset]
        struct Asset: Decodable {
            let name: String
            let browser_download_url: String
        }
        enum CodingKeys: String, CodingKey {
            case tag = "tag_name"
            case assets
        }
    }

    private static func fetchRelease() async throws -> Release {
        let url = URL(string: "https://api.github.com/repos/Flowseal/tg-ws-proxy/releases/latest")!
        let data = try await HTTPClient.data(url, headers: ["Accept": "application/vnd.github+json"])
        return try JSONDecoder().decode(Release.self, from: data)
    }
}
