import Foundation
import Darwin

public struct DnsPreset: Identifiable, Equatable {
    public var id: String
    public var name: String
    public var detail: String
    public var servers: [String]

    public init(id: String, name: String, detail: String, servers: [String]) {
        self.id = id
        self.name = name
        self.detail = detail
        self.servers = servers
    }
}

public enum DnsState: Equatable {
    case dhcp
    case manual([String])
    case unknown(String)
}

public enum DnsService {
    public static let presets: [DnsPreset] = [
        DnsPreset(id: "dhcp", name: "Системный (DHCP)", detail: "DNS от роутера или провайдера", servers: []),
        DnsPreset(id: "xbox", name: "Xbox-DNS.ru", detail: "Альтернативный DNS для сетевых служб", servers: ["111.88.96.50", "111.88.96.51"]),
        DnsPreset(id: "cloudflare", name: "Cloudflare DNS", detail: "1.1.1.1 и 1.0.0.1", servers: ["1.1.1.1", "1.0.0.1"]),
        DnsPreset(id: "google", name: "Google Public DNS", detail: "8.8.8.8 и 8.8.4.4", servers: ["8.8.8.8", "8.8.4.4"]),
        DnsPreset(id: "yandex", name: "Yandex.DNS", detail: "77.88.8.8 и 77.88.8.1", servers: ["77.88.8.8", "77.88.8.1"]),
        DnsPreset(id: "adguard", name: "AdGuard DNS", detail: "Блокирует часть рекламы и трекеров", servers: ["94.140.14.14", "94.140.15.15"])
    ]

    public static func activeService() -> String? {
        let route = Shell.run("/sbin/route", ["-n", "get", "default"]).stdout
        let iface = route
            .split(separator: "\n")
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .first { $0.hasPrefix("interface:") }?
            .split(separator: ":")
            .last
            .map { $0.trimmingCharacters(in: .whitespaces) }
        let hardware = Shell.run("/usr/sbin/networksetup", ["-listallhardwareports"]).stdout
        if let iface, let name = serviceName(hardware: hardware, device: iface) {
            return name
        }
        return firstEnabledService()
    }

    public static func currentServers(service: String) -> DnsState {
        let text = Shell.run("/usr/sbin/networksetup", ["-getdnsservers", service]).stdout
        let lowered = text.lowercased()
        if lowered.contains("aren't any") || lowered.contains("there aren") {
            return .dhcp
        }
        let ips = text
            .split(separator: "\n")
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter(isIP)
        if ips.isEmpty {
            return .unknown(text.trimmingCharacters(in: .whitespacesAndNewlines))
        }
        return .manual(ips)
    }

    public static func apply(service: String, servers: [String]) throws {
        let clean = servers.map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        for server in clean where !isIP(server) {
            throw NetFixError("Некорректный DNS-адрес: \(server)")
        }
        let script = """
        #!/bin/bash
        set -u
        service="$1"
        shift
        if [ "$#" -eq 0 ]; then
          /usr/sbin/networksetup -setdnsservers "$service" Empty
        else
          /usr/sbin/networksetup -setdnsservers "$service" "$@"
        fi
        /usr/bin/dscacheutil -flushcache || true
        /usr/bin/killall -HUP mDNSResponder || true
        """
        try PrivilegedRunner.run(script: script, arguments: [service] + clean)
    }

    public static func isIP(_ value: String) -> Bool {
        var v4 = in_addr()
        var v6 = in6_addr()
        let v4ok = value.withCString { inet_pton(AF_INET, $0, &v4) == 1 }
        if v4ok { return true }
        return value.withCString { inet_pton(AF_INET6, $0, &v6) == 1 }
    }

    private static func serviceName(hardware: String, device: String) -> String? {
        var port: String?
        for line in hardware.split(separator: "\n", omittingEmptySubsequences: false).map(String.init) {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed.hasPrefix("Hardware Port:") {
                port = trimmed.replacingOccurrences(of: "Hardware Port:", with: "").trimmingCharacters(in: .whitespaces)
            } else if trimmed.hasPrefix("Device:") {
                let dev = trimmed.replacingOccurrences(of: "Device:", with: "").trimmingCharacters(in: .whitespaces)
                if dev == device { return port }
            }
        }
        return nil
    }

    private static func firstEnabledService() -> String? {
        let text = Shell.run("/usr/sbin/networksetup", ["-listallnetworkservices"]).stdout
        return text
            .split(separator: "\n")
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .first { !$0.isEmpty && !$0.hasPrefix("An asterisk") && !$0.hasPrefix("*") }
    }
}

public enum HostsService {
    public static func current() -> String {
        (try? String(contentsOfFile: "/etc/hosts", encoding: .utf8)) ?? ""
    }

    public static func apply(activeMods: [String]) throws {
        let bodies = activeMods.map { ModStore.read(directory: Paths.modsHosts, name: $0) }
        let merged = HostsFileEditor.applying(existing: current(), records: HostsFileEditor.records(from: bodies))
        if merged.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw NetFixError("Системный hosts пуст, запись отменена")
        }
        try Paths.ensure()
        let file = Paths.runtime.appendingPathComponent("hosts.new")
        try merged.write(to: file, atomically: true, encoding: .utf8)
        let script = """
        #!/bin/bash
        set -u
        if [[ ! -s "$1" ]]; then
          echo "Пустой hosts, запись отменена" >&2
          exit 1
        fi
        /bin/cp "$1" /etc/hosts
        /bin/chmod 644 /etc/hosts
        /usr/bin/dscacheutil -flushcache || true
        /usr/bin/killall -HUP mDNSResponder || true
        """
        try PrivilegedRunner.run(script: script, arguments: [file.path])
    }
}

public enum LoginItem {
    public static func sync(enabled: Bool) throws {
        let directory = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/LaunchAgents")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let plist = directory.appendingPathComponent("com.netfix.mac.agent.plist")
        let uid = getuid()
        _ = Shell.run("/bin/launchctl", ["bootout", "gui/\(uid)", plist.path])
        _ = Shell.run("/bin/launchctl", ["bootout", "gui/\(uid)/com.netfix.mac.agent"])
        if !enabled {
            try? FileManager.default.removeItem(at: plist)
            return
        }
        let xml = BypassBlueprint.agentPlist(arguments: programArguments())
        try xml.write(to: plist, atomically: true, encoding: .utf8)
        let result = Shell.run("/bin/launchctl", ["bootstrap", "gui/\(uid)", plist.path])
        if !result.ok {
            throw NetFixError(result.output.trimmingCharacters(in: .whitespacesAndNewlines))
        }
    }

    public static func programArguments() -> [String] {
        let bundle = Bundle.main.bundleURL
        if bundle.pathExtension == "app" {
            return ["/usr/bin/open", "-a", bundle.path]
        }
        if let executable = Bundle.main.executablePath {
            return [executable]
        }
        return ["/usr/bin/true"]
    }
}
