import Foundation

public enum HostsFileEditor {
    public static let begin = "# NetFix BEGIN"
    public static let end = "# NetFix END"

    public static func records(from bodies: [String]) -> String {
        bodies
            .flatMap { $0.split(whereSeparator: \.isNewline).map(String.init) }
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { line in !line.isEmpty && line != begin && line != end }
            .joined(separator: "\n")
    }

    public static func removing(existing: String) -> String {
        var output: [String] = []
        var skipping = false
        for line in existing.split(separator: "\n", omittingEmptySubsequences: false).map(String.init) {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed == begin {
                skipping = true
                continue
            }
            if trimmed == end {
                skipping = false
                continue
            }
            if !skipping { output.append(line) }
        }
        return output.joined(separator: "\n")
    }

    public static func applying(existing: String, records: String) -> String {
        var base = removing(existing: existing)
        while base.hasSuffix("\n\n") { base.removeLast() }
        if !base.isEmpty && !base.hasSuffix("\n") { base += "\n" }
        let block = records.trimmingCharacters(in: .whitespacesAndNewlines)
        if block.isEmpty { return base }
        return base + "\n\(begin)\n\(block)\n\(end)\n"
    }
}

public enum ModStore {
    public static func names(in directory: URL) -> [String] {
        let files = (try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)) ?? []
        return files
            .filter { $0.pathExtension.lowercased() == "txt" }
            .map { $0.deletingPathExtension().lastPathComponent }
            .sorted()
    }

    public static func file(directory: URL, name: String) -> URL {
        directory.appendingPathComponent(name).appendingPathExtension("txt")
    }

    public static func read(directory: URL, name: String) -> String {
        (try? String(contentsOf: file(directory: directory, name: name), encoding: .utf8)) ?? ""
    }

    public static func write(directory: URL, name: String, text: String) throws {
        guard let safe = sanitize(name) else { throw NetFixError("Некорректное имя мода") }
        try Paths.ensure()
        try text.write(to: file(directory: directory, name: safe), atomically: true, encoding: .utf8)
    }

    public static func delete(directory: URL, name: String) throws {
        try FileManager.default.removeItem(at: file(directory: directory, name: name))
    }

    public static func sanitize(_ name: String) -> String? {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty || trimmed.count > 64 { return nil }
        if trimmed.contains("/") || trimmed.contains("\\") || trimmed.contains("..") { return nil }
        return trimmed
    }
}
