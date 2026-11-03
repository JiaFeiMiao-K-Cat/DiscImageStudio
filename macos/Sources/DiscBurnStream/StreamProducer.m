#import "StreamProducer.h"

#include <errno.h>
#include <string.h>
#include <unistd.h>

@interface DRStreamProducer () {
    int _fileDescriptor;
    uint64_t _blocksProduced;
    uint64_t _bytesRead;
    NSString *_readError;
    BOOL _cancelled;
}
@end

@implementation DRStreamProducer

- (instancetype)initWithFileDescriptor:(int)fileDescriptor
                             blockSize:(uint32_t)blockSize
                           totalBlocks:(uint64_t)totalBlocks {
    self = [super init];
    if (self != nil) {
        _fileDescriptor = fileDescriptor;
        _blockSize = blockSize;
        _totalBlocks = totalBlocks;
        _blocksProduced = 0;
        _bytesRead = 0;
        _cancelled = NO;
    }
    return self;
}

- (nullable NSString *)readError {
    @synchronized (self) {
        return _readError;
    }
}

- (BOOL)isCancelled {
    @synchronized (self) {
        return _cancelled;
    }
}

- (void)setCancelled:(BOOL)cancelled {
    @synchronized (self) {
        _cancelled = cancelled;
    }
}

// MARK: - DRTrackDataProduction

- (uint64_t)estimateLengthOfTrack:(DRTrack *)track {
    // The caller already knows the exact length (`--sectors`), so the estimate
    // is exact and comes for free.
    (void)track;
    return _totalBlocks;
}

- (BOOL)prepareTrack:(DRTrack *)track forBurn:(id)burn toMedia:(NSDictionary *)mediaInfo {
    (void)track;
    (void)burn;
    (void)mediaInfo;
    return YES;
}

- (uint32_t)produceDataForTrack:(DRTrack *)track
                     intoBuffer:(char *)buffer
                         length:(uint32_t)bufferLength
                      atAddress:(uint64_t)address
                      blockSize:(uint32_t)blockSize
                        ioFlags:(uint32_t *)flags {
    (void)track;
    (void)address;
    (void)flags;

    if (blockSize != _blockSize) {
        // The engine is asking for a geometry we did not describe; refuse
        // rather than write blocks of the wrong size to the disc.
        [self recordError:[NSString stringWithFormat:
                           @"burn engine requested %u-byte blocks, expected %u",
                           blockSize, _blockSize]];
        return 0;
    }
    if (self.isCancelled || self.readError != nil) {
        return 0;
    }

    uint64_t remainingBlocks = _totalBlocks > _blocksProduced ? _totalBlocks - _blocksProduced : 0;
    if (remainingBlocks == 0) {
        return 0;
    }
    uint64_t requestedBlocks = bufferLength / _blockSize;
    if (requestedBlocks == 0) {
        return 0;
    }
    if (requestedBlocks > remainingBlocks) {
        requestedBlocks = remainingBlocks;
    }

    uint64_t wanted = requestedBlocks * _blockSize;
    uint64_t produced = 0;
    while (produced < wanted) {
        ssize_t result = read(_fileDescriptor, buffer + produced, (size_t)(wanted - produced));
        if (result > 0) {
            produced += (uint64_t)result;
            // Account for bytes as they arrive so a short stream reports the
            // number of complete blocks that did arrive.
            _bytesRead += (uint64_t)result;
            continue;
        }
        if (result == 0) {
            // stdin closed before the promised number of blocks arrived.
            uint64_t completeBlocks = _bytesRead / _blockSize;
            [self recordError:[NSString stringWithFormat:@"stdin ended after %llu of %llu blocks",
                               (unsigned long long)completeBlocks,
                               (unsigned long long)_totalBlocks]];
            return 0;
        }
        if (errno == EINTR) {
            continue;
        }
        [self recordError:[NSString stringWithFormat:@"failed to read from stdin: %s", strerror(errno)]];
        return 0;
    }

    // `produced` is always a multiple of the block size here: we read exactly
    // `wanted` bytes, which is a whole number of blocks.
    _blocksProduced += produced / _blockSize;
    return (uint32_t)produced;
}

- (void)cleanupTrackAfterBurn:(DRTrack *)track {
    (void)track;
    // Nothing to release: the file descriptor belongs to the process.
}

// MARK: - Helpers

- (void)recordError:(NSString *)message {
    @synchronized (self) {
        if (_readError == nil) {
            _readError = [message copy];
        }
    }
}

@end
