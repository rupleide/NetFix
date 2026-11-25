import Foundation

public enum AppInfo {
    public static let version = "1.2.0"
    public static let name = "NetFix"
}

public struct NetFixError: LocalizedError {
    public var errorDescription: String?
    public init(_ message: String) { errorDescription = message }
}

public func xmlEscape(_ value: String) -> String {
    value
        .replacingOccurrences(of: "&", with: "&amp;")
        .replacingOccurrences(of: "<", with: "&lt;")
        .replacingOccurrences(of: ">", with: "&gt;")
}

public struct CommandResult {
    public var code: Int32
    public var stdout: String
    public var stderr: String
    public var ok: Bool { code == 0 }
    public var output: String {
        [stdout, stderr].filter { !$0.isEmpty }.joined(separator: "\n")
    }

    public init(code: Int32, stdout: String, stderr: String) {
        self.code = code
        self.stdout = stdout
        self.stderr = stderr
    }
}

public enum Shell {
    public static func run(_ launchPath: String, _ arguments: [String], cwd: URL? = nil, timeout: TimeInterval? = nil) -> CommandResult {
        let fm = FileManager.default
        let id = UUID().uuidString
        let outURL = fm.temporaryDirectory.appendingPathComponent("netfix-out-\(id)")
        let errURL = fm.temporaryDirectory.appendingPathComponent("netfix-err-\(id)")
        defer {
            try? fm.removeItem(at: outURL)
            try? fm.removeItem(at: errURL)
        }
        fm.createFile(atPath: outURL.path, contents: nil)
        fm.createFile(atPath: errURL.path, contents: nil)
        guard let outHandle = try? FileHandle(forWritingTo: outURL),
              let errHandle = try? FileHandle(forWritingTo: errURL) else {
            return CommandResult(code: 127, stdout: "", stderr: "Не удалось создать временные файлы")
        }

        let process = Process()
        process.executableURL = URL(fileURLWithPath: launchPath)
        process.arguments = arguments
        if let cwd { process.currentDirectoryURL = cwd }
        process.standardOutput = outHandle
        process.standardError = errHandle
        do {
            try process.run()
        } catch {
            return CommandResult(code: 127, stdout: "", stderr: error.localizedDescription)
        }
        if let timeout {
            let deadline = Date().addingTimeInterval(timeout)
            while process.isRunning && Date() < deadline {
                Thread.sleep(forTimeInterval: 0.05)
            }
            if process.isRunning {
                process.terminate()
                process.waitUntilExit()
            }
        } else {
            process.waitUntilExit()
        }
        try? outHandle.close()
        try? errHandle.close()
        let stdout = (try? String(contentsOf: outURL, encoding: .utf8)) ?? ""
        let stderr = (try? String(contentsOf: errURL, encoding: .utf8)) ?? ""
        return CommandResult(code: process.terminationStatus, stdout: stdout, stderr: stderr)
    }
}

public enum PrivilegedRunner {
    public static func run(script: String, arguments: [String] = []) throws {
        let fm = FileManager.default
        let stamp = UUID().uuidString
        let bashURL = fm.temporaryDirectory.appendingPathComponent("netfix-\(stamp).sh")
        let appleURL = fm.temporaryDirectory.appendingPathComponent("netfix-\(stamp).applescript")
        defer {
            try? fm.removeItem(at: bashURL)
            try? fm.removeItem(at: appleURL)
        }

        let body = script.hasPrefix("#!") ? script : "#!/bin/bash\n" + script
        try body.write(to: bashURL, atomically: true, encoding: .utf8)
        try fm.setAttributes([.posixPermissions: 0o700], ofItemAtPath: bashURL.path)

        let apple = """
        on run argv
            set cmd to "/bin/bash " & quoted form of (item 1 of argv)
            repeat with i from 2 to (count of argv)
                set cmd to cmd & " " & quoted form of (item i of argv)
            end repeat
            do shell script cmd with administrator privileges
        end run
        """
        try apple.write(to: appleURL, atomically: true, encoding: .utf8)

        var args = [appleURL.path, bashURL.path]
        args.append(contentsOf: arguments)
        let result = Shell.run("/usr/bin/osascript", args)
        if result.code == 0 { return }

        let message = result.output.trimmingCharacters(in: .whitespacesAndNewlines)
        if message.contains("User canceled") || message.contains("(-128)") {
            throw NetFixError("Ввод пароля администратора отменён")
        }
        throw NetFixError(message.isEmpty ? "Команда с правами администратора завершилась с ошибкой" : message)
    }
}

public enum HTTPClient {
    private static let session: URLSession = {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 30
        config.timeoutIntervalForResource = 180
        return URLSession(configuration: config)
    }()

    public static func data(_ url: URL, headers: [String: String] = [:]) async throws -> Data {
        var request = URLRequest(url: url)
        request.setValue("NetFixMac/\(AppInfo.version)", forHTTPHeaderField: "User-Agent")
        headers.forEach { request.setValue($1, forHTTPHeaderField: $0) }
        let (data, response) = try await session.data(for: request)
        try validate(response, url: url)
        return data
    }

    public static func download(_ url: URL, to destination: URL) async throws {
        var request = URLRequest(url: url)
        request.setValue("NetFixMac/\(AppInfo.version)", forHTTPHeaderField: "User-Agent")
        let (temporary, response) = try await session.download(for: request)
        try validate(response, url: url)
        let fm = FileManager.default
        try fm.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? fm.removeItem(at: destination)
        try fm.moveItem(at: temporary, to: destination)
    }

    private static func validate(_ response: URLResponse, url: URL) throws {
        let code = (response as? HTTPURLResponse)?.statusCode ?? 0
        if !(200..<300).contains(code) {
            throw NetFixError("HTTP \(code) для \(url.host ?? url.absoluteString)")
        }
    }
}
