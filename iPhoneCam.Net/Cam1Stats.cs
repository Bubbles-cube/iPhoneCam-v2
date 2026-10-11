namespace iPhoneCam.Net;

/// <summary>每秒統計快照（PC 端顯示用）。</summary>
public sealed record Cam1Stats(
    string Remote,
    long VideoFrames,
    long VideoBytes,
    long ControlMsgs,
    double Fps,
    double Mbps);
