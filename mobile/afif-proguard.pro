# Go engine bindings (gomobile) are called from native code
-keep class libtailscale.** { *; }
-keep class go.** { *; }
-dontwarn libtailscale.**
