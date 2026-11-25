import Foundation

public enum ZapretInstaller {
    public static var isInstalled: Bool {
        FileManager.default.fileExists(atPath: Paths.tpws.path)
    }

    public static var installedVersion: String {
        (try? String(contentsOf: Paths.zapretVersion, encoding: .utf8))?
            .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
    }

    public static func ensure(force: Bool, log: @escaping (String) -> Void) async throws {
        try Paths.ensure()
        let release = try? await fetchRelease()
        if let release, isInstalled, !force, release.tag == installedVersion {
            log("Zapret \(release.tag) уже установлен")
            try ensureHelp()
            return
        }
        if release == nil, isInstalled, !force {
            log("GitHub недоступен. Оставляю установленный Zapret.")
            try ensureHelp()
            return
        }
        if release == nil, restoreFallback() {
            log("GitHub недоступен. Восстановил tpws из последней успешной копии.")
            try ensureHelp()
            return
        }
        guard let release else {
            throw NetFixError("Не удалось скачать Zapret, локальной копии тоже нет")
        }
        guard let asset = release.assets.first(where: {
            $0.name.hasPrefix("zapret-") && $0.name.hasSuffix(".tar.gz") && !$0.name.contains("openwrt")
        }), let url = URL(string: asset.browser_download_url) else {
            throw NetFixError("В релизе Zapret нет архива с бинарниками macOS")
        }

        log("Скачиваю Zapret \(release.tag)…")
        let archive = Paths.cache.appendingPathComponent(asset.name)
        try await HTTPClient.download(url, to: archive)
        log("Распаковываю Zapret…")
        let extracted = try extract(archive)
        var binary = locateTpws(in: extracted)
        if binary == nil || !compatible(binary!) {
            log("Готового бинарника под этот Mac нет. Собираю tpws…")
            guard let root = sourceRoot(in: extracted) else {
                throw NetFixError("В архиве Zapret нет Makefile")
            }
            let make = Shell.run(
                "/usr/bin/make",
                ["mac", "-j\(ProcessInfo.processInfo.activeProcessorCount)"],
                cwd: root
            )
            if !make.ok {
                let tail = make.output.split(separator: "\n").suffix(12).joined(separator: "\n")
                throw NetFixError("Сборка tpws не удалась.\n\(tail)")
            }
            binary = locateTpws(in: extracted)
        }
        guard let binary, compatible(binary) else {
            throw NetFixError("Не найден подходящий бинарник tpws")
        }
        try install(binary: binary, version: release.tag)
        log("Zapret \(release.tag) установлен")
        try ensureHelp()
    }

    private static func install(binary: URL, version: String) throws {
        try? FileManager.default.removeItem(at: Paths.tpws)
        try FileManager.default.copyItem(at: binary, to: Paths.tpws)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: Paths.tpws.path)
        _ = Shell.run("/usr/bin/xattr", ["-dr", "com.apple.quarantine", Paths.tpws.path])
        _ = Shell.run("/usr/bin/codesign", ["-s", "-", "--force", Paths.tpws.path])
        try version.write(to: Paths.zapretVersion, atomically: true, encoding: .utf8)
        let fallback = Paths.fallbackTpws
        try? FileManager.default.createDirectory(at: fallback.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? FileManager.default.removeItem(at: fallback)
        try? FileManager.default.copyItem(at: Paths.tpws, to: fallback)
    }

    private static func restoreFallback() -> Bool {
        guard FileManager.default.isExecutableFile(atPath: Paths.fallbackTpws.path) else { return false }
        do {
            try install(binary: Paths.fallbackTpws, version: installedVersion.isEmpty ? "cached" : installedVersion)
            return true
        } catch {
            return false
        }
    }

    private static func ensureHelp() throws {
        let probe = Shell.run(Paths.tpws.path, ["--help"])
        let help = probe.stdout + probe.stderr
        if help.contains("port") {
            try help.write(to: Paths.tpwsHelp, atomically: true, encoding: .utf8)
            return
        }
        throw NetFixError(
            "macOS не дал запустить tpws. Откройте Системные настройки → Конфиденциальность и безопасность и разрешите файл \(Paths.tpws.path). \(probe.stderr)"
        )
    }

    private static func extract(_ archive: URL) throws -> URL {
        let destination = Paths.cache.appendingPathComponent("zapret-src")
        try? FileManager.default.removeItem(at: destination)
        try FileManager.default.createDirectory(at: destination, withIntermediateDirectories: true)
        let result = Shell.run("/usr/bin/tar", ["-xzf", archive.path, "-C", destination.path])
        if !result.ok {
            throw NetFixError("Не удалось распаковать Zapret. \(result.stderr)")
        }
        _ = Shell.run("/usr/bin/xattr", ["-dr", "com.apple.quarantine", destination.path])
        return destination
    }

    private static func sourceRoot(in extracted: URL) -> URL? {
        if FileManager.default.fileExists(atPath: extracted.appendingPathComponent("Makefile").path) {
            return extracted
        }
        let children = (try? FileManager.default.contentsOfDirectory(at: extracted, includingPropertiesForKeys: nil)) ?? []
        return children.first {
            FileManager.default.fileExists(atPath: $0.appendingPathComponent("Makefile").path)
        }
    }

    private static func locateTpws(in root: URL) -> URL? {
        guard let enumerator = FileManager.default.enumerator(at: root, includingPropertiesForKeys: nil) else {
            return nil
        }
        var found: [URL] = []
        for case let url as URL in enumerator where url.lastPathComponent == "tpws" {
            found.append(url)
        }
        return found.sorted { score($0) > score($1) }.first { compatible($0) }
    }

    private static func score(_ url: URL) -> Int {
        let path = url.path
        if path.contains("binaries/mac64") { return 3 }
        if path.contains("binaries/my") { return 2 }
        return 1
    }

    private static func compatible(_ url: URL) -> Bool {
        let info = Shell.run("/usr/bin/file", ["-b", url.path]).stdout.lowercased()
        if info.contains("text") || info.contains("script") { return false }
        let arch = Shell.run("/usr/bin/uname", ["-m"]).stdout.lowercased()
        if arch.contains("arm") {
            return info.contains("arm64") || info.contains("aarch64") || info.contains("universal")
        }
        return info.contains("x86_64") || info.contains("universal")
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
        let url = URL(string: "https://api.github.com/repos/bol-van/zapret/releases/latest")!
        let data = try await HTTPClient.data(url, headers: ["Accept": "application/vnd.github+json"])
        return try JSONDecoder().decode(Release.self, from: data)
    }
}
