# 界面国际化

[Español](LOCALIZATION.es.md)

点击右上角常驻的「齿轮 + 设置」按钮，在「通用 → 语言」选择「跟随系统」「简体中文」「繁體中文」「English」、「日本語」或「Español」，保存并重启后生效。主页和所有工作区都显示设置按钮，也可按 Ctrl+, 打开；「…」菜单仅保留关于入口。取消、Escape 或关闭设置窗口不会保存语言。切换语言不会自动重启应用，也不会关闭工程或中断任务。

`settings.json` 中的 `language` 使用 `system`、`zh-CN`、`zh-TW`、`en`、`ja` 或 `es`。语言与主题默认均为跟随系统；缺失、空值和不支持的语言也回落到跟随系统，已有配置中有效的语言和主题选择保持不变。跟随系统使用启动时的系统 UI 语言：`zh-TW`、`zh-HK`、`zh-MO`、`zh-Hant` 及显式 `zh-Hant-*` 使用繁体中文；`zh-CN`、`zh-SG`、`zh-Hans` 等其他中文系统使用简体中文；`ja`、`ja-JP` 等日文系统使用日文；`es`、`es-ES`、`es-MX`、`es-419` 等西班牙文系统使用西班牙文；英文、法文等其他系统统一使用英文。运行期间修改系统语言须重启应用才生效。主题使用 Windows 的应用颜色模式选择深色或浅色。语言独立于 `.discproj`，工程名、用户预设名、图层名、文件路径与数值解析规则不参与翻译。

## 添加和维护文案

翻译位于 `src/DiscImageStudio.App/Localization/Catalogs/*.json`，构建时嵌入程序集，无额外运行时依赖。每个键包含 `zh-CN`、`zh-TW`、`en`、`ja` 和 `es` 五种文本；繁体中文使用独立资源，采用「設定」「專案」「檔案」「燒錄」等用语，不在运行时转换用户数据：

```json
{
  "示例 {0}": { "zh-CN": "示例 {0}", "zh-TW": "範例 {0}", "en": "Example {0}", "ja": "例 {0}", "es": "Ejemplo {0}" }
}
```

C# 用 `L.T("源文案")`，插值用 `L.F($"示例 {value}")`，资源键为其 .NET 复合格式 `示例 {0}`；需要显式资源键时可用 `L.Format(key, arguments)`。翻译可调整参数顺序，但必须保留索引和格式说明符。XAML 使用 `xmlns:loc="clr-namespace:DiscImageStudio.Localization"` 与 `{loc:Text ResourceKey}`，在创建窗口和模板时解析。

共享短语只需定义一次，所有资源文件中的键必须唯一。找不到当前语言时回落到中文；键不存在时显示键本身。启动时先初始化语言，再创建窗口及 ViewModel。只设置 UI culture，保留 numeric culture；不要翻译协议标识、命令、路径、用户数据或把翻译后的文本用于逻辑判断。

范围为桌面应用界面及应用层提示；独立命令行帮助、底层编码器诊断和第三方/操作系统错误保留各自语言，许可证原文保持不变。

## 验证

`LocalizationTests` 检查资源语言完整性、格式参数、系统语言回落、设置保存和取消行为，以及保存失败不会应用语言。

```powershell
dotnet run --project src/DiscImageStudio.App -c Release -- ui-snapshot --settings-dialog --language en --output artifacts/settings-en.png
dotnet run --project src/DiscImageStudio.App -c Release -- ui-snapshot --settings-dialog --language zh-CN --output artifacts/settings-zh.png
dotnet run --project src/DiscImageStudio.App -c Release -- ui-snapshot --settings-dialog --language zh-TW --output artifacts/settings-tw.png
dotnet run --project src/DiscImageStudio.App -c Release -- ui-snapshot --settings-dialog --language ja --output artifacts/settings-ja.png
dotnet run --project src/DiscImageStudio.App -c Release -- ui-snapshot --settings-dialog --language es --output artifacts/settings-es.png
```

截图使用一次性设置文件，不改变用户配置。增加语言时同时扩展语言规范化和解析规则、设置下拉选项、各资源及测试。
