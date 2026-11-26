# CD-TEXT 与 CD Extra 实机测试清单

开发环境没有 Windows 和光驱。下表区分了已经模拟验证的部分和必须在实机上确认的部分。

## 已模拟验证（无需光驱）

| 项目 | 方式 |
|---|---|
| CD-TEXT 数据包编码 | 与 libburn 文档中 cdrecord 生成的 22 个数据包逐字节一致；CRC 与 6 位 R-W 拆分与 libburn cookbook 示例一致 |
| 日文曲名 | MS-JIS（语言码 0x69）编码后解码不变；简体中文中 JIS 没有的字会列出并写成“？” |
| 超长文字 | 超过一个块（253 个文本包）时在写入前拒绝 |
| SAO cue sheet | 导入区带 CD-TEXT 标志，各音轨 INDEX 00/01，导出区；音轨起点与导出区地址与原 TAO 计划完全相同 |
| CD Extra 刻录流程 | 脚本化 SAO 光驱：导入区、150/152 块静音预间隙、音轨、末尾 2 块静音、完成会话，然后按原 TAO 代码写第 2 区段并封盘，再读回 CD-TEXT |
| 读回与曲序 | 读回的数据包解码后与设置比较；调换曲序、数据包损坏都会报告 |
| 不支持时的回退 | 光驱不报告 SAO 或拒绝 cue sheet：弹窗确认，盘片尚未写入；确认后走原 TAO / IMAPI 路径，拒绝则取消 |
| 纯图片音频 CD | 单轨 SAO 并封盘；不支持时回到原 IMAPI DAO 路径 |
| 保存 | .discproj（schema 4）、package.cdextra.json、CUE 的 TITLE/PERFORMER 与 .cdt 往返 |

## 需要在实机上测试

准备：Windows 10/11 x64，支持 SAO 的刻录机（如 ASUS DRW-24D5MT），空白 CD-R 若干，一台能显示 CD-TEXT 的 CD 播放机或电脑软件（foobar2000、EAC、ImgBurn 的“读取 CD-TEXT”）。

1. **能力检测**：在设计页打开 CD-TEXT 并填写文字，开始刻录。日志应出现“CD-TEXT 写入能力：SAO=True…”。记下 MaxCue 等数值。
2. **CD Extra + CD-TEXT（含日文）**：2–4 首音乐，曲名中至少一首日文，专辑名、艺术家各一。刻录完成后检查：
   - 四个里程碑：写入完成、已封盘、读回校验通过、**CD-TEXT 读回一致**；
   - 日志中“第 2 区段起点 LBA”与设计页预测值一致（SAO 第 1 区段后应与 TAO 相同）；
   - 在 CD 机上播放：曲目数、曲序、专辑/曲名/艺术家显示正确，第 2 区段图案不会被当成音轨播放；
   - 电脑上读取 CD-TEXT（如 EAC），文字与曲序一致。
3. **纯图片音频 CD + CD-TEXT**：不加音乐，只填图片音轨的曲名/艺术家。刻录后应封盘，CD 机显示文字。
4. **不支持 SAO 的光驱（如有）**：应弹出“光驱无法写入 CD-TEXT”。选“不带 CD-TEXT 继续”，结果应与不使用 CD-TEXT 时完全相同；选取消，盘片应仍为空白。
5. **CD-TEXT 关闭**：同一工程关闭 CD-TEXT 刻录一张，确认行为、日志与加入 CD-TEXT 之前相同。
6. **刻录包**：在 CD Extra 窗口勾选“写入 CD-TEXT”准备刻录包，重新打开刻录包后刻录，结果同第 2 项。
7. **导出**：导出 WAV+CUE，确认 CUE 中有 TITLE/PERFORMER 与 CDTEXTFILE，用 ImgBurn 等软件按 CUE 刻录时能读到文字。

请记录：光驱型号与固件、盘片品牌、请求速度、日志文件（CD Extra 窗口“历史日志”或任务日志），以及失败时的完整错误信息。
