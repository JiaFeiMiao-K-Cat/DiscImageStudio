// Streams raw blocks from stdin into the DiscRecording burn engine.
//
// A DRTrack producer is asked for data on a real-time thread while the laser
// is running, so `produceDataForTrack:...` must never block for long. The
// producer therefore reads straight into the buffer the engine hands us and
// keeps no copy of the stream: at most one engine buffer is alive at a time.

#import <Foundation/Foundation.h>
#import <DiscRecording/DRTrack.h>

NS_ASSUME_NONNULL_BEGIN

@interface DRStreamProducer : NSObject

/// `fileDescriptor` is normally STDIN_FILENO. Exactly `totalBlocks` blocks of
/// `blockSize` bytes are expected; a short stream is recorded in
/// `shortStreamError`.
- (instancetype)initWithFileDescriptor:(int)fileDescriptor
                             blockSize:(uint32_t)blockSize
                           totalBlocks:(uint64_t)totalBlocks NS_DESIGNATED_INITIALIZER;
- (instancetype)init NS_UNAVAILABLE;

@property (nonatomic, readonly) uint32_t blockSize;
@property (nonatomic, readonly) uint64_t totalBlocks;
/// Number of complete blocks handed to the burn engine so far.
@property (nonatomic, readonly) uint64_t blocksProduced;
/// Number of bytes actually read from stdin so far.
@property (nonatomic, readonly) uint64_t bytesRead;

/// Set when production stopped because stdin ended early or a read failed.
/// The message is already user facing ("stdin ended after 0 of 1 blocks").
@property (atomic, copy, readonly, nullable) NSString *readError;

/// Set to YES to stop reading (used while tearing the burn down).
@property (atomic, assign, getter=isCancelled) BOOL cancelled;

// DRTrackDataProduction (informal protocol).
- (uint64_t)estimateLengthOfTrack:(DRTrack *)track;
- (BOOL)prepareTrack:(DRTrack *)track forBurn:(id)burn toMedia:(NSDictionary *)mediaInfo;
- (uint32_t)produceDataForTrack:(DRTrack *)track
                     intoBuffer:(char *)buffer
                         length:(uint32_t)bufferLength
                      atAddress:(uint64_t)address
                      blockSize:(uint32_t)blockSize
                        ioFlags:(uint32_t *)flags;
- (void)cleanupTrackAfterBurn:(DRTrack *)track;

@end

NS_ASSUME_NONNULL_END
