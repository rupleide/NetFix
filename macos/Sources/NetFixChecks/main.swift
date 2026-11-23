import Foundation
import NetFixCore

var failures = 0

func check(_ condition: Bool, _ message: String) {
    if condition { return }
    failures += 1
    fputs("FAIL \(message)\n", stderr)
}

let original = "127.0.0.1 localhost\n# keep\n"
let once = HostsFileEditor.applying(existing: original, records: "1.1.1.1 a.test")
check(once.contains("localhost") && once.contains("1.1.1.1 a.test") && once.contains(HostsFileEditor.begin), "hosts insert")
let replaced = HostsFileEditor.applying(existing: once, records: "8.8.8.8 b.test")
check(!replaced.contains("a.test") && replaced.contains("8.8.8.8 b.test"), "hosts replace")
check(replaced.components(separatedBy: HostsFileEditor.begin).count == 2, "hosts single block")
let cleared = HostsFileEditor.applying(existing: replaced, records: "")
check(!cleared.contains("b.test") && cleared.contains("localhost") && !cleared.contains(HostsFileEditor.begin), "hosts clear")

let help = "--port=<n> --user=<name> --bind-addr=<ip> --split-pos=<p> --disorder --split-tls=sni --hostlist=<file>"
let args = TpwsArgs.resolve(
    ["--split-pos=1", "--tlsrec=sni", "--disorder", "--oob"],
    help: help,
    hostlist: "/tmp/list.txt",
    exclude: nil
)
check(args.contains("--user=root") && args.contains("--split-tls=sni"), "tls rewrite")
check(!args.contains("--oob") && !args.contains(where: { $0.contains("tlsrec") }), "drop unknown")
check(args.contains("--hostlist=/tmp/list.txt"), "hostlist arg")
let kept = TpwsArgs.resolve(["--oob"], help: "", hostlist: nil, exclude: nil)
check(kept.contains("--oob") && kept.contains("--port=988"), "empty help")

let withQuic = BypassBlueprint.pfRules(port: 988, blockQuic: true)
check(withQuic.contains("udp") && withQuic.contains("port 988") && withQuic.contains("user { >root }"), "pf quic")
if let rdr = withQuic.range(of: "rdr pass"), let block = withQuic.range(of: "block drop") {
    check(rdr.lowerBound < block.lowerBound, "pf translation before filter")
}
check(!BypassBlueprint.pfRules(port: 988, blockQuic: false).contains("udp"), "pf plain")
check(StrategyLibrary.parseArgs(" --split-pos=1   --disorder ") == ["--split-pos=1", "--disorder"], "strategy args")
check(StrategyLibrary.isNewer("1.3.0", than: "1.2.0") && !StrategyLibrary.isNewer("1.2.0", than: "1.2.0"), "version compare")
check(StrategyLibrary.isNewer("v72.14", than: "v72.13"), "zapret tag")

let plist = BypassBlueprint.daemonPlist(binary: "/tmp/tpws", arguments: ["--user=root", "--port=988"])
check(!plist.contains("--daemon") && plist.contains("<string>--user=root</string>"), "plist foreground")
let plistURL = FileManager.default.temporaryDirectory.appendingPathComponent("netfix-check.plist")
try plist.write(to: plistURL, atomically: true, encoding: .utf8)
let lint = Shell.run("/usr/bin/plutil", ["-lint", plistURL.path])
check(lint.code == 0, "plutil \(lint.output)")
try? FileManager.default.removeItem(at: plistURL)

let report = ScanReport.parse("split-1 1 2 400\ncombo 3 0 180\nBEST combo\n")
check(report.rows.count == 2 && report.best == "combo" && report.rows[1].ms == 180, "scan parse")
check(ScanReport.parse("split-1 0 3 8000\nBEST none\n").best == nil, "scan none")

let settings = try JSONDecoder().decode(MacSettings.self, from: Data("{\"enableZapret\":false}".utf8))
check(!settings.enableZapret && settings.blockQuic && settings.strategyId == "split-midsld", "settings defaults")
check(ModStore.sanitize("../hosts") == nil && ModStore.sanitize("a/b") == nil && ModStore.sanitize(" youtube ") == "youtube", "mod name")
check(!BypassScripts.start.contains("--daemon") && BypassScripts.start.contains("com.apple/netfix"), "start script")
check(BypassScripts.start.contains("printf '%s\\n'"), "printf newline")
check(BypassScripts.scan.contains("BEST") && BypassScripts.stop.contains("disable_all"), "scan and stop")

if CommandLine.arguments.contains("live") {
    let service = DnsService.activeService() ?? "—"
    print("dns-service \(service)")
    if let name = DnsService.activeService() {
        switch DnsService.currentServers(service: name) {
        case .dhcp: print("dns-state dhcp")
        case .manual(let servers): print("dns-state \(servers.joined(separator: ","))")
        case .unknown(let text): print("dns-state unknown \(text)")
        }
    }
    print("zapret-installed \(ZapretInstaller.isInstalled) version=\(ZapretInstaller.installedVersion)")
    print("tgws-installed \(TgWsInstaller.isInstalled) version=\(TgWsInstaller.installedVersion)")
    let probes = await DiagnosticsService.runAll()
    for probe in probes {
        print("probe \(probe.ok ? "ok" : "fail") \(probe.title) — \(probe.detail)")
    }
}

if failures == 0 {
    print("OK")
    exit(0)
}
fputs("\(failures) failed\n", stderr)
exit(1)
