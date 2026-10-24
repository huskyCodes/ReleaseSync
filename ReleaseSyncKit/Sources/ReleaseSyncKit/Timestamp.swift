import Foundation

/// A moment in time, exact to 100 nanoseconds. It counts ticks since 0001-01-01 UTC, the way the C# engine does,
/// so two times are equal exactly when the C# engine would call them equal. (`Date` can't hold that precision.)
public struct Timestamp: Hashable, Comparable, Sendable {
    public var ticks: Int64

    public static let ticksPerSecond: Int64 = 10_000_000
    static let unixEpochTicks: Int64 = 621_355_968_000_000_000
    public static let zero = Timestamp(ticks: 0)

    public init(ticks: Int64) { self.ticks = ticks }

    /// `subTicks` are the 100 ns steps after the whole second.
    public init(unixSeconds: Int64, subTicks: Int64 = 0) {
        ticks = unixSeconds * Timestamp.ticksPerSecond + subTicks + Timestamp.unixEpochTicks
    }

    public init(_ date: Date) {
        let seconds = date.timeIntervalSince1970
        let whole = seconds.rounded(.down)
        self.init(unixSeconds: Int64(whole), subTicks: Int64(((seconds - whole) * 1e7).rounded()))
    }

    public static func now() -> Timestamp { Timestamp(Date()) }

    public var date: Date { Date(timeIntervalSince1970: Double(ticks - Timestamp.unixEpochTicks) / 1e7) }

    public static func < (a: Timestamp, b: Timestamp) -> Bool { a.ticks < b.ticks }

    public static func + (t: Timestamp, ticks: Int64) -> Timestamp { Timestamp(ticks: t.ticks + ticks) }
    public static func - (t: Timestamp, ticks: Int64) -> Timestamp { Timestamp(ticks: t.ticks - ticks) }
    public static func days(_ n: Int64) -> Int64 { n * 86_400 * ticksPerSecond }
    public static func hours(_ n: Int64) -> Int64 { n * 3_600 * ticksPerSecond }

    // MARK: text

    /// UTC with a trailing Z. Fractional seconds only when needed, up to 7 digits, trailing zeros removed.
    public var text: String {
        let unix = ticks - Timestamp.unixEpochTicks
        let seconds = Timestamp.floorDiv(unix, Timestamp.ticksPerSecond)
        let fraction = Timestamp.floorMod(unix, Timestamp.ticksPerSecond)
        let days = Timestamp.floorDiv(seconds, 86_400)
        let secondOfDay = Timestamp.floorMod(seconds, 86_400)
        let (y, m, d) = Timestamp.civil(fromDays: days)
        var s = Timestamp.pad(y, 4) + "-" + Timestamp.pad(m, 2) + "-" + Timestamp.pad(d, 2)
        s += "T" + Timestamp.pad(secondOfDay / 3600, 2) + ":" + Timestamp.pad(secondOfDay % 3600 / 60, 2) + ":" + Timestamp.pad(secondOfDay % 60, 2)
        if fraction != 0 {
            var digits = Timestamp.pad(fraction, 7)
            while digits.hasSuffix("0") { digits.removeLast() }
            s += "." + digits
        }
        return s + "Z"
    }

    /// Reads `2026-01-01T12:00:00.1234567Z`, an offset such as `+02:00`, or no zone at all (taken as UTC, like the merge does).
    public init?(text: String) {
        let c = Array(text.utf8)
        var i = 0
        func number(_ count: Int) -> Int64? {
            guard i + count <= c.count else { return nil }
            var v: Int64 = 0
            for k in i..<(i + count) { guard c[k] >= 48, c[k] <= 57 else { return nil }; v = v * 10 + Int64(c[k] - 48) }
            i += count
            return v
        }
        func expect(_ ch: Character) -> Bool { guard i < c.count, c[i] == ch.asciiValue! else { return false }; i += 1; return true }
        guard let y = number(4), expect("-"), let mo = number(2), expect("-"), let d = number(2), expect("T"),
              let h = number(2), expect(":"), let mi = number(2), expect(":"), let s = number(2) else { return nil }
        guard (1...12).contains(mo), (1...Timestamp.daysIn(month: mo, year: y)).contains(d), h < 24, mi < 60, s < 60 else { return nil }
        var fraction: Int64 = 0
        if i < c.count, c[i] == 46 {
            i += 1
            var digits = 0
            while i < c.count, c[i] >= 48, c[i] <= 57 {
                if digits < 7 { fraction = fraction * 10 + Int64(c[i] - 48) }
                digits += 1; i += 1
            }
            guard digits > 0 else { return nil }
            if digits < 7 { for _ in digits..<7 { fraction *= 10 } }
        }
        var offset: Int64 = 0
        if i < c.count {
            if c[i] == 90 { i += 1 }
            else if c[i] == 43 || c[i] == 45 {
                let sign: Int64 = c[i] == 45 ? -1 : 1
                i += 1
                guard let oh = number(2), expect(":"), let om = number(2), oh < 24, om < 60 else { return nil }
                offset = sign * (oh * 3600 + om * 60)
            }
        }
        guard i == c.count else { return nil }
        let days = Timestamp.days(fromCivil: y, mo, d)
        self.init(unixSeconds: days * 86_400 + h * 3600 + mi * 60 + s - offset, subTicks: fraction)
    }

    // MARK: calendar arithmetic (proleptic Gregorian, no time zones)

    static func floorDiv(_ a: Int64, _ b: Int64) -> Int64 { let q = a / b; return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q }
    static func floorMod(_ a: Int64, _ b: Int64) -> Int64 { a - floorDiv(a, b) * b }
    static func pad(_ v: Int64, _ width: Int) -> String { let s = String(v); return String(repeating: "0", count: max(0, width - s.count)) + s }
    static func daysIn(month: Int64, year: Int64) -> Int64 {
        switch month {
        case 2: return (year % 4 == 0 && (year % 100 != 0 || year % 400 == 0)) ? 29 : 28
        case 4, 6, 9, 11: return 30
        default: return 31
        }
    }

    /// Days since 1970-01-01 for a date.
    static func days(fromCivil year: Int64, _ month: Int64, _ day: Int64) -> Int64 {
        let y = month <= 2 ? year - 1 : year
        let era = floorDiv(y, 400)
        let yoe = y - era * 400
        let doy = (153 * (month + (month > 2 ? -3 : 9)) + 2) / 5 + day - 1
        let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy
        return era * 146_097 + doe - 719_468
    }

    static func civil(fromDays z0: Int64) -> (Int64, Int64, Int64) {
        let z = z0 + 719_468
        let era = floorDiv(z, 146_097)
        let doe = z - era * 146_097
        let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146_096) / 365
        let doy = doe - (365 * yoe + yoe / 4 - yoe / 100)
        let mp = (5 * doy + 2) / 153
        let d = doy - (153 * mp + 2) / 5 + 1
        let m = mp < 10 ? mp + 3 : mp - 9
        return (yoe + era * 400 + (m <= 2 ? 1 : 0), m, d)
    }
}
