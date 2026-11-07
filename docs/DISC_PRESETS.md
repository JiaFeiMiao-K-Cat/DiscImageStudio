# 可编辑盘片预设

盘片预设分为两层：

- 内置层随应用发布和更新，保存官方预设。
- 用户层位于以下文件，只保存用户新增、覆盖和禁用设置：

```text
%LOCALAPPDATA%\DiscImageStudio\disc-presets.json
```

程序第一次启动时会在用户层放入“自定义 CD 参数”“自定义 DVD 参数”和“自定义蓝光参数”三个可编辑预设。内置预设不会复制到用户文件，因此应用更新后的参数修正和新增预设会自动生效。

新增用户预设的入口在工程设置对话框：「将此套参数保存为预设」先在独立窗口确认名称，再把当前参数追加进用户层；新建工程的模板列表会列出目录中的全部非手动预设，按「最近」「CD」「DVD」「蓝光」分类显示。

在已有工程的设置中保存预设成功后，会立即将这组参数应用到当前工程，并选中新保存的条目；高级预览的实测半径与三项轨距系数同步更新。随后取消设置窗口仍保留已保存并应用的参数，保存之后继续输入但未确认的修改则不会应用。仅修改工程名称不会重置手工填写的实测参数。新建工程对话框保存预设不会修改已打开的工程；同名保存仍新增独立条目。

工程设置对话框同时列出当前盘型的预设：选择其他预设会覆盖对话框里的几何参数，因此切换前先弹出确认，取消则保留原参数且不改变选中项。新建工程与工程设置中的“自定”预设都可右键删除，删除会立即写回用户层并刷新列表；直接编辑 JSON（新增、改名、禁用）后仍需重启程序生效。

## 合并规则

CD、DVD、BD 预设都支持可选的 `pitchLinear`、`pitchQuadratic`、`pitchCubic`，缺省为 `0`。三项系数与内外半径共同定义盘片几何，应成套保存和分享；旧预设无需修改。轨距多项式 `1 + c1*x + c2*x² + c3*x³` 在整个归一化半径区间 `[0, 1]` 内必须保持正值。

- 用户预设使用新 `id` 时，会追加到内置预设之后。
- 用户预设使用与内置预设相同的 `id` 时，会完整覆盖该内置预设。
- `disabledCdPresetIds`、`disabledDvdPresetIds` 和 `disabledBluRayPresetIds` 可以隐藏不需要的内置或用户预设。
- 内置预设更新时，未被同 ID 用户项覆盖的参数会自动更新。

## 添加 DVD 预设

在 `dvdPresets` 数组中复制初始化的用户对象，修改 `id`、名称和参数。例如：

```json
{
  "id": "verbatim-dvd-r-azo-43533",
  "displayName": "威宝 DVD-R AZO (43533)（实测）",
  "description": "威宝 DVD-R AZO 43533 已验证标定参数：23.9914606277–57.9648936579 mm，共 2,297,888 个扇区；采用三次轨距分布。",
  "totalSectors": 2297888,
  "innerRadiusMm": 23.991460627731737,
  "outerRadiusMm": 57.964893657925366,
  "channelBitLengthNm": 133.3,
  "pitchLinear": -3.9256854319227684E-05,
  "pitchQuadratic": 6.964795915429256E-05,
  "pitchCubic": -4.670918950137523E-05
}
```

DVD 的 `totalSectors` 必须大于 0 且是 16 的倍数，以满足 ECC Block 和流式刻录布局要求。`channelBitLengthNm` 见下节，属于保留字段。

此威宝 DVD 预设来自用户确认已验证的 `calibration-f2f851f7.calibration.json`，保留记录中的完整参数精度。

可选字段 `contributor` 记录该套实测参数的贡献者。填写后，预设卡片左上角显示幼苗图标，鼠标悬停立即显示“贡献者：…”；省略该字段或留空则不显示图标，仅含空白的值会被校验拒绝。例如内置的 `philips-dvd-plus-r`：

```json
{
  "id": "philips-dvd-plus-r",
  "displayName": "PHILIPS DVD+R（实测）",
  "description": "PHILIPS DVD+R 实测标定参数：24.157724–57.881039 mm，共 2,295,104 个扇区；采用三次轨距分布。",
  "totalSectors": 2295104,
  "innerRadiusMm": 24.157723798864616,
  "outerRadiusMm": 57.881039159257099,
  "channelBitLengthNm": 133.3,
  "pitchLinear": -0.00015487792452410765,
  "pitchQuadratic": 0.0001962455822194293,
  "pitchCubic": -8.6683602888291885E-05,
  "contributor": "DBPolp"
}
```

`pitchLinear`、`pitchQuadratic`、`pitchCubic` 是三次轨距的三个相对形状系数，缺省为 0，兼容旧预设。模型为 `p(x)=p0*(1+pitchLinear*x+pitchQuadratic*x²+pitchCubic*x³)`，`x=(r-innerRadiusMm)/(outerRadiusMm-innerRadiusMm)`。完整多项式有四项；`p0` 根据总轨长自动计算，界面显示四项的实际 μm 数值。整个 `0≤x≤1` 区间内轨距必须严格为正，不能仅检查内外边界。修改任一系数会切换到未保存的自定义状态，重新选择内置等轨距预设会恢复零系数。

内置 DVD 实测预设还包括 `verbatim-dvd-plus-r-azo-43498`（威宝 DVD+R AZO (43498)，贡献者 `Disappear9`）、`ritek-dvd-plus-r-e-series`（铼德 DVD+R E时代系列，贡献者 `EternalEve`）和 `ritek-dvd-r-e-series`（铼德 DVD-R E时代系列，贡献者 `bczhc`）。

`verbatim-dvd-plus-r-azo-43498` 的参数为：内半径 `23.954070288547207` mm、外半径 `57.981456053451275` mm、`2,295,104` 扇区，三次项系数 `-1.1055153059816393E-05`、`-1.7233535436312236E-06`、`5.55123860136712E-06`。`ritek-dvd-r-e-series` 的参数为：内半径 `24.063109184084034` mm、外半径 `58.23373375457525` mm、`2,298,496` 扇区，三次项系数 `-3.2229430281730407E-06`、`1.0507210681810583E-05`、`-8.562064227346657E-06`。两者均保留来源数值的完整精度。

## 添加 CD 预设

在 `cdPresets` 数组中复制一个现有对象，修改 `id`、名称、扇区数和内外半径。例如：

```json
{
  "id": "cd-74",
  "displayName": "标准 74min CD",
  "description": "ECMA-130／ISO/IEC 10149 标准速度 1200 mm/s；120 mm 盘、约 74 分钟，333,000 扇区（24.5–56.8 mm）。",
  "sectors": 333000,
  "innerRadiusMm": 24.5,
  "outerRadiusMm": 56.8,
  "linearVelocityMmPerSecond": 1200
}
```

## 添加蓝光预设

在 `bluRayPresets` 数组中复制一个现有对象，修改 `id`、名称和参数。例如：

```json
{
  "id": "bd-r-ritek-10x",
  "displayName": "铼德 BD-R 10x（实测）",
  "description": "实测参数：内半径 23.988221 mm、外半径 58.023235 mm、12,219,392 扇区。",
  "totalSectors": 12219392,
  "innerRadiusMm": 23.98822105616562,
  "outerRadiusMm": 58.023234731100764,
  "invertMapping": false
}
```

BD 的 `totalSectors` 必须大于 0、按 32 对齐且不超过单层容量 12,219,392；外半径不超过 60 mm。`invertMapping` 是预置的 3T/6T 极性，应用到工程后仍可在设计页影调模块中修改。

内置 `bd-r-verbatim-64099`（威宝 BD-R (64099)）采用用户文件 `disc-presets.json` 中已保存的「Verbatim 64099」预设，保留完整精度：内半径 `23.987042157339335` mm、外半径 `57.987960341783236` mm、`12,219,392` 扇区、`invertMapping: false`。GUI 与命令行使用相同参数。

内置 `bd-r-verbatim-64293`（威宝 BD-R (64293)）由贡献者 `bczhc` 提供，参数与 64099 相同，保留同样精度。

内置 `bd-r-ritek-12x`（铼德 BD-R 12x）由贡献者 `DeepAQ` 提供：内半径 `23.98795` mm、外半径 `58.02236` mm、`12,219,392` 扇区、`invertMapping: false`。GUI 与命令行使用相同参数。

## 固定参数与保留字段

扫描速度、起始角、图片外半径与 DVD channel bit 长度是方案固定的物理量，界面不提供对应表单，程序生成时一律采用 1200 mm/s（ECMA-130）、0°、57.5 mm 与 133.3 nm。

它们在 JSON 中仍作为字段保留，原因是在旧版的配置文件中这几个参数是必要的，因此需要保证旧版本写出的用户文件可以直接使用。这两个值不参与生成计算。

## i18n 兼容字段

格式已经预留可选的 `fallbackLanguage`、`displayNameResourceKey` 和 `descriptionResourceKey`。当前版本仍直接显示 `displayName` 与 `description`；以后接入多语言资源时会优先解析资源键，并以现有文本作为回退，因此不需要再次升级 JSON 结构。用户预设无需填写资源键。

## 编辑规则

- 当前顶层为 `"schemaVersion": 4`，包含 `cdPresets`、`dvdPresets`、`bluRayPresets`、三个禁用列表与可选 `fallbackLanguage`。程序也接受 `"schemaVersion": 2` 与 `3`；缺少 BD 目录时由内置 BD 模板补齐，保存时升级为 4。
- 每组预设中的 `id` 必须唯一；`__manual__` 是界面“未保存的自定义参数”状态的保留 ID，不能写进 JSON。
- 只能填写模板中列出的生成参数；其他字段会被拒绝，避免把图片布局或校准参数混入盘片预设。
- 半径、`linearVelocityMmPerSecond` 和 `channelBitLengthNm` 必须是大于 0 的有限数值，生成外半径必须大于生成内半径。
- 文件允许 `//` 或 `/* ... */` 注释以及数组末尾的逗号。
- 如果格式或参数无效，程序会指出问题并继续保留上一次成功加载的预设，不会自动覆盖用户文件。
