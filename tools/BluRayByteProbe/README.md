# 蓝光字节与位周期密度分析

分析单层 BD-R 的数字编码链，筛选用于实盘明暗映射测试的输入模式。结果不定义黑白极性，也不生成整盘镜像或执行刻录。详细结论见 [分析报告](../../docs/BLURAY_BYTE_DENSITY.md)。

需要 .NET 10 SDK；独立标准表分析只需 Python 标准库，画图另需 matplotlib / numpy。在仓库根目录运行：

```powershell
dotnet run --project tools/BluRayByteProbe --configuration Release -- artifacts/bluray-byte-density 4
dotnet run --project tools/BluRayByteProbe --configuration Release -- --fine artifacts/bluray-byte-density

python tools/BluRayByteProbe/reference/analyze.py --output-dir artifacts/bluray-byte-density/reference
python tools/BluRayByteProbe/reference/pattern_search.py --output-dir artifacts/bluray-byte-density/reference
python tools/BluRayByteProbe/reference/robust_pattern_search.py --output-dir artifacts/bluray-byte-density/reference

dotnet run --project tools/BluRayByteProbe --configuration Release -- --verify-reference artifacts/bluray-byte-density/reference/independent-finite-vectors.json
dotnet run --project tools/BluRayByteProbe --configuration Release -- --verify-patterns artifacts/bluray-byte-density/reference/independent-pattern-vectors.json
python tools/BluRayByteProbe/plot_results.py artifacts/bluray-byte-density
```

默认命令扫描全部 256 种重复 Q 字节，包括纯 17PP、完整簇的四种 DC 策略、候选的四个 PSN、未抵消扰码的 host 对照、随机邻居及混合模式。第二个参数为并行数（1–16）。`--fine` 扫描五种周期的全部相位、四种布局，并复核跨地址结果；该模式并行数固定至多 4。

Q 指**扰码后的目标用户数据**。实际 host payload 写入 `Q XOR PSN mask`。所有输出都在指定目录，允许覆盖之前同名分析文件。

主要输出：

| 文件 | 内容 |
|---|---|
| `all-bytes.csv` / `.json` / `.md` | 全部 256 字节；密度、DC 敏感性及 run 分布 |
| `all-bytes-density.png` | 16 × 16 密度图 |
| `fine-patterns.json` / `.csv` | 176 个位串/布局/相位组合及地址复核 |
| `fine-pattern-comparison.png` | 字节基线与位级控制比较 |
| `recommended-patterns.csv` | 三种推荐位串的完整构造参数及密度 |
| `context.json` / `periodic-context.json` | 排除 ECC 的合成帧、邻居及 DC 边界试验 |
| `reference/` | 独立标准表、精确循环和搜索结果 |
| `method.json` / `validation.json` / `fine-validation.json` | 统计口径及验证记录 |

完整簇统计分母固定为 `496 × 1932 = 958272` channel bits，包括同步、EDC、LDC/BIS、DC 和 17PP；不包括 run-in/out/Guard3，但保留 run-in 的初始电平和 DSV。自动 DC 使用项目现有 greedy **诊断**算法，其未来位暂置零；不能保证等于刻录机的 DC 决策。全 0、全 1、随机 DC 仅用于敏感性比较，不能当作实际刻录配置。

`FinePatternAnalysis` 对每个可控 payload bit 计算其物理 ECC 位置，再填入周期位值；校验实际编码后的 65,536 个 Q 字节与目标逐字节一致。EDC、BIS、parity 和 DC 均由编码链生成，没有把它们假设成可直接写入的用户位。
