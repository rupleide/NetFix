import AppKit
import SwiftUI
import UniformTypeIdentifiers

struct GamePage: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var game: RhythmGame

    private let laneColors: [Color] = [
        Color(red: 0xf4 / 255, green: 0x3f / 255, blue: 0x5e / 255),
        Color(red: 0xf5 / 255, green: 0x9e / 255, blue: 0x0b / 255),
        Theme.green,
        Color(red: 0x63 / 255, green: 0x66 / 255, blue: 0xf1 / 255)
    ]

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if game.screen != .play {
                pageHeader(headerTitle) { back() }
                    .padding(.horizontal, 24)
                    .padding(.top, 18)
                    .overlay(alignment: .trailing) {
                        volumeControl
                            .padding(.trailing, 24)
                            .padding(.top, 18)
                    }
            }
            Group {
                switch game.screen {
                case .menu: menu
                case .tracks: tracks
                case .stats: stats
                case .keys: keys
                case .editor: editor
                case .play: play
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .background(Theme.bg)
        .onDisappear { game.leave() }
    }

    private var headerTitle: String {
        switch game.screen {
        case .menu: return "Мини-игра"
        case .tracks: return "Выбор трека"
        case .stats: return "История игр"
        case .keys: return "Настройки управления"
        case .editor: return "Создать уровень"
        case .play: return game.title
        }
    }

    private func back() {
        switch game.screen {
        case .menu:
            model.section = .home
        case .play:
            game.leave()
        default:
            game.screen = .menu
        }
    }

    private var menu: some View {
        VStack(spacing: 12) {
            Text("Ритм-игра NetFix")
                .font(.system(size: 22, weight: .bold))
                .foregroundStyle(.white)
            Text("Играй под бит пока идёт сканирование")
                .font(.system(size: 13))
                .foregroundStyle(Theme.muted)
                .padding(.bottom, 12)
            menuRow("Играть", "Выбрать трек", "play.fill", Theme.ring, tint: Color(red: 0.13, green: 0.15, blue: 0.35)) {
                game.reloadLevels()
                game.screen = .tracks
            }
            menuRow("Статистика", "История и рекорды", "chart.line.uptrend.xyaxis", Theme.muted, tint: Color(white: 0.15)) {
                game.screen = .stats
            }
            menuRow("Создать уровень", "Записать свой трек", "star.fill", Theme.muted, tint: Color(white: 0.15)) {
                game.screen = .editor
            }
            menuRow("Настройки", "Управление и клавиши", "info.circle", Theme.muted, tint: Color(white: 0.15)) {
                game.screen = .keys
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private func menuRow(_ title: String, _ subtitle: String, _ icon: String, _ color: Color, tint: Color, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 12) {
                Image(systemName: icon)
                    .font(.system(size: 14, weight: .bold))
                    .foregroundStyle(color)
                    .frame(width: 34, height: 34)
                    .background(tint)
                    .clipShape(Circle())
                VStack(alignment: .leading, spacing: 2) {
                    Text(title)
                        .font(.system(size: 15, weight: .semibold))
                        .foregroundStyle(.white)
                    Text(subtitle)
                        .font(.system(size: 11))
                        .foregroundStyle(color == Theme.ring ? Theme.ring : Theme.muted)
                }
                Spacer()
            }
            .padding(.horizontal, 16)
            .frame(width: 320, height: 68)
            .background(title == "Играть" ? Theme.ring.opacity(0.16) : Theme.header)
            .overlay(RoundedRectangle(cornerRadius: 12).stroke(title == "Играть" ? Theme.ring.opacity(0.6) : Theme.border, lineWidth: 1))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }

    private var tracks: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                Text("Выбери встроенный трек NetFix или свой уровень")
                    .font(.system(size: 13))
                    .foregroundStyle(Theme.muted)
                HStack {
                    TextField("Поиск треков", text: $game.query)
                        .textFieldStyle(.plain)
                        .padding(8)
                        .background(Theme.field)
                        .clipShape(RoundedRectangle(cornerRadius: 8))
                    Button(game.sortByBpm ? "По BPM" : "По названию") { game.sortByBpm.toggle() }
                        .buttonStyle(OutlineButton())
                    Button("Папка треков") { game.openTracksFolder() }
                        .buttonStyle(OutlineButton())
                }
                Text("ZIP с notes.json из этой папки появятся в списке после возврата на экран.")
                    .font(.system(size: 12))
                    .foregroundStyle(Theme.dim)
                warning
                HStack(alignment: .top, spacing: 20) {
                    VStack(alignment: .leading, spacing: 8) {
                        sectionLabel("ТРЕКИ NETFIX")
                        ForEach(filtered(game.levels.filter(\.builtin))) { level in
                            levelCard(level)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    VStack(alignment: .leading, spacing: 8) {
                        sectionLabel("ПОЛЬЗОВАТЕЛЬСКИЕ ТРЕКИ")
                        Button("Загрузить трек") { importLevel() }
                            .buttonStyle(OutlineButton())
                        Button("Osu! режим") { importLevel() }
                            .buttonStyle(OutlineButton())
                        if game.levels.filter({ !$0.builtin }).isEmpty {
                            Text("Своих уровней пока нет. Загрузите ZIP с notes.json или карту osu!mania.")
                                .font(.system(size: 12))
                                .foregroundStyle(Theme.dim)
                        }
                        ForEach(filtered(game.levels.filter { !$0.builtin })) { level in
                            levelCard(level)
                        }
                        if !game.status.isEmpty {
                            Text(game.status)
                                .font(.system(size: 12))
                                .foregroundStyle(Theme.muted)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
            }
            .padding(24)
        }
        .onAppear { game.reloadLevels() }
    }

    private var warning: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: "exclamationmark.triangle.fill")
                .foregroundStyle(Theme.yellow)
            Text("Игра содержит мигающие эффекты и не рекомендуется людям с чувствительностью к ним. Делайте перерывы.")
                .font(.system(size: 12))
                .foregroundStyle(Theme.muted)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(12)
        .frame(maxWidth: 640, alignment: .leading)
        .background(Theme.card)
        .clipShape(RoundedRectangle(cornerRadius: 10))
    }

    private var shownResults: [GameResult] {
        let query = game.query.trimmingCharacters(in: .whitespacesAndNewlines)
        let matched = query.isEmpty ? game.results : game.results.filter { $0.title.localizedCaseInsensitiveContains(query) }
        return matched.sorted { left, right in
            if game.statsByScore, left.score != right.score { return left.score > right.score }
            return left.date > right.date
        }
    }

    private func filtered(_ levels: [LevelMap]) -> [LevelMap] {
        let query = game.query.trimmingCharacters(in: .whitespacesAndNewlines)
        let matched = query.isEmpty ? levels : levels.filter {
            $0.title.localizedCaseInsensitiveContains(query) || $0.author.localizedCaseInsensitiveContains(query)
        }
        return matched.sorted { left, right in
            if game.sortByBpm, left.bpm != right.bpm { return left.bpm < right.bpm }
            return left.title.localizedCaseInsensitiveCompare(right.title) == .orderedAscending
        }
    }

    private func levelCard(_ level: LevelMap) -> some View {
        HStack {
            Button { game.begin(level) } label: {
                VStack(alignment: .leading, spacing: 4) {
                    Text(level.title)
                        .font(.system(size: 14, weight: .semibold))
                        .foregroundStyle(.white)
                    Text("\(level.author.isEmpty ? "NetFix" : level.author) · \(Int(level.bpm)) BPM · \(level.notes.count) нот")
                        .font(.system(size: 12))
                        .foregroundStyle(Theme.muted)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            Button { game.exportLevel(level) } label: {
                Image(systemName: "square.and.arrow.up")
                    .foregroundStyle(Theme.muted)
            }
            .buttonStyle(.plain)
        }
        .padding(12)
        .background(Theme.card)
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(Theme.border, lineWidth: 1))
        .clipShape(RoundedRectangle(cornerRadius: 10))
    }

    private var stats: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 8) {
                Text("Выбери трек в списке, чтобы сыграть ещё раз")
                    .font(.system(size: 13))
                    .foregroundStyle(Theme.dim)
                HStack {
                    TextField("Поиск", text: $game.query)
                        .textFieldStyle(.plain)
                        .padding(8)
                        .background(Theme.field)
                        .clipShape(RoundedRectangle(cornerRadius: 8))
                    Button(game.statsByScore ? "По очкам" : "По дате") { game.statsByScore.toggle() }
                        .buttonStyle(OutlineButton())
                }
                if shownResults.isEmpty {
                    Text("Сыгранных партий пока нет")
                        .foregroundStyle(Theme.muted)
                        .padding(.top, 12)
                }
                ForEach(shownResults) { row in
                    HStack {
                        VStack(alignment: .leading, spacing: 3) {
                            Text(row.title)
                                .font(.system(size: 14, weight: .semibold))
                                .foregroundStyle(.white)
                            Text("\(row.score) · \(row.accuracy)% · комбо \(row.combo)")
                                .font(.system(size: 12))
                                .foregroundStyle(Theme.muted)
                        }
                        Spacer()
                        Text(row.cleared ? "Пройден" : "Срыв")
                            .font(.system(size: 12, weight: .semibold))
                            .foregroundStyle(row.cleared ? Theme.green : Theme.red)
                    }
                    .padding(12)
                    .background(Theme.card)
                    .clipShape(RoundedRectangle(cornerRadius: 10))
                }
            }
            .padding(24)
        }
    }

    private var keys: some View {
        VStack(spacing: 8) {
            Text("Нажми на клавишу и затем новую кнопку")
                .font(.system(size: 13))
                .foregroundStyle(Theme.muted)
                .padding(.bottom, 12)
            keyRow(0, "Дорожка 1 (красная)")
            keyRow(1, "Дорожка 2 (жёлтая)")
            keyRow(2, "Дорожка 3 (зелёная)")
            keyRow(3, "Дорожка 4 (синяя)")
            Text("Стрелки тоже попадают в дорожки: влево, вниз, вверх, вправо.")
                .font(.system(size: 12))
                .foregroundStyle(Theme.dim)
                .padding(.top, 8)
            Button("Сбросить по умолчанию") { game.resetKeys() }
                .buttonStyle(OutlineButton())
                .frame(width: 360)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private func keyRow(_ lane: Int, _ title: String) -> some View {
        HStack {
            Text(title)
                .font(.system(size: 13))
                .foregroundStyle(laneColors[lane])
            Spacer()
            Button(game.rebinding == lane ? "..." : game.keys[lane]) {
                game.rebinding = lane
                game.armKeys()
            }
            .buttonStyle(.plain)
            .font(.system(size: 14, weight: .semibold))
            .foregroundStyle(laneColors[lane])
            .frame(minWidth: 48)
            .padding(.vertical, 6)
            .padding(.horizontal, 12)
            .background(laneColors[lane].opacity(0.15))
            .clipShape(RoundedRectangle(cornerRadius: 6))
        }
        .padding(.horizontal, 16)
        .frame(width: 420, height: 52)
        .background(Theme.header)
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(Theme.border, lineWidth: 1))
    }

    private var editor: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 10) {
                Text("Аудиофайл")
                    .font(.system(size: 12))
                    .foregroundStyle(Theme.muted)
                HStack {
                    Text(game.editorAudioName.isEmpty ? "Файл не выбран" : game.editorAudioName)
                        .font(.system(size: 13))
                        .foregroundStyle(Theme.text)
                        .lineLimit(1)
                    Spacer()
                    Button("Выбрать") { game.pickEditorAudio() }
                        .buttonStyle(FillButton())
                }
                Text("Название трека")
                    .font(.system(size: 12))
                    .foregroundStyle(Theme.muted)
                TextField("Название", text: $game.editorTitle)
                    .textFieldStyle(.plain)
                    .padding(8)
                    .background(Theme.field)
                    .overlay(RoundedRectangle(cornerRadius: 8).stroke(Theme.border, lineWidth: 1))
                Text("BPM: 140 медленно, 200 средне, 240 и выше быстро")
                    .font(.system(size: 12))
                    .foregroundStyle(Theme.muted)
                TextField("140", text: $game.editorBpm)
                    .textFieldStyle(.plain)
                    .padding(8)
                    .background(Theme.field)
                    .overlay(RoundedRectangle(cornerRadius: 8).stroke(Theme.border, lineWidth: 1))
                Text(game.recording ? "Запись: \(game.recorded) нот. Клавиши \(game.keys.joined(separator: " ")) или стрелки." : "Как создать: выберите MP3, укажите BPM, нажмите запись и попадайте в такт.")
                    .font(.system(size: 12))
                    .foregroundStyle(Theme.muted)
                    .fixedSize(horizontal: false, vertical: true)
                if game.recording {
                    Button("Завершить запись") { game.stopRecording() }
                        .buttonStyle(FillButton(color: Theme.red, wide: true))
                } else {
                    Button("Начать запись") { game.startRecording() }
                        .buttonStyle(FillButton(wide: true))
                }
                if !game.status.isEmpty {
                    Text(game.status)
                        .font(.system(size: 12))
                        .foregroundStyle(Theme.green)
                }
            }
            .padding(24)
            .frame(maxWidth: 480, alignment: .leading)
        }
    }

    private var play: some View {
        VStack(spacing: 0) {
            HStack {
                Button { game.leave() } label: {
                    HStack(spacing: 4) {
                        Image(systemName: "chevron.left")
                        Text("Назад")
                    }
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(.white)
                }
                .buttonStyle(.plain)
                Text("\(game.title) · \(Int(game.bpm)) BPM")
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundStyle(.white)
                    .lineLimit(1)
                Spacer()
                volumeControl
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 8)
            .overlay {
                HStack(spacing: 20) {
                    hud("СЧЁТ", "\(game.score)", color: .white)
                    hud("BPM", "\(Int(game.bpm))", color: .white)
                    hud("КОМБО", "\(game.combo)x", color: Color(red: 0x63 / 255, green: 0x66 / 255, blue: 0xf1 / 255))
                    hud("ТОЧНОСТЬ", "\(game.accuracy)%", color: .white)
                }
            }
            ZStack {
                lanes
                if game.comboBurst {
                    Color.white.opacity(0.22)
                        .allowsHitTesting(false)
                }
                if let count = game.countdown {
                    Color.black.opacity(0.72)
                    Text("\(count)")
                        .font(.system(size: 80, weight: .thin))
                        .foregroundStyle(.white)
                }
                if game.finished {
                    result
                }
                if !game.judge.isEmpty && !game.finished {
                    Text(game.judge)
                        .font(.system(size: 18, weight: .bold))
                        .foregroundStyle(game.judge == "MISS" ? Theme.muted : .white)
                        .offset(y: -40)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            HStack(spacing: 8) {
                let arrows = ["←", "↓", "↑", "→"]
                ForEach(0..<4, id: \.self) { lane in
                    Text("\(game.keys[lane]) / \(arrows[lane])")
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundStyle(laneColors[lane])
                        .padding(.horizontal, 12)
                        .padding(.vertical, 6)
                        .background(laneColors[lane].opacity(0.12))
                        .clipShape(RoundedRectangle(cornerRadius: 6))
                }
            }
            .padding(.vertical, 10)
        }
        .background(Theme.bg)
    }

    private var lanes: some View {
        ZStack {
            HStack(spacing: 10) {
                ForEach(0..<4, id: \.self) { lane in
                    Rectangle()
                        .fill(laneColors[lane].opacity(game.flash[lane] ? 0.28 : 0.08))
                        .frame(width: 50)
                }
            }
            ForEach(game.notes) { note in
                if let y = game.noteY(note, height: 520) {
                    RoundedRectangle(cornerRadius: 8)
                        .stroke(laneColors[note.lane], lineWidth: 1.5)
                        .background(RoundedRectangle(cornerRadius: 8).fill(laneColors[note.lane].opacity(0.35)))
                        .frame(width: 50, height: 50)
                        .overlay(Text(game.laneArrows[note.lane]).foregroundStyle(laneColors[note.lane]))
                        .position(x: 85 + CGFloat(note.lane) * 60 + 25, y: y + 25)
                }
            }
            HStack(spacing: 10) {
                ForEach(0..<4, id: \.self) { lane in
                    RoundedRectangle(cornerRadius: 8)
                        .stroke(laneColors[lane].opacity(0.7), lineWidth: 1.5)
                        .frame(width: 50, height: 50)
                        .overlay(Text(game.laneArrows[lane]).foregroundStyle(laneColors[lane]))
                }
            }
            .frame(width: 230)
            .position(x: 200, y: 520 - 70 + 25)
        }
        .frame(width: 400, height: 520)
        .clipped()
    }

    private var result: some View {
        VStack(spacing: 10) {
            Text(game.failed ? "Срыв" : "Трек пройден")
                .font(.system(size: 22, weight: .bold))
                .foregroundStyle(.white)
            Text("\(game.score) очков · \(game.accuracy)% · комбо \(game.bestCombo)")
                .font(.system(size: 13))
                .foregroundStyle(Theme.muted)
            HStack {
                Button("Ещё раз") { game.retry() }
                    .buttonStyle(FillButton())
                Button("В меню") { game.leave() }
                    .buttonStyle(OutlineButton())
                    .frame(width: 120)
            }
        }
        .padding(20)
        .background(Theme.panel)
        .overlay(RoundedRectangle(cornerRadius: 14).stroke(Theme.border, lineWidth: 1))
        .clipShape(RoundedRectangle(cornerRadius: 14))
    }

    private var volumeControl: some View {
        HStack(spacing: 8) {
            Image(systemName: "speaker.wave.2.fill")
                .font(.system(size: 11))
                .foregroundStyle(Theme.dim)
            Slider(value: $game.volume, in: 0...1)
                .frame(width: 90)
                .controlSize(.small)
            Text("\(Int(game.volume * 100))%")
                .font(.system(size: 11))
                .foregroundStyle(Theme.muted)
                .frame(width: 32, alignment: .trailing)
        }
    }

    private func hud(_ title: String, _ value: String, color: Color) -> some View {
        VStack(spacing: 0) {
            Text(title)
                .font(.system(size: 10))
                .foregroundStyle(Theme.dim)
            Text(value)
                .font(.system(size: 16, weight: .light))
                .foregroundStyle(color)
        }
    }

    private func sectionLabel(_ title: String) -> some View {
        Text(title)
            .font(.system(size: 11, weight: .bold))
            .foregroundStyle(Theme.dim)
    }

    private func importLevel() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.zip, .json, .data]
        panel.allowsOtherFileTypes = true
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        game.importFile(url)
    }
}

