import SwiftUI

enum Theme {
    static let bg = Color(red: 0x16 / 255, green: 0x16 / 255, blue: 0x16 / 255)
    static let header = Color(red: 0x1e / 255, green: 0x1e / 255, blue: 0x1e / 255)
    static let card = Color(red: 0x1c / 255, green: 0x1c / 255, blue: 0x1f / 255)
    static let panel = Color(red: 0x1a / 255, green: 0x1a / 255, blue: 0x1a / 255)
    static let field = Color(red: 0x14 / 255, green: 0x14 / 255, blue: 0x18 / 255)
    static let border = Color(red: 0x2a / 255, green: 0x2a / 255, blue: 0x30 / 255)
    static let text = Color(red: 0xf0 / 255, green: 0xf0 / 255, blue: 0xf0 / 255)
    static let muted = Color(red: 0x88 / 255, green: 0x88 / 255, blue: 0x88 / 255)
    static let dim = Color(red: 0x55 / 255, green: 0x55 / 255, blue: 0x55 / 255)
    static let accent = Color(red: 0x3b / 255, green: 0x82 / 255, blue: 0xf6 / 255)
    static let ring = Color(red: 0x7c / 255, green: 0x6a / 255, blue: 0xf7 / 255)
    static let green = Color(red: 0x22 / 255, green: 0xc5 / 255, blue: 0x5e / 255)
    static let yellow = Color(red: 0xea / 255, green: 0xb3 / 255, blue: 0x08 / 255)
    static let red = Color(red: 0xef / 255, green: 0x44 / 255, blue: 0x44 / 255)

    static func level(_ level: String) -> Color {
        switch level {
        case "ok": return green
        case "warn": return yellow
        case "error": return red
        default: return text
        }
    }
}

struct FillButton: ButtonStyle {
    var color: Color = Theme.accent
    var wide = false

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 13, weight: .semibold))
            .foregroundStyle(.white)
            .padding(.horizontal, 14)
            .padding(.vertical, wide ? 11 : 8)
            .frame(maxWidth: wide ? .infinity : nil)
            .background(color.opacity(configuration.isPressed ? 0.75 : 1))
            .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
    }
}

struct OutlineButton: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 13, weight: .medium))
            .foregroundStyle(Theme.muted)
            .padding(.horizontal, 14)
            .padding(.vertical, 11)
            .frame(maxWidth: .infinity)
            .background(configuration.isPressed ? Color.white.opacity(0.04) : Color.clear)
            .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).stroke(Theme.border, lineWidth: 1))
    }
}

struct Card<Content: View>: View {
    @ViewBuilder var content: () -> Content

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            content()
        }
        .padding(16)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Theme.card)
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(Theme.border, lineWidth: 1))
        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
    }
}

struct StatusPill: View {
    var title: String
    var on: Bool
    var alert = false

    var body: some View {
        HStack(spacing: 5) {
            Circle()
                .fill(alert ? Theme.red : (on ? Theme.green : Theme.muted))
                .frame(width: 8, height: 8)
            Text(title)
                .font(.system(size: 11, weight: .bold))
                .foregroundStyle(alert ? Theme.red : Theme.muted)
        }
    }
}

struct AuroraBackground: View {
    var running: Bool

    var body: some View {
        ZStack {
            Theme.bg
            RadialGradient(
                colors: [(running ? Theme.green : Theme.accent).opacity(0.42), .clear],
                center: UnitPoint(x: 0.5, y: 0.22),
                startRadius: 20,
                endRadius: 340
            )
            RadialGradient(
                colors: [Color(red: 0.54, green: 0.36, blue: 0.96).opacity(0.22), .clear],
                center: .topLeading,
                startRadius: 0,
                endRadius: 260
            )
            RadialGradient(
                colors: [Color(red: 0.31, green: 0.27, blue: 0.90).opacity(0.20), .clear],
                center: .bottomTrailing,
                startRadius: 0,
                endRadius: 280
            )
        }
    }
}
