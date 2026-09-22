// The replay scene removes HoloKit tracking components. IL2CPP still retains their
// P/Invoke references. Resolve them only for Simulator, and fail loudly if called.
#include <TargetConditionals.h>
#if TARGET_OS_SIMULATOR
#include <cstdint>
#include <cstdio>
#include <cstdlib>
[[noreturn]] static void UnsupportedHeadTracking()
{
    std::fputs("Body Oracle: HoloKit head tracking is unavailable in Simulator. Use the video replay scene.\n", stderr);
    std::abort();
}
extern "C" {
intptr_t HoloKit_LowLatencyTracking_init() { UnsupportedHeadTracking(); }
void HoloKit_LowLatencyTracking_initHeadTracker(intptr_t) { UnsupportedHeadTracking(); }
void HoloKit_LowLatencyTracking_pauseHeadTracker(intptr_t) { UnsupportedHeadTracking(); }
void HoloKit_LowLatencyTracking_resumeHeadTracker(intptr_t) { UnsupportedHeadTracking(); }
void HoloKit_LowLatencyTracking_addSixDoFData(intptr_t, int64_t, float*, float*) { UnsupportedHeadTracking(); }
void HoloKit_LowLatencyTracking_getHeadTrackerPose(intptr_t, float*, float*) { UnsupportedHeadTracking(); }
void HoloKit_LowLatencyTracking_delete(intptr_t) { UnsupportedHeadTracking(); }
}
#endif
