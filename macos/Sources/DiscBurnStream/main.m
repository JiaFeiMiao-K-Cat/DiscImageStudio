// disc-burn-stream — streaming burn helper for macOS DiscRecording.
//
// Reads raw blocks from stdin, feeds them to a DRTrack producer and writes them
// to a blank disc with DRBurn. The command line, JSON shape, progress lines and
// exit codes are the contract in macos/DESIGN.md §5.2:
//
//   disc-burn-stream --list-drives
//   disc-burn-stream --kind cd-audio|dvd-data --sectors N [--drive I] [--speed X]
//                     [--no-verify] [--no-eject] [--test]
//
// stdout is reserved for --list-drives JSON; everything else goes to stderr.

#import <Foundation/Foundation.h>
#import <DiscRecording/DiscRecording.h>

#import "StreamOptions.h"
#import "StreamProducer.h"

#include <math.h>
#include <signal.h>
#include <stdio.h>
#include <unistd.h>

// MARK: - Stderr helpers

static void WriteStderr(NSString *format, ...) {
    va_list args;
    va_start(args, format);
    NSString *message = [[NSString alloc] initWithFormat:format arguments:args];
    va_end(args);
    fprintf(stderr, "%s\n", message.UTF8String);
    fflush(stderr);
}

static void WriteError(NSString *format, ...) {
    va_list args;
    va_start(args, format);
    NSString *message = [[NSString alloc] initWithFormat:format arguments:args];
    va_end(args);
    fprintf(stderr, "error: %s\n", message.UTF8String);
    fflush(stderr);
}

// MARK: - Progress reporting

/// Last state line written; the burn engine emits many notifications per state
/// change and consumers only need the transitions.
static NSString *gLastState = nil;
static double gLastPercent = -1.0;

static BOOL PercentIsNew(double percent) {
    return gLastPercent < 0.0 || fabs(percent - gLastPercent) >= 0.1;
}

static void EmitPercent(double percent) {
    if (percent < 0.0) { percent = 0.0; }
    if (percent > 100.0) { percent = 100.0; }
    if (!PercentIsNew(percent)) { return; }
    gLastPercent = percent;
    WriteStderr(@"percent: %.1f", percent);
}

static void EmitState(NSString *state) {
    if (gLastState != nil && [gLastState isEqualToString:state]) {
        return;
    }
    gLastState = [state copy];
    WriteStderr(@"state: %@", state);
}

// MARK: - Status mapping

static id StatusValue(NSDictionary *status, CFStringRef key) {
    return [status objectForKey:(__bridge NSString *)key];
}

/// Maps the framework's kDRStatusStateKey values onto the five states the
/// helper contract exposes.
static NSString *StreamStateForBurnState(id rawState) {
    if (![rawState isKindOfClass:[NSString class]]) {
        return nil;
    }
    NSString *state = (NSString *)rawState;
    if ([state isEqualToString:(__bridge NSString *)kDRStatusStatePreparing] ||
        [state isEqualToString:(__bridge NSString *)kDRStatusStateNone]) {
        return @"preparing";
    }
    if ([state isEqualToString:(__bridge NSString *)kDRStatusStateVerifying]) {
        return @"verifying";
    }
    if ([state isEqualToString:(__bridge NSString *)kDRStatusStateDone]) {
        return @"done";
    }
    if ([state isEqualToString:(__bridge NSString *)kDRStatusStateFailed]) {
        return @"failed";
    }
    // Session/track open, write, close and finishing are all part of writing.
    if ([state isEqualToString:(__bridge NSString *)kDRStatusStateSessionOpen] ||
        [state isEqualToString:(__bridge NSString *)kDRStatusStateTrackOpen] ||
        [state isEqualToString:(__bridge NSString *)kDRStatusStateTrackWrite] ||
        [state isEqualToString:(__bridge NSString *)kDRStatusStateTrackClose] ||
        [state isEqualToString:(__bridge NSString *)kDRStatusStateSessionClose] ||
        [state isEqualToString:(__bridge NSString *)kDRStatusStateFinishing]) {
        return @"writing";
    }
    return nil;
}

static NSString *BurnFailureMessage(NSDictionary *status) {
    id errorStatus = StatusValue(status, kDRErrorStatusKey);
    if ([errorStatus isKindOfClass:[NSDictionary class]]) {
        NSString *text = [errorStatus objectForKey:(__bridge NSString *)kDRErrorStatusErrorStringKey];
        if (text.length > 0) { return text; }
        text = [errorStatus objectForKey:(__bridge NSString *)kDRErrorStatusErrorInfoStringKey];
        if (text.length > 0) { return text; }
        NSNumber *code = [errorStatus objectForKey:(__bridge NSString *)kDRErrorStatusErrorKey];
        if (code != nil) {
            return [NSString stringWithFormat:@"burn failed with OSStatus %d", code.intValue];
        }
    }
    return @"burn failed";
}

/// Observes DRBurnStatusChangedNotification and turns it into stderr progress.
/// The notification arrives on the run loop that created the
/// DRNotificationCenter, which is the main run loop here.
@interface DRBurnStatusObserver : NSObject {
    BOOL _finished;
    BOOL _failed;
    NSString *_failureMessage;
}
@property (nonatomic, readonly) BOOL finished;
@property (nonatomic, readonly) BOOL failed;
@property (nonatomic, copy, readonly, nullable) NSString *failureMessage;

- (void)burnStatusChanged:(NSNotification *)notification;
- (void)handleStatus:(NSDictionary *)status;
@end

@implementation DRBurnStatusObserver

- (BOOL)finished {
    return _finished;
}

- (BOOL)failed {
    return _failed;
}

- (nullable NSString *)failureMessage {
    return _failureMessage;
}

- (void)burnStatusChanged:(NSNotification *)notification {
    NSDictionary *status = notification.userInfo;
    if (![status isKindOfClass:[NSDictionary class]]) {
        id object = notification.object;
        DRBurn *burnObject = [object isKindOfClass:[DRBurn class]] ? (DRBurn *)object : nil;
        status = burnObject != nil ? [burnObject status] : nil;
    }
    [self handleStatus:status];
}

- (void)handleStatus:(NSDictionary *)status {
    if (![status isKindOfClass:[NSDictionary class]] || _finished) {
        return;
    }
    NSString *state = StreamStateForBurnState(StatusValue(status, kDRStatusStateKey));
    if (state.length > 0) {
        if ([state isEqualToString:@"done"] || [state isEqualToString:@"failed"]) {
            // The engine may stop reporting just short of 100%.
            EmitPercent(100.0);
            EmitState(state);
            _finished = YES;
            if ([state isEqualToString:@"failed"]) {
                _failed = YES;
                _failureMessage = [BurnFailureMessage(status) copy];
            }
        } else {
            EmitState(state);
        }
    }
    NSNumber *percent = StatusValue(status, kDRStatusPercentCompleteKey);
    if ([percent isKindOfClass:[NSNumber class]]) {
        EmitPercent(percent.doubleValue);
    }
}

@end

// MARK: - Drive description

/// The framework reports media types as constants ("DRDeviceMediaTypeCDR");
/// the contract wants the names a user sees ("CD-R").
static NSString *FriendlyMediaType(id rawValue) {
    if (![rawValue isKindOfClass:[NSString class]]) {
        return nil;
    }
    NSString *raw = (NSString *)rawValue;
    static NSString *const prefix = @"DRDeviceMediaType";
    NSString *suffix = [raw hasPrefix:prefix] ? [raw substringFromIndex:prefix.length] : raw;
    static NSDictionary<NSString *, NSString *> *names = nil;
    static dispatch_once_t onceToken;
    dispatch_once(&onceToken, ^{
        names = @{
            @"CDROM": @"CD-ROM",
            @"CDR": @"CD-R",
            @"CDRW": @"CD-RW",
            @"DVDROM": @"DVD-ROM",
            @"DVDR": @"DVD-R",
            @"DVDRW": @"DVD-RW",
            @"DVDRAM": @"DVD-RAM",
            @"DVDRDualLayer": @"DVD-R DL",
            @"DVDRWDualLayer": @"DVD-RW DL",
            @"DVDPlusR": @"DVD+R",
            @"DVDPlusRW": @"DVD+RW",
            @"DVDPlusRDoubleLayer": @"DVD+R DL",
            @"DVDPlusRWDoubleLayer": @"DVD+RW DL",
            @"BDR": @"BD-R",
            @"BDRE": @"BD-RE",
            @"BDROM": @"BD-ROM",
            @"HDDVDROM": @"HD DVD-ROM",
            @"HDDVDR": @"HD DVD-R",
            @"HDDVDRW": @"HD DVD-RW",
            @"HDDVDRAM": @"HD DVD-RAM",
            @"Unknown": @"Unknown",
        };
    });
    NSString *name = names[suffix];
    return name != nil ? name : suffix;
}

static BOOL MediaIsPresent(DRDevice *device) {
    id state = device.status[DRDeviceMediaStateKey];
    return [state isKindOfClass:[NSString class]] &&
           [(NSString *)state isEqualToString:DRDeviceMediaStateMediaPresent];
}

static NSDictionary *MediaInfo(DRDevice *device) {
    id info = device.status[DRDeviceMediaInfoKey];
    return [info isKindOfClass:[NSDictionary class]] ? info : nil;
}

static NSString *MediaClassPrefix(id rawValue) {
    if (![rawValue isKindOfClass:[NSString class]]) {
        return nil;
    }
    NSString *raw = (NSString *)rawValue;
    static NSString *const prefix = @"DRDeviceMediaType";
    return [raw hasPrefix:prefix] ? [raw substringFromIndex:prefix.length] : raw;
}

static BOOL DeviceCanWriteCD(DRDevice *device) {
    NSDictionary *capabilities = device.info[DRDeviceWriteCapabilitiesKey];
    return [capabilities[DRDeviceCanWriteCDRKey] boolValue] ||
           [capabilities[DRDeviceCanWriteCDRWKey] boolValue];
}

static BOOL DeviceCanWriteDVD(DRDevice *device) {
    NSDictionary *capabilities = device.info[DRDeviceWriteCapabilitiesKey];
    return [capabilities[DRDeviceCanWriteDVDRKey] boolValue] ||
           [capabilities[DRDeviceCanWriteDVDRWKey] boolValue] ||
           [capabilities[DRDeviceCanWriteDVDPlusRKey] boolValue] ||
           [capabilities[DRDeviceCanWriteDVDPlusRWKey] boolValue] ||
           [capabilities[DRDeviceCanWriteDVDRDualLayerKey] boolValue] ||
           [capabilities[DRDeviceCanWriteDVDPlusRDoubleLayerKey] boolValue];
}

static BOOL DeviceCanWriteBluRay(DRDevice *device) {
    NSDictionary *capabilities = device.info[DRDeviceWriteCapabilitiesKey];
    return [capabilities[DRDeviceCanWriteBDRKey] boolValue] ||
           [capabilities[DRDeviceCanWriteBDREKey] boolValue];
}

static NSDictionary *DescribeDrive(DRDevice *device, NSUInteger position) {
    NSDictionary *info = device.info;
    BOOL present = MediaIsPresent(device);
    NSDictionary *mediaInfo = present ? MediaInfo(device) : nil;
    NSString *mediaType = present ? FriendlyMediaType(mediaInfo[DRDeviceMediaTypeKey]) : nil;
    BOOL blank = present && [mediaInfo[DRDeviceMediaIsBlankKey] boolValue];

    return @{
        @"index": @(position + 1),
        @"vendor": info[DRDeviceVendorNameKey] ?: @"",
        @"product": info[DRDeviceProductNameKey] ?: @"",
        @"firmware": info[DRDeviceFirmwareRevisionKey] ?: @"",
        @"cdWritable": @(DeviceCanWriteCD(device)),
        @"dvdWritable": @(DeviceCanWriteDVD(device)),
        @"bdWritable": @(DeviceCanWriteBluRay(device)),
        @"mediaPresent": @(present),
        @"mediaBlank": @(blank),
        @"mediaType": mediaType ?: [NSNull null],
    };
}

static int PrintDriveList(void) {
    NSArray<DRDevice *> *devices = [DRDevice devices];
    NSMutableArray *entries = [NSMutableArray arrayWithCapacity:devices.count];
    for (NSUInteger index = 0; index < devices.count; index++) {
        [entries addObject:DescribeDrive(devices[index], index)];
    }

    NSError *error = nil;
    NSData *data = [NSJSONSerialization dataWithJSONObject:entries options:0 error:&error];
    if (data == nil) {
        // Very unlikely (all values are strings/numbers/NSNull), but never
        // print a partial document on stdout.
        WriteError(@"failed to encode the drive list: %@", error.localizedDescription ?: @"unknown error");
        return 1;
    }
    fwrite(data.bytes, 1, data.length, stdout);
    fputc('\n', stdout);
    fflush(stdout);
    return 0;
}

// MARK: - Burn properties

static NSDictionary *TrackProperties(DRStreamOptions *options, uint32_t blockSize) {
    NSMutableDictionary *properties = [NSMutableDictionary dictionary];
    properties[DRTrackLengthKey] = @(options.sectors);
    properties[DRBlockSizeKey] = @(blockSize);
    if (options.kind == DRStreamKindCDAudio) {
        properties[DRBlockTypeKey] = @(kDRBlockTypeAudio);
        properties[DRDataFormKey] = @(kDRDataFormAudio);
        properties[DRSessionFormatKey] = @(kDRSessionFormatAudio);
        properties[DRTrackModeKey] = @(kDRTrackModeAudio);
        // Let DiscRecording add the standard 2-second pregap for audio.
    } else {
        properties[DRBlockTypeKey] = @(kDRBlockTypeDVDData);
        properties[DRDataFormKey] = @(kDRDataFormDVDData);
        properties[DRSessionFormatKey] = @(kDRSessionFormatDVDData);
        properties[DRTrackModeKey] = @(kDRTrackModeDVDData);
    }

    // stdin cannot be rewound, so "produce again" verification is impossible;
    // the checksum mode verifies the written blocks without asking the
    // producer to replay the stream.
    NSString *verification = options.verify
        ? (__bridge NSString *)kDRVerificationTypeChecksum
        : (__bridge NSString *)kDRVerificationTypeNone;
    properties[(__bridge NSString *)kDRVerificationTypeKey] = verification;
    return properties;
}

/// One CD "×" is 176.4 KB/s, one DVD "×" is 1385 KB/s; DRBurnRequestedSpeedKey
/// is expressed in kilobytes per second.
static double KilobytesPerSecondForSpeedFactor(DRStreamKind kind, double speedFactor) {
    double unit = (kind == DRStreamKindCDAudio) ? 176.4 : 1385.0;
    return speedFactor * unit;
}

static NSDictionary *BurnProperties(DRStreamOptions *options) {
    NSMutableDictionary *properties = [NSMutableDictionary dictionary];
    if (options.speedFactor > 0.0) {
        properties[DRBurnRequestedSpeedKey] = @(KilobytesPerSecondForSpeedFactor(options.kind, options.speedFactor));
    }
    properties[DRBurnVerifyDiscKey] = @(options.verify);
    if (options.testBurn) {
        properties[DRBurnTestingKey] = @YES;
    }
    // The framework defaults to ejecting, so --no-eject must ask for something
    // else explicitly: mounting keeps the disc in the drive.
    if (options.eject) {
        properties[DRBurnCompletionActionKey] = DRBurnCompletionActionEject;
    } else {
        properties[DRBurnCompletionActionKey] = DRBurnCompletionActionMount;
    }
    return properties;
}

// MARK: - Burn

static int RunBurn(DRStreamOptions *options) {
    NSArray<DRDevice *> *devices = [DRDevice devices];
    if (devices.count == 0) {
        WriteError(@"no writable drive");
        return 2;
    }
    if (options.driveIndex < 1 || (NSUInteger)options.driveIndex > devices.count) {
        WriteError(@"no drive at index %ld (%lu drive(s) detected)",
                   (long)options.driveIndex, (unsigned long)devices.count);
        return 2;
    }

    DRDevice *device = devices[(NSUInteger)(options.driveIndex - 1)];
    BOOL isAudio = (options.kind == DRStreamKindCDAudio);

    if (isAudio && !DeviceCanWriteCD(device)) {
        WriteError(@"drive %ld cannot write CD-R media", (long)options.driveIndex);
        return 2;
    }
    if (!isAudio && !DeviceCanWriteDVD(device)) {
        WriteError(@"drive %ld cannot write DVD-R media", (long)options.driveIndex);
        return 2;
    }
    if (!MediaIsPresent(device)) {
        WriteError(@"no media in drive %ld", (long)options.driveIndex);
        return 2;
    }

    NSDictionary *mediaInfo = MediaInfo(device);
    id rawMediaType = mediaInfo[DRDeviceMediaTypeKey];
    NSString *mediaType = FriendlyMediaType(rawMediaType);
    NSString *mediaClass = MediaClassPrefix(rawMediaType);

    if (mediaClass != nil && [mediaClass hasPrefix:@"BD"]) {
        WriteError(@"Blu-ray streaming is not supported by this helper");
        return 2;
    }
    if (mediaClass != nil && [mediaClass hasPrefix:@"HDDVD"]) {
        WriteError(@"HD DVD media is not supported by this helper");
        return 2;
    }
    if (isAudio && !(mediaClass != nil && [mediaClass hasPrefix:@"CD"])) {
        WriteError(@"drive %ld holds %@ media, but --kind cd-audio needs a blank CD-R",
                   (long)options.driveIndex, mediaType ?: @"unsupported");
        return 2;
    }
    if (!isAudio && !(mediaClass != nil && [mediaClass hasPrefix:@"DVD"])) {
        WriteError(@"drive %ld holds %@ media, but --kind dvd-data needs a blank DVD-R",
                   (long)options.driveIndex, mediaType ?: @"unsupported");
        return 2;
    }
    if (![mediaInfo[DRDeviceMediaIsBlankKey] boolValue]) {
        WriteError(@"media in drive %ld is not blank (%@)",
                   (long)options.driveIndex, mediaType ?: @"unknown");
        return 2;
    }

    uint32_t blockSize = DRStreamBlockSizeForKind(options.kind);
    DRStreamProducer *producer = [[DRStreamProducer alloc] initWithFileDescriptor:STDIN_FILENO
                                                                       blockSize:blockSize
                                                                     totalBlocks:options.sectors];
    DRTrack *track = [[DRTrack alloc] initWithProducer:producer];
    [track setProperties:TrackProperties(options, blockSize)];

    DRBurn *burn = nil;
    @try {
        burn = [[DRBurn alloc] initWithDevice:device];
    } @catch (NSException *exception) {
        WriteError(@"cannot open drive %ld: %@", (long)options.driveIndex,
                   exception.reason ?: exception.name);
        return 2;
    }
    [burn setProperties:BurnProperties(options)];

    DRBurnStatusObserver *observer = [[DRBurnStatusObserver alloc] init];
    DRNotificationCenter *center = [DRNotificationCenter currentRunLoopCenter];
    NSString *notificationName = (__bridge NSString *)kDRBurnStatusChangedNotification;
    [center addObserver:observer selector:@selector(burnStatusChanged:) name:notificationName object:burn];

    EmitState(@"preparing");
    @try {
        [burn writeLayout:track];
    } @catch (NSException *exception) {
        producer.cancelled = YES;
        EmitState(@"failed");
        WriteError(@"burn could not be started: %@", exception.reason ?: exception.name);
        [center removeObserver:observer name:notificationName object:burn];
        return 1;
    }

    // DRBurn is asynchronous: wait on this thread's run loop for the
    // completion (or failure) notification. Polling the status dictionary is a
    // belt-and-braces fallback in case a notification is missed.
    NSTimeInterval lastPoll = 0.0;
    while (!observer.finished) {
        @autoreleasepool {
            [[NSRunLoop currentRunLoop] runMode:NSDefaultRunLoopMode
                                     beforeDate:[NSDate dateWithTimeIntervalSinceNow:0.2]];
            NSTimeInterval now = [NSDate timeIntervalSinceReferenceDate];
            if (now - lastPoll >= 1.0) {
                lastPoll = now;
                [observer handleStatus:[burn status]];
            }
        }
    }
    producer.cancelled = YES;
    [center removeObserver:observer name:notificationName object:burn];

    NSString *readError = producer.readError;
    if (readError != nil) {
        WriteError(@"%@", readError);
        return 1;
    }
    if (observer.failed) {
        WriteError(@"%@", observer.failureMessage ?: @"burn failed");
        return 1;
    }
    return 0;
}

// MARK: - Entry point

int main(int argc, const char *argv[]) {
    @autoreleasepool {
        // The CLI may exit before we finish reading; do not die on EPIPE while
        // writing progress to a closed stderr.
        signal(SIGPIPE, SIG_IGN);

        NSMutableArray<NSString *> *arguments = [NSMutableArray arrayWithCapacity:(NSUInteger)argc];
        for (int index = 1; index < argc; index++) {
            NSString *argument = argv[index] != NULL ? [NSString stringWithUTF8String:argv[index]] : nil;
            [arguments addObject:argument ?: @""];
        }

        NSString *parseError = nil;
        DRStreamOptions *options = [DRStreamOptions optionsFromArguments:arguments error:&parseError];
        if (options == nil) {
            WriteError(@"%@", parseError ?: @"invalid arguments");
            WriteStderr(@"run 'disc-burn-stream --help' for usage");
            return 2;
        }
        if (options.showHelp) {
            fputs(DRStreamOptions.usageText.UTF8String, stdout);
            fflush(stdout);
            return 0;
        }
        if (options.listDrives) {
            return PrintDriveList();
        }
        return RunBurn(options);
    }
}
