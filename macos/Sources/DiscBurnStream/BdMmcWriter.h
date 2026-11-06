#import <Foundation/Foundation.h>
#import "StreamOptions.h"

NS_ASSUME_NONNULL_BEGIN

/// Writes a BD-R sequentially with raw MMC commands (no DiscRecording DRBurn).
/// Returns the helper's process exit code (0 success, 1 burn failure, 2 usage/device).
FOUNDATION_EXPORT int DRRunBdMmcBurn(DRStreamOptions *options);

NS_ASSUME_NONNULL_END
