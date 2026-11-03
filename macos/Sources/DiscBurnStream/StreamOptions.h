// Argument parsing for the disc-burn-stream helper.
//
// The helper is a small, UI-agnostic command line tool: it validates its own
// arguments, reports failures on stderr as "error: ..." and uses the exit codes
// documented in macos/DESIGN.md §5.2 (0 success, 1 burn failure, 2 usage /
// drive / media problem).

#import <Foundation/Foundation.h>

NS_ASSUME_NONNULL_BEGIN

typedef NS_ENUM(NSInteger, DRStreamKind) {
    DRStreamKindCDAudio = 0,
    DRStreamKindDVDData = 1,
};

/// Raw block size of one sector for the given kind: CD-DA is 2352 bytes
/// (raw 2352-byte audio blocks), DVD data is 2048 bytes.
FOUNDATION_EXPORT uint32_t DRStreamBlockSizeForKind(DRStreamKind kind);

/// The name used on the command line ("cd-audio" / "dvd-data").
FOUNDATION_EXPORT NSString *DRStreamKindName(DRStreamKind kind);

/// Parsed command line. Instances are immutable and only produced by
/// +optionsFromArguments:error:, which returns nil and sets `error` when the
/// arguments are unusable.
@interface DRStreamOptions : NSObject

@property (nonatomic, readonly) DRStreamKind kind;
@property (nonatomic, readonly) uint64_t sectors;
@property (nonatomic, readonly) NSInteger driveIndex;   // 1-based, defaults to 1
@property (nonatomic, readonly) double speedFactor;     // 0 means "drive maximum"
@property (nonatomic, readonly) BOOL verify;            // default YES, --no-verify turns it off
@property (nonatomic, readonly) BOOL eject;             // default YES, --no-eject turns it off
@property (nonatomic, readonly) BOOL testBurn;          // --test
@property (nonatomic, readonly) BOOL listDrives;        // --list-drives
@property (nonatomic, readonly) BOOL showHelp;          // --help

/// Parses `arguments` (without the executable path).
/// Returns nil and fills `error` (already prefixed for stderr) on failure.
+ (nullable instancetype)optionsFromArguments:(NSArray<NSString *> *)arguments
                                        error:(NSString *_Nullable *_Nullable)error;

/// Multi-line usage text printed by --help.
+ (NSString *)usageText;

@end

NS_ASSUME_NONNULL_END
