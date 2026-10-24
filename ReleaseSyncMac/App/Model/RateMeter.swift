import Observation
import ReleaseSyncKit

/// GitHub's allowance for requests as of the last answer, shared by everything that talks to GitHub so one counter tells the truth.
@MainActor
@Observable
final class RateMeter {
    private(set) var status = RateStatus()

    init(_ status: RateStatus = RateStatus()) { self.status = status }

    func update(from client: SourceClient) async { status = await client.rate }

    /// Under 150 left in the hour: optional extras (the Mac-download checks in Discover) pause so update checks can still run.
    var isTight: Bool { status.remaining >= 0 && status.remaining < 150 }
}
