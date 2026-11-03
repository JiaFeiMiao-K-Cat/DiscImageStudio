import Foundation

public extension ProjectDocument {
    /// 应用标定候选：更新生成几何（半径；DVD 三次项；BD 扇区/极性），保留其他字段。
    /// 与 Windows `CalibrationViewModel.ApplyParameters` 的字段范围一致。
    func applyingCalibration(_ candidate: CalibrationCandidate) -> ProjectDocument {
        var updated = self
        updated.geometry.innerRadiusMm = candidate.innerRadiusMm
        updated.geometry.outerRadiusMm = candidate.outerRadiusMm
        switch discFamily {
        case .dvd:
            updated.geometry.pitchLinear = candidate.pitchLinear
            updated.geometry.pitchQuadratic = candidate.pitchQuadratic
            updated.geometry.pitchCubic = candidate.pitchCubic
        case .bd:
            if let sectors = candidate.sectors, sectors > 0 {
                updated.geometry.sectors = sectors
            }
            if let invert = candidate.invertMapping {
                updated.bluRay = ProjectBluRaySettings(invertMapping: invert)
            }
        case .cd:
            break
        }
        return updated
    }
}
