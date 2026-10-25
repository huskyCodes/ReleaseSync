import Foundation
import Observation
import ReleaseSyncKit

/// What the Discover page shows: a list of projects for a mode and category, built a few at a time so GitHub is asked as little as possible.
/// The Mac-download check (about one request per project, remembered for three days) only runs for the projects that are about to be shown.
@MainActor
@Observable
final class DiscoverModel {
    private(set) var items: [DiscoverItem] = []
    private(set) var isBusy = false
    private(set) var error: String?
    private(set) var status = ""
    private(set) var canLoadMore = false
    private(set) var isDemo = false

    private let service: DiscoverService
    private let client: SourceClient
    private let prefs: ViewPrefs
    private let meter: RateMeter
    private var ordered: [RepoInfo] = []
    private var next = 0
    private var baseline: DiscoverService.Baseline?
    private var filtering = false
    private var task: Task<Void, Never>?
    private var generation = 0
    private var prefetchTask: Task<Void, Never>?
    private var prefetchTag: String?
    /// How long background preparing waits between checks that the person's own searches have finished (tests make it short).
    var prefetchPause: Duration = .milliseconds(1500)

    init(client: SourceClient, prefs: ViewPrefs, meter: RateMeter, blobs: BlobStore? = nil) {
        self.client = client; self.prefs = prefs; self.meter = meter
        self.service = DiscoverService(client: client, blobs: blobs)
    }

    /// Made-up projects to look at without an account.
    init(showing items: [DiscoverItem], client: SourceClient, prefs: ViewPrefs, meter: RateMeter) {
        self.client = client; self.prefs = prefs; self.meter = meter
        self.service = DiscoverService(client: client)
        self.items = items; self.isDemo = true
        self.status = "\(items.count) shown  ·  updated 4 minutes ago  ·  movement vs yesterday"
    }

    private var firstBatch: Int { prefs.discoverLight ? 15 : 36 }

    /// Opens the list for the current mode and category (from what was saved, unless `force`).
    func load(force: Bool = false) {
        guard !isDemo else { return }
        task?.cancel()
        generation += 1
        let mine = generation
        items = []; error = nil; isBusy = true; canLoadMore = false; status = "Loading…"; ordered = []; next = 0
        let mode = prefs.discoverMode, category = DiscoverCatalog.find(prefs.discoverCategory)
        task = Task { [weak self] in
            guard let self else { return }
            do {
                let (candidates, fetched) = try await service.candidates(mode, category, force: force)
                try Task.checkCancellation()
                let key = DiscoverService.key(mode, category.key)
                baseline = await service.baseline(key, week: prefs.discoverCompareWeek)
                ordered = DiscoverService.order(candidates, mode: mode, baseline: baseline)
                await meter.update(from: client)
                let hasToken = await client.hasToken
                filtering = prefs.discoverMacOnly && hasToken && mode != .featured && !meter.isTight
                try await fill(firstBatch)
                try Task.checkCancellation()
                if mode == .featured && hasToken && !prefs.discoverLight && !meter.isTight { await markMacBuilds() }
                await service.record(key, items)
                startPrefetch(category, mode, hasToken: hasToken)
                var parts = ["updated " + fetched.friendlyAgo]
                if let b = baseline { parts.append("movement vs " + (b.age / 3600 < 30 ? "yesterday" : b.days >= 6 ? "last week" : "\(Int(b.days.rounded())) days ago")) }
                else if mode != .featured { parts.append("movement arrows appear on your next visit: today's ranking is saved") }
                status = "\(items.count) shown  ·  " + parts.joined(separator: "  ·  ") + (meter.isTight && prefs.discoverMacOnly && hasToken ? "  ·  Mac check paused to save your requests" : "")
                if items.isEmpty {
                    error = filtering ? "Nothing here has Mac downloads right now. Try another tab or category, or turn off “Only projects with Mac downloads”." : "Nothing matched here right now. Try another tab or category."
                }
            } catch is CancellationError {
                return
            } catch {
                guard mine == generation else { return }
                self.error = error.localizedDescription; status = ""
            }
            if mine == generation { isBusy = false; await meter.update(from: client) }
        }
    }

    func loadMore() {
        guard !isBusy, canLoadMore, !isDemo else { return }
        generation += 1
        let mine = generation
        isBusy = true; canLoadMore = false
        task = Task { [weak self] in
            guard let self else { return }
            do { try await fill(firstBatch) } catch is CancellationError { return } catch { self.error = error.localizedDescription }
            guard mine == generation else { return }
            isBusy = false
            let rest = self.status.range(of: "  ·  ").map { String(self.status[$0.upperBound...]) }
            self.status = "\(self.items.count) shown" + (rest.map { "  ·  " + $0 } ?? "")
            await meter.update(from: client)
        }
    }

    /// Adds the next batch of the list, skipping projects with nothing for the Mac when that filter is on.
    private func fill(_ want: Int) async throws {
        var added = 0
        func add(_ repo: RepoInfo, mac: Bool?) {
            var item = DiscoverService.annotate(repo, rank: items.count + 1, baseline: baseline)
            item.hasMacBuild = mac
            items.append(item); added += 1
        }
        if !filtering {
            while next < ordered.count && added < want { add(ordered[next], mac: nil); next += 1 }
        } else {
            var checkedCount = 0
            let limit = prefs.discoverLight ? 40 : 100
            while next < ordered.count && added < want && checkedCount < limit {
                try Task.checkCancellation()
                let batchStart = next
                let batch = Array(ordered[next..<min(next + 14, ordered.count)])
                next += batch.count; checkedCount += batch.count
                status = "Checking which projects have Mac downloads… \(items.count) found"
                let service = self.service
                let verdicts = await withTaskGroup(of: (Int, Bool?).self) { group -> [Bool?] in
                    for (i, repo) in batch.enumerated() { group.addTask { (i, try? await service.hasMacBuild(repo)) } }       // rate-limited or unreachable: keep it rather than hide it
                    var out = [Bool?](repeating: nil, count: batch.count)
                    for await (i, v) in group { out[i] = v }
                    return out
                }
                for k in batch.indices {
                    if added >= want { next = batchStart + k; break }      // enough for now: the rest stay for "Load more"
                    if verdicts[k] == false { continue }
                    add(batch[k], mac: verdicts[k])
                }
            }
            await service.saveMacChecks()
        }
        canLoadMore = next < ordered.count
    }

    /// For hand-picked projects, which are always shown, adds a "Mac" badge to the ones confirmed to have Mac downloads.
    private func markMacBuilds() async {
        for i in items.indices {
            if Task.isCancelled { return }
            guard let mac = try? await service.hasMacBuild(items[i].repo) else { return }       // allowance used up: stop quietly
            items[i].hasMacBuild = mac
        }
        await service.saveMacChecks()
    }

    func setMode(_ m: DiscoverMode) { guard m != prefs.discoverMode else { return }; prefs.discoverMode = m; load() }
    func setCategory(_ c: String) { guard c != prefs.discoverCategory else { return }; prefetchTask?.cancel(); prefs.discoverCategory = c; load() }
    func setCompare(week: Bool) { guard week != prefs.discoverCompareWeek else { return }; prefs.discoverCompareWeek = week; load() }
    func setMacOnly(_ on: Bool) { prefs.discoverMacOnly = on; load() }
    func setLight(_ on: Bool) {
        prefs.discoverLight = on
        if on { prefetchTask?.cancel(); prefetchTag = nil }          // stop preparing the other tabs right away
        if !isDemo, !items.isEmpty { load() }
    }

    /// Full mode only, with a token and plenty of allowance left (at least 1,500): quietly prepares the other tabs of this category
    /// (their lists, and which projects have Mac downloads) while the person looks at this one, so switching tabs is instant.
    /// It waits for the person's own searches to finish first, and stops as soon as the allowance runs low.
    private func startPrefetch(_ category: DiscoverCategory, _ current: DiscoverMode, hasToken: Bool) {
        let remaining = meter.status.remaining
        guard !isDemo, hasToken, current != .featured, !prefs.discoverLight, !(remaining >= 0 && remaining < 1500) else { return }
        let verify = prefs.discoverMacOnly, week = prefs.discoverCompareWeek
        let tag = "\(category.key)|\(verify)|\(week)"
        if prefetchTag == tag, prefetchTask?.isCancelled == false { return }          // already preparing this category
        prefetchTask?.cancel()
        prefetchTag = tag
        let service = self.service, client = self.client, pause = prefetchPause
        prefetchTask = Task.detached(priority: .utility) { [weak self] in
            for m in [DiscoverMode.trending, .popular, .new, .updated] where m != current {
                var waited = 0
                while waited < 60 && !Task.isCancelled {
                    if await service.canPrefetch() { break }
                    waited += 1; try? await Task.sleep(for: pause)
                }      // the person's own searches go first
                let roomLeft = await service.canPrefetch()
                if Task.isCancelled || !roomLeft { return }
                guard let (items, _) = try? await service.candidates(m, category, force: false) else { continue }
                if verify {
                    let baseline = await service.baseline(DiscoverService.key(m, category.key), week: week)
                    let ordered = DiscoverService.order(items, mode: m, baseline: baseline)
                    await service.preverify(Array(ordered.prefix(48)))
                }
            }
            await self?.prefetchFinished(client)
        }
    }

    private func prefetchFinished(_ client: SourceClient) async { await meter.update(from: client) }

    func stopPrefetch() { prefetchTask?.cancel(); prefetchTag = nil }

    /// Waits for the tabs being prepared in the background (used by tests).
    func waitForPrefetch() async { await prefetchTask?.value }
    /// Waits for the list that is being built (used by tests).
    func waitUntilIdle() async { await task?.value }

    func appeared() { if items.isEmpty && !isBusy && error == nil && !isDemo { load() } }
}
