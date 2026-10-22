import SwiftUI

@main
struct PhoneLensApp: App {
    @StateObject private var camera = CameraController()

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environmentObject(camera)
                .task { camera.prepare() }
        }
    }
}
