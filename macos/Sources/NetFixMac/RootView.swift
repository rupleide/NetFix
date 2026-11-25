import AppKit
import NetFixCore
import SwiftUI

struct RootView: View {
    @EnvironmentObject private var model: AppModel

    private var showsDrawer: Bool {
        switch model.section {
        case .services, .settings, .dns: return true
        default: return false
        }
    }

    var body: some View {
        VStack(spacing: 0) {
            header
            ZStack(alignment: .trailing) {
                Group {
                    if model.section == .diagnostics {
                        DiagnosticsPage()
                    } else if model.section == .faq {
                        FaqPage()
                    } else if model.section == .strategies {
                        ModsPageView()
                    } else if model.section == .game {
                        GamePage()
                    } else {
                        HomePage()
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                if showsDrawer {
                    drawer
                        .frame(width: 400)
                        .background(Theme.panel)
                        .overlay(alignment: .leading) {
                            Rectangle().fill(Theme.border).frame(width: 1)
                        }
                }
                if model.scanning && model.section != .game {
                    scanOverlay
                }
            }
            footer
        }
        .background(Theme.bg)
        .preferredColorScheme(.dark)
        .onAppear {
            model.prepare()
            styleWindow()
        }
        .task {
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 2_000_000_000)
                model.refreshStatus()
            }
        }
    }

    private var header: some View {
        HStack(spacing: 14) {
            HStack(spacing: 0) {
                Text("NetFix")
                    .font(.system(size: 18, weight: .bold))
                    .foregroundStyle(.white)
                Text("  by rupleide")
                    .font(.system(size: 11))
                    .foregroundStyle(Theme.muted)
            }
            StatusPill(title: model.online ? "Сеть" : "Нет сети", on: model.online, alert: !model.online)
                .help(model.online ? "Есть маршрут в интернет" : "Маршрут по умолчанию не найден")
            StatusPill(title: "VPN", on: model.vpnOn)
                .help("Зелёный — маршрут по умолчанию идёт через utun, ipsec или ppp. Серый — прямой интернет.")
            StatusPill(title: "Zapret", on: model.zapretRunning)
            StatusPill(title: "TgWsProxy", on: model.tgRunning)
            Spacer(minLength: 8)
            navButton("Сервисы", icon: "bolt.fill", section: .services)
            navButton("Моды", section: .strategies)
            navButton("FAQ", section: .faq)
            navButton("Диагностика", section: .diagnostics)
            navButton("Игра", section: .game)
            Button { model.open(.settings) } label: {
                Image(systemName: "gearshape")
                    .font(.system(size: 14))
                    .foregroundStyle(model.section == .settings ? .white : Theme.muted)
                    .frame(width: 28, height: 28)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
        }
        .padding(.leading, 78)
        .padding(.trailing, 16)
        .frame(height: 54)
        .background(Theme.header)
    }

    private func navButton(_ title: String, icon: String? = nil, section: AppSection) -> some View {
        Button { model.open(section) } label: {
            HStack(spacing: 6) {
                if let icon {
                    Image(systemName: icon)
                        .font(.system(size: 11, weight: .bold))
                }
                Text(title)
                    .font(.system(size: 13, weight: model.section == section ? .semibold : .regular))
            }
            .foregroundStyle(model.section == section ? .white : Theme.muted)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }

    @ViewBuilder
    private var drawer: some View {
        switch model.section {
        case .services: ServicesPage()
        case .settings: SettingsPage()
        case .dns: DnsPage()
        default: EmptyView()
        }
    }

    private var scanOverlay: some View {
        ZStack {
            Color.black.opacity(0.55)
            if model.offerGame {
                gameOffer
            } else {
                scanLog
            }
        }
    }

    private var gameOffer: some View {
        VStack(spacing: 12) {
            Image(systemName: "gamecontroller.fill")
                .font(.system(size: 36))
                .foregroundStyle(Theme.ring)
            Text("Скучно ждать?")
                .font(.system(size: 19, weight: .bold))
                .foregroundStyle(.white)
            Text("Сканирование займёт время. Хочешь поиграть, пока идёт проверка?")
                .font(.system(size: 13))
                .foregroundStyle(Theme.muted)
                .multilineTextAlignment(.center)
            Toggle("Больше не показывать", isOn: Binding(
                get: { !model.settings.showGameOffer },
                set: { hide in
                    model.settings.showGameOffer = !hide
                    SettingsStore.save(model.settings)
                }
            ))
            .toggleStyle(.switch)
            .font(.system(size: 13))
            .foregroundStyle(Theme.muted)
            .tint(Theme.accent)
            HStack(spacing: 10) {
                Button("Поиграть!") {
                    model.offerGame = false
                    model.section = .game
                }
                .buttonStyle(FillButton(color: Theme.ring))
                Button("Нет") { model.offerGame = false }
                    .buttonStyle(OutlineButton())
                    .frame(width: 90)
            }
        }
        .padding(24)
        .frame(width: 420)
        .background(Theme.panel)
        .overlay(RoundedRectangle(cornerRadius: 14).stroke(Theme.border, lineWidth: 1))
        .clipShape(RoundedRectangle(cornerRadius: 14))
    }

    private var scanLog: some View {
        VStack(alignment: .leading, spacing: 12) {
                Text("Подбор стратегии")
                    .font(.system(size: 16, weight: .bold))
                    .foregroundStyle(.white)
                ProgressView()
                    .controlSize(.small)
                    .tint(Theme.accent)
                ScrollView {
                    VStack(alignment: .leading, spacing: 4) {
                        ForEach(model.logs.suffix(14)) { line in
                            Text(line.text)
                                .font(.system(size: 12, design: .monospaced))
                                .foregroundStyle(Theme.level(line.level))
                                .frame(maxWidth: .infinity, alignment: .leading)
                        }
                    }
                }
            }
            .padding(18)
            .frame(width: 520, height: 340)
            .background(Theme.panel)
            .overlay(RoundedRectangle(cornerRadius: 14).stroke(Theme.border, lineWidth: 1))
            .clipShape(RoundedRectangle(cornerRadius: 14))
    }

    private var footer: some View {
        HStack {
            Spacer()
            Button {
                model.openURL("https://app.tonkeeper.com/transfer/UQCx8X4z86Jej2hc8l_IVni8e0Q8uDHhC8_PJ2zymxngVc2Q")
            } label: {
                HStack(spacing: 6) {
                    Image(systemName: "heart")
                        .font(.system(size: 11))
                    Text("Поддержать разработку")
                        .font(.system(size: 12))
                }
                .foregroundStyle(Theme.dim)
            }
            .buttonStyle(.plain)
        }
        .padding(.horizontal, 16)
        .frame(height: 32)
    }

    private func styleWindow() {
        DispatchQueue.main.async {
            for window in NSApp.windows where window.canBecomeMain {
                window.titlebarAppearsTransparent = true
                window.titleVisibility = .hidden
                window.backgroundColor = NSColor(srgbRed: 22 / 255, green: 22 / 255, blue: 22 / 255, alpha: 1)
                window.title = "NetFix"
                window.minSize = NSSize(width: 1040, height: 680)
                let size = NSSize(width: 1120, height: 740)
                if let screen = window.screen ?? NSScreen.main {
                    let visible = screen.visibleFrame
                    let origin = NSPoint(x: visible.midX - size.width / 2, y: visible.midY - size.height / 2)
                    window.setFrame(NSRect(origin: origin, size: size), display: true)
                }
            }
        }
    }
}

struct MenuContent: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Button("Открыть NetFix") {
            openWindow(id: "main")
            NSApp.activate(ignoringOtherApps: true)
        }
        Divider()
        Button(model.bypassOn ? "Остановить обход" : "Запустить обход") {
            model.toggle()
        }
        .disabled(model.busy)
        Menu("Стратегия") {
            ForEach(model.strategies()) { strategy in
                Button(strategy.title) { model.selectStrategy(strategy.id) }
            }
        }
        Menu("DNS") {
            ForEach(DnsService.presets) { preset in
                Button(preset.name) { model.applyDns(servers: preset.servers) }
            }
        }
        Divider()
        Text("Пинг: \(model.pingText)")
        Button("Обновить пинг") { model.measurePing() }
        Text(model.zapretRunning ? "Zapret работает" : "Zapret остановлен")
        Text(model.tgRunning ? "TG WS Proxy запущен" : "TG WS Proxy не запущен")
        Divider()
        Button("Выход") { NSApp.terminate(nil) }
    }
}
