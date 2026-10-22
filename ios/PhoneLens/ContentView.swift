import SwiftUI

private let accent = Color(red: 0.73, green: 0.84, blue: 0.67)

struct ContentView: View {
    @EnvironmentObject private var camera: CameraController
    @State private var beauty = false
    @State private var connection = false
    @State private var smoothing = 35.0
    @State private var tone = 20.0
    @State private var fill = 12.0

    var body: some View {
        GeometryReader { geometry in
            ScrollView {
                VStack(alignment: .leading, spacing: 20) {
                    header
                    viewfinder.frame(height: max(260, geometry.size.height - 270))
                    controls
                        .opacity(beauty || connection ? 0 : 1)
                        .accessibilityHidden(beauty || connection)
                }
                .padding(.horizontal, 22)
                .padding(.top, 12)
                .padding(.bottom, 20)
            }
            .scrollIndicators(.hidden)
        }
        .background(Color(red: 0.045, green: 0.05, blue: 0.05))
        .preferredColorScheme(.dark)
        .sensoryFeedback(.selection, trigger: beauty)
        .sensoryFeedback(.selection, trigger: connection)
        .sheet(isPresented: $beauty) {
            beautyPanel
                .presentationDetents([.height(430), .large])
                .presentationDragIndicator(.visible)
        }
        .sheet(isPresented: $connection) {
            connectionPanel
                .presentationDetents([.medium, .large])
                .presentationDragIndicator(.visible)
        }
    }

    private var header: some View {
        HStack {
            VStack(alignment: .leading, spacing: 3) {
                Text("PHONE LENS")
                    .font(.system(size: 10, weight: .semibold, design: .rounded))
                    .tracking(3).foregroundStyle(accent)
                Text("你的隨身攝影棚").font(.title3.weight(.semibold))
            }
            Spacer()
            Button { connection = true } label: {
                Image(systemName: "slider.horizontal.3")
                    .font(.system(size: 18))
                    .frame(width: 24, height: 28)
            }
            .buttonBorderShape(.circle)
            .modifier(NativeGlassButton())
            .controlSize(.large)
            .accessibilityLabel("連線與輸出設定").tint(.white)
        }
    }

    private var viewfinder: some View {
        ZStack {
            if camera.isConfigured {
                CameraPreview(session: camera.session)
            } else {
                LinearGradient(colors: [
                    Color(red: 0.23, green: 0.29, blue: 0.27),
                    Color(red: 0.12, green: 0.16, blue: 0.16),
                    Color(red: 0.06, green: 0.08, blue: 0.09)
                ], startPoint: .topLeading, endPoint: .bottomTrailing)
                GeometryReader { proxy in
                    Path { path in
                        for fraction in [1.0 / 3, 2.0 / 3] {
                            path.move(to: CGPoint(x: proxy.size.width * fraction, y: 0))
                            path.addLine(to: CGPoint(x: proxy.size.width * fraction, y: proxy.size.height))
                            path.move(to: CGPoint(x: 0, y: proxy.size.height * fraction))
                            path.addLine(to: CGPoint(x: proxy.size.width, y: proxy.size.height * fraction))
                        }
                    }.stroke(.white.opacity(0.06), lineWidth: 1)
                }
                VStack(spacing: 18) {
                    Image(systemName: "viewfinder")
                        .font(.system(size: 54, weight: .ultraLight))
                        .foregroundStyle(accent.opacity(0.85))
                    VStack(spacing: 6) {
                        Text("好畫面，從這裡開始").font(.headline)
                        Text(camera.status == .permissionDenied ? "允許相機權限以開啟預覽" : "連接實體 iPhone，開啟即時預覽")
                            .font(.caption).foregroundStyle(.white.opacity(0.65))
                    }
                }
            }
            VStack {
                HStack {
                    Label(camera.isConfigured ? "相機預覽" : "版面預覽", systemImage: "viewfinder")
                    Spacer()
                    Text(camera.isConfigured ? "前置" : "DEMO").tracking(1)
                }
                Spacer()
                HStack {
                    Label("原始畫面", systemImage: "circle.lefthalf.filled")
                    Spacer()
                    Text("PHONE LENS").tracking(2)
                }.foregroundStyle(.white.opacity(0.55))
            }
            .font(.caption2.weight(.medium))
            .foregroundStyle(.white.opacity(0.8))
            .padding(18)
        }
        .clipShape(RoundedRectangle(cornerRadius: 28, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: 28, style: .continuous)
                .strokeBorder(.white.opacity(0.1), lineWidth: 1)
        }
        .accessibilityElement(children: .combine)
    }

    private var controls: some View {
        VStack(spacing: 18) {
            HStack {
                VStack(alignment: .leading, spacing: 5) {
                    Label("尚未連接電腦", systemImage: "cable.connector")
                        .font(.subheadline.weight(.medium))
                    Text("輸出功能開發中").font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                Text("1080p / 30").font(.caption.monospacedDigit()).foregroundStyle(.secondary)
            }
            NativeGlassGroup {
              HStack(spacing: 12) {
                Button { beauty = true } label: {
                    VStack(spacing: 4) {
                        Label("美顏", systemImage: "sparkles").font(.subheadline.weight(.semibold))
                        Text("調整預設").font(.caption2).foregroundStyle(.secondary)
                    }
                    .frame(width: 76, height: 44)
                }
                .modifier(NativeGlassButton())
                .tint(.white)
                Button { connection = true } label: {
                    HStack(spacing: 10) {
                        Image(systemName: "desktopcomputer")
                        Text("連接電腦").fontWeight(.semibold)
                        Spacer(minLength: 0)
                        Image(systemName: "arrow.up.right").font(.caption.weight(.bold))
                    }
                    .frame(maxWidth: .infinity, minHeight: 44)
                    .foregroundStyle(.black)
                }
                .modifier(NativeGlassButton(prominent: true))
                .tint(accent)
              }
              .buttonBorderShape(.capsule)
            }
        }
    }

    private var beautyPanel: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                HStack(alignment: .top) {
                    VStack(alignment: .leading, spacing: 5) {
                        Text("自然美顏").font(.title2.weight(.semibold))
                        Text("保留紋理，微調好氣色").font(.subheadline).foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button("完成") { beauty = false }
                        .font(.subheadline.weight(.semibold)).tint(accent)
                        .modifier(NativeGlassButton())
                        .controlSize(.large)
                }
                VStack(spacing: 12) {
                    beautySlider("柔膚", icon: "drop", value: $smoothing)
                    beautySlider("膚色", icon: "circle.lefthalf.filled", value: $tone)
                    beautySlider("補光", icon: "sun.max", value: $fill)
                }
                HStack {
                    Label("僅調整預設，尚未套用影像", systemImage: "info.circle")
                        .font(.caption).foregroundStyle(.secondary)
                    Spacer()
                    Button("重設") { smoothing = 35; tone = 20; fill = 12 }
                        .font(.caption.weight(.semibold)).tint(accent)
                        .modifier(NativeGlassButton())
                        .controlSize(.large)
                }
            }.padding(24).padding(.top, 10)
        }
    }

    private func beautySlider(_ title: String, icon: String, value: Binding<Double>) -> some View {
        HStack(spacing: 14) {
            Image(systemName: icon).foregroundStyle(accent).frame(width: 22).accessibilityHidden(true)
            Text(title).font(.subheadline).frame(width: 36)
            Slider(value: value, in: 0...100, step: 1)
                .tint(accent).accessibilityLabel(title)
                .accessibilityValue("\(Int(value.wrappedValue))，僅預設")
            Text("\(Int(value.wrappedValue))")
                .font(.subheadline.monospacedDigit())
                .frame(width: 28, alignment: .trailing).foregroundStyle(.secondary)
        }.frame(minHeight: 48)
    }

    private var connectionPanel: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                HStack {
                    Text("連接電腦").font(.title2.weight(.semibold))
                    Spacer()
                    Button("完成") { connection = false }.tint(accent).frame(minHeight: 44)
                        .modifier(NativeGlassButton())
                }
                Label("視訊鏡頭輸出開發中", systemImage: "desktopcomputer").foregroundStyle(accent)
                Text("這個版本可預覽介面與調整美顏預設，尚無法將影像傳送到電腦。")
                    .foregroundStyle(.secondary)
                Divider()
                LabeledContent("鏡頭名稱", value: "Phone Lens Camera")
                LabeledContent("預定畫質", value: "1080p · 30 fps")
            }.padding(24)
        }
    }
}

// Native materials handle contrast / transparency preferences and touch feedback.
// Older OS versions keep system bordered buttons without imitating Liquid Glass.
private struct NativeGlassButton: ViewModifier {
    var prominent = false

    @ViewBuilder
    func body(content: Content) -> some View {
        if #available(iOS 26.0, *) {
            if prominent {
                content.buttonStyle(.glassProminent)
            } else {
                content.buttonStyle(.glass)
            }
        } else if prominent {
            content.buttonStyle(.borderedProminent)
        } else {
            content.buttonStyle(.bordered)
        }
    }
}

private struct NativeGlassGroup<Content: View>: View {
    @ViewBuilder var content: () -> Content

    @ViewBuilder var body: some View {
        if #available(iOS 26.0, *) {
            GlassEffectContainer(spacing: 12) { content() }
        } else {
            content()
        }
    }
}

#Preview {
    ContentView().environmentObject(CameraController())
}
