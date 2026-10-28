# Go engine bindings (gomobile) are called from native code
-keep class libtailscale.** { *; }
-keep class go.** { *; }
-dontwarn libtailscale.**

# WorkManager's Room database is created by reflection; R8 stripped its constructor (crash at start-up)
-keep class * extends androidx.room.RoomDatabase { <init>(); }
-keep class androidx.work.impl.WorkDatabase_Impl { *; }
