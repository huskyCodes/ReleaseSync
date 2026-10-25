import ReleaseSyncKit
import SwiftUI

/// "Find my devices on this network": the switch, the paired devices and the controls from docs/LAN-PRESENCE.md (sections 12 and 13).
/// The networking behind it is not built yet; until it is, the screen says so and the switch stays off.
struct FindDevicesSection: View {
    @Environment(LanModel.self) private var lan
    @Environment(Toasts.self) private var toasts
    @State private var askTurnOn = false
    @State private var askRemove: LanPairRecord?
    @State private var askRemoveAll = false
    @State private var showBlocked = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Find my devices on this network").font(.h2).foregroundStyle(Theme.text)
            Text("See which of your own devices are on this network right now, and what each one has installed. Only devices you pair with can see anything, and nothing goes over the internet.")
                .font(.dim).foregroundStyle(Theme.dim).fixedSize(horizontal: false, vertical: true)

            Toggle(isOn: Binding(get: { lan.enabled }, set: { on in if on { askTurnOn = true } else { lan.setEnabled(false) } })) {
                Text("Find my devices on this network").font(.system(size: 13, weight: .semibold)).foregroundStyle(Theme.text)
            }
            .toggleStyle(.switch).disabled(!lan.isAvailable)

            Text(status).font(.faint).foregroundStyle(Theme.faint).fixedSize(horizontal: false, vertical: true)

            if !lan.pairs.isEmpty { pairedList }

            HStack(spacing: 10) {
                Button { lan.pairingRole = .host; lan.pairingStep = .connecting } label: { Label("Add a device", systemImage: "plus") }
                    .buttonStyle(OutlineButtonStyle()).disabled(!lan.isAvailable || !lan.enabled || lan.pairs.count >= Lan.maxPaired)
                if !lan.pairs.isEmpty {
                    Button(role: .destructive) { askRemoveAll = true } label: { Text("Remove all paired devices") }.buttonStyle(OutlineButtonStyle())
                }
            }
            blockedLine
        }
        .alert("Look for your devices on this network?", isPresented: $askTurnOn) {
            Button("Turn on") { lan.setEnabled(true) }
            Button("Not now", role: .cancel) {}
        } message: {
            Text("ReleaseSync looks for your own devices on this Wi-Fi so it can show which ones are on. macOS will ask permission first. Nothing goes over the internet, and only devices you pair with can see anything.")
        }
        .confirmationDialog("Remove \(askRemove?.peerName ?? "this device")?", isPresented: Binding(get: { askRemove != nil }, set: { if !$0 { askRemove = nil } }), titleVisibility: .visible) {
            Button("Remove", role: .destructive) { if let p = askRemove { attempt { try lan.remove(p.pairId) } } }
            Button("Cancel", role: .cancel) {}
        } message: { Text("This Mac forgets it, and it will have to be paired again to show up. Remove it on your other devices too if you lost it.") }
        .confirmationDialog("Remove all paired devices?", isPresented: $askRemoveAll, titleVisibility: .visible) {
            Button("Remove all", role: .destructive) { attempt { try lan.removeAll() } }
            Button("Cancel", role: .cancel) {}
        } message: { Text("Each one will have to be paired again.") }
        .sheet(isPresented: Binding(get: { lan.pairingStep != nil }, set: { if !$0 { lan.pairingStep = nil } })) { PairingSheet() }
    }

    private var status: String {
        if !lan.isAvailable { return "Coming soon. This version can't look for devices on your network yet, so nothing is listening or announcing." }
        if let note = lan.networkNote { return note }
        if !lan.enabled { return "Off. Turn this on to see which of your devices are on this network." }
        return lan.pairs.isEmpty ? "Looking. Add a device to get started." : "Looking for your devices on this network…"
    }

    private var pairedList: some View {
        VStack(spacing: 0) {
            ForEach(Array(lan.pairs.enumerated()), id: \.element.pairId) { i, p in
                HStack(spacing: 14) {
                    Image(systemName: SyncDevice(id: "", name: "", platform: p.peerPlatform, appVersion: "").symbolName).font(.system(size: 17)).foregroundStyle(Theme.dim).frame(width: 28)
                    VStack(alignment: .leading, spacing: 1) {
                        Text(p.peerName.isEmpty ? "Unnamed device" : p.peerName).font(.system(size: 13, weight: .semibold)).foregroundStyle(Theme.text)
                        Text("\(p.peerPlatform)  ·  paired \(p.createdAt.friendlyAgo)").font(.faint).foregroundStyle(Theme.faint)
                    }
                    Spacer()
                    if p.paused { Pill(text: "Paused", kind: .warn) }
                    Button(p.paused ? "Resume" : "Pause") { lan.setPaused(p.pairId, !p.paused) }.buttonStyle(OutlineButtonStyle(height: 28))
                        .help(p.paused ? "Connect to this device again" : "Don't connect to this device until you resume (keeps the pairing)")
                    Button("Remove") { askRemove = p }.buttonStyle(OutlineButtonStyle(height: 28))
                }
                .padding(.vertical, 10)
                if i < lan.pairs.count - 1 { Divider().overlay(Theme.line.opacity(0.6)) }
            }
        }
        .padding(.horizontal, 14)
        .background(Theme.input, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(Theme.line, lineWidth: 1))
    }

    @ViewBuilder private var blockedLine: some View {
        let recent = lan.blockedInLastHour()
        if lan.isAvailable && lan.enabled || !recent.isEmpty {
            HStack(spacing: 8) {
                Text(recent.isEmpty ? "No unknown connection attempts in the last hour." : "\(recent.count) unknown connection \(recent.count == 1 ? "attempt was" : "attempts were") blocked in the last hour.")
                    .font(.faint).foregroundStyle(Theme.faint)
                if !recent.isEmpty {
                    Button("Show") { showBlocked = true }.buttonStyle(.plain).font(.system(size: 11.5, weight: .semibold)).foregroundStyle(Theme.accentText)
                        .popover(isPresented: $showBlocked) {
                            VStack(alignment: .leading, spacing: 6) {
                                Text("Kept in memory only and cleared when you quit.").font(.faint).foregroundStyle(Theme.faint)
                                ForEach(recent.suffix(30).reversed()) { b in
                                    HStack { Text(b.at.formatted(date: .omitted, time: .standard)); Spacer(minLength: 16); Text(b.address).textSelection(.enabled) }.font(.system(size: 12, design: .monospaced))
                                }
                            }.padding(14).frame(minWidth: 300)
                        }
                }
            }
        }
    }

    private func attempt(_ work: () throws -> Void) {
        do { try work() } catch { toasts.show(error.localizedDescription, .error) }
    }
}

/// The Add a device window: the six digits are shown on both devices and the user confirms on both that they match (docs/LAN-PRESENCE.md, section 5).
struct PairingSheet: View {
    @Environment(LanModel.self) private var lan

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Add a device").font(.h2).foregroundStyle(Theme.text)
            switch lan.pairingStep ?? .connecting {
            case .connecting:
                Text(lan.pairingRole == .host ? "Open ReleaseSync on the other device and choose “Join a device”, then pick this Mac. This window closes by itself after two minutes."
                                              : "Looking for devices that are ready to pair…").font(.dim).foregroundStyle(Theme.dim)
                ProgressView().controlSize(.small)
            case .confirm(let sas, let name, let platform):
                Text(lan.pairingRole == .host ? "Pairing with \(name) (\(platform))." : "Pairing with \(name) (\(platform)).").font(.system(size: 13, weight: .semibold)).foregroundStyle(Theme.text)
                Text(LanPairingStep.spaced(sas)).font(.system(size: 40, weight: .semibold, design: .rounded)).monospacedDigit().foregroundStyle(Theme.text).frame(maxWidth: .infinity)
                    .padding(.vertical, 8).background(Theme.soft, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
                Text("Does the other device show the same digits? Only tap Yes if both screens match exactly.").font(.dim).foregroundStyle(Theme.dim).fixedSize(horizontal: false, vertical: true)
                HStack {
                    Button("No, cancel") { lan.pairingStep = .failed(.declinedHere) }.buttonStyle(OutlineButtonStyle())
                    Button("Yes, they match") { lan.pairingStep = .waitingForPeer }.buttonStyle(PrimaryButtonStyle())
                }
            case .waitingForPeer:
                Text("Waiting for the other device to confirm…").font(.dim).foregroundStyle(Theme.dim)
                ProgressView().controlSize(.small)
            case .paired:
                Label("Paired", systemImage: "checkmark.circle.fill").foregroundStyle(Theme.success)
            case .failed(let why):
                Label(message(why), systemImage: "exclamationmark.triangle.fill").foregroundStyle(Theme.warn).fixedSize(horizontal: false, vertical: true)
            }
            HStack { Spacer(); Button("Close") { lan.pairingStep = nil }.buttonStyle(OutlineButtonStyle()) }
        }
        .padding(24).frame(width: 420)
    }

    private func message(_ why: LanPairingFailure) -> String {
        switch why {
        case .busy: return "That device is already pairing with something else. Try again in a moment."
        case .timedOut: return "That took too long. Start again."
        case .declinedHere: return "You said the digits didn't match, so nothing was saved."
        case .declinedByPeer: return "The other device said the digits didn't match, so nothing was saved."
        case .notMatching: return "The digits didn't match. Someone else may be on this network. Nothing was saved."
        case .damaged: return "Something went wrong while pairing. Nothing was saved."
        }
    }
}
