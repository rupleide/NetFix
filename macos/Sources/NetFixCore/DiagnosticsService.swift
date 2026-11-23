import Foundation
import Darwin

public struct ProbeResult: Identifiable, Equatable {
    public var id: String
    public var title: String
    public var detail: String
    public var ok: Bool

    public init(id: String, title: String, detail: String, ok: Bool) {
        self.id = id
        self.title = title
        self.detail = detail
        self.ok = ok
    }
}

public enum DiagnosticsService {
    private static let datacenters: [(Int, String)] = [
        (1, "149.154.175.50"),
        (2, "149.154.167.51"),
        (3, "149.154.175.100"),
        (4, "149.154.167.91"),
        (5, "91.108.56.130")
    ]

    private static let sites: [(String, String)] = [
        ("YouTube", "https://www.youtube.com"),
        ("Discord", "https://discord.com"),
        ("Telegram Web", "https://web.telegram.org"),
        ("Google", "https://www.google.com")
    ]

    public static func runAll() async -> [ProbeResult] {
        var rows: [ProbeResult] = []
        rows.append(await internet())
        rows.append(dns())
        rows.append(contentsOf: await datacenterProbes())
        rows.append(contentsOf: await siteProbes())
        return rows
    }

    public static func internet() async -> ProbeResult {
        guard let url = URL(string: "https://www.gstatic.com/generate_204") else {
            return ProbeResult(id: "internet", title: "Интернет", detail: "Нет адреса проверки", ok: false)
        }
        let result = await http(url, timeout: 6)
        return ProbeResult(
            id: "internet",
            title: "Интернет",
            detail: result.ok ? "Есть, \(result.ms) мс" : "Нет ответа",
            ok: result.ok
        )
    }

    public static func dns() -> ProbeResult {
        let result = Shell.run("/usr/bin/dscacheutil", ["-q", "host", "-a", "name", "telegram.org"])
        let ok = result.stdout.contains("ip_address")
        let ip = result.stdout
            .split(separator: "\n")
            .first { $0.contains("ip_address") }
            .map { $0.replacingOccurrences(of: "ip_address:", with: "").trimmingCharacters(in: .whitespaces) }
        return ProbeResult(
            id: "dns",
            title: "DNS telegram.org",
            detail: ok ? (ip ?? "резолвится") : "Не резолвится",
            ok: ok
        )
    }

    public static func datacenterProbes() async -> [ProbeResult] {
        await Task.detached(priority: .userInitiated) {
            datacenters.map { dc, ip in
                if let ms = tcpMillis(host: ip, port: 443, timeout: 3) {
                    return ProbeResult(id: "dc\(dc)", title: "Telegram DC\(dc)", detail: "\(ip) · \(ms) мс", ok: true)
                }
                return ProbeResult(id: "dc\(dc)", title: "Telegram DC\(dc)", detail: "\(ip) · нет TCP 443", ok: false)
            }
        }.value
    }

    public static func siteProbes() async -> [ProbeResult] {
        var rows: [ProbeResult] = []
        for (name, raw) in sites {
            guard let url = URL(string: raw) else { continue }
            let result = await http(url, timeout: 8)
            let detail = result.code == 0 ? "таймаут" : "HTTP \(result.code) · \(result.ms) мс"
            rows.append(ProbeResult(id: name, title: name, detail: detail, ok: result.ok))
        }
        return rows
    }

    public static func report(zapretRunning: Bool) async -> (probes: [ProbeResult], advice: [String]) {
        var probes = await runAll()
        async let spoof = dnsSpoof()
        let sni = sniProbe()
        let discord = discordProbes()
        let udp = udpProbe()
        probes.append(await spoof)
        probes.append(sni)
        probes.append(contentsOf: discord)
        probes.append(udp)
        return (probes, advice(probes, zapretRunning: zapretRunning))
    }

    private static func dnsSpoof() async -> ProbeResult {
        let system = systemAddresses("telegram.org")
        let remote = await dohAddresses("telegram.org")
        if system.isEmpty {
            return ProbeResult(id: "spoof", title: "DNS и DoH", detail: "telegram.org не резолвится локально", ok: false)
        }
        if remote.isEmpty {
            return ProbeResult(id: "spoof", title: "DNS и DoH", detail: "Локально \(system.joined(separator: ", ")). DoH не ответил", ok: true)
        }
        let overlap = !Set(system).isDisjoint(with: remote)
        if overlap {
            return ProbeResult(id: "spoof", title: "DNS и DoH", detail: "Совпадает с Cloudflare", ok: true)
        }
        return ProbeResult(id: "spoof", title: "DNS и DoH", detail: "Локально \(system.joined(separator: ", ")), DoH \(remote.joined(separator: ", "))", ok: false)
    }

    private static func sniProbe() -> ProbeResult {
        let telegram = handshake(name: "telegram.org")
        let neutral = handshake(name: "example.com")
        let blocked = telegram != "ok" && neutral == "ok"
        let detail = blocked
            ? "telegram.org: \(telegram), example.com: \(neutral)"
            : "telegram.org: \(telegram), example.com: \(neutral)"
        return ProbeResult(id: "sni", title: "SNI telegram.org", detail: detail, ok: !blocked)
    }

    private static func discordProbes() -> [ProbeResult] {
        let hosts = [("Gateway", "162.159.130.234"), ("Media", "162.159.129.235"), ("API", "162.159.128.233")]
        return hosts.map { label, ip in
            if let ms = tcpMillis(host: ip, port: 443, timeout: 4) {
                return ProbeResult(id: "discord-\(label)", title: "Discord \(label)", detail: "\(ip) · \(ms) мс", ok: true)
            }
            return ProbeResult(id: "discord-\(label)", title: "Discord \(label)", detail: "\(ip) · нет TCP 443", ok: false)
        }
    }

    private static func udpProbe() -> ProbeResult {
        let ports: [UInt16] = [50000, 50001, 50002, 50003, 50004]
        let refused = ports.filter { udpRefused("162.159.130.234", $0) }.count
        if refused == ports.count {
            return ProbeResult(id: "udp", title: "Discord UDP", detail: "Порты 50000–50004 закрыты", ok: false)
        }
        return ProbeResult(id: "udp", title: "Discord UDP", detail: "Отказов ICMP: \(refused) из \(ports.count)", ok: true)
    }

    private static func advice(_ probes: [ProbeResult], zapretRunning: Bool) -> [String] {
        var lines: [String] = []
        func failed(_ id: String) -> Bool { probes.first { $0.id == id }?.ok == false }
        if failed("spoof") {
            lines.append("Системный DNS для telegram.org не совпадает с ответом Cloudflare. Откройте «Настройка DNS» и включите Cloudflare.")
        }
        if failed("sni") {
            lines.append("TLS с именем telegram.org обрывается, а нейтральное имя на том же адресе проходит. Нажмите «Починить»: tpws режет SNI.")
        }
        let datacenters = probes.filter { $0.id.hasPrefix("dc") }
        if !datacenters.isEmpty, datacenters.allSatisfy({ !$0.ok }), probes.first(where: { $0.id == "internet" })?.ok == true {
            lines.append("Интернет есть, а дата-центры Telegram по TCP закрыты. Запустите обход и укажите MTProto-прокси из журнала.")
        }
        if failed("udp") {
            lines.append(zapretRunning
                ? "Голосовой UDP Discord закрыт. Обход NetFix работает для TCP. В Discord можно переключить голос на TCP."
                : "Голосовой UDP Discord закрыт. NetFix чинит TCP-сайты. Для голоса включите в Discord транспорт TCP.")
        }
        if lines.isEmpty {
            lines.append("Явных блокировок по этим проверкам нет.")
        }
        return lines
    }

    private static func systemAddresses(_ name: String) -> [String] {
        let text = Shell.run("/usr/bin/dscacheutil", ["-q", "host", "-a", "name", name]).stdout
        return text.split(separator: "\n").compactMap { line in
            guard line.contains("ip_address") else { return nil }
            return line.replacingOccurrences(of: "ip_address:", with: "").trimmingCharacters(in: .whitespaces)
        }
    }

    private static func dohAddresses(_ name: String) async -> [String] {
        var parts = URLComponents(string: "https://cloudflare-dns.com/dns-query")
        parts?.queryItems = [URLQueryItem(name: "name", value: name), URLQueryItem(name: "type", value: "A")]
        guard let url = parts?.url else { return [] }
        var request = URLRequest(url: url, timeoutInterval: 6)
        request.setValue("application/dns-json", forHTTPHeaderField: "Accept")
        guard let (data, _) = try? await URLSession.shared.data(for: request),
              let json = try? JSONDecoder().decode(DohResponse.self, from: data) else { return [] }
        return json.Answer?.map(\.data) ?? []
    }

    private static func handshake(name: String) -> String {
        let result = Shell.run(
            "/usr/bin/openssl",
            ["s_client", "-connect", "149.154.167.51:443", "-servername", name, "-brief"],
            timeout: 6
        )
        let text = (result.stdout + result.stderr).lowercased()
        if text.contains("protocol") || text.contains("verification") || result.code == 0 { return "ok" }
        if text.contains("reset") || text.contains("errno=54") { return "rst" }
        return "timeout"
    }

    private static func udpRefused(_ host: String, _ port: UInt16) -> Bool {
        var hints = addrinfo(
            ai_flags: AI_ADDRCONFIG,
            ai_family: AF_UNSPEC,
            ai_socktype: SOCK_DGRAM,
            ai_protocol: IPPROTO_UDP,
            ai_addrlen: 0,
            ai_canonname: nil,
            ai_addr: nil,
            ai_next: nil
        )
        var info: UnsafeMutablePointer<addrinfo>?
        guard getaddrinfo(host, String(port), &hints, &info) == 0, let info else { return false }
        defer { freeaddrinfo(info) }
        let fd = socket(info.pointee.ai_family, info.pointee.ai_socktype, info.pointee.ai_protocol)
        if fd < 0 { return false }
        defer { close(fd) }
        if connect(fd, info.pointee.ai_addr, info.pointee.ai_addrlen) != 0 { return false }
        let flags = fcntl(fd, F_GETFL, 0)
        _ = fcntl(fd, F_SETFL, flags | O_NONBLOCK)
        var byte: UInt8 = 0
        _ = send(fd, &byte, 1, 0)
        var pollInfo = pollfd(fd: fd, events: Int16(POLLIN), revents: 0)
        _ = poll(&pollInfo, 1, 300)
        var socketError: Int32 = 0
        var length = socklen_t(MemoryLayout<Int32>.size)
        _ = getsockopt(fd, SOL_SOCKET, SO_ERROR, &socketError, &length)
        return socketError == ECONNREFUSED
    }

    private struct DohResponse: Decodable {
        var Answer: [DohAnswer]?
    }

    private struct DohAnswer: Decodable {
        var data: String
    }

    private static func http(_ url: URL, timeout: TimeInterval) async -> (ok: Bool, ms: Int, code: Int) {
        var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: timeout)
        request.setValue("NetFixMac/\(AppInfo.version)", forHTTPHeaderField: "User-Agent")
        let started = Date()
        do {
            let (_, response) = try await URLSession.shared.data(for: request)
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            let ms = Int(Date().timeIntervalSince(started) * 1000)
            let ok = code == 204 || (200..<400).contains(code)
            return (ok, ms, code)
        } catch {
            return (false, Int(timeout * 1000), 0)
        }
    }

    private static func tcpMillis(host: String, port: UInt16, timeout: TimeInterval) -> Int? {
        var hints = addrinfo(
            ai_flags: AI_ADDRCONFIG,
            ai_family: AF_UNSPEC,
            ai_socktype: SOCK_STREAM,
            ai_protocol: IPPROTO_TCP,
            ai_addrlen: 0,
            ai_canonname: nil,
            ai_addr: nil,
            ai_next: nil
        )
        var info: UnsafeMutablePointer<addrinfo>?
        guard getaddrinfo(host, String(port), &hints, &info) == 0, let info else { return nil }
        defer { freeaddrinfo(info) }
        let fd = socket(info.pointee.ai_family, SOCK_STREAM, IPPROTO_TCP)
        if fd < 0 { return nil }
        defer { close(fd) }
        let flags = fcntl(fd, F_GETFL, 0)
        _ = fcntl(fd, F_SETFL, flags | O_NONBLOCK)
        let started = Date()
        if connect(fd, info.pointee.ai_addr, info.pointee.ai_addrlen) == 0 {
            return 0
        }
        if errno != EINPROGRESS { return nil }
        var pollInfo = pollfd(fd: fd, events: Int16(POLLOUT), revents: 0)
        if poll(&pollInfo, 1, Int32(timeout * 1000)) <= 0 { return nil }
        var socketError: Int32 = 0
        var length = socklen_t(MemoryLayout<Int32>.size)
        if getsockopt(fd, SOL_SOCKET, SO_ERROR, &socketError, &length) != 0 || socketError != 0 {
            return nil
        }
        return max(0, Int(Date().timeIntervalSince(started) * 1000))
    }
}
