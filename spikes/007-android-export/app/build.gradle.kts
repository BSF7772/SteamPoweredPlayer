plugins {
    id("com.android.application")
}

android {
    namespace = "spp.spike.s7"
    compileSdk = 36
    defaultConfig {
        applicationId = "spp.spike.s7"
        minSdk = 29
        targetSdk = 36
        versionCode = 1
        versionName = "0.1"
        ndk { abiFilters += "arm64-v8a" }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    packaging { resources { excludes += setOf("META-INF/*.md", "META-INF/versions/9/OSGI-INF/MANIFEST.MF") } }
}

dependencies {
    implementation("androidx.media3:media3-muxer:1.11.1")
    implementation("androidx.media3:media3-transformer:1.11.1")
    implementation("androidx.media3:media3-effect:1.11.1")
    implementation("androidx.media3:media3-common:1.11.1")
    testImplementation("junit:junit:4.13.2")
}
