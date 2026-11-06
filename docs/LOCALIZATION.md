# 界面国际化

点击右上角常驻的「齿轮 + 设置」按钮，在「通用 → 语言」选择「跟随系统」「简体中文」「繁體中文」「English」「日本語」「Türkçe」「Español」或「Čeština」，保存后立即生效。主页和所有工作区都显示设置按钮，也可按 Ctrl+, 打开；取消、Escape 或关闭设置窗口不会保存语言。切换语言不会自动重启应用，也不会关闭工程或中断任务。

`settings.json` 中的 `language` 使用 `system`、`zh-CN`、`zh-TW`、`en`、`ja`、`tr`、`es` 或 `cs`。语言与主题默认均为跟随系统；缺失、空值和不支持的语言也回落到跟随系统，已有配置中有效的语言和主题选择保持不变。跟随系统使用启动时的系统 UI 语言：`zh-TW`、`zh-HK`、`zh-MO`、`zh-Hant` 及显式 `zh-Hant-*` 使用繁体中文；`zh-CN`、`zh-SG`、`zh-Hans` 等其他中文系统使用简体中文；`ja`、`ja-JP` 等日文系统使用日文；`tr`、`tr-TR` 等土耳其语系统使用土耳其语；`es`、`es-ES`、`es-MX`、`es-419` 等西班牙文系统使用西班牙文；`cs`、`cs-CZ` 等捷克文系统使用捷克文；英文、法文等其他系统统一使用英文。运行期间修改系统语言须重启应用才生效。主题使用 Windows 的应用颜色模式选择深色或浅色。语言独立于 `.discproj`，工程名、用户预设名、图层名、文件路径与数值解析规则不参与翻译。

## 添加和维护文案

翻译位于 `src/DiscImageStudio.App/Localization/Catalogs/*.json`，构建时嵌入程序集，无额外运行时依赖。每个键必须提供非空的简体中文 `zh-CN`。繁体中文 `zh-TW`、英文 `en`、日文 `ja`、土耳其文 `tr`、西班牙文 `es` 和捷克文 `cs` 可按进度补充，允许省略或留空。查找顺序为当前语言 → 英文 `en` → 简体中文 `zh-CN` → 原始资源键；缺失、空字符串和纯空白译文都会跳到下一项，不要求一次补齐全部语言。例如，下面的条目目前只提供中英文，西班牙文等尚未补译的界面会显示英文：

```json
{
  "示例 {0}": { "zh-CN": "示例 {0}", "en": "Example {0}" }
}
```

C# 用 `L.T("源文案")`，插值用 `L.F($"示例 {value}")`，资源键为其 .NET 复合格式 `示例 {0}`；需要显式资源键时可用 `L.Format(key, arguments)`。翻译可调整参数顺序，但必须保留索引和格式说明符。XAML 使用 `xmlns:loc="clr-namespace:DiscImageStudio.Localization"` 与 `{loc:Text ResourceKey}`，已加载的控件和模板会随语言切换原地刷新。

共享短语只需定义一次，所有资源文件中的键必须唯一。键不存在时显示键本身。启动时先初始化语言，再创建窗口及 ViewModel。只设置 UI culture，保留 numeric culture；不要翻译协议标识、命令、路径、用户数据或把翻译后的文本用于逻辑判断。繁体中文使用独立资源，采用「設定」「專案」「檔案」「燒錄」等用语，不在运行时转换用户数据。

范围为桌面应用界面及应用层提示；独立命令行帮助、底层编码器诊断和第三方/操作系统错误保留各自语言，许可证原文保持不变。

标定拟合器返回的原因是界面提示的一部分，显示前应逐条通过 `L.T` 翻译，再拼接到侧栏、状态栏或日志。固定原因及相应英文收录在 `CalibrationFitDiagnostics.json`；新增原因时同时补充资源，不能只翻译外层的“计算未通过”。拟合器内部仍保留原始原因，避免界面语言影响参数判定。

含动态数值的校验异常通过 Core 的 `DiagnosticMessage` 保存 `FormattableString` 模板及参数，界面使用 `L.Error(exception)` 先翻译模板再格式化。异常的原类型、原始消息和 `ParamName` 保留供诊断使用；不要把已经拼好数值或系统参数后缀的消息当作资源键。只有明确标记的嵌套模板参与翻译，文件路径、图层名、预设 ID 等普通字符串参数保持原样，未标记的外部异常沿用原文回退。

`CalibrationPopupMessages.json`、`ProjectValidationMessages.json` 中新增的弹窗词条同样支持全部已注册的界面语言：在同一资源键下添加 `es`、`ja` 等字段即可，无需修改异常或界面代码。动态参数和嵌套说明均使用上述回退顺序；组合消息的外层模板及嵌套说明可分别补译，未补齐的部分继续回退。

## 提交补译 PR

1. 在对应的 JSON 文件中找到待翻译的键，结合 `zh-CN` 原文和实际页面确认含义。可以只补充一个语言、一个页面或少量条目，保留已有译文与资源键。
2. 添加或填写对应语言字段。尚未确认的译文可以继续省略或留空，依次回退到英文、简体中文；无需把其他语言复制到该字段来凑齐条目。
3. 保留 `{0}`、`{1:0.00}` 等 .NET 占位符、换行和有意使用的标记。按下面的命令检查资源，再用目标语言查看涉及的页面，留意按钮宽度、换行及术语一致性。
4. 在 PR 描述中列出补译的语言、页面和验证结果。后续 PR 可以继续补充其余条目。

## 验证

`CalibrationFitLocalizationTests` 复现边界片段过短的拟合失败，检查原因在英文侧栏、底栏和日志中的显示，以及语言切换后保留拟合结果和描点。设置 `DISC_IMAGE_STUDIO_UI_SNAPSHOTS` 为截图输出目录时，还会生成 `calibration-boundary-failure-en.png`。

`LocalizationTests` 检查每个资源键都有非空 `zh-CN`，并校验已有非空译文的 .NET 格式参数。其他语言缺失或为空是允许的，测试同时验证当前语言优先、英文／简体中文／原始键回退、系统语言选择、设置保存和取消行为，以及保存失败不会应用语言。非空捷克文译文还需通过排版与术语检查；需要英文对照的检查只在该键有非空英文时执行。`PopupLocalizationTests` 验证新增异常模板能采用其他语言译文，且动态参数和嵌套说明遵循相同的回退顺序。`RelocalizationTests` 检查运行时切换语言后工程几何、进行中的读取和预览结果保持不变，已打开页面的文案正确刷新。

先构建并运行包含上述检查的回归程序：

```powershell
dotnet build DiscImageStudio.slnx --configuration Release
dotnet run --project tests/DiscImageStudio.ArchitectureTests --configuration Release
```

再按本次补译的语言和页面渲染截图。例如检查西班牙文设置页和捷克文标定页：

```powershell
dotnet run --project src/DiscImageStudio.App -c Release -- ui-snapshot --settings-dialog --language es --output artifacts/settings-es.png
dotnet run --project src/DiscImageStudio.App -c Release -- ui-snapshot --tab 5 --language cs --output artifacts/calibration-cs.png
```

截图使用一次性设置文件，不改变用户配置。也可在应用中切换语言，确认已有输入和测量进度保留。增加一种尚未支持的语言时，需要同时扩展语言规范化和解析规则、设置下拉选项及测试；该语言的资源翻译仍可分批补充。
