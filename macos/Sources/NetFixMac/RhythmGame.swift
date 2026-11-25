import AppKit
import AVFoundation
import Foundation
import NetFixCore
import UniformTypeIdentifiers

enum GameScreen {
    case menu, tracks, stats, keys, editor, play
}

struct ChartNote: Identifiable, Equatable {
    var id: Int
    var time: Double
    var lane: Int
    var hit = false
    var missed = false
}

struct LevelMap: Identifiable, Equatable {
    var id: String
    var title: String
    var author: String
    var bpm: Double
    var notes: [ChartNote]
    var audioURL: URL?
    var folder: URL?
    var builtin: Bool
}

struct GameResult: Identifiable, Codable, Equatable {
    var id: UUID
    var title: String
    var score: Int
    var accuracy: Int
    var combo: Int
    var cleared: Bool
    var date: Date
}

private struct NoteFile: Codable {
    var Title: String?
    var Author: String?
    var TrackFile: String?
    var Bpm: Double?
    var Notes: [NotePoint]
}

private struct NotePoint: Codable {
    var Time: Double
    var Lane: Int
}

private struct GameDisk: Codable {
    var keys: [String]
    var history: [GameResult]
    var volume: Double?
}

@MainActor
final class RhythmGame: ObservableObject {
    @Published var screen: GameScreen = .menu
    @Published var levels: [LevelMap] = []
    @Published var results: [GameResult] = []
    @Published var keys = ["A", "S", "W", "D"]
    @Published var volume = 0.5 {
        didSet {
            player?.volume = Self.linearVolume(volume)
            if storesVolume { saveDisk() }
        }
    }
    @Published var playing = false
    @Published var countdown: Int?
    @Published var score = 0
    @Published var combo = 0
    @Published var accuracy = 100
    @Published var judge = ""
    @Published var title = ""
    @Published var bpm = 140.0
    @Published var failed = false
    @Published var finished = false
    @Published var notes: [ChartNote] = []
    @Published var clock = 0.0
    @Published var flash = [false, false, false, false]
    @Published var editorTitle = ""
    @Published var editorBpm = "140"
    @Published var editorAudioName = ""
    @Published var recording = false
    @Published var recorded = 0
    @Published var rebinding: Int?
    @Published var status = ""
    @Published var bestCombo = 0
    @Published var query = ""
    @Published var sortByBpm = false
    @Published var statsByScore = false
    @Published var comboBurst = false

    private var fall = 1.6
    private var timer: Timer?
    private var started: Date?
    private var countdownMark: Date?
    private var player: AVAudioPlayer?
    private var monitor: Any?
    private var misses = 0
    private var streak = 0
    private var hits = 0
    private var total = 0
    private var maxCombo = 0
    private var ended = false
    private var audioURL: URL?
    private var editorStarted: Date?
    private var editorNotes: [NotePoint] = []
    private var storesVolume = false
    private let arrows = ["◀", "▼", "▲", "▶"]

    var laneArrows: [String] { arrows }

    init() {
        loadDisk()
        reloadLevels()
        storesVolume = true
    }

    func reloadLevels() {
        absorbTracks()
        var rows = [builtin()]
        let root = Self.levelsRoot
        let folders = (try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil)) ?? []
        for folder in folders where folder.hasDirectoryPath {
            if let map = Self.readLevel(at: folder) {
                rows.append(map)
            }
        }
        levels = rows
    }

    func openTracksFolder() {
        try? Paths.ensure()
        NSWorkspace.shared.open(Paths.tracks)
    }

    func exportLevel(_ level: LevelMap) {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.zip]
        panel.nameFieldStringValue = "\(level.title).zip"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        let source = level.folder ?? Self.writeTemporary(level)
        try? FileManager.default.removeItem(at: url)
        let result = Shell.run("/usr/bin/ditto", ["-c", "-k", "--keepParent", source.path, url.path])
        status = result.ok ? "Уровень экспортирован" : "Не удалось собрать ZIP"
    }

    private func absorbTracks() {
        try? Paths.ensure()
        let zips = (try? FileManager.default.contentsOfDirectory(at: Paths.tracks, includingPropertiesForKeys: nil)) ?? []
        for zip in zips where zip.pathExtension.lowercased() == "zip" {
            let dest = Self.levelsRoot.appendingPathComponent("track-\(zip.deletingPathExtension().lastPathComponent)", isDirectory: true)
            if FileManager.default.fileExists(atPath: dest.appendingPathComponent("notes.json").path) { continue }
            try? FileManager.default.createDirectory(at: dest, withIntermediateDirectories: true)
            _ = Shell.run("/usr/bin/ditto", ["-x", "-k", zip.path, dest.path])
        }
    }

    private static func writeTemporary(_ level: LevelMap) -> URL {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("netfix-level-\(UUID().uuidString)", isDirectory: true)
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let file = NoteFile(
            Title: level.title,
            Author: level.author,
            TrackFile: nil,
            Bpm: level.bpm,
            Notes: level.notes.map { NotePoint(Time: $0.time, Lane: $0.lane) }
        )
        if let data = try? JSONEncoder().encode(file) {
            try? data.write(to: folder.appendingPathComponent("notes.json"))
        }
        return folder
    }

    func begin(_ level: LevelMap) {
        stopClock()
        notes = level.notes.enumerated().map {
            ChartNote(id: $0.offset, time: $0.element.time, lane: min(3, max(0, $0.element.lane)))
        }
        title = level.title
        bpm = level.bpm
        fall = Self.fallSeconds(level.bpm)
        score = 0
        combo = 0
        accuracy = 100
        misses = 0
        streak = 0
        hits = 0
        total = notes.count
        maxCombo = 0
        bestCombo = 0
        ended = false
        failed = false
        finished = false
        judge = ""
        clock = 0
        flash = [false, false, false, false]
        audioURL = level.audioURL
        screen = .play
        playing = true
        countdown = 3
        countdownMark = Date()
        started = nil
        installMonitor()
        timer = Timer.scheduledTimer(withTimeInterval: 1.0 / 60.0, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.frame() }
        }
    }

    func leave() {
        stopClock()
        playing = false
        recording = false
        countdown = nil
        if screen == .play { screen = .menu }
    }

    func retry() {
        guard let level = levels.first(where: { $0.title == title }) ?? levels.first else { return }
        begin(level)
    }

    func press(_ lane: Int) {
        if recording {
            record(lane)
            return
        }
        guard playing, countdown == nil, !ended else { return }
        guard let index = notes.indices
            .filter({ notes[$0].lane == lane && !notes[$0].hit && !notes[$0].missed })
            .min(by: { notes[$0].time < notes[$1].time }) else { return }
        let distance = abs(notes[index].time - clock)
        if distance <= 0.06 {
            register(index, base: 300, text: "PERFECT")
        } else if distance <= 0.15 {
            register(index, base: 100, text: "GOOD")
        } else {
            combo = 0
            judge = "MISS"
        }
    }

    func noteY(_ note: ChartNote, height: CGFloat) -> CGFloat? {
        guard !note.hit, !note.missed else { return nil }
        let spawn = note.time - fall
        guard clock >= spawn else { return nil }
        let progress = (clock - spawn) / fall
        let hit = height - 70
        return -50 + progress * (hit + 50)
    }

    func importFile(_ url: URL) {
        let access = url.startAccessingSecurityScopedResource()
        defer { if access { url.stopAccessingSecurityScopedResource() } }
        let ext = url.pathExtension.lowercased()
        if ext == "json" {
            let dest = Self.levelsRoot.appendingPathComponent(UUID().uuidString, isDirectory: true)
            try? FileManager.default.createDirectory(at: dest, withIntermediateDirectories: true)
            try? FileManager.default.copyItem(at: url, to: dest.appendingPathComponent("notes.json"))
            copySiblingAudio(of: url, to: dest)
            status = Self.readLevel(at: dest) == nil ? "В файле нет нот" : "Уровень добавлен"
            reloadLevels()
            return
        }
        if ext == "osu" {
            importOsu(textURL: url, folder: url.deletingLastPathComponent())
            return
        }
        let dest = Self.levelsRoot.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try? FileManager.default.createDirectory(at: dest, withIntermediateDirectories: true)
        _ = Shell.run("/usr/bin/ditto", ["-x", "-k", url.path, dest.path])
        if let osu = Self.firstFile(in: dest, ext: "osu") {
            importOsu(textURL: osu, folder: osu.deletingLastPathComponent())
            return
        }
        status = Self.readLevel(at: dest) == nil ? "В архиве нет notes.json или карты osu!" : "Уровень добавлен"
        reloadLevels()
    }

    private func copySiblingAudio(of notes: URL, to dest: URL) {
        let folder = notes.deletingLastPathComponent()
        guard let files = try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil) else { return }
        let audio = ["mp3", "m4a", "wav", "aiff", "ogg"]
        guard let match = files.first(where: { audio.contains($0.pathExtension.lowercased()) }) else { return }
        _ = Self.storeAudio(match, in: dest)
    }

    @discardableResult
    private static func storeAudio(_ source: URL, in dest: URL) -> String {
        if source.pathExtension.lowercased() == "ogg", let ffmpeg = ffmpegPath() {
            let name = source.deletingPathExtension().lastPathComponent + ".m4a"
            let output = dest.appendingPathComponent(name)
            let result = Shell.run(ffmpeg, ["-y", "-i", source.path, "-c:a", "aac", output.path], timeout: 60)
            if result.ok { return name }
        }
        let name = source.lastPathComponent
        try? FileManager.default.copyItem(at: source, to: dest.appendingPathComponent(name))
        return name
    }

    private static func ffmpegPath() -> String? {
        let candidates = ["/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg"]
        if let found = candidates.first(where: { FileManager.default.isExecutableFile(atPath: $0) }) {
            return found
        }
        let which = Shell.run("/usr/bin/which", ["ffmpeg"])
        let path = which.stdout.trimmingCharacters(in: .whitespacesAndNewlines)
        return which.ok && !path.isEmpty ? path : nil
    }

    func pickEditorAudio() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.mp3, .audio, .mpeg4Audio]
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        editorAudio = url
        editorAudioName = url.lastPathComponent
        if editorTitle.isEmpty {
            editorTitle = url.deletingPathExtension().lastPathComponent
        }
    }

    func startRecording() {
        guard editorAudio != nil else {
            status = "Сначала выберите аудиофайл"
            return
        }
        editorNotes = []
        recorded = 0
        recording = true
        editorStarted = Date()
        play(url: editorAudio)
        installMonitor()
    }

    func stopRecording() {
        recording = false
        player?.stop()
        let bpmValue = Double(editorBpm.replacingOccurrences(of: ",", with: ".")) ?? 140
        let folder = Self.levelsRoot.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        var audioName = ""
        if let source = editorAudio {
            audioName = source.lastPathComponent
            let access = source.startAccessingSecurityScopedResource()
            defer { if access { source.stopAccessingSecurityScopedResource() } }
            try? FileManager.default.copyItem(at: source, to: folder.appendingPathComponent(audioName))
        }
        let file = NoteFile(
            Title: editorTitle.isEmpty ? "Свой уровень" : editorTitle,
            Author: "Вы",
            TrackFile: audioName,
            Bpm: bpmValue,
            Notes: editorNotes
        )
        if let data = try? JSONEncoder().encode(file) {
            try? data.write(to: folder.appendingPathComponent("notes.json"))
        }
        status = "Уровень сохранён, нот: \(editorNotes.count)"
        reloadLevels()
        screen = .tracks
    }

    func armKeys() {
        installMonitor()
    }

    func bind(_ lane: Int, key: String) {
        guard keys.indices.contains(lane) else { return }
        keys[lane] = key.uppercased()
        rebinding = nil
        saveDisk()
    }

    func resetKeys() {
        keys = ["A", "S", "W", "D"]
        saveDisk()
    }

    private var editorAudio: URL?

    private func frame() {
        if let mark = countdownMark, countdown != nil {
            let left = 3 - Int(Date().timeIntervalSince(mark))
            if left > 0 {
                countdown = left
                return
            }
            countdown = nil
            countdownMark = nil
            started = Date()
            play(url: audioURL)
        }
        guard playing, countdown == nil, !ended, let started else { return }
        clock = Date().timeIntervalSince(started)
        var alive = false
        for index in notes.indices {
            if notes[index].hit || notes[index].missed { continue }
            alive = true
            let y = noteTop(notes[index])
            if y > 520 {
                notes[index].missed = true
                combo = 0
                misses += 1
                streak += 1
                judge = "MISS"
                if misses >= 10 || streak >= 10 {
                    finish(failed: true)
                    return
                }
            }
        }
        if !alive {
            finish(failed: false)
        }
    }

    private func noteTop(_ note: ChartNote) -> Double {
        let progress = (clock - (note.time - fall)) / fall
        return -50 + progress * (500 - 70 + 50)
    }

    private func register(_ index: Int, base: Int, text: String) {
        notes[index].hit = true
        combo += 1
        maxCombo = max(maxCombo, combo)
        bestCombo = maxCombo
        hits += 1
        streak = 0
        score += base * combo
        accuracy = total > 0 ? Int(Double(hits) / Double(total) * 100) : 100
        judge = text
        let lane = notes[index].lane
        noteChord(lane)
        flash[lane] = true
        let token = lane
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.12) { [weak self] in
            guard let self, self.flash.indices.contains(token) else { return }
            self.flash[token] = false
        }
    }

    private var chord: [(lane: Int, time: Date)] = []

    private func noteChord(_ lane: Int) {
        let now = Date()
        chord.append((lane, now))
        chord.removeAll { now.timeIntervalSince($0.time) > 0.12 }
        if Set(chord.map(\.lane)).count >= 3 {
            comboBurst = true
            chord.removeAll()
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.22) { [weak self] in
                self?.comboBurst = false
            }
        }
    }

    private func record(_ lane: Int) {
        let time = player?.currentTime ?? Date().timeIntervalSince(editorStarted ?? Date())
        editorNotes.append(NotePoint(Time: time, Lane: lane))
        recorded = editorNotes.count
        flash[lane] = true
    }

    private func finish(failed: Bool) {
        ended = true
        self.failed = failed
        finished = true
        playing = false
        player?.stop()
        timer?.invalidate()
        timer = nil
        accuracy = total > 0 ? Int(Double(hits) / Double(total) * 100) : 100
        results.insert(GameResult(
            id: UUID(),
            title: title,
            score: score,
            accuracy: accuracy,
            combo: maxCombo,
            cleared: !failed,
            date: Date()
        ), at: 0)
        if results.count > 200 { results.removeLast(results.count - 200) }
        saveDisk()
    }

    private func stopClock() {
        timer?.invalidate()
        timer = nil
        player?.stop()
        player = nil
        if let monitor {
            NSEvent.removeMonitor(monitor)
            self.monitor = nil
        }
    }

    private func play(url: URL?) {
        guard let url else { return }
        player = try? AVAudioPlayer(contentsOf: url)
        player?.volume = Self.linearVolume(volume)
        player?.prepareToPlay()
        player?.play()
    }

    private func installMonitor() {
        guard monitor == nil else { return }
        monitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown]) { [weak self] event in
            guard let self else { return event }
            var handled = false
            MainActor.assumeIsolated {
                handled = self.take(event)
            }
            return handled ? nil : event
        }
    }

    private func take(_ event: NSEvent) -> Bool {
        if event.isARepeat { return playing || recording || rebinding != nil }
        let name = keyName(event)
        if let lane = rebinding {
            bind(lane, key: name)
            return true
        }
        guard playing || recording else { return false }
        guard let lane = lane(for: event, name: name) else { return false }
        press(lane)
        return true
    }

    private func lane(for event: NSEvent, name: String) -> Int? {
        switch event.keyCode {
        case 123: return 0
        case 125: return 1
        case 126: return 2
        case 124: return 3
        default: break
        }
        return keys.firstIndex { $0.caseInsensitiveCompare(name) == .orderedSame }
    }

    private func keyName(_ event: NSEvent) -> String {
        switch event.keyCode {
        case 123: return "LEFT"
        case 125: return "DOWN"
        case 126: return "UP"
        case 124: return "RIGHT"
        default:
            return (event.charactersIgnoringModifiers ?? "").uppercased()
        }
    }

    private func builtin() -> LevelMap {
        let pattern = [0, 2, 1, 3, 0, 1, 2, 3, 1, 0, 3, 2]
        let beat = 60.0 / 140.0
        let notes = (0..<48).map { index in
            ChartNote(id: index, time: beat * Double(index + 2), lane: pattern[index % pattern.count])
        }
        return LevelMap(id: "builtin", title: "NetFix, Default Beat", author: "NetFix", bpm: 140, notes: notes, audioURL: nil, folder: nil, builtin: true)
    }

    private func importOsu(textURL: URL, folder: URL) {
        guard let text = try? String(contentsOf: textURL, encoding: .utf8) else { return }
        let parsed = Self.parseOsu(text)
        let dest = Self.levelsRoot.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try? FileManager.default.createDirectory(at: dest, withIntermediateDirectories: true)
        var audioName = ""
        if let audio = parsed.audio {
            let source = folder.appendingPathComponent(audio)
            if FileManager.default.fileExists(atPath: source.path) {
                audioName = Self.storeAudio(source, in: dest)
            }
        }
        let file = NoteFile(
            Title: parsed.title,
            Author: "osu!",
            TrackFile: audioName,
            Bpm: parsed.bpm,
            Notes: parsed.notes.map { NotePoint(Time: $0.0, Lane: $0.1) }
        )
        if let data = try? JSONEncoder().encode(file) {
            try? data.write(to: dest.appendingPathComponent("notes.json"))
        }
        status = "Карта osu! добавлена"
        reloadLevels()
    }

    private static func parseOsu(_ text: String) -> (title: String, audio: String?, bpm: Double, notes: [(Double, Int)]) {
        var title = "osu!"
        var audio: String?
        var bpm = 140.0
        var keys = 4
        var section = ""
        var notes: [(Double, Int)] = []
        for raw in text.split(whereSeparator: \.isNewline) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.hasPrefix("[") && line.hasSuffix("]") {
                section = line
                continue
            }
            if line.hasPrefix("Title:") { title = String(line.dropFirst(6)).trimmingCharacters(in: .whitespaces) }
            if line.hasPrefix("AudioFilename:") { audio = String(line.dropFirst(14)).trimmingCharacters(in: .whitespaces) }
            if line.hasPrefix("CircleSize:") { keys = Int(line.dropFirst(11).trimmingCharacters(in: .whitespaces)) ?? 4 }
            if section == "[TimingPoints]", bpm == 140 {
                let parts = line.split(separator: ",")
                if parts.count > 1, let beat = Double(parts[1]), beat > 0 {
                    bpm = min(400, max(60, 60000 / beat))
                }
            }
            if section == "[HitObjects]" {
                let parts = line.split(separator: ",")
                guard parts.count >= 3, let x = Double(parts[0]), let ms = Double(parts[2]) else { continue }
                let columns = max(keys, 1)
                var lane = Int(x / (512.0 / Double(columns)))
                if columns > 4 { lane = Int(Double(lane) / Double(columns) * 4.0) }
                notes.append((ms / 1000, min(3, max(0, lane))))
            }
        }
        return (title, audio, bpm, notes)
    }

    private static var levelsRoot: URL {
        let url = Paths.support.appendingPathComponent("levels", isDirectory: true)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    private static var diskURL: URL {
        Paths.support.appendingPathComponent("game.json")
    }

    private static func readLevel(at folder: URL, file: URL? = nil) -> LevelMap? {
        let notesURL = file ?? folder.appendingPathComponent("notes.json")
        guard let data = try? Data(contentsOf: notesURL),
              let file = try? JSONDecoder().decode(NoteFile.self, from: data) else { return nil }
        let notes = file.Notes.enumerated().map { ChartNote(id: $0.offset, time: $0.element.Time, lane: $0.element.Lane) }
        guard !notes.isEmpty else { return nil }
        let audio: URL?
        if let name = file.TrackFile, !name.isEmpty {
            let candidate = folder.appendingPathComponent(name)
            audio = FileManager.default.fileExists(atPath: candidate.path) ? candidate : nil
        } else {
            audio = nil
        }
        return LevelMap(
            id: folder.path,
            title: file.Title ?? folder.lastPathComponent,
            author: file.Author ?? "",
            bpm: file.Bpm ?? 140,
            notes: notes,
            audioURL: audio,
            folder: folder,
            builtin: folder.lastPathComponent.hasPrefix("track-")
        )
    }

    private static func firstFile(in folder: URL, ext: String) -> URL? {
        guard let items = FileManager.default.enumerator(at: folder, includingPropertiesForKeys: nil) else { return nil }
        for case let url as URL in items where url.pathExtension.lowercased() == ext {
            return url
        }
        return nil
    }

    private static func fallSeconds(_ bpm: Double) -> Double {
        let tempo = bpm > 0 ? bpm : 140
        let adjusted = pow(140 / tempo, 1.2)
        return min(2.6, max(0.6, 1.6 * adjusted))
    }

    private func loadDisk() {
        guard let data = try? Data(contentsOf: Self.diskURL),
              let disk = try? JSONDecoder().decode(GameDisk.self, from: data) else { return }
        if disk.keys.count == 4 { keys = disk.keys }
        results = disk.history
        if let volume = disk.volume { self.volume = min(1, max(0, volume)) }
    }

    private static func linearVolume(_ slider: Double) -> Float {
        Float(pow(slider, 3))
    }

    private func saveDisk() {
        let disk = GameDisk(keys: keys, history: results, volume: volume)
        guard let data = try? JSONEncoder().encode(disk) else { return }
        try? Paths.ensure()
        try? data.write(to: Self.diskURL)
    }
}
