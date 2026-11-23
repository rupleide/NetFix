import SwiftUI
import NetFixCore

struct HomePage: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        ZStack {
            AuroraBackground(running: model.bypassOn)
            VStack(alignment: .leading, spacing: 0) {
                VStack(spacing: 4) {
                    HStack(spacing: 8) {
                        Image(systemName: "bolt.fill")
                            .font(.system(size: 16, weight: .bold))
                        Text("Починить Telegram и Discord")
                            .font(.system(size: 22, weight: .bold))
                    }
                    .foregroundStyle(.white)
                    Text("Одна кнопка — скачает, настроит и запустит всё необходимое.")
                        .font(.system(size: 13))
                        .foregroundStyle(Theme.muted)
                }
                .frame(maxWidth: .infinity)
                .padding(.bottom, 18)

                HStack(alignment: .center, spacing: 0) {
                    Color.clear.frame(width: 230)
                    Spacer(minLength: 0)
                    powerButton
                    Spacer(minLength: 0)
                    speedColumn
                        .frame(width: 230, alignment: .leading)
                }
                .frame(height: 210)
                .padding(.bottom, 8)

                progress
                Text(statusLine)
                    .font(.system(size: 13))
                    .foregroundStyle(Theme.muted)
                    .padding(.top, 8)
                    .padding(.bottom, 16)

                logHeader
                log
            }
            .padding(.horizontal, 32)
            .padding(.top, 22)
            .padding(.bottom, 12)
        }
    }

    private var statusLine: String {
        if model.busy { return model.statusText }
        if model.bypassOn { return "Всё уже работает" }
        if model.logs.isEmpty { return "Готово" }
        return model.statusText
    }

    private var powerButton: some View {
        Button(action: model.toggle) {
            ZStack {
                Circle()
                    .stroke((model.bypassOn ? Theme.green : Theme.accent).opacity(0.55), lineWidth: 2.5)
                    .frame(width: 188, height: 188)
                Circle()
                    .stroke((model.bypassOn ? Theme.green : Theme.accent).opacity(0.22), lineWidth: 1)
                    .frame(width: 174, height: 174)
                if model.busy {
                    TimelineView(.animation) { context in
                        let turn = context.date.timeIntervalSinceReferenceDate
                            .truncatingRemainder(dividingBy: 1.1) / 1.1
                        Circle()
                            .trim(from: 0, to: 0.28)
                            .stroke(Theme.ring, style: StrokeStyle(lineWidth: 3, lineCap: .round))
                            .frame(width: 188, height: 188)
                            .rotationEffect(.degrees(turn * 360))
                    }
                }
                Circle()
                    .fill(Color(red: 0.04, green: 0.04, blue: 0.09))
                    .frame(width: 156, height: 156)
                VStack(spacing: 2) {
                    Image(systemName: "power")
                        .font(.system(size: 28, weight: .medium))
                        .foregroundStyle(model.bypassOn ? Theme.green : Theme.ring)
                        .padding(.bottom, 6)
                    Text(buttonTitle)
                        .font(.system(size: 13, weight: .semibold))
                    buttonSubtitle
                }
                .foregroundStyle(Color(white: 0.85))
            }
            .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .disabled(model.busy)
        .opacity(model.busy ? 0.85 : 1)
    }

    private var buttonTitle: String {
        if model.busy { return model.bypassOn ? "Останавливаю" : "Запускаю" }
        if model.bypassOn { return "Подключено" }
        return "Починить"
    }

    @ViewBuilder
    private var buttonSubtitle: some View {
        if model.busy {
            Text("обход").font(.system(size: 13, weight: .semibold))
        } else if let since = model.connectedSince {
            TimelineView(.periodic(from: since, by: 1)) { context in
                Text(Self.clock(since: since, now: context.date))
                    .font(.system(size: 13, weight: .semibold))
            }
        } else {
            Text("интернет").font(.system(size: 13, weight: .semibold))
        }
    }

    private var speedColumn: some View {
        VStack(alignment: .leading, spacing: 14) {
            metric(icon: "arrow.down", title: "Входящий", value: model.downloadText, unit: "Мбит/с")
            metric(icon: "arrow.up", title: "Исходящий", value: model.uploadText, unit: "Мбит/с")
            HStack(alignment: .center, spacing: 6) {
                metric(icon: "hourglass", title: "Пинг", value: model.pingText, unit: "мс")
                Button(action: model.measureLink) {
                    Image(systemName: "arrow.clockwise")
                        .font(.system(size: 11, weight: .bold))
                        .foregroundStyle(Theme.muted)
                        .frame(width: 22, height: 22)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .disabled(model.measuringLink)
                .opacity(model.measuringLink ? 0.4 : 0.7)
                .help("Измерить пинг и скорость")
            }
        }
        .fixedSize(horizontal: true, vertical: false)
        .padding(.trailing, 4)
    }

    private func metric(icon: String, title: String, value: String, unit: String) -> some View {
        HStack(alignment: .center, spacing: 10) {
            Image(systemName: icon)
                .font(.system(size: 14, weight: .medium))
                .foregroundStyle(Color(white: 0.67))
                .frame(width: 18)
            VStack(alignment: .leading, spacing: 1) {
                Text(title)
                    .font(.system(size: 11))
                    .foregroundStyle(Theme.dim)
                HStack(alignment: .firstTextBaseline, spacing: 4) {
                    Text(value)
                        .font(.system(size: 18, weight: .semibold))
                        .foregroundStyle(Theme.text)
                        .lineLimit(1)
                    Text(unit)
                        .font(.system(size: 11))
                        .foregroundStyle(Theme.dim)
                        .lineLimit(1)
                }
            }
            .fixedSize()
        }
    }

    private var progress: some View {
        Capsule()
            .fill(Color(white: 0.16))
            .frame(height: 3)
            .overlay(alignment: .leading) {
                Capsule()
                    .fill(model.bypassOn ? Theme.green : Theme.accent)
                    .frame(maxWidth: model.busy ? 220 : (model.bypassOn ? .infinity : 0))
            }
            .padding(.bottom, 10)
    }

    private var logHeader: some View {
        HStack {
            Text("Лог")
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(Theme.text)
            Spacer()
            Button("Скопировать", action: model.copyLogs)
                .buttonStyle(.plain)
                .foregroundStyle(Theme.muted)
            Button("Очистить", action: model.clearLogs)
                .buttonStyle(.plain)
                .foregroundStyle(Theme.muted)
        }
        .font(.system(size: 12))
        .padding(.bottom, 8)
    }

    private var log: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 3) {
                    if model.logs.isEmpty {
                        Text("Журнал появится после запуска")
                            .font(.system(size: 12, design: .monospaced))
                            .foregroundStyle(Theme.dim)
                    }
                    ForEach(model.logs) { line in
                        Text(Self.line(line))
                            .font(.system(size: 12, design: .monospaced))
                            .foregroundStyle(line.level == "ok" ? Theme.green : Theme.level(line.level))
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .id(line.id)
                    }
                }
                .padding(12)
            }
            .background(Color.black.opacity(0.28))
            .overlay(RoundedRectangle(cornerRadius: 12).stroke(Theme.border, lineWidth: 1))
            .clipShape(RoundedRectangle(cornerRadius: 12))
            .onReceive(model.$logs) { logs in
                if let last = logs.last {
                    proxy.scrollTo(last.id, anchor: .bottom)
                }
            }
        }
    }

    private static func line(_ line: LogLine) -> String {
        let formatter = DateFormatter()
        formatter.dateFormat = "HH:mm:ss"
        let mark = line.level == "ok" ? "[OK] " : "• "
        return "[\(formatter.string(from: line.time))] \(mark)\(line.text)"
    }

    private static func clock(since: Date, now: Date) -> String {
        let seconds = max(0, Int(now.timeIntervalSince(since)))
        return String(format: "%02d:%02d", seconds / 60, seconds % 60)
    }
}

struct ServicesPage: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            drawerHeader("Управление сервисами")
            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    serviceCard(
                        letter: "Z",
                        color: Theme.accent,
                        title: "Zapret",
                        running: model.zapretRunning,
                        actionTitle: model.zapretRunning ? "Закрыть" : "Запустить",
                        stop: model.zapretRunning,
                        action: { model.zapretRunning ? model.stopFromServices() : model.toggle() }
                    )
                    serviceCard(
                        letter: "T",
                        color: Color(red: 0.20, green: 0.55, blue: 0.95),
                        title: "tg-ws-proxy",
                        running: model.tgRunning,
                        actionTitle: model.tgRunning ? "Закрыть" : "Запустить",
                        stop: model.tgRunning,
                        action: { model.tgRunning ? model.closeTelegram() : model.openTelegram() }
                    )
                    Text("Настройка Zapret")
                        .font(.system(size: 12))
                        .foregroundStyle(Theme.muted)
                        .padding(.top, 4)
                    Text("Выбранная стратегия: \(model.strategyName(model.settings.strategyId))")
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(Theme.text)
                    Button { model.section = .strategies } label: {
                        Label("Выбрать стратегию", systemImage: "gearshape")
                    }
                    .buttonStyle(FillButton(wide: true))
                    .disabled(model.busy)
                    Button { model.scan() } label: {
                        Label("Начать сканирование", systemImage: "magnifyingglass")
                    }
                    .buttonStyle(OutlineButton())
                    .disabled(model.busy)
                    Button { model.section = .dns } label: {
                        Label("Настройка DNS", systemImage: "network")
                    }
                    .buttonStyle(OutlineButton())
                    Divider().overlay(Theme.border)
                    if model.zapretVersion.isEmpty && model.tgVersion.isEmpty {
                        Text("Компоненты ещё не скачаны")
                            .font(.system(size: 13, weight: .semibold))
                            .foregroundStyle(Theme.muted)
                    } else {
                        Label("Компоненты на месте", systemImage: "checkmark")
                            .font(.system(size: 13, weight: .semibold))
                            .foregroundStyle(Theme.green)
                    }
                    versionRow("Zapret", model.zapretVersion)
                    versionRow("TgWsProxy", model.tgVersion)
                    Button { model.updateComponents() } label: {
                        Label("Обновить компоненты", systemImage: "arrow.clockwise")
                    }
                    .buttonStyle(OutlineButton())
                    .disabled(model.busy)
                }
                .padding(16)
            }
        }
    }

    private func serviceCard(
        letter: String,
        color: Color,
        title: String,
        running: Bool,
        actionTitle: String,
        stop: Bool,
        action: @escaping () -> Void
    ) -> some View {
        HStack(spacing: 12) {
            Text(letter)
                .font(.system(size: 16, weight: .bold))
                .foregroundStyle(.white)
                .frame(width: 36, height: 36)
                .background(color)
                .clipShape(RoundedRectangle(cornerRadius: 8))
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundStyle(Theme.text)
                HStack(spacing: 6) {
                    Circle().fill(running ? Theme.green : Theme.muted).frame(width: 7, height: 7)
                    Text(running ? "Запущен" : "Не запущен")
                        .font(.system(size: 12))
                        .foregroundStyle(running ? Theme.green : Theme.muted)
                }
            }
            Spacer()
            Button(actionTitle, action: action)
                .buttonStyle(FillButton(color: stop ? Theme.red : Theme.accent))
                .disabled(model.busy)
        }
        .padding(12)
        .background(Theme.card)
        .clipShape(RoundedRectangle(cornerRadius: 12))
    }

    private func versionRow(_ name: String, _ version: String) -> some View {
        HStack(spacing: 6) {
            Text("\(name): \(version.isEmpty ? "нет" : version)")
                .font(.system(size: 13))
                .foregroundStyle(Theme.text)
            if !version.isEmpty {
                Image(systemName: "checkmark")
                    .font(.system(size: 11, weight: .bold))
                    .foregroundStyle(Theme.green)
            }
        }
    }
}

struct ModsPageView: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            pageHeader(model.modsPage == .hub ? "Модификации" : modsTitle) {
                if model.modsPage == .hub {
                    model.section = .home
                } else {
                    model.modsPage = .hub
                }
            }
            switch model.modsPage {
            case .hub:
                hub
            case .strategies:
                strategies
            case .lists:
                ModColumn(
                    title: "Листы доменов",
                    hint: "Попадут в hostlist при следующем запуске обхода.",
                    names: model.listMods,
                    active: model.settings.activeListMods,
                    selected: model.selectedList,
                    onSelect: model.selectList,
                    onToggle: model.toggleList,
                    onCreate: { model.createMod(kind: .list, name: $0) },
                    onImport: { model.importMod(kind: .list) }
                )
                .onAppear { model.focus(.list) }
            case .hosts:
                ModColumn(
                    title: "Hosts",
                    hint: "Активные моды записываются в /etc/hosts между метками NetFix. Остальной файл сохраняется.",
                    names: model.hostMods,
                    active: model.settings.activeHostsMods,
                    selected: model.selectedHost,
                    onSelect: model.selectHost,
                    onToggle: model.toggleHost,
                    onCreate: { model.createMod(kind: .hosts, name: $0) },
                    onImport: { model.importMod(kind: .hosts) },
                    applyTitle: "Применить к hosts",
                    onApply: model.applyHosts
                )
                .onAppear { model.focus(.hosts) }
            }
            Spacer(minLength: 0)
        }
        .padding(24)
        .background(Theme.bg)
    }

    private var modsTitle: String {
        switch model.modsPage {
        case .hub: return "Модификации"
        case .strategies: return "Стратегии tpws"
        case .lists: return "Листы доменов"
        case .hosts: return "Hosts"
        }
    }

    private var hub: some View {
        HStack(alignment: .top, spacing: 16) {
            VStack(alignment: .leading, spacing: 10) {
                Label("Важная информация", systemImage: "exclamationmark.triangle.fill")
                    .font(.system(size: 13, weight: .bold))
                    .foregroundStyle(Theme.yellow)
                Text("На macOS вместо bat-файлов Windows используются стратегии tpws, листы доменов и правки hosts. Игры через UDP этот обход не затрагивает: tpws видит только TCP 80 и 443.")
                    .font(.system(size: 13))
                    .foregroundStyle(Theme.muted)
                    .fixedSize(horizontal: false, vertical: true)
                Text("Как включить мод: создайте лист или hosts, отметьте его и запустите обход. Стратегию выбирают здесь или в панели «Сервисы».")
                    .font(.system(size: 13))
                    .foregroundStyle(Theme.text)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(16)
            .frame(maxWidth: .infinity, alignment: .topLeading)
            .background(Theme.card)
            .overlay(RoundedRectangle(cornerRadius: 16).stroke(Theme.yellow.opacity(0.7), lineWidth: 1.5))
            .clipShape(RoundedRectangle(cornerRadius: 16))

            LazyVGrid(columns: [GridItem(.flexible(), spacing: 12), GridItem(.flexible(), spacing: 12)], spacing: 12) {
                hubTile("Стратегии tpws", "Запуск Zapret", "arrow.triangle.branch", Theme.accent) {
                    model.modsPage = .strategies
                }
                hubTile("Листы доменов", "Списки сайтов", "list.bullet.rectangle", Theme.green) {
                    model.modsPage = .lists
                }
                hubTile("Hosts", "Локальные записи", "folder", Theme.ring) {
                    model.modsPage = .hosts
                }
                hubTile("Редактор", "Правка списков", "pencil", Color.orange) {
                    model.modsPage = .lists
                }
            }
            .frame(maxWidth: .infinity)
        }
    }

    private func hubTile(_ title: String, _ subtitle: String, _ icon: String, _ color: Color, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            VStack(spacing: 10) {
                Image(systemName: icon)
                    .font(.system(size: 22, weight: .semibold))
                    .foregroundStyle(.white)
                    .frame(width: 54, height: 54)
                    .background(color)
                    .clipShape(RoundedRectangle(cornerRadius: 14))
                Text(title)
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundStyle(Theme.text)
                Text(subtitle)
                    .font(.system(size: 12))
                    .foregroundStyle(Theme.muted)
            }
            .frame(maxWidth: .infinity, minHeight: 160)
            .background(Theme.card)
            .overlay(RoundedRectangle(cornerRadius: 16).stroke(color.opacity(0.45), lineWidth: 1))
            .clipShape(RoundedRectangle(cornerRadius: 16))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }

    private var strategies: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("Активная: \(model.strategyName(model.settings.strategyId))")
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundStyle(Theme.text)
                Spacer()
                Button("Переименовать") { model.renameDraft = model.strategyName(model.settings.strategyId); model.renamingStrategy = true }
                    .buttonStyle(OutlineButton())
                Button("Подобрать") { model.scan() }
                    .buttonStyle(FillButton())
                    .disabled(model.busy)
            }
            ScrollView {
                VStack(spacing: 8) {
                    ForEach(model.strategies()) { strategy in
                        strategyRow(strategy)
                    }
                }
            }
            VStack(alignment: .leading, spacing: 8) {
                Text("Своя стратегия tpws")
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(Theme.text)
                TextField("Название", text: $model.strategyTitle)
                    .textFieldStyle(.plain)
                    .padding(8)
                    .background(Theme.field)
                    .clipShape(RoundedRectangle(cornerRadius: 8))
                TextField("Аргументы, например --split-pos=1 --disorder", text: $model.strategyArgs)
                    .textFieldStyle(.plain)
                    .font(.system(size: 12, design: .monospaced))
                    .padding(8)
                    .background(Theme.field)
                    .clipShape(RoundedRectangle(cornerRadius: 8))
                Button("Добавить стратегию") { model.addStrategy() }
                    .buttonStyle(FillButton())
            }
            .alert("Название стратегии", isPresented: $model.renamingStrategy) {
                TextField("Название", text: $model.renameDraft)
                Button("Сохранить") { model.renameSelectedStrategy() }
                Button("Отмена", role: .cancel) {}
            }
            if !model.settings.lastScan.isEmpty {
                Text("Последний подбор")
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(Theme.text)
                ForEach(model.settings.lastScan) { row in
                    Text("\(model.strategyName(row.id)): \(row.ok)/\(row.ok + row.fail), \(row.ms) мс")
                        .font(.system(size: 12, design: .monospaced))
                        .foregroundStyle(row.ok > 0 ? Theme.green : Theme.yellow)
                }
            }
        }
    }

    private func strategyRow(_ strategy: TpwsStrategy) -> some View {
        let selected = model.settings.strategyId == strategy.id
        return Button { model.selectStrategy(strategy.id) } label: {
            HStack(alignment: .top, spacing: 10) {
                Image(systemName: model.settings.favoriteStrategies.contains(strategy.id) ? "star.fill" : "star")
                    .foregroundStyle(Theme.yellow)
                    .onTapGesture { model.toggleFavorite(strategy.id) }
                VStack(alignment: .leading, spacing: 3) {
                    Text(strategy.title)
                        .font(.system(size: 14, weight: .semibold))
                        .foregroundStyle(selected ? Theme.green : Theme.text)
                    Text(strategy.detail)
                        .font(.system(size: 12))
                        .foregroundStyle(Theme.muted)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Spacer()
                if strategy.id.hasPrefix("user-") {
                    Image(systemName: "trash")
                        .foregroundStyle(Theme.red)
                        .onTapGesture { model.deleteStrategy(strategy.id) }
                }
                if selected {
                    Image(systemName: "checkmark")
                        .foregroundStyle(Theme.accent)
                }
            }
            .padding(12)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(selected ? Theme.accent.opacity(0.16) : Theme.card)
            .overlay(RoundedRectangle(cornerRadius: 12).stroke(selected ? Theme.accent : Theme.border, lineWidth: 1))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}

struct DnsPage: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 8) {
                Button { model.section = .services } label: {
                    Image(systemName: "chevron.left")
                        .font(.system(size: 13, weight: .bold))
                        .foregroundStyle(Theme.muted)
                        .frame(width: 28, height: 28)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                Text("Настройка DNS")
                    .font(.system(size: 16, weight: .bold))
                    .foregroundStyle(.white)
                Spacer()
                Button { model.section = .home } label: {
                    Image(systemName: "xmark")
                        .font(.system(size: 12, weight: .bold))
                        .foregroundStyle(Theme.muted)
                        .frame(width: 28, height: 28)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
            .padding(.horizontal, 16)
            .frame(height: 52)
            .overlay(alignment: .bottom) { Rectangle().fill(Theme.border).frame(height: 1) }
            ScrollView {
                VStack(spacing: 8) {
                    Text(model.dnsServiceName.isEmpty ? "Сетевая служба" : model.dnsServiceName)
                        .font(.system(size: 12))
                        .foregroundStyle(Theme.muted)
                        .frame(maxWidth: .infinity, alignment: .leading)
                    ForEach(DnsService.presets) { preset in
                        dnsCard(preset)
                    }
                    customCard
                }
                .padding(16)
            }
        }
        .onAppear { model.loadDns() }
    }

    private func dnsCard(_ preset: DnsPreset) -> some View {
        let active = isActive(preset)
        return Button { model.applyDns(servers: preset.servers) } label: {
            HStack(alignment: .top, spacing: 10) {
                Text(String(preset.name.prefix(1)))
                    .font(.system(size: 14, weight: .bold))
                    .foregroundStyle(.white)
                    .frame(width: 28, height: 28)
                    .background(active ? Theme.green : Theme.accent.opacity(0.8))
                    .clipShape(Circle())
                VStack(alignment: .leading, spacing: 3) {
                    HStack(spacing: 8) {
                        Text(preset.name)
                            .font(.system(size: 14, weight: .semibold))
                            .foregroundStyle(Theme.text)
                        if active {
                            Text("активен")
                                .font(.system(size: 10, weight: .bold))
                                .foregroundStyle(Theme.green)
                                .padding(.horizontal, 6)
                                .padding(.vertical, 2)
                                .background(Theme.green.opacity(0.15))
                                .clipShape(Capsule())
                        }
                    }
                    Text(preset.detail)
                        .font(.system(size: 12))
                        .foregroundStyle(Theme.muted)
                        .multilineTextAlignment(.leading)
                    if !preset.servers.isEmpty {
                        Text(preset.servers.joined(separator: "   "))
                            .font(.system(size: 11, design: .monospaced))
                            .foregroundStyle(Theme.dim)
                    }
                }
                Spacer(minLength: 0)
            }
            .padding(12)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Theme.card)
            .overlay(RoundedRectangle(cornerRadius: 12).stroke(active ? Theme.green : Theme.border, lineWidth: active ? 1.5 : 1))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(model.busy)
    }

    private var customCard: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Свой DNS")
                .font(.system(size: 14, weight: .semibold))
                .foregroundStyle(Theme.text)
            field("Основной", text: Binding(
                get: { model.settings.customDnsPrimary },
                set: { model.settings.customDnsPrimary = $0; SettingsStore.save(model.settings) }
            ))
            field("Запасной", text: Binding(
                get: { model.settings.customDnsSecondary },
                set: { model.settings.customDnsSecondary = $0; SettingsStore.save(model.settings) }
            ))
            Button("Применить свой DNS") { model.applyCustomDns() }
                .buttonStyle(FillButton(wide: true))
                .disabled(model.busy)
            Button { model.applyDns(servers: []) } label: {
                Label("Сбросить все настройки DNS", systemImage: "xmark")
            }
            .buttonStyle(OutlineButton())
            .disabled(model.busy)
        }
        .padding(12)
        .background(Theme.card)
        .clipShape(RoundedRectangle(cornerRadius: 12))
    }

    private func field(_ title: String, text: Binding<String>) -> some View {
        TextField(title, text: text)
            .textFieldStyle(.plain)
            .font(.system(size: 13))
            .foregroundStyle(Theme.text)
            .padding(8)
            .background(Theme.field)
            .overlay(RoundedRectangle(cornerRadius: 8).stroke(Theme.border, lineWidth: 1))
    }

    private func isActive(_ preset: DnsPreset) -> Bool {
        if preset.servers.isEmpty { return model.dnsSummary == "DHCP" }
        return model.dnsServers == preset.servers
    }
}

struct ModColumn: View {
    @EnvironmentObject private var model: AppModel
    var title: String
    var hint: String
    var names: [String]
    var active: [String]
    var selected: String?
    var onSelect: (String) -> Void
    var onToggle: (String) -> Void
    var onCreate: (String) -> Void
    var onImport: () -> Void
    var applyTitle: String?
    var onApply: (() -> Void)?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(title)
                .font(.system(size: 16, weight: .semibold))
                .foregroundStyle(Theme.text)
            Text(hint)
                .font(.system(size: 12))
                .foregroundStyle(Theme.muted)
                .fixedSize(horizontal: false, vertical: true)
            HStack {
                Button("Новый") { model.creatingMod = true }
                    .buttonStyle(FillButton())
                Button("Импорт") { onImport() }
                    .buttonStyle(FillButton())
                Button("Экспорт") { model.exportSelectedMod() }
                    .buttonStyle(OutlineButton())
                    .disabled(selected == nil)
                if let applyTitle, let onApply {
                    Button(applyTitle) { onApply() }
                        .buttonStyle(FillButton(color: Theme.green))
                        .disabled(model.busy)
                }
            }
            HStack(alignment: .top, spacing: 12) {
                modList("Доступные", names.filter { !active.contains($0) }, activate: true)
                modList("Активные", names.filter { active.contains($0) }, activate: false)
            }
            HStack {
                Button("A−") { model.editorZoom = max(0.8, model.editorZoom - 0.15) }
                    .buttonStyle(OutlineButton())
                Button("A+") { model.editorZoom = min(1.8, model.editorZoom + 0.15) }
                    .buttonStyle(OutlineButton())
                Text("Масштаб \(Int(model.editorZoom * 100))%")
                    .font(.system(size: 12))
                    .foregroundStyle(Theme.muted)
            }
            TextEditor(text: $model.editorText)
                .font(.system(size: 12 * model.editorZoom, design: .monospaced))
                .scrollContentBackground(.hidden)
                .foregroundStyle(Theme.text)
                .padding(8)
                .background(Theme.field)
                .clipShape(RoundedRectangle(cornerRadius: 8))
                .frame(minHeight: 180)
            HStack {
                Button("Сохранить") { model.saveEditor() }
                    .buttonStyle(FillButton())
                    .disabled(selected == nil)
                Button("Удалить") { model.deleteSelected() }
                    .buttonStyle(FillButton(color: Theme.red))
                    .disabled(selected == nil)
            }
        }
        .alert("Новый мод", isPresented: $model.creatingMod) {
            TextField("Имя", text: $model.modDraft)
            Button("Создать") {
                onCreate(model.modDraft)
                model.modDraft = ""
            }
            Button("Отмена", role: .cancel) { model.modDraft = "" }
        }
    }

    private func modList(_ title: String, _ rows: [String], activate: Bool) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title)
                .font(.system(size: 11, weight: .bold))
                .foregroundStyle(Theme.dim)
            if rows.isEmpty {
                Text("Пусто")
                    .font(.system(size: 12))
                    .foregroundStyle(Theme.muted)
            }
            ForEach(rows, id: \.self) { name in
                HStack {
                    Button(name) { onSelect(name) }
                        .buttonStyle(.plain)
                        .foregroundStyle(selected == name ? Theme.accent : Theme.text)
                    Spacer()
                    Button(activate ? "Включить" : "Выключить") { onToggle(name) }
                        .buttonStyle(.plain)
                        .font(.system(size: 11, weight: .semibold))
                        .foregroundStyle(activate ? Theme.accent : Theme.muted)
                }
            }
        }
        .padding(8)
        .frame(maxWidth: .infinity, minHeight: 90, alignment: .topLeading)
        .background(Theme.field)
        .clipShape(RoundedRectangle(cornerRadius: 8))
    }

}

struct DiagnosticsPage: View {
    @EnvironmentObject private var model: AppModel

    private var datacenters: [ProbeResult] {
        model.probes.filter { $0.id.hasPrefix("dc") }
    }

    private var others: [ProbeResult] {
        model.probes.filter { !$0.id.hasPrefix("dc") }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            pageHeader("Диагностика") { model.section = .home }
            HStack {
                Button("Проверить снова") { model.diagnose() }
                    .buttonStyle(FillButton())
                    .disabled(model.busy)
                Button("Исправить в один клик") { model.repair() }
                    .buttonStyle(FillButton(color: Theme.green))
                    .disabled(model.busy)
            }
            Capsule()
                .fill(model.busy ? Theme.accent : (model.probes.isEmpty ? Color(white: 0.16) : Theme.accent))
                .frame(height: 3)
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    if model.probes.isEmpty {
                        Text("Нажмите «Проверить снова», чтобы посмотреть интернет, DNS, дата-центры Telegram, SNI, Discord и сайты.")
                            .foregroundStyle(Theme.muted)
                            .font(.system(size: 13))
                    }
                    if !model.advice.isEmpty {
                        sectionTitle("Что делать", icon: "lightbulb.fill", color: Theme.yellow)
                        ForEach(model.advice, id: \.self) { line in
                            Text(line)
                                .font(.system(size: 13))
                                .foregroundStyle(Theme.text)
                                .fixedSize(horizontal: false, vertical: true)
                                .padding(12)
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .background(Theme.card)
                                .clipShape(RoundedRectangle(cornerRadius: 10))
                        }
                    }
                    if !datacenters.isEmpty {
                        sectionTitle("Доступность серверов Telegram", icon: "paperplane.fill", color: Theme.accent)
                        LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 10), count: 4), spacing: 10) {
                            ForEach(datacenters) { probe in
                                probeCard(probe, compact: true)
                            }
                        }
                    }
                    if !others.isEmpty {
                        sectionTitle("Сеть и сайты", icon: "globe", color: Theme.green)
                        LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 10), count: 2), spacing: 10) {
                            ForEach(others) { probe in
                                probeCard(probe, compact: false)
                            }
                        }
                    }
                }
            }
        }
        .padding(24)
        .background(Theme.bg)
    }

    private func cardTitle(_ probe: ProbeResult) -> String {
        if probe.id.hasPrefix("dc") {
            return "DC \(probe.id.dropFirst(2))"
        }
        return probe.title
    }

    private func sectionTitle(_ title: String, icon: String, color: Color) -> some View {
        HStack(spacing: 8) {
            RoundedRectangle(cornerRadius: 2).fill(color).frame(width: 3, height: 18)
            Image(systemName: icon).foregroundStyle(color)
            Text(title)
                .font(.system(size: 15, weight: .semibold))
                .foregroundStyle(Theme.text)
        }
    }

    private func probeCard(_ probe: ProbeResult, compact: Bool) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text(cardTitle(probe))
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(Theme.text)
                Spacer()
                Circle().fill(probe.ok ? Theme.green : Theme.red).frame(width: 8, height: 8)
            }
            Text(probe.detail)
                .font(.system(size: 12))
                .foregroundStyle(probe.ok ? Theme.green : Theme.red)
                .lineLimit(compact ? 2 : 3)
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Theme.card)
        .overlay(RoundedRectangle(cornerRadius: 12).stroke(Theme.border, lineWidth: 1))
    }
}

struct FaqPage: View {
    @EnvironmentObject private var model: AppModel

    private let groups: [(String, String, String, Color, [(String, String)])] = [
        ("Telegram", "Настройка прокси и загрузка медиа", "paperplane.fill", Color(red: 0.20, green: 0.55, blue: 0.95), [
            ("Как настроить Telegram?", "После запуска TG WS Proxy откройте Telegram → Настройки → Продвинутые → Тип соединения → Прокси и добавьте MTProto: 127.0.0.1, порт и секрет из окна прокси."),
            ("Что делает кнопка?", "Ставит Zapret, если его ещё нет, обновляет списки доменов и запускает tpws с правилами PF. Если TG WS Proxy включён, открывает его.")
        ]),
        ("Discord", "Обновление и голосовые каналы", "bubble.left.and.bubble.right.fill", Color(red: 0.45, green: 0.40, blue: 0.95), [
            ("Почему сайт может не открыться?", "Часть сайтов, включая Discord и YouTube, ходит по QUIC (UDP 443). tpws видит только TCP. Блокировка QUIC в настройках заставляет клиент перейти на TCP."),
            ("Голос Discord", "Голосовые каналы идут по UDP и через tpws не проходят. Обход на macOS рассчитан на сайты и TCP 80/443.")
        ]),
        ("Общее", "YouTube, Zapret и сетевые ошибки", "lightbulb.fill", Theme.green, [
            ("Почему нужен пароль администратора?", "tpws должен работать от root и читать PF. Пароль спрашивает macOS, NetFix его не сохраняет."),
            ("Чем это отличается от Windows?", "Идея та же: одна кнопка и Zapret без VPN. На Windows используется winws и WinDivert. На Mac WinDivert нет, поэтому TCP 80/443 идёт через tpws."),
            ("Программы от root", "Их трафик не заворачивается в tpws: иначе PF зациклится. Обычные приложения это не затрагивает."),
            ("Gatekeeper", "Если macOS блокирует tpws или TG WS Proxy, подтвердите запуск в Системных настройках → Конфиденциальность и безопасность."),
            ("Закрыл окно, а обход ещё работает", "Так и задумано. Остановка — повторное нажатие кнопки. Значок NetFix остаётся в строке меню.")
        ])
    ]

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                pageHeader("Частые вопросы") { model.section = .home }
                ForEach(groups, id: \.0) { group in
                    groupCard(group)
                }
                VStack(alignment: .leading, spacing: 8) {
                    Text("Канал проекта")
                        .font(.system(size: 11, weight: .bold))
                        .foregroundStyle(Theme.green)
                        .padding(.horizontal, 8)
                        .padding(.vertical, 3)
                        .background(Theme.green.opacity(0.18))
                        .clipShape(Capsule())
                    Text("Новые обходы в Telegram")
                        .font(.system(size: 18, weight: .bold))
                        .foregroundStyle(.white)
                    Text("Там публикуют рабочие стратегии, обновления Zapret и TG WS Proxy.")
                        .font(.system(size: 13))
                        .foregroundStyle(Theme.green.opacity(0.85))
                    Button("Открыть канал") { model.openURL("https://t.me/NetFixRuBi") }
                        .buttonStyle(.plain)
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(Theme.green)
                }
                .padding(16)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(Theme.green.opacity(0.14))
                .overlay(alignment: .leading) { Rectangle().fill(Theme.green).frame(width: 3) }
                .clipShape(RoundedRectangle(cornerRadius: 12))
                VStack(alignment: .leading, spacing: 8) {
                    Text("Не нашли решение своей проблемы?")
                        .font(.system(size: 15, weight: .bold))
                        .foregroundStyle(Theme.text)
                    Text("Вбейте текст ошибки в поиск. Если ничего не помогло, опишите проблему в Issues репозитория NetFix.")
                        .font(.system(size: 13))
                        .foregroundStyle(Theme.muted)
                        .fixedSize(horizontal: false, vertical: true)
                    Button("Открыть Issues") { model.openURL("https://github.com/rupleide/NetFix/issues") }
                        .buttonStyle(.plain)
                        .foregroundStyle(Theme.accent)
                        .font(.system(size: 13))
                }
                .padding(16)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(Theme.card)
                .clipShape(RoundedRectangle(cornerRadius: 12))
            }
            .padding(24)
        }
        .background(Theme.bg)
    }

    private func groupCard(_ group: (String, String, String, Color, [(String, String)])) -> some View {
        let open = model.openFaq == group.0
        return VStack(alignment: .leading, spacing: 10) {
            Button { model.toggleFaq(group.0) } label: {
                HStack(spacing: 12) {
                    Image(systemName: group.2)
                        .font(.system(size: 16, weight: .semibold))
                        .foregroundStyle(.white)
                        .frame(width: 36, height: 36)
                        .background(group.3)
                        .clipShape(RoundedRectangle(cornerRadius: 10))
                    VStack(alignment: .leading, spacing: 2) {
                        Text(group.0)
                            .font(.system(size: 15, weight: .semibold))
                            .foregroundStyle(Theme.text)
                        Text(group.1)
                            .font(.system(size: 12))
                            .foregroundStyle(Theme.muted)
                    }
                    Spacer()
                    Image(systemName: open ? "chevron.down" : "chevron.right")
                        .font(.system(size: 12, weight: .bold))
                        .foregroundStyle(Theme.muted)
                        .frame(width: 28, height: 28)
                        .background(Color.white.opacity(0.05))
                        .clipShape(Circle())
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            if open {
                ForEach(group.4, id: \.0) { item in
                    VStack(alignment: .leading, spacing: 4) {
                        Text(item.0)
                            .font(.system(size: 13, weight: .semibold))
                            .foregroundStyle(Theme.text)
                        Text(item.1)
                            .font(.system(size: 13))
                            .foregroundStyle(Theme.muted)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
            }
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Theme.card)
        .overlay(alignment: .leading) { Rectangle().fill(group.3).frame(width: 3) }
        .clipShape(RoundedRectangle(cornerRadius: 12))
    }
}

struct SettingsPage: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            drawerHeader("Настройки")
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    if !model.notices.isEmpty {
                        sectionLabel("ОБНОВЛЕНИЯ")
                        ForEach(model.notices) { notice in
                            VStack(alignment: .leading, spacing: 4) {
                                Text(notice.title)
                                    .font(.system(size: 13, weight: .semibold))
                                    .foregroundStyle(Theme.text)
                                Text(notice.detail)
                                    .font(.system(size: 12))
                                    .foregroundStyle(Theme.muted)
                                if let url = notice.url {
                                    Button("Открыть") { model.openURL(url) }
                                        .buttonStyle(.plain)
                                        .font(.system(size: 12, weight: .semibold))
                                        .foregroundStyle(Theme.accent)
                                }
                            }
                        }
                    }
                    sectionLabel("ФАЙЛЫ")
                    pathRow("Zapret (tpws)", model.settings.customTpwsPath.isEmpty ? Paths.tpws.path : model.settings.customTpwsPath)
                    pathRow("TG WS Proxy", model.settings.customTgAppPath.isEmpty ? Paths.apps.path : model.settings.customTgAppPath)
                    HStack {
                        Button("Свой tpws") { model.browseTpws() }
                            .buttonStyle(OutlineButton())
                        Button("Свой TG WS Proxy") { model.browseTelegramApp() }
                            .buttonStyle(OutlineButton())
                    }
                    Button("Вернуть встроенные пути") { model.clearCustomPaths() }
                        .buttonStyle(OutlineButton())
                    Button("Открыть папку NetFix") { model.openSupport() }
                        .buttonStyle(OutlineButton())
                    sectionLabel("АВТОЗАПУСК")
                    toggle("TG WS Proxy вместе с обходом", model.isOn(\.enableTgWsProxy))
                    toggle("NetFix при входе в систему", Binding(
                        get: { model.settings.autostartApp },
                        set: { model.setAutostart($0) }
                    ))
                    toggle("Сворачивать в меню при запуске", model.isOn(\.startMinimized))
                    sectionLabel("ПРОЧЕЕ")
                    toggle("Запускать Zapret", model.isOn(\.enableZapret))
                    toggle("Обход только по спискам доменов", model.isOn(\.useHostlist))
                    toggle("Блокировать QUIC (UDP 443)", model.isOn(\.blockQuic))
                    toggle("Предлагать поиграть при проверке", model.isOn(\.showGameOffer))
                    Button("Проверить обновления") { model.checkUpdates() }
                        .buttonStyle(OutlineButton())
                    Button("Экспорт настроек") { model.exportSettings() }
                        .buttonStyle(OutlineButton())
                    Button("Импорт настроек") { model.importSettings() }
                        .buttonStyle(OutlineButton())
                    Button("Сбросить все настройки") { model.resetSettings() }
                        .buttonStyle(FillButton(color: Theme.red, wide: true))
                    Text("NetFix \(AppInfo.version) для macOS")
                        .font(.system(size: 12))
                        .foregroundStyle(Theme.dim)
                    sectionLabel("TELEGRAM-КАНАЛ ПРОЕКТА")
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Там появляются новые обходы, обновления Zapret, TG WS Proxy и самого NetFix.")
                            .font(.system(size: 13))
                            .foregroundStyle(Theme.green.opacity(0.9))
                            .fixedSize(horizontal: false, vertical: true)
                        Button("Открыть канал") { model.openURL("https://t.me/NetFixRuBi") }
                            .buttonStyle(.plain)
                            .foregroundStyle(Theme.green)
                            .font(.system(size: 13, weight: .semibold))
                    }
                    .padding(12)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(Theme.green.opacity(0.12))
                    .overlay(RoundedRectangle(cornerRadius: 12).stroke(Theme.green.opacity(0.45), lineWidth: 1))
                }
                .padding(16)
                .toggleStyle(.switch)
            }
        }
    }

    private func sectionLabel(_ title: String) -> some View {
        Text(title)
            .font(.system(size: 11, weight: .bold))
            .foregroundStyle(Theme.dim)
            .padding(.top, 4)
    }

    private func pathRow(_ title: String, _ path: String) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title)
                .font(.system(size: 12))
                .foregroundStyle(Theme.muted)
            Text(path)
                .font(.system(size: 12, design: .monospaced))
                .foregroundStyle(Theme.text)
                .lineLimit(2)
                .textSelection(.enabled)
        }
    }

    private func toggle(_ title: String, _ binding: Binding<Bool>) -> some View {
        Toggle(title, isOn: binding)
            .font(.system(size: 13))
            .foregroundStyle(Theme.text)
            .tint(Theme.accent)
    }
}

private func drawerHeader(_ title: String) -> some View {
    DrawerHeader(title: title)
}

private struct DrawerHeader: View {
    @EnvironmentObject private var model: AppModel
    var title: String

    var body: some View {
        HStack {
            Text(title)
                .font(.system(size: 16, weight: .bold))
                .foregroundStyle(.white)
            Spacer()
            Button { model.closePanel() } label: {
                Image(systemName: "xmark")
                    .font(.system(size: 12, weight: .bold))
                    .foregroundStyle(Theme.muted)
                    .frame(width: 28, height: 28)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
        }
        .padding(.horizontal, 16)
        .frame(height: 52)
        .overlay(alignment: .bottom) { Rectangle().fill(Theme.border).frame(height: 1) }
    }
}

func pageHeader(_ title: String, back: @escaping () -> Void) -> some View {
    PageHeader(title: title, back: back)
}

private struct PageHeader: View {
    var title: String
    var back: () -> Void

    var body: some View {
        HStack(spacing: 12) {
            Button(action: back) {
                HStack(spacing: 4) {
                    Image(systemName: "chevron.left")
                    Text("Назад")
                }
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(Theme.text)
                .padding(.horizontal, 10)
                .padding(.vertical, 6)
                .background(Color.white.opacity(0.06))
                .clipShape(RoundedRectangle(cornerRadius: 8))
            }
            .buttonStyle(.plain)
            Text(title)
                .font(.system(size: 20, weight: .bold))
                .foregroundStyle(.white)
            Spacer()
        }
    }
}
