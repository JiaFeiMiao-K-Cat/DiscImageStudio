# CD / DVD 生成物理模型复核

后续盘种兼容性检查见文末 [2026-11-06 复核](#2026-11-06-盘种兼容性复核)；其余历史结论保留原日期与代码基准。

复核日期：2026-09-21。对象为 `DiscImageStudio-GitHub` 当前工作区（包含尚未提交的标定实现），基准提交 `6c55a18`。本次只做标准核对、源码审计和独立数值验证，未修改生成器、预设或刻录行为。

## 结论

基础螺旋公式和 DVD 扇区长度未发现计算错误。但是，当前模型不能凭标准名义参数精确预测真实盘面的角度：默认 CD 参数存在几何一致性问题；固定线密度的单线标定会把密度误差吸收到半径；CD 交织的有限流边界和参考时序也没有进入几何模型。还缺少刻录盘种与生成模型的匹配检查。

不能仅凭这次审计确定某张实盘弯曲的唯一原因，也不应把 DVD 位长直接改成另一名义值作为修复。

## 标准依据与适用范围

- 官方 [ECMA-359 第一版，2004](https://ecma-international.org/wp-content/uploads/ECMA-359_1st_edition_december_2004.pdf)：单层 DVD-R，主要检查 §§10.7–10.9、18–23、24.2。核对了 PDF 第 28、29、52、56 页原页。
- 官方 [ECMA-267 第三版，2001](https://ecma-international.org/wp-content/uploads/ECMA-267_3rd_edition_april_2001.pdf)：DVD-ROM，主要检查 §§10.6、18–21。不能用它取代全部可刻录介质的规则。
- 官方 [ECMA-130 第二版，1996](https://ecma-international.org/wp-content/uploads/ECMA-130_2nd_edition_june_1996.pdf)：CD-ROM 几何与公共 CIRC 编码参考，§§8.6、11.3–11.4、19.4、附录 C。音轨并不等于 CD-ROM 数据轨。
- 官方 [ECMA-394 第一版，2010](https://ecma-international.org/wp-content/uploads/ECMA-394_1st_edition_december_2010.pdf)：CD-R，多速录制、预刻槽与 Red Book 兼容关系，§§1.2、2.4–2.5。此文件通过引用 Red Book 规定部分几何要求，并没有直接重列完整的 CD 几何表。

ECMA-359 的关键约束如下，均为标准要求，不能视为某一张盘的实测值：

| 项目 | 约束 | 条款 |
|---|---|---|
| 数据起点半径 | 23.9–24.0 mm | 10.7.1.2，原文为直径 48.0 mm，公差 −0.2/+0.0 mm |
| 12 cm 盘数据终点半径 | 不超过 58.0 mm | 10.7.1.2 |
| 平均螺距 | 0.74 ± 0.01 μm | 10.8 |
| 局部螺距偏差 | 相对 0.74 μm 最大 ±0.03 μm | 10.8 |
| 平均通道位长 | 133.3 ± 1.4 nm | 10.9 |
| 数据区首 PSN | 0x030000，物理扇区连续无间隙 | 24.2 |

ECMA-267 的数据起点直径下限与 DVD-R 不同：其 §10.6.1 是 48.0 mm，公差 −0.4/+0.0 mm。不能把两份标准的起点容差混用。

## 已确认正确的部分

DVD 的每扇区通道位数是 `13 × 2 × (32 + 1456) = 38688`，每 16 扇区 ECC 块为 619008 位。`DvdEccBlockEncoder.cs:8` 使用的数值正确；扇区偏移、同步帧和 PO 行交织未发现通用长度漏算。

当前轨长为：

```text
CD:  L = (N / 75) × v                         [mm]
DVD: L = N × 38688 × channelBitLengthNm × 10^-6 [mm]
```

`CdDiscParameters.cs:59–75` 使用等螺距的面积近似，`ArchimedeanSpiral.cs:33–70,115–120` 使用精确弧长。其近似关系为 `pitch = π(ro²−ri²)/L`、`θ = 2L(r−ri)/(ro²−ri²)`。CD 近似与精确螺旋的整盘角差约 0.000135°，不能解释肉眼明显的弯曲。DVD 的精确弧长反解、纳米到毫米换算和默认图像坐标方向也未发现错误。

DVD-R 的 linking 用于特定增量追加情形；§24.2 明确扇区连续。没有依据给每个 ECC 块统一追加一段所谓隐藏空隙。

## 发现一：默认 CD 80 分钟预设的参数组合不合理

`src/DiscImageStudio.Core/Presets/built-in-disc-presets.json:6–12` 同时使用 359849 扇区、24.5–56.8 mm、1200 mm/s。按实际生成公式反算：

| 预设 | 隐含螺距（μm） |
|---|---:|
| CD 80 分钟默认 | 1.432856 |
| CD 74 分钟默认 | 1.548384 |
| 铼德水蓝 CD 实测半径预设 | 1.492598 |
| Verbatim CD 实测半径预设 | 1.500257 |
| DVD 12 cm 默认 | 0.739837 |
| Verbatim DVD-R 实测半径预设 | 0.738304 |

ECMA-130 §11.3 的螺距范围是 1.5–1.7 μm；§11.4 的基准扫描速度范围是 1200–1400 mm/s。CD80 默认组合明显不能作为符合该几何范围的通用精确预设。其 24.5 mm 起点也不能直接等同于 §8.6 的用户数据区起点（24.8–25.0 mm）。

这是对软件参数组合的检查，不是据此判定用户的 CD-R 盘片不合格。标为实测的预设仍固定采用 1200 mm/s，并不代表线密度也经过测量。不能仅为落入标准范围而擅改用户实测半径。

## 发现二：标准容差远大于绘图所需的累计角度精度

`ImageTargetMapper.cs:58–77`、`FastDispersionImageWriter.cs:345–365` 用容量、两个半径和固定位长反求恒定螺距。DVD 默认参数产生约 45956.06 圈。标准允许局部螺距变化，且 58 mm 是上限，因此这些输入只能构成一个初始模型。

数值敏感性（均为改变一个输入、其余保持不变的模型对照）：

- DVD 位长改变 10 ppm，整盘累计角度改变约 165.44°。
- DVD 位长由 133.33 改为 133.3 nm，累计角度改变约 10.34037 圈。
- DVD 内半径或外半径增加 0.001 mm，累计角度改变约 −201.76°。

上述值是模型相位差，不是已测得的实盘误差。133.33 nm 在标准容差内；133.3 nm 也不是每张盘的真实精确位长。盲目替换名义值可能显著改变生成结果。

## 发现三：单条径向线不能独立确认半径和线密度

`CalibrationTarget.cs:468–473` 固定线速度／位长，`CalibrationCrossFitter.cs:73` 只拟合两个半径和整体旋转；当前流程不使用同心圆的源半径对应关系。

独立反例：生成模型为 DVD `(ri=24, ro=58, bit=133.33)`，假定真实模型为 `(24,58,133.3)`。固定拟合位长仍为 133.33，只将候选半径改为约 `(24.00540257,58.01305620)`，再调整整体旋转，即可在 25–57 mm 范围几乎完全复现同一条弯曲线（2049 点精确弧长计算的最大相位残差约 2.04×10^-10 rad）。候选在默认 ±0.02 mm 搜索范围内。这是解析构造候选后的前向验证，没有声称当前优化器必然返回这组候选。

因此低残差可能只是把位长误差解释成半径误差。现功能可以获得有用的补偿参数，但结果应理解为“固定线密度假设下的等效参数”。若要确认真实物理半径与密度，需要加入已知源半径的圆环、已知数据位置或独立边界测量；单纯多开放一个拟合参数仍不能解决信息不足。

## 发现四：CD 交织补偿有有限流边界效应，物理时序原点待验证

`CddaInterleaver.cs:8–16` 的延迟表与 ECMA-130 图 C.4 的数据路径一致，不是错误的重复交织。独立标签仿真把现有补偿后 PCM 再送入标准正向 CIRC 的 24 个数据路径：10 扇区测试中 20856 个稳态数据位置全部一致，但组合结果是目标图案整体延后 111 帧。

需要严格区分两件事：

- **确定的有限流现象：**初始化产生 1332 个零占位字节；末端分散于最后 108 帧的 1332 个目标字节没有发出。`CddaInterleaver.cs:46–55` 的 Flush 只处理扇区尾部，整扇区输入时不排空环形缓冲。这会影响图案边界。
- **有条件的物理偏移：**111 帧是图 C.4 参考时序下的组合延迟，约 15.102 ms，1200 mm/s 下约 18.122 mm 弧长。如果未由刻录链路的时序原点抵消，角差随半径改变，无法只用起始角消除。尚未测量其相对 ATIP、INDEX 01 和实盘数据起点的关系，因此不能把它直接宣称为已经证实的实盘偏移。

`CdTrackGenerator.cs:229–246` 按未加入上述时序的 globalByte 采样，几何标定同样不包括这一级编码。现有兼容说明也已声明预览不模拟完整 CIRC/EFM。需补充端到端物理时序验证，不能仅依据同源生成与预览一致就证明盘面位置正确。

## 发现五：刻录介质类型没有与模型匹配

`WindowsImapiBurner.cs:151–166,377–390` 只检查 IMAPI 支持、空白状态及容量，没有查询实际物理盘种。生成器却采用单层 ID（`DvdDataFrameBuilder.cs:41`）和单层单向螺旋。

这缺少对双层 DVD、DVD-RAM 等不同物理格式的主动拦截。IMAPI 能写入某种介质，不代表当前绘图模型适用；实际是否能进入写入仍取决于设备和状态。双层的层切换、第二层方向及位长未建模。

可用微软的 [CurrentPhysicalMediaType](https://learn.microsoft.com/en-us/windows/win32/api/imapi2/nf-imapi2-idiscformat2data-get_currentphysicalmediatype) 与 [物理介质枚举](https://learn.microsoft.com/en-us/windows/win32/api/imapi2/ne-imapi2-imapi_media_physical_type) 做匹配检查。

## 建议的修正顺序

1. 为预设增加隐含螺距检查，区分标准初值、实测值和补偿所得值；优先处理 CD80 默认组合。明确起止半径对应哪个数据位置，并把绘图裁切范围与轨道锚点分开。
2. 核实 CD 交织、音轨起点及刻录链路的联合时序；再修正采样偏移和头尾策略，保持最终扇区数不变。
3. 给标定增加独立的径向或数据位置约束。若实测残差仍有系统性局部弯曲，再评估变化螺距／相位修正模型。
4. 写入前检查实际介质类型及容量与所选模型一致。

本次没有进行实盘刻录或光学亮度测量。验证包括标准原文与原页、公式复算、精确螺旋数值对照、参数不可辨识反例及 CIRC 数据路径标签仿真；没有将这些检查扩展声称为完整光学仿真或实盘精度认证。

审计复现材料位于工作区根目录 `tmp/standards-audit/`：`circ-audit.py`、`circ-audit-result.json`、`geometry-audit.py`、`geometry-audit-result.json`，以及两份用户 PDF 的提取文本与关键原页。

## 2026-11-06 盘种兼容性复核

基准为 `b86404f` 加当前工作区的 DVD SY6/EDC 修正。本轮检查生成模型、介质识别、容量查询及刻录准入；未改变生产代码或刻录策略，未操作光驱。DVD 编码本身的逐表与多项式检查见 [DVD_ENGINE.md](DVD_ENGINE.md#编码标准复核2026-11-06)。

### 优先修复：DVD-RAM 与双层 DVD 的准入不匹配

`WindowsImapiBurner.IsWritableMedia(DvdData, ...)` 接受 DVD-RAM 及 DVD±R DL、DVD+RW DL。实际刻录先后两次调用这一检查，随后 `BurnData` 仅检查空白状态与容量；`ReadDvdSectorCount` 对双层盘的拒绝只用于读取标定容量，不约束写入。DVD-RAM 连该容量查询也能通过。

使用当前 Release 程序集运行纯函数探针，确认 RAM 和上述全部 DL 类型通过写入类型检查；DL 被容量查询拒绝，RAM 则通过。探针提供模拟介质类型、profile 和空白标志，未查询设备，因此不把这一结果描述为已执行实盘写入。[微软介质枚举](https://learn.microsoft.com/en-us/windows/win32/api/imapi2/ne-imapi2-imapi_media_physical_type)与 [profile 定义](https://learn.microsoft.com/en-us/windows/win32/api/imapi2/ne-imapi2-imapi_profile_type)已核对。

这些盘片超出当前单层绘图模型：

- [ECMA-330 §15.1.1、§17.4–17.5](https://ecma-international.org/wp-content/uploads/ECMA-330_3rd_edition_june_2005.pdf#page=43)：DVD-RAM 重写区每物理扇区为 43,152 通道位，另有分区布局、滑移和替换；当前按 38,688 位及连续螺旋定位。
- [ECMA-364 §14.2、§16.2、§31.1](https://ecma-international.org/wp-content/uploads/ECMA-364_3rd_edition_december_2007.pdf#page=45)、[ECMA-382 §10.8–10.9、§24.2](https://ecma-international.org/wp-content/uploads/ECMA-382_2nd_edition_june_2010.pdf#page=33)及 [ECMA-374 §14.2、§16.2、§29.1](https://ecma-international.org/wp-content/uploads/ECMA-374_2nd_edition_june_2008.pdf#page=43)：对应 DL 介质的第二层反向记录、PSN 位反转及约 146.6/146.7 nm 位长均未建模。当前 fast 路径始终使用线性 `LBA + 0x30000` 与一个螺旋，不能通过增加总扇区数适配。

建议把支持的单层介质规则统一用于读取容量、列举速度及实际写入，并在预缓冲后继续复查。普通数据能够交给设备写入，并不意味着当前模型能正确预测图案位置。README 的“支持任何 DVD”也应同步收窄。

### 单层 DVD：区分 payload 与精确回放

| 介质 | 普通数据区 Sector Information | 现状 |
| --- | --- | --- |
| DVD-R | `0x00` | 该头字段与当前模型一致 |
| DVD+R | `0x00` | 该头字段与当前模型一致；不能套用 +RW 的值 |
| DVD-RW | `0x20` | 精确模型缺少介质配置 |
| DVD+RW | `0x62` | 精确模型缺少介质配置 |

依据分别为 [ECMA-359 §16.1](https://ecma-international.org/wp-content/uploads/ECMA-359_1st_edition_december_2004.pdf#page=45)、[ECMA-349 §13.1.1](https://ecma-international.org/wp-content/uploads/ECMA-349_4th_edition_june_2008.pdf#page=35)、[ECMA-338 §17.1](https://ecma-international.org/wp-content/uploads/ECMA-338_1st_edition_december_2002.pdf#page=44)、[ECMA-337 §13.1.1](https://ecma-international.org/wp-content/uploads/ECMA-337_4th_edition_june_2008.pdf#page=34)。这些盘种的普通数据区起点、16 组扰码初值及 PSN 索引一致。头字段会影响 ID/IED/EDC/ECC/DSV，但不据此判定单层 ±R/RW 的 fast payload 扰码错误，也不需要因此全部禁用。精确模型应按盘种配置头字段和连接规则，再验证设备调制及实盘效果。

### CD-R/RW：编码检查通过，仍有边界限制

- 当前 Mode 1 的 EDC 固定向量、全部 P/Q syndrome、位序及扰码隔离检查通过；CD-R/RW 的介质/profile 配对正确。两类介质共用 EFM/CIRC，并按 [ECMA-394](https://ecma-international.org/wp-content/uploads/ECMA-394_1st_edition_december_2010.pdf#page=67) / [ECMA-395](https://ecma-international.org/wp-content/uploads/ECMA-395_1st_edition_december_2010.pdf#page=74) §5.5.3 的可刻录盘规则生成导入区 Header，不能直接套用压制 CD-ROM 的 A0 偏移。
- 默认 80min 预设仍隐含 1.432856 μm 螺距，不在其引用的 ECMA-130 §11.3 的 1.5–1.7 μm 范围。应明确名义初值与实测补偿参数的区别，不能将其描述为所有 CD-R/RW 都适用的标准精确几何。
- `CddaInterleaver` 的有限流行为仍在：以 10 个非零扇区复现，相同输出长度内有 1332 个零占位，末端 1332 个逻辑字节未输出。`Flush` 仅补齐扇区，未排空延迟线。这是图案边缘及物理时序建模问题，不能据此认定整张 CD 不可读。
- `BurnCdDao` 隐式允许超过设备报告的 [LastPossibleStartOfLeadout](https://learn.microsoft.com/en-us/windows/win32/api/imapi2/nf-imapi2-idiscformat2rawcd-get_lastpossiblestartofleadout) 最多 3 扇区。建议默认严格容量，超刻由明确选项控制；现有设备上的可用结果不能推广到所有刻录机/盘片。
- 已写入的 CD-RW 会被拒绝，当前不提供擦除流程；这是既有支持范围。

### 蓝光：已有准入范围较完整

生成器只接受单层、32 扇区对齐且不超过 12,219,392 扇区。实际写入要求 profile `0x41`、全新空白未格式化盘、从 LBA 0 开始的首轨，以及完整的单层无备用区容量；BD-RE、RRM、ROM、多层/XL、已格式化及备用区布局都会被拒绝，缩小镜像不能绕过物理容量检查。已有模拟回归覆盖这些条件，本轮未发现新的确定准入错误。

[ISO/IEC 30190:2021](https://www.iso.org/standard/80770.html) §7、§13 同时包含单层 HTL/LTH，编码和扰码规则共用。当前没有读取 DI 的 recorded-mark polarity 标志，因此 HTL/LTH 仍是介质识别与光学实测缺口，不能直接判定为编码错误。保留现有人工 `InvertMapping` 标定；是否需要反转视觉映射应由实际图案确认。

建议顺序：先统一 DVD-RAM/DL 的准入限制和支持说明，再增加 DVD-RW/+RW 的精确模型配置；随后整理 CD 默认几何、首尾时序与超刻策略，并补充 HTL/LTH 的介质身份日志和实测记录。本轮复现材料位于当前仓库的 `artifacts/media-compat-audit/`，其中准入探针和 CD 检查均不访问设备。

## 2026-11-06 #90：CD/BD 图案读盘风险及调整位

### 已有实盘证据的范围

[#90](https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/issues/90) 和 [讨论 #150](https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/discussions/150) 的同盘对照支持 DVD 的重复 `92` 码字状态与读盘失败有关；`9B` 桥接针对 EFMPlus 状态，不能移植成 CD/BD 通用处理。讨论中的偏置持续时间指标及读盘失败比例也不能直接作为 CD/BD 的安全阈值。

[#90 的一条 BD-R 反馈](https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/issues/90#issuecomment-6660834015)称，把同一份 DVD 数据写入 BD-R 后没有受影响。这是有限的有利实盘信息，但同一 host payload 在 BD 扰码、交织和 17PP 后并不是本项目的 `AA`/`44` 图案，不能据此认定蓝光绘图已经通过回读验证。本轮没有操作实体光驱，也没有找到当前 CD 四阶调色板或 BD `AA`/`44` 的同等系统回读对照。

### CD：merging bits 可以消偏，subcode 不是同一种控制位

[ECMA-130 附录 B/E](https://ecma-international.org/wp-content/uploads/ECMA-130_2nd_edition_june_1996.pdf#page=41)明确指出，规律输入可能使合并位无法充分降低 DSV，扰码用于减小这种风险。每个 EFM 字之间的三个 merging bits 要同时满足 3T–11T、避免伪同步及低频控制要求；RLL 合法不等于没有低频偏置。CD Mode 1 绘图主动抵消扰码，音轨本身也不能借用 Mode 1 的扰码保护，因此这类风险不能仅凭编码格式排除。

P-W subcode 是每个 588T 帧的一个控制符号，随后也经过 EFM；它不是码字间的 merging bits。P/Q 承载轨道、地址和 CRC 等信息，不能任意改作消偏位；ECMA-130 §22.1 还规定该 CD-ROM 数据轨格式的 R-W 为零。音轨的扩展子码用途须另按相应格式核实。当前 `DynamicCdRawImageSession` 由 IMAPI 建立音轨子码，没有写 R-W 内容；选择 [Raw P-W / Cooked R-W](https://learn.microsoft.com/en-us/windows/win32/api/imapi2/ne-imapi2-imapi_format2_raw_cd_data_sector_type) 只决定子通道传输格式，不授予主机选择 EFM merging bits 的能力。固件仍生成实际合并位。

对 Table D.1 的四个生产调色板码字独立转录，并穷举相同码字相邻时的四种 merging 候选，得到以下局部结果。计数只含 EFM 字与 merging bits，**没有 CIRC、校验、帧同步和子码，不是整轨仿真**：

| 字节 | 相同码字连续时允许的 merging bits | 12 个字的局部最大 \|DSV\| | 4,096 个无中断字的局部最大 \|DSV\| |
| --- | --- | --- | --- |
| `0D` | `001`、`010`、`100` | 7 | 7 |
| `10` | `000`、`100` | 8 | 8 |
| `63` | 仅 `000` | 11 | 4,095 |
| `AA` | 仅 `000` | 4 | 4 |

这里 `0D`/`10` 可以通过合并位选择维持平衡；`AA` 每字的奇数次翻转会交替极性；`63` 的唯一合并形式每 17T 留下大小为 1 的净偏置，单靠该连续段内的 merging bits 无法消除。实际 CD 每 12 个数据符号就插入 4 个 CIRC 校验符号，另有帧同步和子码，所以 4,095 这个局部反例不能解释为现有 CD 的 DSV 或实盘故障。它说明四阶灰度中的 `63` 应单独验证，不能由黑白图案的结果代替。现有生产工程只有 CIRC 数据位置映射/补偿和字节预览，没有完整正向 CIRC/子码/EFM 合并位模型；外部 [disc-probe 的 CD 模型](https://github.com/Ashley-QwQ/disc-probe/blob/main/docs/CD_PHYSICAL_MODEL.md)也明确使用校验/子码占位而非完整物理编码。下文的后续独立完整链路实验补齐了这些步骤，纯 `63` 样本没有复现该局部反例的持续累积。

### BD：DC 位有实际调节自由度，当前 greedy 不能代表固件

[ISO/IEC 30190:2021](https://www.iso.org/standard/80770.html) §13.15–13.18 规定每帧 28 个 DC 位，17PP 保持奇偶性，使这些位能改变后续信号极性；§13.17.3 要求利用前一 DC 块末位最小化下一块结束处的累计 DSV。数据游程为 2T–8T，帧同步允许 9T。它与 DVD `92` 陷入特定 EFMPlus 状态的机制不同。

当前 `BluRayStreamingGenerator` 只输出抵消扰码后的 host payload，真正的 ECC、17PP、DC 位选择由刻录机执行。`BluRayGreedyDcControlEncoder` 仅用于 `BluRayRecordingUnitEncoder` 的诊断路径及相关测试/工具；其未来未决定控制位暂置零，已经暴露 `SuboptimalDcControlDecisions`，不能当作设备实际行为或完整标准符合性证明。

本轮独立探针直接编译 Encoding 源文件，无设备、无整套 solution 构建。在 PSN `0x100000`、`0xCA73E0` 各编码一个完整 32 扇区 RUB，统计其中 496 帧、958,272 channel bits 的完整物理簇；保留 run-in 结束处电平/DSV，包含 EDC、LDC/BIS、DC 和帧同步。两地址结果：

| 扰码后目标 Q | 当前 greedy 的累计最大 \|DSV\| | 512T 窗口绝对均值 > 1/8 的比例 |
| --- | --- | --- |
| 连续 `AA` | 3,863 / 3,869 | 0.713% / 0.573% |
| 连续 `44` | 36 / 35 | 0 / 0 |
| `AA`/`44` 逐字节交替 | 53 / 47 | 0 / 0 |
| `AA`/`44` 每 64 个 payload 字节交替 | 25 / 25 | 0 / 0 |
| 固定种子随机 Q | 29 / 31 | 0 / 0 |

全部十个簇的完整相邻跳变间隔均在 2T–9T 内。`AA` 的高峰出现在第 424 帧附近，当前 greedy 分别有 422/416 个可改善的局部决策。以上比例只是描述性数字；512T 在不同格式对应不同物理长度，不能套用 DVD 实验的读盘临界值。

为区分负载固有风险与控制策略，另做了两个仅改 DC 位、保持同一 payload/ECC 的完整簇对照：

- 保留已有控制序列，仅逐帧重选第一个 DC 位以减小帧尾 DSV，峰值降到 253/256，但短窗口偏置仍存在。
- 每帧对全部 28 个 DC 位最多做 8 轮坐标搜索，以完整帧的最大绝对 DSV、平方和及帧尾 DSV 为目标；两地址峰值分别降到 **31/27**，512T 高偏置窗口比例均为零，2T–9T 间隔检查仍通过。

第二个对照证明，3,863/3,869 的漂移不是 `AA` 负载不可避免的结果，DC 位选择足以显著改变它。但该实验目标不等同于标准要求的逐块最小化，也没有证实对应真实固件，所以没有替换生产模型或修改蓝光 payload。仅反复做局部逐块重估的另一对照未消除漂移，不能把它当作已收敛解。下一步应独立验证有完整前视的标准 DC 决策、跨帧边界及同步/终止上下文，再决定如何改进诊断模型；当前数值既不能证明 BD 实盘存在同样故障，也不能证明它没有风险。

### 后续回归与实盘对照

CD 应覆盖音轨与 Mode 1、二阶与四阶灰度，尤其是 `63` 纯色、两端纯色、长条纹和混合图案；BD 应覆盖 `AA`、`44`、混合图案与普通随机数据，并区分 HTL/LTH 及实际支持的单层 BD-R。先在同一盘片批次、同一刻录机/速度下对照内中外圈，再用另一读盘设备复核。数据轨需要完整回读和内容比对，音轨需要考虑读取偏移的原始音频比对；同时记录失败 LBA、耗时/重试及同角度图像。不能以能挂载、单块可读或只比较 DSV 峰值代替这些检查。

本地探针及 JSON 位于 `artifacts/issue90-cd-bd/`（按仓库规则忽略）：`cd_merge_probe.py`、`BdRiskProbe.csproj`、`Program.cs`、`FrameReselectProbe.cs`、`RefinementProbe.cs`。复现命令为 `python artifacts/issue90-cd-bd/cd_merge_probe.py` 以及 `dotnet run --project artifacts/issue90-cd-bd/BdRiskProbe.csproj -c Release`；BD 对照再分别加 `-- --frame-reselect`、`-- --frame-optimize`、`-- --refine`。这些命令只做数字模型计算，不操作光驱。

## 2026-11-06 CD 后续：完整编码链与数据轨子码

后续检查将上一轮局部码字实验扩展为独立的完整数字链路。结论分开记录：**测试范围内未复现持续 DSV 累积，但确认了 CD 数据盘 DAO 转换保留音轨 ADR3 子码的问题**。以下为修复前审计记录，当时只新增诊断材料与文档，没有修改生产输出或操作光驱；后续实现见[数据轨 ADR3 修复](#2026-11-06-cd-数据轨-adr3-修复)。

### 实际输入与模型验证

从生产 Release 生成器提取 23 组样本：音轨分别开启/关闭绘图预补偿，Mode 1 数据轨，覆盖纯 `0D/10/63/AA`、黑白长条纹、`63/AA` 长条纹、四阶混合及随机基线。每组为完整 512 扇区短轨生成后的 LBA 64–191，共 128 扇区；它不是完整 80 分钟盘片的几何或实盘信号。

同时通过离线 IMAPI image creator 捕获实际 P-W 子码和主通道字节，未创建刻录格式/设备接口。确认音轨 PCM 字节未被改写，Raw P-W 与 Cooked R-W 在未提供 R-W 内容时相同；Mode 1 送入 CIRC 前的字节经过一次实际扇区 EDC/RSPC/扰码，再按 [ECMA-130 §§15–16](https://ecma-international.org/wp-content/uploads/ECMA-130_2nd_edition_june_1996.pdf#page=26) 交换相邻字节。`CddaInterleaver` 只是绘图预补偿，不能代替硬件 CIRC。纯 `63` 音轨 F1 仍为 100% `63`，Mode 1 为约 87.1%；不能假定这些输入已被普通数据扰码随机化。

独立 Python 探针包含：

- Annex C 的两帧分组延迟、C2(28,24)、28 路 `4×lane` 交织、C1(32,28)、偶数路一帧延迟和最终 P/Q 反相。Q 位在 C2 向量 12–15，直接按标准校验矩阵求解。完整校验依赖最长 109 帧，不能只按数据字节的 108 帧处理。
- 官方 PDF 256 项 EFM 表，两种文本提取逐项一致；每个 588T 帧包含同步、S0/S1 或实际子码、32 个 CIRC 符号及合并位。逐候选检查 3T–11T、伪同步、累计最小绝对 DSV，并在同分时优先有跳变。DSV 与极性跨帧连续，不按扇区清零。
- 23 组输入的 6,924,288 字节经过独立数据逆变换全部一致；6,003 个抽样 C1/C2 码字的四个独立 Horner syndrome 全零；有限域乘积、24 路脉冲和错误检出另行验证。
- 独立第二实现核对两个纯 `63` 输出的 14,636,496T、796,544 个 F2 字节、254 个 Q 包 CRC，以及 26,656 处合并位选择。没有 RLL 违规、错解码或非预期同步；其子码语义检查另外发现下述 ADR3 问题，不能把 CRC/位流通过写成所有格式检查通过。

统计丢弃开头 392 个冷启动帧，另保留末端一扇区余量，既不计入零初态，也不将人为补尾用作降低偏置。无公开权威完整 CIRC/EFM golden 实盘向量可用于逐位比较；模型与独立校验不能替代固件、写策略或光学回读。

### 偏置结果：合并位和完整帧结构有效

同一默认合并位同分顺序下，实际样本的结果如下。低频列是无去均值 Welch 得到的 **NRZI 数字序列**在 1× 通道速率下低于 20 kHz 的功率占比，不是实测 HF 合规值或读盘失败阈值。

| 场景 | 最大绝对 DSV | 低于 20 kHz 的功率占比 |
| --- | ---: | ---: |
| 音轨纯 `0D` / `AA` | 11 / 11 | 0.0476% / 0.0445% |
| 音轨纯 `10` | 15 | 0.0589% |
| 音轨纯 `63` | 23 | 0.5561% |
| 音轨四阶混合，开启预补偿 | 48 | 0.1542% |
| 音轨随机基线 | 41 | 0.1696% |
| Mode 1 纯 `63` | 39 | 0.3368% |
| Mode 1 随机基线 | 31 | 0.1688% |

对 6 个代表样本再检查 6 种四字节对齐、3 种 section 相位及两种合法跳变同分顺序，共 126 组；图案最大 DSV 为 53，未见持续累积。纯 `63` 音轨仅改变合并位同分选择，低频占比就从 0.5561% 降为 0.2019%，峰值仍为 23。这是 CD 调整位能改善结果的直接数字证据，但当前主机接口不控制刻录机选择哪些合并位。

另检查当前调色板组成的 386 个有限周期样本（字节对及相位、11 种游程长度、128 个随机重复模板等），并对 6 个最强候选及随机对照作 16 组长序列验证。图案峰值 19–26，同长度随机对照为 29–32；未见持续 DSV 漂移。一个模板在 216T 短窗中偏置较多，而且随同分策略改变；588T 及更长窗口没有绝对均值超过 1/8 的样本。该 1/8 仅作描述性统计，不能沿用 DVD 实验的实盘阈值。

因此，前文连续 4,096 个 `63` 码字的局部峰值 4,095 **不是本项目 CD 轨道的预测值**。当前证据不足以要求更换四阶调色板或添加通用保护字节；仍需对 `63`、四阶纹理、不同光驱/固件及 CD-R/RW 做实际回读。

### 确认的问题：Mode 1 DAO 保留了音轨 ADR3/ISRC 包

修复前，`src/DiscImageStudio.Burning/CdDataRawImageSession.cs` 中 `PatchDataSubchannel` 仅执行 `q[0] = (q[0] & 0x0F) | 0x40`，保留 ADR 低四位，再重算 CRC。原生音轨模板即使 `TrackInfo.ISRC == null` 也周期性输出 ADR3，所以转换结果包含 `0x43`（数据轨 CONTROL + 音轨 ISRC 格式）。显式设置 ISRC 为空或 null 被 IMAPI 拒绝，不能依赖清空属性消除这些包。

实际 512 扇区离线捕获：

| 区域 | ADR1 | ADR2 | ADR3 |
| --- | ---: | ---: | ---: |
| Lead-in | 20,251 | 2,249 | 0 |
| Pregap | 136 | 7 | 7 |
| Program | 461 | 25 | 26 |
| Lead-out | 6,075 | 675 | 0 |

全部捕获 Q 包的 CRC 正确，每连续 10 个 section 中 ADR1 最少为 9，出现频率本身没有违规；问题是数据轨不应保留 ISRC 模式。独立修复原型（仅在探针中）重建了 33 个 ADR3 包，CRC 全部通过，9 个区域边界时间样例与原生有效 ADR1 的字段一致。

[ECMA-130 §22.3.2](https://ecma-international.org/wp-content/uploads/ECMA-130_2nd_edition_june_1996.pdf#page=30)限定数据轨 Q 模式为 1/2，模式 3 用于音轨。[ECMA-394 §5.2.3.1、§5.6.3.2](https://ecma-international.org/wp-content/uploads/ECMA-394_1st_edition_december_2010.pdf#page=54)及 [ECMA-395 对应条款](https://ecma-international.org/wp-content/uploads/ECMA-395_1st_edition_december_2010.pdf#page=77)的 consumer RID/ISRC 扩展没有将这些包合法化：本次包的 selector 为 `00`（空 ISRC），不是 RID；数据应用 RID 另有主通道/PMA 位置。

这是已确认的输出格式问题，可能影响严格读盘设备的子码解释；尚未证明它导致数据读取失败或 #90 的 DVD 故障。它只涉及本应用的 **CD 数据盘 DAO 子码转换**；音轨 ADR3 有其正常用途，普通 ISO 文件本身也不携带这些子码。

建议修复局限在数据轨转换：根据当前 LBA 与轨长，把 ADR3 重建成完整 ADR1 时间包，再重算 CRC；分别处理预间隙、数据区与 lead-out，保留 lead-in 的 TOC 结构及合法 ADR1/ADR2。不能仅把 `0x43` 改成 `0x41`，因为原来的 ISRC 位布局不是 TNO/INDEX/时间布局。应补齐各区域字段、CRC、ADR1 出现频率、Raw/Cooked P-W 一致性与音轨不受影响的回归。

为排除不合法子码掩盖 DSV 风险，对 8 个 Mode 1 样本改用合法 ADR1 参考子码重跑完整链路：最大 DSV 仍为 25–39，纯 `63` 仍为 39，低频结果也接近原捕获。这是诊断对照，没有修改生产输出。

复现材料均位于 `artifacts/issue90-cd-full/`：`fixtures/manifest.json` 与 `fixtures/q-probe/report.json`、`circ_model.py`、`channel_model.py`、`run_probe.py`、`periodic_search.py`，以及各 baseline/sensitivity/legal-Q/periodic JSON 和 `independent_checks/`。本轮没有重跑整套构建，因为生产代码未变更；上述独立数字探针及离线 IMAPI 检查已完成。

English: the complete offline CIRC/subcode/EFM/merging-bit model did not reproduce sustained DSV drift in 23 production fixtures, 126 alignment/tie comparisons or the finite periodic-pattern search. A pure `63` audio fixture peaked at 23, rather than the previous uninterrupted-word counterexample's 4,095. Legal merging-bit tie choices changed its low-frequency NRZI energy, so firmware choices still matter. Separately, at the time of this audit the CD Mode 1 DAO adapter preserved audio ADR3/ISRC packets while changing CONTROL to data; CRCs and cadence passed, but the Q-mode semantics conflicted with the data-track format. The subsequent data-only repair is documented below. No production output or physical disc was changed during the audit, and no hardware-readability guarantee is inferred.

## 2026-11-06 CD 数据轨 ADR3 修复

上述审计后，已在 `CdDataRawImageStream` 实现数据盘专用转换。主通道编码和音轨的 `DynamicCdRawImageSession` 均未修改。只在预间隙、数据区或 lead-out 遇到 ADR3 时，清除原 ISRC 内容，重建完整 ADR1 包及反码 CRC：

| 区域 | TNO / INDEX | 相对帧数 | 绝对帧数 |
| --- | --- | ---: | ---: |
| 预间隙，`-150 ≤ LBA < 0` | `01 / 00` | `-LBA` | `LBA + 150` |
| 数据区，`0 ≤ LBA < sectors` | `01 / 01` | `LBA` | `LBA + 150` |
| Lead-out，`LBA ≥ sectors` | `AA / 01` | `LBA - sectors` | `LBA + 150` |

时间写成 BCD MSF，ZERO 字节为零，CONTROL/ADR 为 `0x41`。已有 ADR1/ADR2 的字段及 lead-in TOC 结构保持原样；P 仍只在数据区清零，区外保持原生值，R-W 逐位保留。

预间隙采用实测 IMAPI 模板的 150→1 相对倒计时，保持与相邻原生 ADR1 连续。ECMA-130 §22.3.3.3 对暂停末尾为零的文字与这一原生约定存在差异，§22.2 还规定了轨尾 P 标志；本次未更改原生 ADR1 计时或既有 P 策略。这是针对数据轨 ADR3 的修复，不表示整个 DAO 子码的全部标准边界已审定。

回归包括独立多项式除法、9 个原生计时/CRC 固定向量、各区边界、秒/分钟进位及 `99:59:74` 上限；非零 ISRC 输入确认所有时间字段都重建，合法 ADR1/ADR2 和 TOC 不被误改，P/R-W 保持既有行为。新增真实 IMAPI 测试对 Raw P-W 与 Cooked R-W 分别扫描完整的 512 扇区短轨 DAO 镜像（含全部 lead-in、预间隙及 lead-out），检查模式、时间、CRC 和每连续 10 个 section 的 ADR1 数量。音轨镜像与独立直接 COM 调用得到的原生镜像逐字节相同，非对称 PCM 字节和音轨 ADR3 均保留。测试不创建 recorder 或刻录接口。

旧实现（仅调整测试调用签名，保留原有行为）在首个 ADR3 转换向量失败，修复后通过。构建与完整回归结果见 [验证记录](VALIDATION.md)，本地日志在 `artifacts/cd-subcode-fix/`。未进行实体盘刻录或读回，未据此认定 #90 的 DVD 故障已经解决。

English: the data-only DAO adapter now rebuilds complete ADR1 packets when it encounters audio ADR3 in the pregap, program area or lead-out. Existing TOC/ADR1/ADR2 and P/R-W behavior remain unchanged. Reconstructed pregap timing follows native IMAPI's 150-to-1 countdown; the pre-existing timing/P differences noted above are outside this repair. Independent vectors and polynomial division cover boundaries through `99:59:74`. Full native images in both 96-byte subchannel layouts pass mode, timing, CRC and cadence checks, while audio remains byte-identical to an independent direct IMAPI reference, including its ADR3 metadata and PCM bytes. The regression fails with the previous behavior and passes with the repair. No optical recorder was accessed.
