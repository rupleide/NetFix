import Foundation

public struct UpdateNotice: Identifiable, Equatable {
    public var id: String
    public var title: String
    public var detail: String
    public var url: String?

    public init(id: String, title: String, detail: String, url: String?) {
        self.id = id
        self.title = title
        self.detail = detail
        self.url = url
    }
}

public enum UpdateChecker {
    public static func collect(zapret: String, telegram: String) async -> [UpdateNotice] {
        var notices: [UpdateNotice] = []
        if let app = await release(repo: "rupleide/NetFix", current: AppInfo.version, macAsset: true) {
            notices.append(app)
        }
        if let zapret = await release(repo: "bol-van/zapret", current: zapret, macAsset: false) {
            notices.append(zapret)
        }
        if let telegram = await release(repo: "Flowseal/tg-ws-proxy", current: telegram, macAsset: false) {
            notices.append(telegram)
        }
        return notices
    }

    private static func release(repo: String, current: String, macAsset: Bool) async -> UpdateNotice? {
        guard !current.isEmpty || macAsset else { return nil }
        guard let url = URL(string: "https://api.github.com/repos/\(repo)/releases/latest"),
              let data = try? await HTTPClient.data(url, headers: ["Accept": "application/vnd.github+json"]),
              let item = try? JSONDecoder().decode(Release.self, from: data) else {
            return nil
        }
        let latest = item.tag_name
        let baseline = current.isEmpty ? "0" : current
        guard StrategyLibrary.isNewer(latest, than: baseline) else { return nil }
        let asset = item.assets.first { name in
            let lower = name.name.lowercased()
            return lower.contains("mac") || lower.hasSuffix(".dmg") || lower.hasSuffix(".zip")
        }
        let link = macAsset ? (asset?.browser_download_url ?? item.html_url) : item.html_url
        let detail = macAsset && asset == nil
            ? "Версия \(latest) есть на GitHub. Сборки для macOS в релизе нет, откроется страница."
            : "Доступна версия \(latest)."
        return UpdateNotice(id: repo, title: repo.split(separator: "/").last.map(String.init) ?? repo, detail: detail, url: link)
    }

    private struct Release: Decodable {
        var tag_name: String
        var html_url: String
        var assets: [Asset]
        struct Asset: Decodable {
            var name: String
            var browser_download_url: String
        }
    }
}
