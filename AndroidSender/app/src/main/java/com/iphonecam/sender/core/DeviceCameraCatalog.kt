package com.iphonecam.sender.core

import android.os.Build

/**
 * 手機硬體對照表：先偵測 [Build.MODEL]，再依機型列出「預期鏡頭」——
 * 包含原廠鎖住、第三方 App 無法開啟的鏡頭，讓 UI 能正確標示「可用／原廠未開放」。
 * 未收錄的機型回 null，改由實際列舉結果推斷（見 StreamEngine.enumerateLenses）。
 *
 * 擴充方式：在 profiles 內新增一條 Profile 即可（models 用前綴匹配，容納 /DS 等地區後綴）。
 */
object DeviceCameraCatalog {

    enum class LensKind(val label: String) {
        ULTRAWIDE("後置超廣角"),
        WIDE("後置主鏡"),
        TELE("後置長焦"),
        FRONT("前置鏡頭"),
    }

    /** 預期鏡頭：focal＝等效焦距(mm)，locked＝原廠鎖住（第三方 App 打不開）。 */
    data class ExpectedLens(val kind: LensKind, val focal: Float, val locked: Boolean)

    data class Profile(val models: List<String>, val lenses: List<ExpectedLens>)

    private val profiles = listOf(
        // Galaxy S21 / S21+ / S21 Ultra：後置長焦原廠鎖住，第三方 App 只開放主鏡與超廣角。
        Profile(
            models = listOf(
                "SM-G991", "SM-G996", "SM-G998",   // 國際版 S21 / S21+ / S21 Ultra
                "SC-51B", "SC-52B",                 // 日本 docomo
                "SCG09", "SCG10",                   // 日本 au
                "SM-G991B", "SM-G991U", "SM-G991W", // 常見變體（前綴已涵蓋，列出以利辨識）
            ),
            lenses = listOf(
                ExpectedLens(LensKind.ULTRAWIDE, 13f, false),
                ExpectedLens(LensKind.WIDE, 26f, false),
                ExpectedLens(LensKind.TELE, 29f, true),
                ExpectedLens(LensKind.FRONT, 26f, false),
            ),
        ),
    )

    /** 依機型取得對照表（前綴匹配，容納地區後綴）。 */
    fun forDevice(model: String = Build.MODEL): Profile? =
        profiles.firstOrNull { p -> p.models.any { model.startsWith(it, ignoreCase = true) } }
}
