// AFIF Phone — the Tailscale engine and its sign-in/status screens as a library module.
// Source: tailscale-android (BSD-3-Clause), built against Linphone's Gradle/Kotlin versions.
import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    id("com.android.library")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
    id("org.jetbrains.kotlin.plugin.serialization")
}

val tsVersion: String = providers.gradleProperty("tailscaleVersion").getOrElse("unknown")
val afifAppId: String = providers.gradleProperty("afifAppId").getOrElse("com.afiftech.phone")

android {
    namespace = "com.tailscale.ipn"
    compileSdk = 37

    defaultConfig {
        minSdk = 28
        consumerProguardFiles("proguard-rules.pro", "afif-proguard.pro")
        manifestPlaceholders["leanbackRequired"] = "false"
        buildConfigField("boolean", "USE_GOOGLE_DNS_FALLBACK", "true")
        buildConfigField("String", "VERSION_NAME", "\"$tsVersion\"")
        buildConfigField("String", "APPLICATION_ID", "\"$afifAppId\"")
    }

    buildFeatures {
        buildConfig = true
        compose = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    lint { abortOnError = false }
}

kotlin { compilerOptions { jvmTarget.set(JvmTarget.JVM_17) } }

dependencies {
    implementation(tslibs.androidx.fragment)
    implementation(tslibs.androidx.core)
    implementation(tslibs.androidx.coreKtx)
    implementation(tslibs.androidx.browser)
    implementation(tslibs.androidx.security.crypto)
    implementation(tslibs.androidx.work.runtime)
    implementation(tslibs.androidx.work.runtimeKtx)

    implementation(tslibs.kotlinx.serialization.json)
    implementation(tslibs.kotlinx.coroutines.core)
    implementation(tslibs.androidx.room.ktx)
    runtimeOnly(tslibs.kotlinx.coroutines.android)
    implementation(tslibs.kotlin.reflect)

    implementation(platform(tslibs.androidx.compose.bom))
    implementation(tslibs.androidx.compose.material3)
    implementation(tslibs.androidx.compose.material.icons)
    implementation(tslibs.androidx.compose.ui)
    implementation(tslibs.androidx.compose.ui.toolingPreview)
    implementation(tslibs.androidx.lifecycle.viewmodel.ktx)
    implementation(tslibs.androidx.activity.compose)
    implementation(tslibs.accompanist.permissions)
    implementation(tslibs.accompanist.systemuicontroller)
    implementation(tslibs.androidx.core.splashscreen)
    implementation(tslibs.androidx.compose.animation)

    implementation(tslibs.androidx.navigation.compose)
    implementation(tslibs.androidx.navigation.ui)

    implementation(tslibs.coil.compose)
    implementation(tslibs.zxing.core)
    implementation(tslibs.vico.compose)
    implementation(tslibs.vico.composeM3)

    // the Go engine (WireGuard + Tailscale control client), built by the CI from the same tailscale-android
    // commit and published to the build's local Maven folder
    api("com.tailscale:libtailscale:$tsVersion@aar")
}
