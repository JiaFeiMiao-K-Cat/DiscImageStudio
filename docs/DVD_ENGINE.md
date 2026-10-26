# DVD NRZI 图像约束求解器

本项目把连续 16 个 DVD 数据扇区作为一个 ECC Block，根据二维图片或指定的 NRZI pit/land 约束搜索可读的用户数据。

固定约定：

- 单层盘片 Data Zone，Sector Information 为 `0x00`；
- DVD-R Data Frame 的 RSV 为六个 `0x00`；
- 只输入 ECC Block 的初始 LBA，后续扇区自动使用 `LBA + 1 ... LBA + 15`；
- `PSN = PSN offset + LBA`，默认 offset 为 `0x30000`；
- DSV 绝对值相等时选择 Stream 1；
- `pit = 0 = 黑色`，`land = 1 = 白色`；
- 每次输出 32768 字节，即 16 个连续的 2048-byte payload。

编码链包括 ID、IED、全零 RSV、EDC、数据扰码、RS-PC PO/PI、PO 行交织、EFMPlus 主表/替代表、SYNC/DSV 控制和 NRZI 转换。

独立[灰阶实验工具](../tools/DvdGrayProbe/README.md)使用 `FastDispersionImageWriter.WriteScrambledCodewordPatternStream`，在同一 payload 码字中心坐标系下直接指定扰码 Q，再写出 `P = Q XOR mask`。该接口不经过固定灰度分档，适合测试 `64/E4` 等候选码字和离散抖动图集；普通图片生成的调色板不受影响。快速生成不回放全盘 ECC/EFM，也不预测光学亮度。回归测试对混合码字执行完整 ECC/EFM 小样本回放，另验证实际 payload、几何、并行一致性和取消。

## 构建和自检

构建需要 Windows 和 .NET 10 SDK；直接运行非自包含产物需要 .NET 10 Windows 桌面运行时：

```powershell
dotnet build DvdImageSolver.slnx --configuration Release
dotnet run --project src/DvdImageSolver --configuration Release -- selftest
```

自检覆盖：

- ECMA-267 调制表抽样；
- ID、IED、RSV 和 EDC；
- 扰码异或往返；
- 208 行 PI 与 172 列 PO 的零余数；
- 619008 个物理 NRZI 电平的数量、确定性和 RLL(2,10) 数据游程；
- 16-bit码字在pit/land两种初始电平下的预计算灰度、结束电平和DSV增量；
- 随机SYNC Frame的码字复放灰度、SYNC选择、最终State/DSV/电平与逐bit编码器一致；
- 双流EFMPlus前缀状态与逐bit编码器在无需备用SYNC重放时完全一致；
- state-control最终边界与完整DVD前向编码一致，求解过程不物化619008-bit电平数组；
- dispersion模式的`0x92/0xA5`扰码域映射、反扰码ISO payload和最终边界与完整编码一致；
- 最小 ISO9660 描述符、Path Table、根目录、payload LBA 和模板替换结果。
- 半径标定 PNG 预览和连续多 ECC Block 原始镜像输出。
- 内圈 ISO9660/Joliet 文件、Unicode 子目录和外圈绘图数据的隔离保护。

## 目标文件

目标文件使用字符表示物理 NRZI 电平：

```text
0 或 P    pit，黑色
1 或 L    land，白色
?         不约束
```

空白和下划线会被忽略。文件必须包含以下二者之一：

- 38688 个符号：约束一个物理扇区，通过 `--target-sector 0..15` 指定它在 ECC Block 中的位置；
- 619008 个符号：约束完整 ECC Block。

SYNC、RLL、EDC 和 ECC 会限制可实现的图案，因此任意逐 bit 图像通常不存在零误差解。`full`模式以不匹配的NRZI bits为目标；图片的`state-control`模式把目标二值化为黑或白，先计算pit/land多数方向是否错误，再计算实际land数量到纯黑0或纯白16的灰度距离。搜索目标只使用可控payload码字，最终可见度则统计全部channel码字。

`dispersion`模式不控制绝对pit/land多数，而用EFMPlus跳变密度形成黑白二阶纹理。采用扰码后字节`Q=0xA5/0x92`，所有状态表示分别固定为5/2次transition。图片亮度0–127映射A5、128–255映射92，预览亮度为0/255。新生成的图片不再使用66或B9作为独立灰阶。NRZI整体反色不改变transition位置。

已抖动的全不透明灰度图片若只含`0/255`（允许全黑或全白），生成与标定预览使用最近像素采样，保留像素的原始档位；普通连续灰度、彩色或带透明度的图片仍采用双线性采样。主程序的CD/DVD环形复制对黑白离散输入先进行高质量缩放和旋转，再重新二阶抖动，避免缩放后的连续灰度直接分档而损失混合层次。`full`和`state-control`的二值目标保持原来的采样规则。

## 图片映射

可以直接输入 PNG、JPEG、BMP、TIFF 或 GIF：

```powershell
dotnet run --project src/DvdImageSolver --configuration Release -- solve `
  --image picture.png `
  --total-sectors 2295104 `
  --iso-output solved-disc.iso `
  --output payload.bin `
  --lba 0 `
  --psn-offset 0x30000 `
  --inner-radius-mm 24 `
  --outer-radius-mm 58 `
  --channel-bit-nm 133.3 `
  --start-angle-deg 0 `
  --spiral-direction ccw `
  --image-threshold 128 `
  --alpha-threshold 1 `
  --iterations 10000 `
  --constraint-output mapped-target.txt `
  --channel-output channel.bin
```

其中 `--total-sectors` 是盘片报告的 2048-byte 数据块总数；也可以写成别名 `--total-blocks`。它和每扇区 38688 channel bits、`--channel-bit-nm` 一起确定总轨道长度。程序再根据内外半径和轨距曲线反解轨距尺度。

### 随半径变化的三次轨距

DVD 生成、流式输出、图片预览、ISO 回读和辅助标定共用 `DvdTrackGeometry`。设归一化半径 `x=(r-ri)/(ro-ri)`，局部轨距为：

```text
p(x) = a0 + a1*x + a2*x² + a3*x³                 [mm]
     = p0 * (1 + c1*x + c2*x² + c3*x³)
dr/dθ = p(x)/(2π)
```

完整三次多项式有四个系数。输入的 `--pitch-linear`、`--pitch-quadratic`、`--pitch-cubic` 是三个无量纲相对系数 `c1,c2,c3`；常数项 `a0=p0` 根据固定的总轨道长度自动求解，另外三项为 `ai=p0*ci`。因此四项满足容量和内外半径的长度约束，而不是四项全部独立。界面以 μm 展示完整四项，系数的变量是 `x`，不是以 mm 为单位的绝对半径。

轨长使用 `L=∫sqrt(1+(2πr/p(r))²)dr`，角度使用 `θ(r)=∫2π/p(r)dr`；变化轨距不能再用恒定螺距的 `(r-ri)/b` 计算角度。程序验证整个生成环带内轨距严格为正，包括多项式内部极值。三项均为零时使用原恒定轨距模型，旧参数和预设省略这些字段时默认零。

例如在原 `solve` 命令后增加 `--pitch-linear 0.02 --pitch-quadratic -0.03 --pitch-cubic 0.015`。这只是参数格式示例，不是任何盘片的实测值。`calibrate` 使用相同选项指定生成曲线，另以 `--actual-pitch-linear`、`--actual-pitch-quadratic`、`--actual-pitch-cubic` 指定实际曲线；也接受 `--generated-pitch-*` 别名。`simulate` 的 `--pitch-*` 指实际盘片曲线。

镜像和预览的 JSON 记录相对系数及轨距基值，标定会话保存相对系数、内外半径、扇区数及位长，能够重建完整四项。照片整体转角仍是独立拟合量。

坐标和采样约定：

- 图片中心对应盘片中心，图片短边对应外径，长边多出的区域保留；
- 轨道在内半径处从 0° 开始，0° 指向图片右侧；默认按数学方向逆时针，可用 `--spiral-direction cw` 改为顺时针；
- 图片黑色映射为 pit，白色映射为 land；灰度低于 `--image-threshold` 算黑色；
- alpha 低于 `--alpha-threshold` 的透明区域不施加约束；
- `--lba` 从轨道起始数据块按零开始计数，程序只映射该 LBA 开始的连续 16 个 sector；
- `--constraint-output` 可选，用于导出实际生成的 619008-symbol 线性约束以便检查。

## 求解

```powershell
dotnet run --project src/DvdImageSolver --configuration Release -- solve `
  --target target.txt `
  --output payload.bin `
  --lba 0 `
  --psn-offset 0x30000 `
  --initial-state 1 `
  --initial-dsv 0 `
  --initial-level land `
  --target-sector 0 `
  --iterations 10000 `
  --mutation-bytes 4 `
  --seed 1 `
  --channel-output channel.bin
```

重要参数：

- `--lba`：ECC Block 的初始 LBA，不是 16 个 LBA；
- `--psn-offset`：十进制或 `0x` 十六进制，默认 `0x30000`；
- `--initial-state`：EFMPlus 状态 1 到 4；
- `--initial-dsv`：进入当前 ECC Block 前的累计 DSV；
- `--initial-level`：进入当前块前的物理状态 `pit` 或 `land`；
- `--seed-payload`：可选的 32768-byte 初始解；未提供时默认生成确定性的随机初始解；
- `--initial zero`：改用全零初始 payload；
- `--channel-output`：额外输出最终 NRZI 电平，每8个电平按 MSB-first 打包。

`--target` 和 `--image` 二选一。图片模式还必须提供 `--total-sectors` 或 `--total-blocks`。

### State-control码字灰度求解模式

原有不限制payload位置的算法保留为`--algorithm full`，也接受别名`exact`。旧的空间控制字节池算法已经删除；新模式用以下参数启用：

```powershell
dotnet run --project src/DvdImageSolver --configuration Release -- solve `
  --image picture.png `
  --total-sectors 2297888 `
  --output payload.bin `
  --algorithm state-control `
  --bridge-bytes 2 `
  --beam-width 8 `
  --retry-beam-width 32 `
  --retry-frame-fraction 0.25 `
  --batch-frames 8 `
  --batch-candidates 4 `
  --search-lanes 1 `
  --iterations 3
```

state-control把每个91-byte Sync Frame作为一个求解段。EDC、PI、PO、固定字段和SYNC仍然精确生成并参与State、DSV和pit/land边界传播；它们不参与自由字节搜索目标，但会参与最终整盘可见度评分。程序按`EFMPlus state × 输入电平 × 当前累计DSV`计算最优黑/白字节对。目标位置对每条活跃Stream只尝试其当前精确状态对应的黑或白字节；每个前缀只保留评分最好的`--beam-width`条路径。每个包含图像目标的自由段还会把首尾各`--bridge-bytes`个payload字节作为控制位置，但控制位置也只在当前DSV字节对中选择，不再枚举全部256个值。

程序启动时预处理全部65536个16-bit码字在初始pit和land时的：

- 16个NRZI位置中land的数量，即0到16的17级灰度；
- 码字结束后的pit/land；
- 精确DSV增量；
- 前导和尾随零数，用于RLL边界检查。

字节对对每种可能的EFMPlus表示同时计算灰度误差和写入该码字后的`|DSV|`，联合代价为`最坏灰度误差 × 16 + 最坏输出|DSV|`。因此一个灰度级仍比15个DSV单位更重要，但当前DSV的方向和大小可以在灰度相近的字节之间改变选择。常用的DSV -256..256字节对会预计算；若传入或运行中超出此范围则按精确DSV即时计算，不截断。这里的字节是扰码之后、进入EFMPlus的recording-frame字节；写入ISO的原始payload会经过扰码异或逆映射，因此其数值不一定只有这几种。

内层候选以一个EFMPlus码字为单位评分，不展开16个channel levels。每条beam路径同时保存Stream 1和Stream 2，不会提前把其中一条错误丢弃。SYNC主/副码、Stream 1平局选择、累计DSV和最终边界仍按原编码器的整数规则逐码字精确复放；每个Sync Frame的幸存序列还会用完整91字节字级编码器重算，以处理DSV越界时的备用SYNC。最终结果只做码字级多数、灰度与边界复核；只有显式要求`--channel-output`时才展开逐bit数据。

state-control搜索时使用严格字典序目标：先最小化自由payload的`controlled-majority-error`，只有多数判错数相等时才比较`controlled-gray-error`，仍相等时才比较边界和DSV。控制台与JSON中的主`majority-error`/`score`表示最终全部channel码字的可见误差：每个Sync Frame包含2个SYNC码字和91个数据码字，一个ECC Block在不透明图片下的分母固定为`416 × 93 = 38688`。主`gray-error`同样覆盖这38688个码字。自由字节搜索分数另存为`controlledScore`和`controlledGrayScore`；所有误差都是越低越好。

每个外层pass先用当前完整ECC Block精确计算每个Sync Frame的controlled majority/gray误差，再选择最差的`--retry-frame-fraction`（默认0.25）帧。程序先把这些帧作为一次跨块warm-start候选，以快速离开随机初始解；无论局部EFM评分多好，都必须更新EDC、PO、PI并完成整块精确复放后才能接受。候选ECC更新会复用当前recording frames：只重建payload发生变化的sector、受影响的PO列和PI行，结果由自检逐字确认与全量ECC重建一致。随后候选按`--batch-frames`（默认8）组成小批量，每组并行生成和复核`--batch-candidates`（默认4）个不同EFM序列。只有整块controlled评分严格改善时才接受。批量失败后依次拆成更小的组，直到单帧；单帧失败时可用`--retry-beam-width`（默认32）再试一次。一次拆分只沿第一条成功分支继续，避免穷举整棵拆分树。一个pass没有任何候选被接受就提前停止。`--retry-frame-fraction 0`表示每个pass处理全部有错帧，`--retry-beam-width 0`关闭单帧宽束重试。

`--beam-width`、`--retry-beam-width`和`--batch-candidates`越大，越容易找到ECC反馈后仍有效的候选，但运行时间近似随候选复核次数增加。`--batch-frames`较大时单次局部改善更强，但更容易因PO/PI反馈而被整块验收拒绝；较小则更稳但需要更多次完整重建。`--iterations`或范围模式的`--iterations-per-block`表示闭环pass数，默认3。兼容别名`--algorithm control`仍可使用；旧的`--frame-candidates`、`--parallel-candidates`、`--parallelism`、`--word-candidates`和`--control-bytes`会被拒绝。`--mutation-bytes`只属于`full`模式。

`--search-lanes 1..8`用于降低局部极小值造成的接近50%误差。lane 1使用传入的初始payload并保持严格单调验收；其余lane用`--seed`派生的确定性独立payload重新开始，并允许按`--initial-temperature`到`--final-temperature`的退火温度暂时接受整块精确分数较差的候选，以跨过PO/PI造成的局部障碍。每个lane始终另存完整ECC精确复放后的历史最优，所有lane并行运行，最后按controlled majority、gray、最终`|DSV|`依次择优，因此探索步骤不会使最终输出劣于lane 1。默认1不增加成本。质量优先可先在少量sector上试`--search-lanes 4 --iterations-per-block 10`；独立重启会改变未受图像约束的payload，因此需要保留指定payload内容时应保持1。

`full/exact`表示不限制可变payload位置，并不表示穷举`256^32768`空间或保证全局最优。图片先由`--image-threshold`逐像素二值化；state-control再对每个16-bit payload码字内的图像样本做黑白多数投票，目标只会是纯黑0或纯白16，8:8平局用码字中心样本决定。求解时先最小化实际码字多数方向判错数，再以land数量到0/16的距离作为灰度误差。它不匹配16个位置各自的bit图案；若要求逐channel-bit位置完全对应，应使用`full`。

### Dispersion单字节纹理模式

`--algorithm dispersion`（别名`byte-pool`）直接在扰码域生成payload，不执行随机搜索：

```text
图片亮度   0..127 -> Q = 0xA5 -> 固定5次transition -> 预览0
图片亮度 128..255 -> Q = 0x92 -> 固定2次transition -> 预览255
ISO payload P = Q XOR sector scrambler byte
```

每个payload码字都按其16个channel bits的盘面中心采样图片，再按上述固定区间分档，因此该模式不受`--constraint-step`或`--image-threshold`影响；`--image-threshold`仅控制`full/state-control`的二值目标。ID、IED、全零RSV、EDC、PI、PO和SYNC仍按标准生成；程序不展开619008个NRZI bits，但会用完整recording frames逐码字重放，精确传递最终EFMPlus state、DSV和pit/land到下一ECC Block。

#### 影调抖动

两个纹理本身只有黑白，但图片的中间调可以在每个payload字节上按比例混合它们：

```powershell
--tone-dither 1 --tone-levels 2 --tone-brightness 0
```

- `--tone-dither 0..1`：0 保持原来的 `--image-threshold` 硬阈值；1 按字节抖动阈值，使灰度按黑白码字的空间比例还原。
- `--tone-levels`：DVD 只有两种纹理，只接受 2。
- `--tone-brightness -64..64`：整体平移灰度。

抖动相位由逻辑payload字节索引决定，与预览采样无关；`simulate` 读取 ISO 时只按 `0xA5/0x92` 分类，因此读回模拟自然反映抖动结果。缺省不传这些参数时输出与旧版逐字节一致。

只需要尽快生成可刻录镜像、不需要输出最终EFMPlus边界时，可加`--fast-output true`。快速输出仍在相同的payload码字中心采样图片，并产生与普通dispersion模式相同的2048-byte sector；区别是它用预计算的32 KiB扰码掩码直接从`Q`反推ISO字节`P`，不构造Data Frame、EDC、PI、PO和Recording Frame，也不回放EFMPlus、DSV或pit/land状态。JSON中的`exactEccEfmReplay`会是`false`且`finalBoundary`为`null`。这不会省略刻录所需的数据：刻录机仍会从2048-byte sector自行生成这些派生字段。

```powershell
& ".\src\DvdImageSolver\bin\Release\net10.0-windows\DvdImageSolver.exe" solve `
  --image target.png `
  --total-sectors 2297888 `
  --iso-output "R:\full-disc-fast.iso" `
  --fill-sectors 2297888 `
  --lba 0 `
  --psn-offset 0x30000 `
  --inner-radius-mm 24 `
  --outer-radius-mm 58 `
  --channel-bit-nm 133.3 `
  --algorithm dispersion `
  --fast-output true `
  --fast-parallelism 0 `
  --initial zero
```

`--fast-parallelism 0`会按逻辑处理器数量并行生成互不依赖的ECC Block payload，再按LBA顺序写入镜像；也可以设为`1..64`限制CPU占用。并行不会改变字节选择或输出顺序。

快速文件输出与流式输出在支持硬件加速时默认启用四点双精度 SIMD 采样。每组仍使用四个实际 payload 码字中心，不把跨 SYNC/ECC 的位置当作连续等距样本。三次轨距的同段求逆共用一次查段，并向量化原有四次 Newton/Hermite 计算；跨段和边界回退到标量几何。全不透明黑白图使用向量正余弦，接近半像素或图片边缘的点按原标量路径重算；普通灰阶、彩色和透明图保留原有标量正余弦与插值，只加速三次轨距几何。恒定轨距、解析图案回调和无 SIMD 硬件的机器继续使用标量采样。恒定轨距的四点路径在当前实测中存在性能回退，因此暂不启用。全程保留 `double`，不使用单精度坐标或近似螺旋公式。

调用生成 API 时可设置 `MultiBlockSolveOptions.UseSimdSampling = false` 使用标量参考路径，便于性能与输出比较；默认值为 `true`，无需改动现有 GUI 或命令行参数。回归覆盖两种采样路径的 payload/计数一致性、三次轨距分段边界、半像素边界、透明度阈值、顺逆时针和并行输出顺序。吞吐应按相同输入与 LBA 范围的实际 payload 字节计时；单独三角函数的加速比不代表整盘加速比。

恒定轨距的快速求逆缓存求解完成后不变的内半径积分值、半径平方与轨距系数，并在一次 Newton 修正中复用相同的平方根。该路径保留原有 `double` 运算顺序、除法和边界行为，不使用近似积分或乘倒数替代除法。独立旧公式回归逐位比较半径、角度及短螺旋边界；性能比较须使用优化前后的独立引擎，因为 `UseSimdSampling` 开关两侧都会使用这项标量优化。

`payload-transition-class-error=0`表示所有受控payload都正确分配到了二阶`0xA5/0x92`类别，不表示固定字段、ECC和SYNC也具有相同transition密度。输出JSON的`dispersionPool.levels`记录两档输入区间、Q字节、跳变数和名义预览亮度；这些预览值不等同于实盘反射率。

输出包括：

- `payload.bin`：16 个2048-byte扇区顺序拼接；
- `payload.bin.json`：PSN、初始/结束边界状态、误差、迭代次数和运行时间；
- 可选的 `channel.bin`：77376-byte 打包 NRZI 电平。

## 输出可刻录 ISO

`solve` 可以只输出 ISO，也可以同时保留 32768-byte payload：

```powershell
dotnet run --project src/DvdImageSolver --configuration Release -- solve `
  --image picture.png `
  --total-sectors 2295104 `
  --iso-output solved-disc.iso `
  --lba 0 `
  --iterations 10000
```

图片模式会直接复用 `--total-sectors` 作为 ISO 的 logical sector 数。使用文本约束时需另给 `--iso-sectors`：

```powershell
--target target.txt --iso-output solved-disc.iso --iso-sectors 2295104
```

新建镜像包含最小 ISO9660 文件系统和一个 `SOLVED.BIN` 文件，求解出的 16 个 2048-byte sector 会写到 `--lba ... --lba+15`。当 LBA 为 0 时，这些数据位于 ISO9660 system area，程序会在文件系统数据区再放一份 `SOLVED.BIN` 可见副本。支持稀疏文件的 NTFS 上，新镜像会尽量使用稀疏存储；它的逻辑长度仍然是 `total-sectors × 2048`，刻录软件读取到的空洞是全零。

也可以基于已有 ISO 替换连续 16 个 sector：

```powershell
dotnet run --project src/DvdImageSolver --configuration Release -- solve `
  --image picture.png `
  --total-sectors 2295104 `
  --iso-template original.iso `
  --iso-output solved-disc.iso `
  --lba 32 `
  --iterations 10000
```

模板大小必须是 2048 的整数倍；图片模式下，模板 sector 数还必须等于 `--total-sectors`。替换已分配给目录、文件或 UDF/ISO 元数据的 sector 会破坏原文件系统，因此模板模式应选择预留区域或目标文件已经占用的 extent。新建最小 ISO 会拒绝覆盖固定的 ISO9660 volume descriptor sectors 16、17。

ISO 中保存的是 2048-byte 用户数据，而不是 619008-bit raw channel stream。标准 DVD 刻录机会重新生成 ID、EDC、ECC、EFMPlus 和 NRZI；因此实际图案还取决于驱动器是否遵循同一编码选择，以及传入求解器的初始 state、DSV 和 pit/land 是否与真实刻录边界一致。

### 连续铺满盘片或指定 sector 数

`--fill-sectors` 大于16时，程序会从 `--lba` 开始逐个求解连续 ECC Block，并把每一块的最终 state、DSV、pit/land 传递给下一块。sector 数必须是16的倍数。针对容量为2297888 sectors的盘片，整盘命令为：

```powershell
dotnet run --project src/DvdImageSolver --configuration Release -- solve `
  --image picture.png `
  --total-sectors 2297888 `
  --iso-output full-disc.iso `
  --fill-sectors 2297888 `
  --lba 0 `
  --psn-offset 0x30000 `
  --inner-radius-mm 24 `
  --outer-radius-mm 58 `
  --channel-bit-nm 133.3 `
  --constraint-step 16 `
  --algorithm state-control `
  --bridge-bytes 2 `
  --beam-width 8 `
  --retry-beam-width 32 `
  --retry-frame-fraction 0.25 `
  --batch-frames 8 `
  --batch-candidates 4 `
  --search-lanes 1 `
  --iterations-per-block 3 `
  --initial-state 1 `
  --initial-dsv 0 `
  --initial-level land
```

只做前1600个 sectors 时改成：

```powershell
--fill-sectors 1600
```

此时输出文件包含从 LBA 0 到1599的1600个 sector；`--total-sectors` 仍表示整张盘的容量，用来计算轨道几何。若 `--lba` 不为0，输出还会保留此前的全零 sector，因此镜像长度为 `(lba + fill-sectors) × 2048`。

范围模式会覆盖包括 ISO9660 descriptor 在内的所有指定 sector，因此输出是可直接刻录的原始2048-byte/sector镜像，而不是可挂载的 ISO9660 文件系统。`--constraint-step`表示自由payload搜索的图像采样间隔；state-control会对落在同一payload码字内的二值样本做多数投票。最终可见度评分不受该稀疏步长限制，会按每个16-bit channel码字中心重新采样图片，并包含派生、固定和SYNC码字。二值图要拉大盘面明暗差，建议`--constraint-step 16`，即每个payload码字都有一个目标；`32`可作为更快但密度减半的折中，`256`只约束约1/16的payload码字，因此整体majority error通常会接近随机水平。整盘共有143618个ECC Blocks，正式运行前应先用160到1600 sectors测量速度和效果。

dispersion模式始终在每个payload码字中心采样图片，忽略`--constraint-step`的稀疏搜索含义；因此命令中可以省略该参数。

## 文件与图案混合模式

给出`--data-dir`即启用混合模式。程序把该目录及其子目录递归写成标准 ISO9660 + Joliet 文件系统。`--file-position inner`（默认）将文件放在内圈，图案位于其外侧；`--file-position outer` 将文件内容放到盘尾，图案位于前部元数据之后、文件之前。图案起止边界按 16-sector ECC Block 对齐，始终保留全盘几何，不把剩余绘图区拉伸到整盘。最终 ISO 在 Windows 等系统中可以像普通数据光盘一样打开，Joliet 目录保留中文文件名。

```powershell
dotnet run --project src/DvdImageSolver --configuration Release -- solve `
  --image picture.png `
  --data-dir ".\disc-files" `
  --file-position outer `
  --iso-output hybrid-disc.iso `
  --total-sectors 2297888 `
  --volume-label MY_DISC `
  --inner-radius-mm 24 `
  --outer-radius-mm 58
```

混合模式默认使用`--algorithm dispersion --fast-output true`，适合直接生成整个外圈。也可显式加`--fast-output false`：此时程序会先编码并回放内圈的所有数据块，求出进入外圈时的 EFMPlus state、DSV 和 pit/land，再用精确边界绘图。精确回放会随内圈数据量增加耗时。

GUI 设计页的“随盘文件”区域可选择文件夹，并切换“文件位置：内圈／外圈”；导出页与之共享设置。该选项随工程保存，也用于流式刻录。流式路径固定使用快速 dispersion：先扫描目录并固定布局，按盘面顺序输出元数据、文件区和图案区，不创建完整临时 ISO；外圈模式先写图案，再写文件。布局完成后若源文件大小发生变化，流式写入会拒绝继续。预览用黄色标出文件系统占用区，颜色不会进入写盘数据。

此模式不接受`--lba`或`--fill-sectors`，两者由文件布局和盘片容量自动计算。`--drawing-start-lba N` 可在默认绘图起点之后额外留空，但必须处于可绘图区内，并与`--psn-offset`一起对齐到16 sectors。`--file-position outer` 必须与 `--data-dir` 一起使用。

当前 ISO9660 写入器支持递归目录和 Joliet Unicode 名称；单个文件不能超过4 GiB，且为避免链接环和意外引用目录外数据，`--data-dir`中不接受重解析点/符号链接。

## 半径标定预览

标定程序借鉴CD版本的正向投影方式：沿全局channel位置顺序采样，同一个位置先在“生成时假定的螺旋线”上读取目标图，再投到“猜测的实际螺旋线”。多个轨道样本落到同一输出像素时取平均，既保留轨距差累计出的真实相位，又避免逐输出像素反求最近轨道造成的气泡莫尔纹。

```powershell
dotnet run --project src/DvdImageSolver --configuration Release -- calibrate `
  --image picture.png `
  --output calibration-preview.png `
  --total-sectors 2297888 `
  --generated-inner-radius-mm 24 `
  --generated-outer-radius-mm 58 `
  --actual-inner-radius-mm 24.2 `
  --actual-outer-radius-mm 57.8 `
  --channel-bit-nm 133.3 `
  --spiral-direction ccw `
  --image-threshold 128 `
  --alpha-threshold 1 `
  --preview-size 2048 `
  --samples-per-sector 16
```

`--samples-per-sector`控制正向投影密度，范围1–4096，默认16；DVD每sector约5.16 mm轨道长度，16相当于约0.32 mm一次采样。提高到32或64会减少空像素并改善细节，耗时近似线性增加。预览不提供任意旋转偏移参数；生成与实际螺旋共用0°起点，整体旋转不影响半径标定。预览之外还会输出 `calibration-preview.png.json`，其中`mappingMode`为`forward-channel-splat-average`，并记录采样密度、两组螺距、总圈数和轨道长度。它不包含求解器剩余误差和刻录机实现差异。

标定默认预览整盘。若实际只生成了部分镜像，可给出与求解命令相同的 `--lba` 和 `--fill-sectors`，未写入的盘面区域会保持透明。

## 生成 ISO 的读回模拟

`calibrate` 的输入是源图片，描述"按生成几何写入、按实测几何读回"的理想效果；它使用与生成一致的二阶分界128，`--image-threshold`保留为兼容参数但不改变该预览。`simulate` 则读取已经生成的 ISO 镜像本身：把每个采样到的 payload 字节经 DVD 扰码还原后分类为`0xA5/0x92`，分别显示为0/255，再用实测半径的螺旋线把码字中心投到盘面。为查看旧多阶镜像，读回仍将`0x66`显示为0、`0xB9`显示为85；这些值不用于新图片生成。这是与 cdimage 校准对话框同构的闭环——生成几何已经固化在 ISO 里，模拟只需要一套实测几何参数。

```powershell
dotnet run --project src/DvdImageSolver --configuration Release -- simulate `
  --iso drawing.iso `
  --output readback-preview.png `
  --inner-radius-mm 24.2 `
  --outer-radius-mm 57.8 `
  --preview-size 2048 `
  --samples-per-sector 16
```

`--total-sectors`、`--lba` 与`--fill-sectors`缺省时读取求解命令写出的`drawing.iso.json`（绘制起始LBA与绘制扇区数），没有sidecar时回退为整个ISO文件、从LBA 0开始。`--samples-per-sector`控制每个扇区采样的payload字节个数（1–2048，默认16）。扰码后不属于二阶码表或兼容旧值66/B9的字节跳过不计。文件系统前缀可能偶然命中码表，应使用sidecar中的绘图区间排除它；ISO中本身不存储PI/PO纠错码。输出旁同样写出`readback-preview.png.json`，其中`mappingMode`为`payload-scramble-classify-splat`，并记录分类命中数、螺距与总圈数。

采样位置取码字中心：生成端 `FastDispersionImageWriter` 按 `PayloadDirectChannelOffset + 8`（16 位码字的一半）把图片目标落在码字中心，读回端使用同一位置，两个方向的投影因此不会相差 8 个通道位（约 1 µm 轨道长度）。除此之外读回与生成共用同一套螺旋线、方向与扰码掩码，所以"生成时的参数"与"预览时的参数"一致时，读回图逐采样复现生成图。

预览工作区在切换 ISO 时读取 sidecar 的 `imageMapping.totalSectors`、`innerRadiusMm`、`outerRadiusMm` 并载入标定几何：外部导入的镜像不会因为沿用上一次的盘片尺寸而被压成细环，用户之后的微调仍以这组参数为起点。

## 预览工作区的两种模式

预览页的输入开关把上面两条路径都暴露出来，二者共用防抖、取消与导出动作：

- **成品读回**（读回模拟，`simulate`）输入是已生成的 ISO，参数只有一套实测几何，看到的是镜像里真实写下的字节。
- **设计投影**（标定预览，默认，`calibrate`）输入是源图片，参数是生成/实测两套几何。它不读取 ISO，因此改参数后刷新快得多；代价是画面为源图在实测几何下的投影，不代表镜像里实际存在的码字——求解器误差与文件系统前缀都不会出现在其中。

标定预览直接使用设计页合成图作为输入，与生成镜像的源相同，因此两侧的图案一致。

## 仅执行前向编码

```powershell
dotnet run --project src/DvdImageSolver --configuration Release -- encode `
  --input payload.bin `
  --output channel.bin `
  --lba 0 `
  --psn-offset 0x30000 `
  --initial-state 1 `
  --initial-dsv 0 `
  --initial-level land
```

首个 PSN 必须是16的倍数；否则程序会拒绝编码，因为输入没有从 ECC Block 边界开始。

## 调制表再生成

`Encoding/EfmPlusTables.Generated.cs` 由 ECMA-267 Annex G 机械提取生成。若需要从规范 PDF 重新生成：

```powershell
python tools/generate_efm_tables.py ECMA-267.pdf src/DvdImageSolver/Encoding/EfmPlusTables.Generated.cs
```
