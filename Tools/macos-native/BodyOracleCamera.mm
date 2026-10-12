// Zero-copy macOS camera for Body Oracle.
// AVFoundation delivers IOSurface-backed pixel buffers; CVMetalTextureCache wraps each one as an
// MTLTexture on Unity's own Metal device, so no frame is copied through the CPU. Build with
// Tools/macos-native/build.sh, which writes Assets/Plugins/macOS/BodyOracleMac.bundle.
#import <AVFoundation/AVFoundation.h>
#import <CoreVideo/CoreVideo.h>
#import <Metal/Metal.h>
#include "IUnityInterface.h"
#include "IUnityGraphicsMetal.h"

// Frames handed to Unity stay retained until this many newer frames have been acquired, so a
// texture is never recycled while a command buffer that samples it may still be in flight.
enum { BOHeldFrames = 4 };

static IUnityGraphicsMetalV1 *unityMetal;

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces *interfaces)
{
    unityMetal = UNITY_GET_INTERFACE(interfaces, IUnityGraphicsMetalV1);
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload(void) { unityMetal = NULL; }

@interface BOCamera : NSObject <AVCaptureVideoDataOutputSampleBufferDelegate>
@end

@implementation BOCamera
{
    AVCaptureSession *_session;
    dispatch_queue_t _queue;
    CVMetalTextureCacheRef _cache;
    CVMetalTextureRef _latest;          // newest frame, written on the capture queue
    uint64_t _latestId;
    CVMetalTextureRef _held[BOHeldFrames];
    int _heldCursor;
    uint64_t _acquiredId;
    NSString *_error;
}

- (instancetype)initWithDevice:(id<MTLDevice>)device name:(NSString *)name width:(int)width height:(int)height fps:(int)fps
{
    if (!(self = [super init])) return nil;
    if (CVMetalTextureCacheCreate(kCFAllocatorDefault, NULL, device, NULL, &_cache) != kCVReturnSuccess)
    {
        _error = @"Could not create a Metal texture cache.";
        return self;
    }
    AVCaptureDevice *camera = nil;
    // The same enumeration WebCamTexture.devices reports, on every supported macOS version.
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
    for (AVCaptureDevice *candidate in [AVCaptureDevice devicesWithMediaType:AVMediaTypeVideo])
        if ([candidate.localizedName isEqualToString:name]) { camera = candidate; break; }
#pragma clang diagnostic pop
    if (camera == nil) camera = [AVCaptureDevice defaultDeviceWithMediaType:AVMediaTypeVideo];
    if (camera == nil) { _error = @"No camera found."; return self; }

    NSError *error = nil;
    AVCaptureDeviceInput *input = [AVCaptureDeviceInput deviceInputWithDevice:camera error:&error];
    if (input == nil) { _error = error.localizedDescription ?: @"Could not open the camera."; return self; }

    _session = [AVCaptureSession new];
    [_session beginConfiguration];
    // Requested size, only when the camera offers it; forcing a size would stretch other aspects.
    if (width == 1280 && height == 720 && [_session canSetSessionPreset:AVCaptureSessionPreset1280x720])
        _session.sessionPreset = AVCaptureSessionPreset1280x720;
    if ([_session canAddInput:input]) [_session addInput:input];
    AVCaptureVideoDataOutput *output = [AVCaptureVideoDataOutput new];
    // BGRA is what Unity samples directly; Metal compatibility makes the buffers IOSurface-backed.
    output.videoSettings = @{
        (id)kCVPixelBufferPixelFormatTypeKey: @(kCVPixelFormatType_32BGRA),
        (id)kCVPixelBufferMetalCompatibilityKey: @YES,
    };
    output.alwaysDiscardsLateVideoFrames = YES;
    _queue = dispatch_queue_create("org.realitydeslab.body-oracle.camera", DISPATCH_QUEUE_SERIAL);
    [output setSampleBufferDelegate:self queue:_queue];
    if ([_session canAddOutput:output]) [_session addOutput:output];
    else { _error = @"The camera does not accept a video output."; [_session commitConfiguration]; _session = nil; return self; }
    [_session commitConfiguration];

    if ([camera lockForConfiguration:nil])
    {
        CMTime duration = CMTimeMake(1, fps);
        for (AVFrameRateRange *range in camera.activeFormat.videoSupportedFrameRateRanges)
            if (range.minFrameRate <= fps && fps <= range.maxFrameRate)
            {
                camera.activeVideoMinFrameDuration = duration;
                camera.activeVideoMaxFrameDuration = duration;
                break;
            }
        [camera unlockForConfiguration];
    }
    [_session startRunning];
    return self;
}

- (void)captureOutput:(AVCaptureOutput *)output didOutputSampleBuffer:(CMSampleBufferRef)sample fromConnection:(AVCaptureConnection *)connection
{
    CVPixelBufferRef pixels = CMSampleBufferGetImageBuffer(sample);
    if (pixels == NULL) return;
    CVMetalTextureRef texture = NULL;
    // sRGB matches WebCamTexture in a linear-colour project: sampling returns linear values.
    if (CVMetalTextureCacheCreateTextureFromImage(kCFAllocatorDefault, _cache, pixels, NULL, MTLPixelFormatBGRA8Unorm_sRGB,
            CVPixelBufferGetWidth(pixels), CVPixelBufferGetHeight(pixels), 0, &texture) != kCVReturnSuccess) return;
    @synchronized (self)
    {
        if (_latest != NULL) CFRelease(_latest);
        _latest = texture;
        _latestId++;
    }
}

- (uint64_t)acquire:(void **)texture width:(int *)width height:(int *)height
{
    @synchronized (self)
    {
        if (_latest == NULL || _latestId == _acquiredId) return 0;
        if (_held[_heldCursor] != NULL) CFRelease(_held[_heldCursor]);
        _held[_heldCursor] = (CVMetalTextureRef)CFRetain(_latest);
        _heldCursor = (_heldCursor + 1) % BOHeldFrames;
        id<MTLTexture> metal = CVMetalTextureGetTexture(_latest);
        *texture = (__bridge void *)metal;
        *width = (int)metal.width;
        *height = (int)metal.height;
        _acquiredId = _latestId;
        return _acquiredId;
    }
}

- (NSString *)error { return _error; }

- (void)stop
{
    [_session stopRunning];
    _session = nil;
    if (_queue != nil) dispatch_sync(_queue, ^{});   // no callback may run after this point
    @synchronized (self)
    {
        if (_latest != NULL) { CFRelease(_latest); _latest = NULL; }
        for (int i = 0; i < BOHeldFrames; i++)
            if (_held[i] != NULL) { CFRelease(_held[i]); _held[i] = NULL; }
    }
    if (_cache != NULL) { CFRelease(_cache); _cache = NULL; }
}
@end

// Returns an opaque handle, or NULL when Unity is not rendering with Metal.
extern "C" void *BodyOracleCamera_Start(const char *name, int width, int height, int fps)
{
    if (unityMetal == NULL) return NULL;
    id<MTLDevice> device = unityMetal->MetalDevice();
    if (device == nil) return NULL;
    BOCamera *camera = [[BOCamera alloc] initWithDevice:device name:@(name ?: "") width:width height:height fps:fps];
    return (__bridge_retained void *)camera;
}

// Returns a new frame id and its MTLTexture, or 0 when no newer frame has arrived.
extern "C" uint64_t BodyOracleCamera_Acquire(void *handle, void **texture, int *width, int *height)
{
    if (handle == NULL) return 0;
    return [(__bridge BOCamera *)handle acquire:texture width:width height:height];
}

// Copies the start error into buffer; returns 0 when the camera started cleanly.
extern "C" int BodyOracleCamera_Error(void *handle, char *buffer, int size)
{
    NSString *error = handle == NULL ? @"Metal plugin interface unavailable." : [(__bridge BOCamera *)handle error];
    if (error == nil) return 0;
    strlcpy(buffer, error.UTF8String, (size_t)size);
    return 1;
}

extern "C" void BodyOracleCamera_Stop(void *handle)
{
    if (handle == NULL) return;
    BOCamera *camera = (__bridge_transfer BOCamera *)handle;
    [camera stop];
}
