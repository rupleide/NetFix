import Foundation

public enum BypassBlueprint {
    public static let port = 988
    public static let label = "com.netfix.tpws"

    public static func pfRules(port: Int, blockQuic: Bool) -> String {
        var lines = [
            "rdr pass on lo0 inet proto tcp from !127.0.0.0/8 to any port {80, 443} -> 127.0.0.1 port \(port)"
        ]
        if blockQuic {
            lines.append("block drop quick inet proto udp from any to any port 443 user { >root }")
        }
        lines.append("pass out route-to (lo0 127.0.0.1) inet proto tcp from any to any port {80, 443} user { >root }")
        return lines.joined(separator: "\n") + "\n"
    }

    public static func daemonPlist(binary: String, arguments: [String]) -> String {
        let items = ([binary] + arguments)
            .map { "<string>\(xmlEscape($0))</string>" }
            .joined(separator: "\n    ")
        return """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key>
          <string>\(label)</string>
          <key>ProgramArguments</key>
          <array>
            \(items)
          </array>
          <key>RunAtLoad</key>
          <true/>
          <key>KeepAlive</key>
          <true/>
          <key>ThrottleInterval</key>
          <integer>2</integer>
          <key>StandardOutPath</key>
          <string>/Library/Logs/NetFix/tpws.out.log</string>
          <key>StandardErrorPath</key>
          <string>/Library/Logs/NetFix/tpws.err.log</string>
        </dict>
        </plist>
        """
    }

    public static func agentPlist(arguments: [String]) -> String {
        let items = arguments
            .map { "<string>\(xmlEscape($0))</string>" }
            .joined(separator: "\n    ")
        return """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key>
          <string>com.netfix.mac.agent</string>
          <key>ProgramArguments</key>
          <array>
            \(items)
          </array>
          <key>RunAtLoad</key>
          <true/>
          <key>LimitLoadToSessionType</key>
          <string>Aqua</string>
        </dict>
        </plist>
        """
    }
}
