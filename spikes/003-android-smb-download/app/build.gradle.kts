plugins {
    id("com.android.application")
}

android {
    namespace = "spp.spike.s3"
    compileSdk = 36
    defaultConfig {
        applicationId = "spp.spike.s3"
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
    implementation("org.codelibs:jcifs:3.0.4")
    testImplementation("junit:junit:4.13.2")
}
