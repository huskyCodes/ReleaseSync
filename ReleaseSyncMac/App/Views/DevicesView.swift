import ReleaseSyncKit
import SwiftUI

struct DevicesView: View {
    @Environment(LibraryModel.self) private var model

    var body: some View {
        List {
            Section {
                ForEach(model.devices, id: \.id) { device in
                    HStack(spacing: 12) {
                        Image(systemName: device.symbolName).font(.title2).frame(width: 32).foregroundStyle(.secondary)
                        VStack(alignment: .leading, spacing: 1) {
                            Text(device.name.isEmpty ? "Unnamed device" : device.name)
                            Text(detail(device)).font(.callout).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Text("Seen \(device.lastSeen.friendlyAgo)").font(.callout).foregroundStyle(.tertiary)
                    }
                    .padding(.vertical, 3)
                }
            } header: {
                Text("Devices that keep this library in sync")
            } footer: {
                Text("This Mac isn't in the list yet. For now it only reads your library and never changes it.")
            }
        }
    }

    private func detail(_ d: SyncDevice) -> String {
        d.appVersion.isEmpty ? d.platform : "\(d.platform) · ReleaseSync \(d.appVersion)"
    }
}
