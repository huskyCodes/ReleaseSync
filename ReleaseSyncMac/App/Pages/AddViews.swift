import ReleaseSyncKit
import SwiftUI

/// The window for adding a project: a preview of what it offers for the Mac, search results, or the question about changing the library.
struct AddSheet: View {
    @Environment(AddModel.self) private var add
    @Environment(LibraryModel.self) private var model

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            switch add.stage {
            case .closed: EmptyView()
            case .loading(let text):
                HStack(spacing: 12) { ProgressView().controlSize(.small); Text(text).font(.dim).foregroundStyle(Theme.dim) }.padding(.vertical, 30)
            case .failed(let message):
                Label(message, systemImage: "exclamationmark.triangle.fill").foregroundStyle(Theme.warn).fixedSize(horizontal: false, vertical: true)
            case .needsPermission:
                permission
            case .results(let query, let found):
                results(query, found)
            case .preview(let p):
                preview(p)
            }
            if case .preview = add.stage {} else { HStack { Spacer(); Button("Close") { add.close() }.buttonStyle(OutlineButtonStyle()) } }
        }
        .padding(24).frame(width: 520)
    }

    private var permission: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Let this Mac change your library?").font(.h2).foregroundStyle(Theme.text)
            Text("Adding an app saves it to your library, which is kept in your private GitHub Gist and shared with your other devices. This Mac will also show up in your list of devices. Your other devices keep working as they do now.")
                .font(.dim).foregroundStyle(Theme.dim).fixedSize(horizontal: false, vertical: true)
            HStack {
                Spacer()
                Button("Not now") { add.close() }.buttonStyle(OutlineButtonStyle())
                Button("Turn on and continue") { Task { await add.allowChangesAndContinue() } }.buttonStyle(PrimaryButtonStyle())
            }
        }
    }

    private func results(_ query: String, _ found: [RepoInfo]) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Results for “\(query)”").font(.h2).foregroundStyle(Theme.text)
            if found.isEmpty { Text("Nothing matched. Try other words, or paste a link to the project.").font(.dim).foregroundStyle(Theme.dim) }
            ScrollView {
                LazyVStack(spacing: 0) {
                    ForEach(found, id: \.fullName) { r in
                        Button { Task { await add.open(repo: r) } } label: {
                            HStack(spacing: 12) {
                                Avatar(name: r.name, size: 34, url: r.avatarUrl.flatMap(URL.init(string:)))
                                VStack(alignment: .leading, spacing: 1) {
                                    Text(r.fullName).font(.system(size: 13, weight: .semibold)).foregroundStyle(Theme.text).lineLimit(1)
                                    Text(isBlankText(r.description) ? " " : r.description!).font(.faint).foregroundStyle(Theme.faint).lineLimit(1)
                                }
                                Spacer(minLength: 8)
                                Image(systemName: "star.fill").font(.system(size: 10)).foregroundStyle(Theme.warn)
                                Text(r.stars.compact).font(.system(size: 12, weight: .semibold)).foregroundStyle(Theme.dim)
                            }
                            .padding(.vertical, 8).contentShape(Rectangle())
                        }.buttonStyle(.plain)
                        Divider().overlay(Theme.line.opacity(0.5))
                    }
                }
            }.frame(maxHeight: 360)
        }
    }

    private func preview(_ p: AddModel.Preview) -> some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(alignment: .top, spacing: 14) {
                Avatar(name: p.info.name, size: 54, url: model.isDemo ? nil : p.info.avatarUrl.flatMap(URL.init(string:)))
                VStack(alignment: .leading, spacing: 3) {
                    Text(p.info.name).font(.system(size: 19, weight: .semibold)).foregroundStyle(Theme.text)
                    Text(p.info.fullName).font(.faint).foregroundStyle(Theme.faint)
                    HStack(spacing: 8) {
                        Pill(text: RepoLinks.sourceName(p.info.source))
                        if p.info.stars > 0 { Label(p.info.stars.compact, systemImage: "star.fill").font(.system(size: 12, weight: .semibold)).foregroundStyle(Theme.dim).labelStyle(.titleAndIcon) }
                        if let l = p.info.language, !l.isEmpty { Pill(text: l) }
                    }.padding(.top, 4)
                }
                Spacer(minLength: 0)
            }
            if !isBlankText(p.info.description) { Text(p.info.description!).font(.dim).foregroundStyle(Theme.dim).fixedSize(horizontal: false, vertical: true) }

            VStack(alignment: .leading, spacing: 8) {
                if let latest = p.latest {
                    HStack { Text("Latest release").font(.dim).foregroundStyle(Theme.dim); Spacer(); Text(latest.tag + (latest.prerelease ? "  (pre-release)" : "")).font(.system(size: 13, weight: .semibold)).foregroundStyle(Theme.text) }
                    if let chosen = p.chosen {
                        HStack(alignment: .firstTextBaseline) {
                            Text("Download").font(.dim).foregroundStyle(Theme.dim)
                            Spacer()
                            if p.choices.count > 1 {
                                Menu {
                                    ForEach(p.choices, id: \.name) { c in Button(c.name + (c.size > 0 ? "  (\(Fmt.size(c.size)))" : "")) { add.choose(c) } }
                                } label: { Text(chosen.name).font(.system(size: 13, weight: .semibold)).lineLimit(1) }.menuStyle(.button).buttonStyle(.plain).fixedSize()
                            } else { Text(chosen.name).font(.system(size: 13, weight: .semibold)).foregroundStyle(Theme.text).lineLimit(1) }
                        }
                        if chosen.size > 0 { HStack { Text("Size").font(.dim).foregroundStyle(Theme.dim); Spacer(); Text(Fmt.size(chosen.size)).font(.system(size: 13)).foregroundStyle(Theme.text) } }
                    } else {
                        Label("No download for the Mac in this release. You can still add it to keep an eye on it.", systemImage: "exclamationmark.triangle.fill")
                            .font(.system(size: 12.5)).foregroundStyle(Theme.warn).fixedSize(horizontal: false, vertical: true)
                    }
                } else {
                    Text("This project hasn't published a release yet.").font(.dim).foregroundStyle(Theme.dim)
                }
            }
            .padding(14).background(Theme.soft, in: RoundedRectangle(cornerRadius: 12, style: .continuous))

            if p.inLibrary { Label("Already in your library", systemImage: "checkmark.circle.fill").foregroundStyle(Theme.success) }
            else if p.chosen != nil {
                @Bindable var add = add
                Toggle("Install it now", isOn: $add.installNow).toggleStyle(.switch)
            }
            HStack {
                Button("Cancel") { add.close() }.buttonStyle(OutlineButtonStyle())
                Spacer()
                if !p.inLibrary {
                    Button { Task { await add.saveForLater(p.info); add.close() } } label: { Label("Try later", systemImage: "bookmark") }.buttonStyle(OutlineButtonStyle())
                    Button { Task { await add.add() } } label: { Text(add.installNow && p.chosen != nil ? "Add and install" : "Add to library") }.buttonStyle(PrimaryButtonStyle())
                }
            }
        }
    }
}
