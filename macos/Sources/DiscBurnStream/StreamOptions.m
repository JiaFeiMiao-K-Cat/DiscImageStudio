#import "StreamOptions.h"

uint32_t DRStreamBlockSizeForKind(DRStreamKind kind) {
    switch (kind) {
        case DRStreamKindCDAudio:
            return 2352;
        case DRStreamKindDVDData:
        case DRStreamKindBDData:
            return 2048;
    }
    return 2048;
}

NSString *DRStreamKindName(DRStreamKind kind) {
    switch (kind) {
        case DRStreamKindCDAudio:
            return @"cd-audio";
        case DRStreamKindDVDData:
            return @"dvd-data";
        case DRStreamKindBDData:
            return @"bd-data";
    }
    return @"unknown";
}

/// Parses a non-negative decimal integer; rejects empty strings, signs,
/// trailing garbage and overflow.
static BOOL ParseUnsigned(NSString *text, unsigned long long *outValue) {
    if (text.length == 0) {
        return NO;
    }
    const char *utf8 = text.UTF8String;
    if (utf8 == NULL || utf8[0] == '-' || utf8[0] == '+') {
        return NO;
    }
    errno = 0;
    char *end = NULL;
    unsigned long long value = strtoull(utf8, &end, 10);
    if (errno != 0 || end == utf8 || *end != '\0') {
        return NO;
    }
    *outValue = value;
    return YES;
}

/// Parses a strictly positive decimal number ("2", "4.5"); rejects signs,
/// empty strings and trailing garbage.
static BOOL ParsePositiveDouble(NSString *text, double *outValue) {
    if (text.length == 0) {
        return NO;
    }
    const char *utf8 = text.UTF8String;
    if (utf8 == NULL || utf8[0] == '-' || utf8[0] == '+') {
        return NO;
    }
    errno = 0;
    char *end = NULL;
    double value = strtod(utf8, &end);
    if (errno != 0 || end == utf8 || *end != '\0' || !(value > 0.0)) {
        return NO;
    }
    *outValue = value;
    return YES;
}

@implementation DRStreamOptions

@synthesize burnStrategy = _burnStrategy;

+ (nullable instancetype)optionsFromArguments:(NSArray<NSString *> *)arguments
                                        error:(NSString *_Nullable *_Nullable)errorMessage {
    DRStreamOptions *options = [[DRStreamOptions alloc] init];
    options->_kind = DRStreamKindCDAudio;
    options->_sectors = 0;
    options->_driveIndex = 1;
    options->_speedFactor = 0.0;
    options->_verify = YES;
    options->_eject = YES;
    options->_burnStrategy = @"auto";

    __block NSString *failure = nil;
    void (^fail)(NSString *, ...) = ^(NSString *format, ...) {
        va_list args;
        va_start(args, format);
        failure = [[NSString alloc] initWithFormat:format arguments:args];
        va_end(args);
    };

    BOOL sawKind = NO;
    BOOL sawSectors = NO;
    BOOL sawBurnOption = NO;

    for (NSUInteger index = 0; index < arguments.count; index++) {
        NSString *argument = arguments[index];
        // Every option that takes a value consumes the next argument.
        NSString *(^nextValue)(NSString *) = ^NSString *(NSString *option) {
            if (index + 1 >= arguments.count) {
                fail(@"%@ requires a value", option);
                return nil;
            }
            return arguments[index + 1];
        };

        if ([argument isEqualToString:@"--help"] || [argument isEqualToString:@"-h"]) {
            // --help wins over every other argument.
            options->_showHelp = YES;
            break;
        }
        if ([argument isEqualToString:@"--list-drives"]) {
            options->_listDrives = YES;
            continue;
        }
        if ([argument isEqualToString:@"--strategy"]) {
            NSString *value = nextValue(argument);
            if (value == nil) { break; }
            index += 1;
            NSString *lowered = value.lowercaseString;
            if (![lowered isEqualToString:@"auto"] && ![lowered isEqualToString:@"bddao"]) {
                fail(@"unsupported --strategy value \"%@\" (expected auto or bddao)", value);
                break;
            }
            options->_burnStrategy = lowered;
            sawBurnOption = YES;
            continue;
        }
        if ([argument isEqualToString:@"--kind"]) {
            NSString *value = nextValue(argument);
            if (value == nil) { break; }
            index += 1;
            if ([value.lowercaseString isEqualToString:@"cd-audio"]) {
                options->_kind = DRStreamKindCDAudio;
            } else if ([value.lowercaseString isEqualToString:@"dvd-data"]) {
                options->_kind = DRStreamKindDVDData;
            } else if ([value.lowercaseString isEqualToString:@"bd-data"]) {
                options->_kind = DRStreamKindBDData;
            } else {
                fail(@"unsupported --kind value \"%@\" (expected cd-audio, dvd-data or bd-data)", value);
                break;
            }
            sawKind = YES;
            sawBurnOption = YES;
            continue;
        }
        if ([argument isEqualToString:@"--sectors"]) {
            NSString *value = nextValue(argument);
            if (value == nil) { break; }
            index += 1;
            unsigned long long parsed = 0;
            if (!ParseUnsigned(value, &parsed)) {
                fail(@"invalid --sectors value \"%@\"", value);
                break;
            }
            if (parsed == 0) {
                fail(@"--sectors must be greater than zero");
                break;
            }
            options->_sectors = (uint64_t)parsed;
            sawSectors = YES;
            sawBurnOption = YES;
            continue;
        }
        if ([argument isEqualToString:@"--drive"]) {
            NSString *value = nextValue(argument);
            if (value == nil) { break; }
            index += 1;
            unsigned long long parsed = 0;
            if (!ParseUnsigned(value, &parsed) || parsed < 1) {
                fail(@"invalid --drive value \"%@\" (1-based drive index expected)", value);
                break;
            }
            options->_driveIndex = (NSInteger)parsed;
            sawBurnOption = YES;
            continue;
        }
        if ([argument isEqualToString:@"--speed"]) {
            NSString *value = nextValue(argument);
            if (value == nil) { break; }
            index += 1;
            double parsed = 0;
            if (!ParsePositiveDouble(value, &parsed)) {
                fail(@"invalid --speed value \"%@\" (positive speed factor expected)", value);
                break;
            }
            options->_speedFactor = parsed;
            sawBurnOption = YES;
            continue;
        }
        if ([argument isEqualToString:@"--no-verify"]) {
            options->_verify = NO;
            sawBurnOption = YES;
            continue;
        }
        if ([argument isEqualToString:@"--no-eject"]) {
            options->_eject = NO;
            sawBurnOption = YES;
            continue;
        }
        if ([argument isEqualToString:@"--test"]) {
            options->_testBurn = YES;
            sawBurnOption = YES;
            continue;
        }

        fail(@"unknown argument \"%@\"", argument);
        break;
    }

    if (failure != nil) {
        if (errorMessage != NULL) { *errorMessage = failure; }
        return nil;
    }
    if (options->_showHelp) {
        return options;
    }
    if (options->_listDrives) {
        if (sawBurnOption) {
            if (errorMessage != NULL) {
                *errorMessage = @"--list-drives cannot be combined with burn options";
            }
            return nil;
        }
        return options;
    }
    if (!sawKind) {
        if (errorMessage != NULL) { *errorMessage = @"--kind is required"; }
        return nil;
    }
    if (!sawSectors) {
        if (errorMessage != NULL) { *errorMessage = @"--sectors is required"; }
        return nil;
    }
    return options;
}

+ (NSString *)usageText {
    return @"disc-burn-stream — DiscRecording streaming burn helper\n"
            "\n"
            "Usage:\n"
            "  disc-burn-stream --list-drives\n"
            "  disc-burn-stream --kind cd-audio|dvd-data|bd-data --sectors N [--drive I] [--speed X]\n"
            "                    [--no-verify] [--no-eject] [--test]\n"
            "  disc-burn-stream --help\n"
            "\n"
            "Options:\n"
            "  --list-drives  Print a JSON array describing the attached drives and exit.\n"
            "  --kind         cd-audio (2352-byte blocks), dvd-data or bd-data (2048-byte blocks).\n"
            "  --sectors N    Number of blocks to read from stdin and burn (required).\n"
            "  --drive I      1-based drive index as reported by --list-drives (default 1).\n"
            "  --speed X      Requested speed factor; CD 1x is 176 KB/s, DVD 1x is 1385 KB/s, BD 1x is 4496 KB/s.\n"
            "  --strategy     BD burn strategy: auto (default) or bddao.\n"
            "  --no-verify    Skip disc verification (verification is on by default).\n"
            "  --no-eject     Keep the disc in the drive when the burn completes.\n"
            "  --test         Test burn: the disc is not actually written.\n"
            "\n"
            "stdin carries the raw blocks; progress is written to stderr as\n"
            "  state: preparing|writing|verifying|done|failed\n"
            "  percent: 42.3\n"
            "  error: <message>\n"
            "Exit codes: 0 success, 1 burn failure, 2 invalid arguments / drive / media.\n";
}

@end
