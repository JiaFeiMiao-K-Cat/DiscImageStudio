#import "BdMmcWriter.h"

#import <IOKit/IOKitLib.h>
#import <IOKit/IOCFPlugIn.h>
#import <IOKit/scsi/SCSITaskLib.h>
#include <errno.h>
#include <unistd.h>

// Raw BD-R SRM writer: IOKit SCSI passthrough, no DiscRecording DRBurn.
// The command sequence mirrors the Windows BluRayMmcCommands implementation:
// read the existing track, WRITE(10) 32-sector commands, SYNCHRONIZE CACHE,
// CLOSE TRACK, CLOSE SESSION. It never issues FORMAT UNIT or allocates spares.

typedef struct {
    IOCFPlugInInterface **plugin;
    MMCDeviceInterface **mmc;
    SCSITaskDeviceInterface **device;
} DRMmcHandle;

static void EmitState(NSString *state) {
    fprintf(stderr, "state: %s\n", state.UTF8String);
}

static void EmitPercent(double percent) {
    fprintf(stderr, "percent: %.3f\n", percent);
}

static void EmitError(NSString *message) {
    fprintf(stderr, "error: %s\n", message.UTF8String);
}

static uint32_t ReadBE32(const uint8_t *p) {
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) | ((uint32_t)p[2] << 8) | p[3];
}

static void PrintSense(const SCSI_Sense_Data *sense) {
    const uint8_t *b = (const uint8_t *)sense;
    fprintf(stderr, "sense:");
    for (size_t i = 0; i < sizeof(SCSI_Sense_Data); i++) fprintf(stderr, " %02x", b[i]);
    fprintf(stderr, "\n");
}

static int ExecCDB(DRMmcHandle *handle, const uint8_t *cdb, UInt8 cdbLength,
                   void *buffer, UInt32 length, UInt8 direction,
                   UInt32 timeoutMs, const char *label) {
    SCSITaskInterface **task = (*handle->device)->CreateSCSITask(handle->device);
    if (task == NULL) {
        EmitError([NSString stringWithFormat:@"cannot create SCSI task for %s", label]);
        return -1;
    }
    (*task)->SetTimeoutDuration(task, timeoutMs);
    IOReturn setCDB = (*task)->SetCommandDescriptorBlock(task, (UInt8 *)cdb, cdbLength);
    if (setCDB != kIOReturnSuccess) {
        (*task)->Release(task);
        EmitError([NSString stringWithFormat:@"cannot set CDB for %s (0x%x)", label, setCDB]);
        return -1;
    }
    if (length > 0) {
        SCSITaskSGElement sg = { (IOVirtualAddress)buffer, length };
        (*task)->SetScatterGatherEntries(task, &sg, 1, length, direction);
    }

    SCSI_Sense_Data sense;
    memset(&sense, 0, sizeof(sense));
    SCSITaskStatus status = 0;
    UInt64 transferred = 0;
    IOReturn result = (*task)->ExecuteTaskSync(task, &sense, &status, &transferred);
    (*task)->Release(task);

    if (result != kIOReturnSuccess || status != 0) {
        EmitError([NSString stringWithFormat:@"%s failed (kr=0x%x status=0x%x transferred=%llu)",
                   label, result, status, (unsigned long long)transferred]);
        PrintSense(&sense);
        return -1;
    }
    return 0;
}

static IOReturn ExecNoData(DRMmcHandle *handle, const uint8_t *cdb, UInt8 cdbLength,
                           UInt32 timeoutMs, const char *label) {
    SCSITaskInterface **task = (*handle->device)->CreateSCSITask(handle->device);
    if (task == NULL) return kIOReturnError;
    (*task)->SetTimeoutDuration(task, timeoutMs);
    (*task)->SetCommandDescriptorBlock(task, (UInt8 *)cdb, cdbLength);
    SCSI_Sense_Data sense;
    memset(&sense, 0, sizeof(sense));
    SCSITaskStatus status = 0;
    UInt64 transferred = 0;
    IOReturn result = (*task)->ExecuteTaskSync(task, &sense, &status, &transferred);
    (*task)->Release(task);
    if (result != kIOReturnSuccess || status != 0) {
        EmitError([NSString stringWithFormat:@"%s failed (kr=0x%x status=0x%x)", label, result, status]);
        PrintSense(&sense);
        return result != kIOReturnSuccess ? result : kIOReturnError;
    }
    return kIOReturnSuccess;
}

static int PreventRemoval(DRMmcHandle *handle, BOOL prevent) {
    uint8_t cdb[6] = {0x1e, 0, 0, 0, prevent ? 1u : 0u, 0};
    return ExecNoData(handle, cdb, 6, 30000, prevent ? "PREVENT ALLOW MEDIUM REMOVAL" : "ALLOW MEDIUM REMOVAL") == kIOReturnSuccess ? 0 : -1;
}

static int ReadBdMedia(DRMmcHandle *handle, uint32_t *maxBlocks, uint32_t *trackNumber,
                       uint32_t *nextWritable, uint32_t *freeBlocks, uint32_t *trackSize) {
    uint8_t config[8] = {0};
    uint8_t getConfig[10] = {0x46, 0, 0, 0, 0, 0, 0, 0, sizeof(config), 0};
    if (ExecCDB(handle, getConfig, 10, config, sizeof(config),
                kSCSIDataTransfer_FromTargetToInitiator, 30000, "GET CONFIGURATION") != 0) return -1;
    uint16_t profile = ((uint16_t)config[6] << 8) | config[7];
    if (profile != 0x0041) {
        EmitError([NSString stringWithFormat:@"BD-R SRM profile required (found 0x%04x)", profile]);
        return -1;
    }

    uint8_t disc[64] = {0};
    uint8_t readDisc[10] = {0x51, 0, 0, 0, 0, 0, 0, 0, sizeof(disc), 0};
    if (ExecCDB(handle, readDisc, 10, disc, sizeof(disc),
                kSCSIDataTransfer_FromTargetToInitiator, 30000, "READ DISC INFORMATION") != 0) return -1;
    unsigned discStatus = disc[2] & 3;
    if (discStatus != 0) {
        EmitError([NSString stringWithFormat:@"BD-R is not blank (disc status %u)", discStatus]);
        return -1;
    }
    uint32_t lastTrack = disc[6] | ((uint32_t)disc[11] << 8);
    if (lastTrack == 0 || lastTrack > 0xffff) {
        EmitError(@"BD-R reports no writable track");
        return -1;
    }

    uint8_t capacities[128] = {0};
    uint8_t readCaps[10] = {0x23, 0, 0, 0, 0, 0, 0, 0, sizeof(capacities), 0};
    if (ExecCDB(handle, readCaps, 10, capacities, sizeof(capacities),
                kSCSIDataTransfer_FromTargetToInitiator, 30000, "READ FORMAT CAPACITIES") != 0) return -1;
    uint32_t maximum = ReadBE32(capacities + 4);
    if (maximum == 0) {
        EmitError(@"BD-R reported zero format capacity");
        return -1;
    }

    uint8_t track[48] = {0};
    uint8_t readTrack[10] = {
        0x52, 1,
        (uint8_t)(lastTrack >> 24), (uint8_t)(lastTrack >> 16), (uint8_t)(lastTrack >> 8), (uint8_t)lastTrack,
        0, 0, sizeof(track), 0
    };
    if (ExecCDB(handle, readTrack, 10, track, sizeof(track),
                kSCSIDataTransfer_FromTargetToInitiator, 30000, "READ TRACK INFORMATION") != 0) return -1;
    uint32_t returnedTrack = track[2] | ((uint32_t)track[32] << 8);
    if (returnedTrack != lastTrack) {
        EmitError([NSString stringWithFormat:@"BD-R returned track %u, expected %u", returnedTrack, lastTrack]);
        return -1;
    }
    uint32_t start = ReadBE32(track + 8);
    uint32_t next = ReadBE32(track + 12);
    uint32_t free = ReadBE32(track + 16);
    uint32_t size = ReadBE32(track + 24);
    if (next != 0 || start != 0) {
        EmitError([NSString stringWithFormat:@"BD-R is not at the beginning (track start %u, next %u)", start, next]);
        return -1;
    }
    *maxBlocks = maximum;
    *trackNumber = lastTrack;
    *nextWritable = next;
    *freeBlocks = free;
    *trackSize = size;
    return 0;
}

static int SetBdSpeed(DRMmcHandle *handle, double speedFactor) {
    if (speedFactor <= 0.0) return 0;
    // MMC SET CD SPEED: read speed 0xFFFF (maximum), write speed in KB/s.
    // One BD "×" is 4496 KB/s (kDRDeviceBurnSpeedBD1x).
    double requested = speedFactor * 4496.0;
    if (requested > 65535.0) {
        EmitError([NSString stringWithFormat:@"requested BD speed %.0f KB/s is out of range", requested]);
        return -1;
    }
    uint32_t kbps = (uint32_t)(requested + 0.5);
    uint8_t cdb[10] = {
        0xbb, 0,
        0xff, 0xff,
        (uint8_t)(kbps >> 8), (uint8_t)kbps,
        0, 0, 0, 0
    };
    if (ExecNoData(handle, cdb, 10, 30000, "SET CD SPEED") != kIOReturnSuccess) {
        EmitError([NSString stringWithFormat:@"cannot set BD write speed to %.2f× (%u KB/s)", speedFactor, kbps]);
        return -1;
    }
    fprintf(stderr, "speed: requested %.2f× (%u KB/s)\n", speedFactor, kbps);
    return 0;
}

static int WaitReady(DRMmcHandle *handle, NSString *label) {
    uint8_t cdb[6] = {0x00, 0, 0, 0, 0, 0};
    for (int attempt = 0; attempt < 120; attempt++) {
        SCSITaskInterface **task = (*handle->device)->CreateSCSITask(handle->device);
        if (task == NULL) return -1;
        (*task)->SetTimeoutDuration(task, 30000);
        (*task)->SetCommandDescriptorBlock(task, cdb, 6);
        SCSI_Sense_Data sense;
        memset(&sense, 0, sizeof(sense));
        SCSITaskStatus status = 0;
        UInt64 transferred = 0;
        IOReturn result = (*task)->ExecuteTaskSync(task, &sense, &status, &transferred);
        (*task)->Release(task);
        if (result == kIOReturnSuccess && status == 0) return 0;
        const uint8_t *b = (const uint8_t *)&sense;
        BOOL inProgress = (b[2] & 0x0f) == 2 && b[12] == 4 && (b[13] == 1 || b[13] == 7 || b[13] == 8);
        if (!inProgress) {
            EmitError([NSString stringWithFormat:@"%@ failed while waiting for the drive", label]);
            PrintSense(&sense);
            return -1;
        }
        sleep(1);
    }
    EmitError([NSString stringWithFormat:@"%@ timed out waiting for the drive", label]);
    return -1;
}

static int SyncCache(DRMmcHandle *handle) {
    uint8_t cdb[10] = {0x35, 0x02, 0, 0, 0, 0, 0, 0, 0, 0};
    if (ExecNoData(handle, cdb, 10, 200000, "SYNCHRONIZE CACHE") != kIOReturnSuccess) return -1;
    return WaitReady(handle, @"SYNCHRONIZE CACHE");
}

static int CloseTrack(DRMmcHandle *handle, uint32_t trackNumber) {
    uint8_t cdb[10] = {
        0x5b, 0x01, 0x01, 0,
        (uint8_t)(trackNumber >> 8), (uint8_t)trackNumber,
        0, 0, 0, 0
    };
    if (ExecNoData(handle, cdb, 10, 200000, "CLOSE TRACK") != kIOReturnSuccess) return -1;
    return WaitReady(handle, @"CLOSE TRACK");
}

static int CloseDisc(DRMmcHandle *handle) {
    uint8_t cdb[10] = {0x5b, 0x01, 0x06, 0, 0, 0, 0, 0, 0, 0};
    if (ExecNoData(handle, cdb, 10, 200000, "CLOSE SESSION") != kIOReturnSuccess) return -1;
    return WaitReady(handle, @"CLOSE SESSION");
}

static int Eject(DRMmcHandle *handle) {
    uint8_t cdb[6] = {0x1b, 0, 0, 0, 0x02, 0};
    return ExecNoData(handle, cdb, 6, 30000, "START STOP UNIT (eject)") == kIOReturnSuccess ? 0 : -1;
}

static int OpenMmc(NSInteger driveIndex, DRMmcHandle *handle) {
    io_iterator_t iterator = 0;
    kern_return_t match = IOServiceGetMatchingServices(kIOMainPortDefault,
        IOServiceMatching("IOBDServices"), &iterator);
    if (match != KERN_SUCCESS) {
        EmitError([NSString stringWithFormat:@"cannot enumerate BD services (0x%x)", match]);
        return 2;
    }
    io_service_t service;
    NSInteger index = 0;
    int result = 2;
    while ((service = IOIteratorNext(iterator))) {
        index++;
        if (index != driveIndex) { IOObjectRelease(service); continue; }
        IOCFPlugInInterface **plugin = NULL;
        SInt32 score = 0;
        kern_return_t kr = IOCreatePlugInInterfaceForService(service,
            kIOMMCDeviceUserClientTypeID, kIOCFPlugInInterfaceID, &plugin, &score);
        IOObjectRelease(service);
        if (kr != KERN_SUCCESS || plugin == NULL) {
            EmitError([NSString stringWithFormat:@"cannot open MMC interface (0x%x)", kr]);
            break;
        }
        MMCDeviceInterface **mmc = NULL;
        HRESULT hr = (*plugin)->QueryInterface(plugin,
            CFUUIDGetUUIDBytes(kIOMMCDeviceInterfaceID), (LPVOID *)&mmc);
        if (hr != S_OK || mmc == NULL) {
            EmitError([NSString stringWithFormat:@"MMC query failed (0x%x)", hr]);
            (*plugin)->Release(plugin);
            break;
        }
        SCSITaskDeviceInterface **device = (*mmc)->GetSCSITaskDeviceInterface(mmc);
        if (device == NULL) {
            EmitError(@"MMC device does not expose SCSITaskDeviceInterface");
            (*mmc)->Release(mmc);
            (*plugin)->Release(plugin);
            break;
        }
        kern_return_t exclusive = (*device)->ObtainExclusiveAccess(device);
        if (exclusive != kIOReturnSuccess) {
            EmitError([NSString stringWithFormat:@"cannot obtain exclusive access (0x%x)", exclusive]);
            (*device)->Release(device);
            (*mmc)->Release(mmc);
            (*plugin)->Release(plugin);
            break;
        }
        handle->plugin = plugin;
        handle->mmc = mmc;
        handle->device = device;
        result = 0;
        break;
    }
    IOObjectRelease(iterator);
    if (result != 0 && index == 0) EmitError(@"no BD-capable drive found");
    return result;
}

static void CloseMmc(DRMmcHandle *handle) {
    if (handle->device != NULL) {
        (*handle->device)->ReleaseExclusiveAccess(handle->device);
        (*handle->device)->Release(handle->device);
        handle->device = NULL;
    }
    if (handle->mmc != NULL) {
        (*handle->mmc)->Release(handle->mmc);
        handle->mmc = NULL;
    }
    if (handle->plugin != NULL) {
        (*handle->plugin)->Release(handle->plugin);
        handle->plugin = NULL;
    }
}

int DRRunBdMmcBurn(DRStreamOptions *options) {
    if (options.sectors == 0 || options.sectors > UINT32_MAX || options.sectors % 32 != 0) {
        EmitError(@"BD-R sector count must be a non-zero multiple of 32");
        return 2;
    }    DRMmcHandle handle = {0};
    int opened = OpenMmc(options.driveIndex, &handle);
    if (opened != 0) return opened;
    PreventRemoval(&handle, YES);

    uint32_t maximum = 0, trackNumber = 0, nextWritable = 0, freeBlocks = 0, trackSize = 0;
    if (ReadBdMedia(&handle, &maximum, &trackNumber, &nextWritable, &freeBlocks, &trackSize) != 0) {
        PreventRemoval(&handle, NO);
        CloseMmc(&handle);
        return 1;
    }
    uint32_t sectors = (uint32_t)options.sectors;
    if (sectors > maximum || sectors > freeBlocks) {
        EmitError([NSString stringWithFormat:@"requested %u sectors, but the disc has %u free of %u",
                   sectors, freeBlocks, maximum]);
        PreventRemoval(&handle, NO);
        CloseMmc(&handle);
        return 1;
    }

    EmitState(@"preparing");
    EmitPercent(0.0);
    if (SetBdSpeed(&handle, options.speedFactor) != 0) {
        PreventRemoval(&handle, NO);
        CloseMmc(&handle);
        return 1;
    }

    // Keep each WRITE(10) within a single 128 KiB SCSI transfer when the
    // sector count allows it; fall back to 64 KiB for odd 32-sector streams.
    const UInt32 sectorsPerWrite = (sectors % 64 == 0) ? 64 : 32;
    const UInt32 bytesPerWrite = sectorsPerWrite * 2048;
    uint8_t *buffer = malloc(bytesPerWrite);
    if (buffer == NULL) {
        EmitError(@"out of memory for the write buffer");
        PreventRemoval(&handle, NO);
        CloseMmc(&handle);
        return 1;
    }

    EmitState(@"writing");
    for (uint32_t lba = 0; lba < sectors; lba += sectorsPerWrite) {
        size_t filled = 0;
        while (filled < bytesPerWrite) {
            ssize_t got = read(STDIN_FILENO, buffer + filled, bytesPerWrite - filled);
            if (got < 0) {
                EmitError([NSString stringWithFormat:@"stdin read failed: %s", strerror(errno)]);
                free(buffer);
                PreventRemoval(&handle, NO);
                CloseMmc(&handle);
                return 1;
            }
            if (got == 0) {
                EmitError([NSString stringWithFormat:@"stdin ended at LBA %u (received %zu of %u bytes)",
                           lba, filled, bytesPerWrite]);
                free(buffer);
                PreventRemoval(&handle, NO);
                CloseMmc(&handle);
                return 1;
            }
            filled += (size_t)got;
        }

        uint8_t cdb[10] = {
            0x2a, 0,
            (uint8_t)(lba >> 24), (uint8_t)(lba >> 16), (uint8_t)(lba >> 8), (uint8_t)lba,
            0,
            (uint8_t)(sectorsPerWrite >> 8), (uint8_t)sectorsPerWrite,
            0
        };
        if (ExecCDB(&handle, cdb, 10, buffer, bytesPerWrite,
                    kSCSIDataTransfer_FromInitiatorToTarget, 200000, "WRITE(10)") != 0) {
            free(buffer);
            PreventRemoval(&handle, NO);
            CloseMmc(&handle);
            return 1;
        }
        uint64_t completed = (uint64_t)(lba + sectorsPerWrite) * 2048;
        uint64_t total = (uint64_t)sectors * 2048;
        EmitPercent((double)completed / (double)total * 100.0);
    }
    free(buffer);

    if (SyncCache(&handle) != 0 ||
        CloseTrack(&handle, trackNumber) != 0 ||
        CloseDisc(&handle) != 0) {
        PreventRemoval(&handle, NO);
        CloseMmc(&handle);
        return 1;
    }
    EmitPercent(100.0);
    EmitState(@"done");
    if (options.eject) Eject(&handle);
    PreventRemoval(&handle, NO);
    CloseMmc(&handle);
    return 0;
}
