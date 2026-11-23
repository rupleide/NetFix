import Foundation

public struct MacSettings: Codable {
    public var enableZapret: Bool
    public var enableTgWsProxy: Bool
    public var autostartApp: Bool
    public var startMinimized: Bool
    public var blockQuic: Bool
    public var useHostlist: Bool
    public var strategyId: String
    public var activeListMods: [String]
    public var activeHostsMods: [String]
    public var customDnsPrimary: String
    public var customDnsSecondary: String
    public var lastScan: [ScanScore]
    public var showGameOffer: Bool
    public var customTpwsPath: String
    public var customTgAppPath: String
    public var userStrategies: [UserStrategy]
    public var strategyTitles: [String: String]
    public var favoriteStrategies: [String]

    public init() {
        enableZapret = true
        enableTgWsProxy = true
        autostartApp = false
        startMinimized = false
        blockQuic = true
        useHostlist = true
        strategyId = "split-midsld"
        activeListMods = []
        activeHostsMods = []
        customDnsPrimary = ""
        customDnsSecondary = ""
        lastScan = []
        showGameOffer = true
        customTpwsPath = ""
        customTgAppPath = ""
        userStrategies = []
        strategyTitles = [:]
        favoriteStrategies = []
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let defaults = MacSettings()
        enableZapret = try container.decodeIfPresent(Bool.self, forKey: .enableZapret) ?? defaults.enableZapret
        enableTgWsProxy = try container.decodeIfPresent(Bool.self, forKey: .enableTgWsProxy) ?? defaults.enableTgWsProxy
        autostartApp = try container.decodeIfPresent(Bool.self, forKey: .autostartApp) ?? defaults.autostartApp
        startMinimized = try container.decodeIfPresent(Bool.self, forKey: .startMinimized) ?? defaults.startMinimized
        blockQuic = try container.decodeIfPresent(Bool.self, forKey: .blockQuic) ?? defaults.blockQuic
        useHostlist = try container.decodeIfPresent(Bool.self, forKey: .useHostlist) ?? defaults.useHostlist
        strategyId = try container.decodeIfPresent(String.self, forKey: .strategyId) ?? defaults.strategyId
        activeListMods = try container.decodeIfPresent([String].self, forKey: .activeListMods) ?? []
        activeHostsMods = try container.decodeIfPresent([String].self, forKey: .activeHostsMods) ?? []
        customDnsPrimary = try container.decodeIfPresent(String.self, forKey: .customDnsPrimary) ?? ""
        customDnsSecondary = try container.decodeIfPresent(String.self, forKey: .customDnsSecondary) ?? ""
        lastScan = try container.decodeIfPresent([ScanScore].self, forKey: .lastScan) ?? []
        showGameOffer = try container.decodeIfPresent(Bool.self, forKey: .showGameOffer) ?? defaults.showGameOffer
        customTpwsPath = try container.decodeIfPresent(String.self, forKey: .customTpwsPath) ?? ""
        customTgAppPath = try container.decodeIfPresent(String.self, forKey: .customTgAppPath) ?? ""
        userStrategies = try container.decodeIfPresent([UserStrategy].self, forKey: .userStrategies) ?? []
        strategyTitles = try container.decodeIfPresent([String: String].self, forKey: .strategyTitles) ?? [:]
        favoriteStrategies = try container.decodeIfPresent([String].self, forKey: .favoriteStrategies) ?? []
    }
}

public enum SettingsStore {
    private static let encoder: JSONEncoder = {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return encoder
    }()

    public static func load() -> MacSettings {
        load(from: Paths.settings)
    }

    public static func load(from url: URL) -> MacSettings {
        guard let data = try? Data(contentsOf: url),
              let settings = try? JSONDecoder().decode(MacSettings.self, from: data) else {
            return MacSettings()
        }
        return settings
    }

    public static func save(_ settings: MacSettings) {
        save(settings, to: Paths.settings)
    }

    public static func save(_ settings: MacSettings, to url: URL) {
        do {
            try Paths.ensure()
            let data = try encoder.encode(settings)
            try data.write(to: url, options: .atomic)
        } catch {}
    }
}
