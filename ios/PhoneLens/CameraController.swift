import AVFoundation
import Combine

enum CameraStatus: Equatable {
    case preparing
    case previewReady
    case permissionDenied
    case unavailable

    var title: String {
        switch self {
        case .preparing: "正在準備相機…"
        case .previewReady: "相機預覽已就緒"
        case .permissionDenied: "請允許相機權限"
        case .unavailable: "此裝置沒有可用相機"
        }
    }
}

final class CameraController: NSObject, ObservableObject {
    let session = AVCaptureSession()

    @Published private(set) var status: CameraStatus = .preparing
    @Published private(set) var isConfigured = false

    private let sessionQueue = DispatchQueue(label: "tw.phonecamera.phonelens.camera")
    // Accessed only on sessionQueue; published UI state is updated on the main queue.
    private var configuredOnQueue = false

    func prepare() {
        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .authorized:
            configureAndStart()
        case .notDetermined:
            AVCaptureDevice.requestAccess(for: .video) { [weak self] granted in
                if granted {
                    self?.configureAndStart()
                } else {
                    self?.publish(.permissionDenied, configured: false)
                }
            }
        case .denied, .restricted:
            publish(.permissionDenied, configured: false)
        @unknown default:
            publish(.unavailable, configured: false)
        }
    }

    private func configureAndStart() {
        sessionQueue.async { [weak self] in
            guard let self else { return }
            guard !self.configuredOnQueue else {
                if !self.session.isRunning { self.session.startRunning() }
                return
            }

            self.session.beginConfiguration()
            self.session.sessionPreset = .hd1920x1080

            guard let device = AVCaptureDevice.default(
                .builtInWideAngleCamera,
                for: .video,
                position: .front
            ) else {
                self.session.commitConfiguration()
                self.publish(.unavailable, configured: false)
                return
            }

            do {
                let input = try AVCaptureDeviceInput(device: device)
                guard self.session.canAddInput(input) else {
                    self.session.commitConfiguration()
                    self.publish(.unavailable, configured: false)
                    return
                }
                self.session.addInput(input)
                self.session.commitConfiguration()
                self.configuredOnQueue = true
                self.session.startRunning()
                self.publish(.previewReady, configured: true)
            } catch {
                self.session.commitConfiguration()
                self.publish(.unavailable, configured: false)
            }
        }
    }

    private func publish(_ status: CameraStatus, configured: Bool) {
        DispatchQueue.main.async {
            self.status = status
            self.isConfigured = configured
        }
    }
}
