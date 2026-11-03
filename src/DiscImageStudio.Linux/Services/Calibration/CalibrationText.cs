using System.Globalization;
using System.Text.RegularExpressions;

namespace DiscImageStudio.Linux.Services.Calibration;

/// <summary>English for the calibration messages DiscImageStudio.Core reports. Core writes
/// them in Chinese and the Windows catalogs do not cover them, so the Linux page translates
/// them here; anything unknown falls back to the shared catalogs, then to the original.</summary>
public static partial class CalibrationText
{
    private static readonly Dictionary<string, string> Messages = new(StringComparer.Ordinal)
    {
        ["三次轨距参数仅适用于 DVD。"] = "Cubic track-pitch parameters apply to DVD only.",
        ["三次轨距拟合需要至少 12 个不同半径的点且覆盖至少 60% 绘图区；本次保持轨距系数，只拟合内外半径。"] =
            "Fitting the cubic track pitch needs at least 12 points at different radii covering at least 60% of the drawing band. This fit kept the pitch coefficients and fitted only the inner and outer radii.",
        ["三次轨距没有带来稳定改善，保留原轨距系数；可增加内、中、外圈点位后重试。"] =
            "The cubic track pitch did not give a stable improvement, so the existing pitch coefficients were kept. Add points near the inner, middle and outer rings and try again.",
        ["两条轮廓不能得到有效的联合投影，请检查布点。"] = "The two outlines do not give a valid joint projection. Check the point placement.",
        ["候选绕行过多，完整轮廓采样超出上限；请缩小搜索范围或提高采样上限后再确认形状。"] =
            "The candidate winds too many times and the full outline exceeds the sampling limit. Narrow the search range before checking the shape.",
        ["内缘轮廓与外缘位置不符，请确认选中的是贯通的实体内缘，而非盘面的印刷圆环。"] =
            "The inner outline does not match the outer edge. Make sure you marked the physical centre hole, not a printed ring on the disc.",
        ["十字线最多包含四条臂。"] = "A crosshair has at most four arms.",
        ["十字线记录无效。"] = "The curve record is invalid.",
        ["同一角点或共享角点不能重复录入。"] = "The same corner cannot be entered twice.",
        ["外缘和内缘各需要至少 5 个分散的点，且每条轮廓不超过 256 个点。"] =
            "The outer edge and the centre hole each need at least 5 spread-out points, and at most 256 points per outline.",
        ["外缘轮廓无法形成稳定椭圆，请核对实体外缘上的点。"] = "The outer outline does not form a stable ellipse. Check the points on the physical outer edge.",
        ["存在形状接近的参数候选，请对照整条曲线后再应用。"] = "Candidates with similar shapes exist. Compare the whole curve before applying.",
        ["存在拟合程度接近的绕圈候选，暂不能唯一确定参数；请在预测差异明显的另一半径补选角点。"] =
            "Candidates with a different number of turns fit almost as well, so the parameters are not unique yet. Add points at a radius where the predictions differ clearly.",
        ["完整曲线采样超出点数上限；请缩小预测范围或提高采样上限后再确认形状。"] =
            "The full curve exceeds the sampling limit. Narrow the prediction range before checking the shape.",
        ["宽范围只进行了有限多起点搜索，候选列表不代表穷尽所有可能参数。"] =
            "The wide range was searched from a limited number of starting points; the candidate list is not exhaustive.",
        ["尚未确认照片矫正，不能保存描线点。"] = "Confirm the photo rectification before saving traced points.",
        ["已拟合 DVD 三次轨距；三个形状系数与内外半径可能互相补偿，低残差不表示参数唯一，应用前请核对整条预测曲线。"] =
            "Fitted the cubic DVD track pitch. The three shape coefficients and the radii can compensate for each other, so a low residual does not prove the parameters are unique. Check the whole predicted curve before applying.",
        ["当前曲线对两个半径的约束较相似，暂不能仅凭低残差判断参数唯一；请扩大径向覆盖并检查点位精度。"] =
            "This curve constrains both radii in a similar way, so a low residual alone does not prove the parameters are unique. Cover a wider radial range and check the point accuracy.",
        ["当前点位无法稳定区分三个轨距系数与内外半径，本次保留原轨距系数；请扩大径向覆盖并提高点位精度。"] =
            "These points cannot separate the three pitch coefficients from the radii, so the existing pitch coefficients were kept. Cover a wider radial range and place points more accurately.",
        ["拍摄角度或轮廓对应关系导致投影退化，请换角度拍摄或重新布点。"] =
            "The camera angle or the outline points make the projection degenerate. Photograph from another angle or place the points again.",
        ["控制块或控制点编号无效。"] = "Invalid control cell or point number.",
        ["控制块或角点编号无效。"] = "Invalid control cell or corner number.",
        ["控制块编号无效。"] = "Invalid control cell number.",
        ["控制点与预测图形偏差较大，请检查分组与弧片身份、控制点对应关系和显示尺度。"] =
            "The control points deviate strongly from the prediction. Check the point correspondence and the display scale.",
        ["控制点坐标或权重无效。"] = "Invalid control point coordinates or weights.",
        ["控制点数量超出限制。"] = "Too many control points.",
        ["控制点覆盖的径向范围偏窄，建议补选更靠内、外的角点复核。"] =
            "The control points cover a narrow radial range. Add points further in and further out.",
        ["控制点集中在相近半径，无法稳定区分内外半径；请补选靠内圈或外圈的角点。"] =
            "The control points sit at similar radii, so the inner and outer radii cannot be separated. Add points near the inner or outer ring.",
        ["搜索半径范围应在 0–5 mm 内，控制点容差应在 0–10 mm 内，轨距系数搜索范围应在 0–10 内。"] =
            "The radius search range must be 0–5 mm, the point tolerance 0–10 mm and the pitch search range 0–10.",
        ["搜索范围内没有有效模型，请检查控制点对应关系或扩大搜索范围。"] =
            "No valid model was found in the search range. Check the points or widen the search range.",
        ["搜索范围内没有能覆盖全部观测点的有效正半径曲线延拓，请检查照片尺度、盘心或参数范围。"] =
            "No curve in the search range reaches every traced point. Check the photo scale, the disc centre or the parameter range.",
        ["搜索范围包含过多绕圈组合，已进行有限多起点搜索；请缩小范围或增加分散的控制点后复核。"] =
            "The search range allows too many turn combinations and was searched from a limited number of starts. Narrow the range or add spread-out points.",
        ["旋转必须是有限数值。"] = "The rotation must be a finite number.",
        ["曲线偏差较大，请检查照片矫正、点位和是否始终沿同一条线标点。"] =
            "The curve deviates strongly. Check the rectification, the point positions and that every point is on the same line.",
        ["曲线出现明显向内回退，请按内到外的顺序沿同一条线标点。"] =
            "The curve clearly steps back inwards. Place points along one line from the inside out.",
        ["曲线旋转或臂编号无效。"] = "Invalid curve rotation or arm number.",
        ["曲线点坐标无效，请使用矫正后以盘心为原点的坐标。"] = "Invalid curve point coordinates.",
        ["曲线点数量超出限制。"] = "Too many curve points.",
        ["曲线点较多，拟合使用均匀抽取的点；完整顺序仍用于角度展开。"] =
            "There are many curve points, so the fit uses an even subsample; the full order is still used to unwrap the angle.",
        ["曲线编号无效或重复。"] = "Invalid or duplicate curve number.",
        ["曲线覆盖的内外范围偏窄，参数可能互相补偿；请补充更靠内、外的点。"] =
            "The curve covers a narrow radial range, so the parameters may compensate for each other. Add points further in and further out.",
        ["曲线覆盖范围太窄，无法稳定分离内外半径；请继续标出更靠内、外的曲线。"] =
            "The curve covers too narrow a range to separate the inner and outer radii. Keep tracing the curve further in and further out.",
        ["最佳结果靠近搜索边界，建议扩大搜索范围并补充控制点复核。"] =
            "The best result is near the edge of the search range. Widen the range and add points to check it.",
        ["最佳结果靠近搜索边界，建议扩大搜索范围并补充曲线点复核。"] =
            "The best result is near the edge of the search range. Widen the range and add curve points to check it.",
        ["有相邻曲线点的转角接近半圈，绕圈判断可能不可靠；请补点后复核。"] =
            "Two neighbouring points are almost half a turn apart, so the turn count may be wrong. Add points between them and check again.",
        ["标定会话为空。"] = "The calibration session is empty.",
        ["标定会话文件过大。"] = "The calibration session file is too large.",
        ["标定参数无效：请检查盘片类型、内外半径、扇区数和线速度／位长。"] =
            "Invalid calibration parameters: check the disc type, radii, sector count and linear velocity / bit length.",
        ["标定固定盘片类型、扇区数和线速度／位长；DVD 可拟合内外半径、三次轨距与整体旋转。"] =
            "Calibration keeps the disc type, sector count and linear velocity / bit length fixed; DVD can also fit the cubic track pitch.",
        ["标定图案 ID 无效。"] = "Invalid calibration pattern ID.",
        ["标定图案 PNG 与固定参考文件不一致。"] = "The calibration pattern PNG does not match the built-in reference.",
        ["标定图案与生成参数或图案规则不一致。"] = "The calibration pattern does not match its generation parameters.",
        ["标定图案像素与固定参考文件不一致。"] = "The calibration pattern pixels do not match the built-in reference.",
        ["标定图案含重复控制块 ID。"] = "The calibration pattern contains duplicate control cells.",
        ["标定图案控制块数量无效。"] = "Invalid number of calibration pattern control cells.",
        ["标定图案的可绘制扇区范围无效。"] = "Invalid drawing sector range for the calibration pattern.",
        ["标定照片为空或过大，请重新导入照片。"] = "The calibration photo is empty or too large. Import the photo again.",
        ["标定组编号无效。"] = "Invalid calibration group number.",
        ["标定记录中的兼容图案选项无效，请重新创建标定图案。"] =
            "The record's pattern options are invalid. Create the calibration target again.",
        ["源控制点不在生成时的绘图环带内。"] = "A source control point is outside the drawing band used for generation.",
        ["照片中的盘片过小，无法稳定矫正。"] = "The disc is too small in the photo to rectify reliably. Photograph it closer.",
        ["照片变换不可逆。"] = "The photo transform cannot be inverted.",
        ["照片变换产生了无效坐标。"] = "The photo transform produced invalid coordinates.",
        ["照片变换必须包含 9 个有限数值。"] = "The photo transform must contain 9 finite numbers.",
        ["照片变换退化，无法恢复盘面坐标。"] = "The photo transform is degenerate; disc coordinates cannot be recovered.",
        ["照片坐标必须为有限数值。"] = "Photo coordinates must be finite numbers.",
        ["照片轮廓控制点无效。"] = "Invalid photo outline points.",
        ["盘片与内缘的实体尺寸无效。常规盘为直径 120 mm、孔径 15 mm。"] =
            "Invalid physical disc size. A standard disc is 120 mm across with a 15 mm centre hole.",
        ["相邻曲线点转角接近半圈，无法判断是否漏掉整圈；请在这两点之间沿实际曲线补点。"] =
            "Two neighbouring points are almost half a turn apart, so a missed full turn cannot be ruled out. Add points between them along the real curve.",
        ["缺少内置标定参考图案资源。"] = "The built-in calibration reference pattern is missing.",
        ["至少确认 4 个用于拟合的角点，并选择分布在不同半径的控制块；验证组与未确认角点不参与拟合。"] =
            "Confirm at least 4 corners at different radii for the fit.",
        ["蓝光标定需要完整的 32 扇区簇、74.5 nm 位长和不超过 60 mm 的外半径。"] =
            "Blu-ray calibration needs whole 32-sector clusters, a 74.5 nm bit length and an outer radius of at most 60 mm.",
        ["角度展开以相邻点之间没有遗漏整圈为前提；请对照完整曲线，在弯曲或绕行处补点后确认。"] =
            "Unwrapping assumes no full turn is skipped between neighbouring points. Compare the whole curve and add points where it bends before applying.",
        ["角点坐标必须是有限数值。"] = "Corner coordinates must be finite numbers.",
        ["该位置接近照片投影地平线，无法矫正。"] = "This position is near the photo's vanishing line and cannot be rectified.",
        ["该观测半径在当前参数下没有有效的正半径曲线延拓，请检查照片尺度、盘心或参数范围。"] =
            "This traced radius has no valid curve under the current parameters. Check the photo scale, the disc centre or the parameter range.",
        ["该进度没有有效的正半径轨迹。"] = "There is no valid track at this position.",
        ["请分别输入实体外缘和实体内缘轮廓。"] = "Mark both the physical outer edge and the physical centre hole.",
        ["请沿同一条曲线从内到外至少标出 6 个点，覆盖靠内、中间和靠外的位置。"] =
            "Place at least 6 points along one curve from the inside out, covering the inner, middle and outer parts.",
        ["请选择 12 cm 或 8 cm 光盘。"] = "Choose a 12 cm or 8 cm disc.",
        ["轨距系数接近搜索边界，请复核点位，必要时扩大系数搜索范围。"] =
            "A pitch coefficient is near the edge of its search range. Check the points.",
        ["轮廓中存在无效坐标。"] = "The outline contains invalid coordinates.",
        ["轮廓在当前轨距参数下没有有效映射。"] = "The outline has no valid mapping under the current pitch parameters.",
        ["轮廓点重复或接近一条直线，请沿外缘和内缘分别分散布点。"] =
            "The outline points repeat or lie almost on a line. Spread the points around the outer edge and the centre hole.",
        ["轮廓点集中在局部，请在两条完整圆周的不同方向补点。"] =
            "The outline points are bunched together. Spread them around both full circles.",
        ["部分描点在候选模型下超出原图案环带，已按连续曲线保留；请复核照片尺度、盘心及端点是否沿同一条可见曲线。"] =
            "Some traced points lie outside the original pattern band under this candidate and were kept as a continuous curve. Check the photo scale, the centre and that the end points follow the same visible curve.",
        ["预测曲线需要有限、递增且大于零的观测半径区间。"] = "The prediction needs a valid, increasing radius range.",
    };

    public static string En(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        if (Messages.TryGetValue(message, out string? english))
        {
            return english;
        }

        Match both = EdgesMismatch().Match(message);
        if (both.Success)
        {
            return $"The two physical outlines do not fit together: outer edge error {both.Groups[1].Value} px, centre hole error {both.Groups[2].Value} px. Check both outlines and the disc size.";
        }

        Match fitted = EdgesFitted().Match(message);
        if (fitted.Success)
        {
            return $"Rectified using the outer edge and the centre hole. Outline error: outer {fitted.Groups[1].Value} px, hole {fitted.Groups[2].Value} px.";
        }

        Match version = SessionVersion().Match(message);
        if (version.Success)
        {
            return $"Unsupported calibration session version {version.Groups[1].Value}. Create the calibration target again.";
        }

        return Translator.T(message);
    }

    public static string Number(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^两条实体轮廓无法同时吻合：外缘误差 ([\d.]+) px，内缘误差 ([\d.]+) px")]
    private static partial Regex EdgesMismatch();

    [GeneratedRegex(@"^已联合校正外缘与内缘。轮廓误差：外缘 ([\d.]+) px，内缘 ([\d.]+) px")]
    private static partial Regex EdgesFitted();

    [GeneratedRegex(@"^不支持的标定会话版本 (\d+)")]
    private static partial Regex SessionVersion();
}
