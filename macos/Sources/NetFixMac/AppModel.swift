import AppKit
import Combine
import NetFixCore
import SwiftUI
import UniformTypeIdentifiers

enum AppSection: String, CaseIterable, Identifiable {
    case home, services, strategies, dns, hosts, diagnostics, faq, settings, game

    var id: String { rawValue }

    var title: String {
        switch self {
        case .home: return "Главная"
        case .services: return "Сервисы"
        case .strategies: return "Стратегии"
        case .dns: return "DNS"
        case .hosts: return "Hosts"
        case .diagnostics: return "Диагностика"
        case .faq: return "FAQ"
        case .settings: return "Настройки"
        case .game: return "Игра"
        }
    }
}

enum EditorKind {
    case list
    case hosts
}

enum ModsPage {
    case hub
    case strategies
    case lists
    case hosts
}

struct LogLine: Identifiable, Equatable {
    var id: Int
    var text: String
    var level: String
    var time = Date()
}

@MainActor
final class AppModel: ObservableObject {
    @Published var settings: MacSettings
    @Published var section: AppSection = .home
    @Published var logs: [LogLine] = []
    @Published var busy = false
    @Published var statusText = "Обход выключен"
    @Published var statusLevel = "info"
    @Published var zapretRunning = false
    @Published var tgRunning = false
    @Published var zapretVersion = ""
    @Published var tgVersion = ""
    @Published var listMods: [String] = []
    @Published var hostMods: [String] = []
    @Published var selectedList: String?
    @Published var selectedHost: String?
    @Published var editorText = ""
    @Published var editorKind: EditorKind = .list
    @Published var probes: [ProbeResult] = []
    @Published var dnsServiceName = ""
    @Published var dnsSummary = "—"
    @Published var creatingMod = false
    @Published var modDraft = ""
    @Published var openFaq: String?
    @Published var modsPage: ModsPage = .hub
    @Published var online = false
    @Published var vpnOn = false
    @Published var downloadText = "—"
    @Published var uploadText = "—"
    @Published var pingText = "—"
    @Published var measuringLink = false
    @Published var scanning = false
    @Published var offerGame = false
    @Published var connectedSince: Date?
    @Published var dnsServers: [String] = []
    @Published var advice: [String] = []
    @Published var notices: [UpdateNotice] = []
    @Published var editorZoom = 1.0
    @Published var strategyTitle = ""
    @Published var strategyArgs = ""
    @Published var renamingStrategy = false
    @Published var renameDraft = ""

    private var logSeq = 0
    private var didApplyMinimize = false
    private var didMeasurePing = false

    var bypassOn: Bool {
        let zapretOk = !settings.enableZapret || zapretRunning
        let telegramOk = !settings.enableTgWsProxy || tgRunning
        let any = settings.enableZapret || settings.enableTgWsProxy
        return any && zapretOk && telegramOk
    }

    init() {
        settings = SettingsStore.load()
        Paths.apply(settings)
        reloadMods()
    }

    func prepare() {
        try? Paths.ensure()
        Paths.apply(settings)
        reloadMods()
        checkUpdates()
        loadDns()
        refreshStatus()
        if !didMeasurePing {
            didMeasurePing = true
            measurePing()
        }
        if settings.startMinimized && !didApplyMinimize {
            didApplyMinimize = true
            NSApp.windows.forEach { $0.orderOut(nil) }
        }
    }

    func focus(_ kind: EditorKind) {
        persistEditor()
        editorKind = kind
        switch kind {
        case .list:
            editorText = selectedList.map { ModStore.read(directory: Paths.modsLists, name: $0) } ?? ""
        case .hosts:
            editorText = selectedHost.map { ModStore.read(directory: Paths.modsHosts, name: $0) } ?? ""
        }
    }

    func isOn(_ keyPath: WritableKeyPath<MacSettings, Bool>) -> Binding<Bool> {
        Binding(
            get: { self.settings[keyPath: keyPath] },
            set: { value in
                self.settings[keyPath: keyPath] = value
                SettingsStore.save(self.settings)
            }
        )
    }

    func open(_ next: AppSection) {
        if next == .strategies, section != .strategies {
            modsPage = .hub
        }
        section = section == next ? .home : next
    }

    func closePanel() {
        section = section == .dns ? .services : .home
    }

    func toggle() {
        guard !busy else { return }
        if !settings.enableZapret && settings.enableTgWsProxy && tgRunning {
            note("TG WS Proxy уже открыт. Закройте его из иконки в строке меню.", level: "info")
            return
        }
        busy = true
        let running = zapretRunning
        let snapshot = settings
        Task.detached { [weak self] in
            guard let self else { return }
            if running {
                await self.stopBypass()
            } else {
                await self.startBypass(snapshot)
            }
            await self.endJob()
        }
    }

    func scan() {
        guard !busy else { return }
        busy = true
        scanning = true
        offerGame = settings.showGameOffer
        let snapshot = settings
        Task.detached { [weak self] in
            guard let self else { return }
            await self.performScan(snapshot)
            await self.endJob()
        }
    }

    func diagnose() {
        guard !busy else { return }
        busy = true
        Task.detached { [weak self] in
            guard let self else { return }
            await self.note("Диагностика…")
            let report = await DiagnosticsService.report(zapretRunning: self.zapretRunning)
            await self.showProbes(report.probes, advice: report.advice)
            await self.endJob()
        }
    }

    func installZapret() { install(zapret: true) }
    func installTelegram() { install(zapret: false) }

    func updateComponents() {
        guard !busy else { return }
        busy = true
        Task.detached { [weak self] in
            guard let self else { return }
            await self.installOne(zapret: true)
            await self.installOne(zapret: false)
            await self.endJob()
        }
    }

    func closeTelegram() {
        let apps = NSWorkspace.shared.runningApplications.filter { app in
            let name = app.localizedName ?? ""
            return name.localizedCaseInsensitiveContains("WS Proxy")
                || name.localizedCaseInsensitiveContains("TgWs")
        }
        guard !apps.isEmpty else {
            note("TG WS Proxy не запущен", level: "info")
            return
        }
        apps.forEach { $0.terminate() }
        note("Закрываю TG WS Proxy", level: "info")
    }

    func openTelegram() {
        guard !busy else { return }
        busy = true
        Task.detached { [weak self] in
            guard let self else { return }
            await self.startTelegram()
            await self.endJob()
        }
    }

    func stopFromServices() {
        guard !busy else { return }
        busy = true
        Task.detached { [weak self] in
            guard let self else { return }
            await self.stopBypass()
            await self.endJob()
        }
    }

    func applyDns(servers: [String]) {
        guard !busy else { return }
        guard let service = DnsService.activeService() else {
            note("Не нашёл активную сетевую службу", level: "error")
            return
        }
        busy = true
        Task.detached { [weak self] in
            guard let self else { return }
            do {
                try DnsService.apply(service: service, servers: servers)
                await self.note(servers.isEmpty ? "DNS переключён на DHCP" : "DNS обновлён: \(servers.joined(separator: ", "))", level: "ok")
                await self.loadDns()
            } catch {
                await self.note(error.localizedDescription, level: "error")
            }
            await self.endJob()
        }
    }

    func applyCustomDns() {
        let servers = [settings.customDnsPrimary, settings.customDnsSecondary]
        applyDns(servers: servers)
    }

    func applyHosts() {
        guard !busy else { return }
        persistEditor()
        busy = true
        let names = settings.activeHostsMods
        Task.detached { [weak self] in
            guard let self else { return }
            do {
                try HostsService.apply(activeMods: names)
                await self.note(names.isEmpty ? "Записи NetFix убраны из hosts" : "Hosts обновлён", level: "ok")
            } catch {
                await self.note(error.localizedDescription, level: "error")
            }
            await self.endJob()
        }
    }

    func toggleFaq(_ title: String) {
        openFaq = openFaq == title ? nil : title
    }

    func selectStrategy(_ id: String) {
        settings.strategyId = id
        SettingsStore.save(settings)
        note("Стратегия «\(strategyName(id))» выбрана. Применится при следующем запуске.", level: "info")
    }

    func strategyName(_ id: String) -> String {
        StrategyLibrary.title(id: id, custom: settings.userStrategies, titles: settings.strategyTitles)
    }

    func strategies() -> [TpwsStrategy] {
        let rows = StrategyLibrary.all(custom: settings.userStrategies, titles: settings.strategyTitles)
        return rows.sorted { left, right in
            let leftStar = settings.favoriteStrategies.contains(left.id)
            let rightStar = settings.favoriteStrategies.contains(right.id)
            if leftStar != rightStar { return leftStar && !rightStar }
            return left.title.localizedCaseInsensitiveCompare(right.title) == .orderedAscending
        }
    }

    func toggleFavorite(_ id: String) {
        if settings.favoriteStrategies.contains(id) {
            settings.favoriteStrategies.removeAll { $0 == id }
        } else {
            settings.favoriteStrategies.append(id)
        }
        SettingsStore.save(settings)
    }

    func addStrategy() {
        let title = strategyTitle.trimmingCharacters(in: .whitespacesAndNewlines)
        let args = StrategyLibrary.parseArgs(strategyArgs)
        guard !title.isEmpty, !args.isEmpty else {
            note("Нужны название и аргументы tpws", level: "error")
            return
        }
        let strategy = UserStrategy(id: "user-\(UUID().uuidString)", title: title, args: args.joined(separator: " "))
        settings.userStrategies.append(strategy)
        SettingsStore.save(settings)
        strategyTitle = ""
        strategyArgs = ""
        note("Стратегия «\(title)» добавлена", level: "ok")
    }

    func renameSelectedStrategy() {
        let title = renameDraft.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !title.isEmpty else { return }
        let id = settings.strategyId
        settings.strategyTitles[id] = title
        if let index = settings.userStrategies.firstIndex(where: { $0.id == id }) {
            settings.userStrategies[index].title = title
        }
        SettingsStore.save(settings)
        renamingStrategy = false
    }

    func deleteStrategy(_ id: String) {
        settings.userStrategies.removeAll { $0.id == id }
        settings.strategyTitles[id] = nil
        settings.favoriteStrategies.removeAll { $0 == id }
        if settings.strategyId == id { settings.strategyId = "split-midsld" }
        SettingsStore.save(settings)
    }

    func repair() {
        section = .home
        if zapretRunning || bypassOn {
            note("Обход уже запущен", level: "ok")
            return
        }
        toggle()
    }

    func checkUpdates() {
        let zapret = zapretVersion.isEmpty ? ZapretInstaller.installedVersion : zapretVersion
        let telegram = tgVersion.isEmpty ? TgWsInstaller.installedVersion : tgVersion
        Task.detached {
            let notices = await UpdateChecker.collect(zapret: zapret, telegram: telegram)
            await self.showNotices(notices)
        }
    }

    func resetSettings() {
        settings = MacSettings()
        SettingsStore.save(settings)
        Paths.apply(settings)
        try? LoginItem.sync(enabled: false)
        note("Настройки сброшены", level: "ok")
    }

    func browseTpws() {
        let panel = NSOpenPanel()
        panel.allowsOtherFileTypes = true
        panel.canChooseDirectories = false
        panel.allowedContentTypes = [.unixExecutable, .data]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        settings.customTpwsPath = url.path
        SettingsStore.save(settings)
        Paths.apply(settings)
        note("Будет использоваться выбранный tpws", level: "ok")
    }

    func browseTelegramApp() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.application]
        panel.canChooseDirectories = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        settings.customTgAppPath = url.path
        SettingsStore.save(settings)
        Paths.apply(settings)
        note("Будет открываться выбранный TG WS Proxy", level: "ok")
    }

    func clearCustomPaths() {
        settings.customTpwsPath = ""
        settings.customTgAppPath = ""
        SettingsStore.save(settings)
        Paths.apply(settings)
    }

    func exportSelectedMod() {
        let name = editorKind == .list ? selectedList : selectedHost
        guard let name else { return }
        let directory = editorKind == .list ? Paths.modsLists : Paths.modsHosts
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.plainText]
        panel.nameFieldStringValue = "\(name).txt"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        persistEditor()
        do {
            if FileManager.default.fileExists(atPath: url.path) {
                try FileManager.default.removeItem(at: url)
            }
            try FileManager.default.copyItem(at: ModStore.file(directory: directory, name: name), to: url)
            note("Мод экспортирован", level: "ok")
        } catch {
            note(error.localizedDescription, level: "error")
        }
    }

    func setAutostart(_ enabled: Bool) {
        settings.autostartApp = enabled
        SettingsStore.save(settings)
        do {
            try LoginItem.sync(enabled: enabled)
            note(enabled ? "NetFix будет открываться при входе" : "Автозапуск выключен", level: "ok")
        } catch {
            note(error.localizedDescription, level: "error")
        }
    }

    func selectList(_ name: String) {
        persistEditor()
        editorKind = .list
        selectedHost = nil
        selectedList = name
        editorText = ModStore.read(directory: Paths.modsLists, name: name)
    }

    func selectHost(_ name: String) {
        persistEditor()
        editorKind = .hosts
        selectedList = nil
        selectedHost = name
        editorText = ModStore.read(directory: Paths.modsHosts, name: name)
    }

    func saveEditor() {
        persistEditor()
        note("Мод сохранён", level: "ok")
    }

    func createMod(kind: EditorKind, name: String, text: String? = nil) {
        guard let safe = ModStore.sanitize(name) else {
            note("Некорректное имя мода", level: "error")
            return
        }
        let directory = kind == .list ? Paths.modsLists : Paths.modsHosts
        let body = text ?? (kind == .list ? "example.com\n" : "0.0.0.0 example.com\n")
        do {
            try ModStore.write(directory: directory, name: safe, text: body)
            reloadMods()
            if kind == .list { selectList(safe) } else { selectHost(safe) }
        } catch {
            note(error.localizedDescription, level: "error")
        }
    }

    func importMod(kind: EditorKind) {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.plainText]
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        let access = url.startAccessingSecurityScopedResource()
        defer { if access { url.stopAccessingSecurityScopedResource() } }
        guard let text = try? String(contentsOf: url, encoding: .utf8) else {
            note("Не удалось прочитать файл", level: "error")
            return
        }
        createMod(kind: kind, name: url.deletingPathExtension().lastPathComponent, text: text)
    }

    func deleteSelected() {
        if editorKind == .list, let name = selectedList {
            try? ModStore.delete(directory: Paths.modsLists, name: name)
            settings.activeListMods.removeAll { $0 == name }
            selectedList = nil
        } else if let name = selectedHost {
            try? ModStore.delete(directory: Paths.modsHosts, name: name)
            settings.activeHostsMods.removeAll { $0 == name }
            selectedHost = nil
        }
        editorText = ""
        SettingsStore.save(settings)
        reloadMods()
    }

    func toggleList(_ name: String) {
        toggle(&settings.activeListMods, name)
    }

    func toggleHost(_ name: String) {
        toggle(&settings.activeHostsMods, name)
    }

    func loadDns() {
        guard let service = DnsService.activeService() else {
            dnsServiceName = ""
            dnsSummary = "Сетевая служба не найдена"
            return
        }
        dnsServiceName = service
        switch DnsService.currentServers(service: service) {
        case .dhcp:
            dnsServers = []
            dnsSummary = "DHCP"
        case .manual(let servers):
            dnsServers = servers
            dnsSummary = servers.joined(separator: ", ")
        case .unknown(let text):
            dnsServers = []
            dnsSummary = text.isEmpty ? "Не удалось прочитать" : text
        }
    }

    func exportSettings() {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.json]
        panel.nameFieldStringValue = "netfix-settings.json"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        SettingsStore.save(settings, to: url)
        note("Настройки экспортированы", level: "ok")
    }

    func importSettings() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.json]
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        let access = url.startAccessingSecurityScopedResource()
        defer { if access { url.stopAccessingSecurityScopedResource() } }
        settings = SettingsStore.load(from: url)
        SettingsStore.save(settings)
        reloadMods()
        note("Настройки импортированы", level: "ok")
    }

    func openSupport() {
        try? Paths.ensure()
        NSWorkspace.shared.open(Paths.support)
    }

    func openURL(_ raw: String) {
        guard let url = URL(string: raw) else { return }
        NSWorkspace.shared.open(url)
    }

    func copy(_ text: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
        statusText = "Скопировано"
        statusLevel = "ok"
    }

    func copyLogs() {
        let formatter = DateFormatter()
        formatter.dateFormat = "HH:mm:ss"
        let text = logs.map { "[\(formatter.string(from: $0.time))] \($0.text)" }.joined(separator: "\n")
        copy(text.isEmpty ? "" : text)
        statusText = text.isEmpty ? "Лог пуст" : "Лог скопирован"
    }

    func clearLogs() {
        logs.removeAll()
        statusText = "Готово"
        statusLevel = "info"
    }

    func measureLink() {
        guard !measuringLink else { return }
        measuringLink = true
        Task.detached { [weak self] in
            let ping = await Self.elapsedPing()
            let down = await Self.megabits(upload: false)
            let up = await Self.megabits(upload: true)
            await self?.applyLink(ping: ping, down: down, up: up)
        }
    }

    func refreshStatus() {
        Task.detached { [weak self] in
            let running = BypassController.isRunning()
            let zapretVersion = ZapretInstaller.installedVersion
            let tgVersion = TgWsInstaller.installedVersion
            let link = Self.linkFlags()
            await self?.applyRuntime(
                running: running,
                zapretVersion: zapretVersion,
                tgVersion: tgVersion,
                online: link.online,
                vpn: link.vpn
            )
        }
    }

    func note(_ text: String, level: String = "info") {
        logSeq += 1
        logs.append(LogLine(id: logSeq, text: text, level: level))
        if logs.count > 300 {
            logs.removeFirst(logs.count - 300)
        }
        statusText = text
        statusLevel = level
    }

    private func install(zapret: Bool) {
        guard !busy else { return }
        busy = true
        Task.detached { [weak self] in
            guard let self else { return }
            await self.installOne(zapret: zapret)
            await self.endJob()
        }
    }

    nonisolated private func installOne(zapret: Bool) async {
        do {
            if zapret {
                try await ZapretInstaller.ensure(force: true) { message in
                    Task { await self.note(message) }
                }
            } else {
                try await TgWsInstaller.ensure(force: true) { message in
                    Task { await self.note(message) }
                }
            }
        } catch {
            await note(error.localizedDescription, level: "error")
        }
    }

    nonisolated private func startBypass(_ settings: MacSettings) async {
        if settings.enableZapret {
            do {
                try await ZapretInstaller.ensure(force: false) { message in
                    Task { await self.note(message) }
                }
                await ListsRepository.ensureFresh { message in
                    Task { await self.note(message) }
                }
                let lists = try ListsRepository.materialize(
                    useHostlist: settings.useHostlist,
                    activeMods: settings.activeListMods
                )
                if lists.host == nil, settings.useHostlist {
                    await note("Список доменов пуст, обход пойдёт на весь TCP 80/443", level: "warn")
                }
                let strategy = StrategyLibrary.resolve(id: settings.strategyId, custom: settings.userStrategies, titles: settings.strategyTitles)
                await note("Запускаю «\(strategy.title)». macOS запросит пароль администратора.")
                try BypassController.launch(
                    strategy: strategy,
                    blockQuic: settings.blockQuic,
                    hostlist: lists.host,
                    exclude: lists.exclude
                )
                await note("Zapret запущен: \(strategy.title)", level: "ok")
            } catch {
                await note(error.localizedDescription, level: "error")
            }
        }
        if settings.enableTgWsProxy {
            await startTelegram()
        }
        if !settings.enableZapret && !settings.enableTgWsProxy {
            await note("В настройках выключены Zapret и TG WS Proxy", level: "warn")
        }
    }

    nonisolated private func stopBypass() async {
        do {
            try BypassController.shutdown()
            await note("Обход остановлен", level: "ok")
        } catch {
            await note(error.localizedDescription, level: "error")
        }
    }

    nonisolated private func startTelegram() async {
        do {
            try await TgWsInstaller.ensure(force: false) { message in
                Task { await self.note(message) }
            }
            try TgWsInstaller.launch()
            try? await Task.sleep(nanoseconds: 1_200_000_000)
            if let hint = TgWsInstaller.proxyHint() {
                await note(hint, level: "ok")
            } else {
                await note("TG WS Proxy открыт. В Telegram укажите MTProto-прокси 127.0.0.1. Порт и секрет — в окне прокси.", level: "info")
            }
        } catch {
            await note(error.localizedDescription, level: "warn")
        }
    }

    nonisolated private func performScan(_ settings: MacSettings) async {
        do {
            try await ZapretInstaller.ensure(force: false) { message in
                Task { await self.note(message) }
            }
            await ListsRepository.ensureFresh { message in
                Task { await self.note(message) }
            }
            let lists = try ListsRepository.materialize(
                useHostlist: settings.useHostlist,
                activeMods: settings.activeListMods
            )
            await note("Подбираю стратегию. Пароль спросится один раз, проверка займёт около минуты.")
            let text = try BypassController.scan(
                strategies: StrategyLibrary.all(custom: settings.userStrategies, titles: settings.strategyTitles),
                blockQuic: settings.blockQuic,
                hostlist: lists.host,
                exclude: lists.exclude
            )
            await applyScan(ScanReport.parse(text))
        } catch {
            await note(error.localizedDescription, level: "error")
        }
    }

    private func applyScan(_ report: (rows: [ScanScore], best: String?)) {
        settings.lastScan = report.rows
        for row in report.rows {
            let title = StrategyLibrary.title(id: row.id, custom: settings.userStrategies, titles: settings.strategyTitles)
            note("\(title): \(row.ok) из \(row.ok + row.fail), \(row.ms) мс", level: row.ok > 0 ? "ok" : "warn")
        }
        if let best = report.best {
            settings.strategyId = best
            note("Выбрана стратегия \(StrategyLibrary.title(id: best, custom: settings.userStrategies, titles: settings.strategyTitles))", level: "ok")
        } else {
            note("Ни одна стратегия не открыла проверочные сайты", level: "error")
        }
        SettingsStore.save(settings)
    }

    private func showProbes(_ rows: [ProbeResult], advice lines: [String] = []) {
        probes = rows
        advice = lines
        let failed = rows.filter { !$0.ok }.count
        note(failed == 0 ? "Диагностика: всё отвечает" : "Диагностика: проблемных проверок \(failed)", level: failed == 0 ? "ok" : "warn")
    }

    private func showNotices(_ rows: [UpdateNotice]) {
        notices = rows
        if let first = rows.first {
            note("\(first.title): \(first.detail)", level: "info")
        }
    }

    private func endJob() {
        busy = false
        scanning = false
        offerGame = false
        refreshStatus()
    }

    private func applyRuntime(
        running: Bool,
        zapretVersion: String,
        tgVersion: String,
        online: Bool,
        vpn: Bool
    ) {
        zapretRunning = running
        self.zapretVersion = zapretVersion
        self.tgVersion = tgVersion
        self.online = online
        vpnOn = vpn
        tgRunning = NSWorkspace.shared.runningApplications.contains { app in
            let name = app.localizedName ?? ""
            return name.localizedCaseInsensitiveContains("WS Proxy") || name.localizedCaseInsensitiveContains("TgWs")
        }
        if bypassOn {
            if connectedSince == nil { connectedSince = Date() }
        } else {
            connectedSince = nil
        }
    }

    func measurePing() {
        Task.detached { [weak self] in
            let ping = await Self.elapsedPing()
            await self?.setPing(ping)
        }
    }

    private func setPing(_ value: String) {
        pingText = value
    }

    private func applyLink(ping: String, down: String, up: String) {
        pingText = ping
        downloadText = down
        uploadText = up
        measuringLink = false
    }

    nonisolated private static func linkFlags() -> (online: Bool, vpn: Bool) {
        let route = Shell.run("/sbin/route", ["-n", "get", "default"]).stdout
        let iface = route
            .split(separator: "\n")
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .first { $0.hasPrefix("interface:") }?
            .split(separator: ":")
            .last
            .map { $0.trimmingCharacters(in: .whitespaces) }
        let name = iface ?? ""
        let online = !name.isEmpty
        let vpn = name.hasPrefix("utun") || name.hasPrefix("ipsec") || name.hasPrefix("ppp")
        return (online, vpn)
    }

    nonisolated private static func elapsedPing() async -> String {
        guard let url = URL(string: "https://www.gstatic.com/generate_204") else { return "—" }
        var request = URLRequest(url: url)
        request.timeoutInterval = 6
        let start = Date()
        do {
            let (_, response) = try await URLSession.shared.data(for: request)
            guard let http = response as? HTTPURLResponse, (200..<400).contains(http.statusCode) else { return "—" }
            return "\(Int(Date().timeIntervalSince(start) * 1000))"
        } catch {
            return "—"
        }
    }

    nonisolated private static func megabits(upload: Bool) async -> String {
        let start = Date()
        do {
            let count: Int
            if upload {
                guard let url = URL(string: "https://speed.cloudflare.com/__up") else { return "—" }
                var request = URLRequest(url: url)
                request.httpMethod = "POST"
                request.timeoutInterval = 20
                let body = Data(count: 400_000)
                let (_, response) = try await URLSession.shared.upload(for: request, from: body)
                guard let http = response as? HTTPURLResponse, (200..<400).contains(http.statusCode) else { return "—" }
                count = body.count
            } else {
                guard let url = URL(string: "https://speed.cloudflare.com/__down?bytes=1500000") else { return "—" }
                var request = URLRequest(url: url)
                request.timeoutInterval = 20
                let (data, response) = try await URLSession.shared.data(for: request)
                guard let http = response as? HTTPURLResponse, (200..<400).contains(http.statusCode), !data.isEmpty else {
                    return "—"
                }
                count = data.count
            }
            let seconds = Date().timeIntervalSince(start)
            guard seconds > 0.05 else { return "—" }
            let value = Double(count) * 8 / seconds / 1_000_000
            return String(format: "%.1f", value).replacingOccurrences(of: ".", with: ",")
        } catch {
            return "—"
        }
    }

    private func reloadMods() {
        listMods = ModStore.names(in: Paths.modsLists)
        hostMods = ModStore.names(in: Paths.modsHosts)
    }

    private func persistEditor() {
        if editorKind == .list, let name = selectedList {
            try? ModStore.write(directory: Paths.modsLists, name: name, text: editorText)
        } else if let name = selectedHost {
            try? ModStore.write(directory: Paths.modsHosts, name: name, text: editorText)
        }
    }

    private func toggle(_ list: inout [String], _ name: String) {
        if list.contains(name) {
            list.removeAll { $0 == name }
        } else {
            list.append(name)
        }
        SettingsStore.save(settings)
    }
}
