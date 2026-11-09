// SPDX-License-Identifier: GPL-3.0-or-later
plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.plugin.compose")
    id("org.jetbrains.kotlin.plugin.serialization")
}

android {
    namespace = "app.steampoweredplayer"
    compileSdk = 36
    defaultConfig {
        applicationId = "app.steampoweredplayer"
        minSdk = 29
        targetSdk = 36
        versionCode = 1
        versionName = "0.4.0"
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        // x86_64: the development emulator. tools\deploy-phone.ps1 builds only the phone's own CPU, which is smaller and quicker.
        ndk { abiFilters += (findProperty("sppAbi") as String?)?.let { listOf(it) } ?: listOf("arm64-v8a", "x86_64") }
    }
    buildTypes {
        // What goes on the real phone: optimised like a release, signed with the local debug key so `adb install -r` can update it.
        // Not shrunk: R8 rules for JCIFS and libmpv's JNI aren't worth the risk for a build that only ever goes over USB.
        create("phone") {
            initWith(getByName("release"))
            signingConfig = signingConfigs.getByName("debug")
            matchingFallbacks += "release"
            (findProperty("sppIdSuffix") as String?)?.let { applicationIdSuffix = it }
        }
        // Instrumented tests clear the app's data and Gradle uninstalls what it tested. Their own id keeps the debug app's saved
        // server profiles and Keystore-sealed passwords out of reach.
        create("itest") {
            initWith(getByName("debug"))
            applicationIdSuffix = ".itest"
            matchingFallbacks += "debug"
        }
    }
    testBuildType = "itest"
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    buildFeatures { compose = true }
    packaging { resources { excludes += setOf("META-INF/*.md", "META-INF/versions/9/OSGI-INF/MANIFEST.MF") } }
    testOptions {
        unitTests.all {
            // The golden files are shared with the C# tests, byte for byte.
            it.systemProperty("spp.golden", rootProject.projectDir.resolve("../tests/fixtures/golden").canonicalPath)
        }
    }
}

dependencies {
    implementation(platform("androidx.compose:compose-bom:2026.03.01"))
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.activity:activity-compose:1.12.4")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.10.0")
    implementation("androidx.work:work-runtime-ktx:2.11.0")
    implementation("org.codelibs:jcifs:3.0.4")
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.12.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.11.0")
    debugImplementation("androidx.compose.ui:ui-tooling")
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.jetbrains.kotlinx:kotlinx-coroutines-test:1.11.0")
    androidTestImplementation(platform("androidx.compose:compose-bom:2026.03.01"))
    androidTestImplementation("androidx.compose.ui:ui-test-junit4")
    androidTestImplementation("androidx.test:core:1.7.0")
    androidTestImplementation("androidx.test:runner:1.7.0")
    androidTestImplementation("androidx.test.ext:junit:1.3.0")
    androidTestImplementation("androidx.test.espresso:espresso-core:3.7.0")
}
