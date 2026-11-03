using Avalonia.Controls;
using DiscImageStudio.Localization;

namespace DiscImageStudio.Desktop;

public sealed partial class MainWindow
{
    private Control Burn()
    {
        var path = new TextBox { Text = _lastArtifact ?? Session.Document!.Outputs.OutputPath };
        var drives = new ComboBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        var summary = Text("连接 USB 刻录机后点击刷新。", 12);
        IReadOnlyList<MacRecorder> recorders = [];
        async Task Refresh()
        {
            recorders = await MacDiscRecorder.ListAsync();
            drives.ItemsSource = recorders.Select(r => r.Name).ToArray(); drives.SelectedIndex = recorders.Count > 0 ? 0 : -1;
            summary.Text = recorders.Count == 0 ? L.T("未检测到刻录机。请检查 USB 连接及设备是否支持刻录。") : L.T("已检测到刻录设备。");
        }
        double speed = 0;
        var fields = Column(Text("Mac 使用系统 DiscRecording 写入已生成的文件；请先在导出页生成并检查成品。"),
            Text("镜像 / 音轨路径"), path, Button("选择文件", async () =>
            {
                string? selected = await PickOpen("选择刻录文件", "*.wav", "*.iso"); if (selected is not null) path.Text = selected;
            }), Text("刻录机"), drives, Button("刷新设备", Refresh), summary);
        Number("刻录速度（0 = 自动）", speed, 0, 52, v => speed = v, fields, 1);
        fields.Children.Add(Button("检查盘片", async () =>
        {
            if (drives.SelectedIndex < 0) throw new InvalidOperationException("请选择刻录机。");
            var media = await MacDiscRecorder.InspectAsync(recorders[drives.SelectedIndex].Id);
            summary.Text = $"{media.Description} · {(media.Blank ? "空白" : "非空白")} · {media.FreeSectors:N0} sectors";
        }));
        fields.Children.Add(Text("仅使用可报废的空白盘测试。写入期间不能退出程序或断开光驱。此入口不擦除、不追加、不超刻。", 12));
        fields.Children.Add(Button("刻录文件…", async () =>
        {
            if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("此刻录入口只支持 macOS。");
            if (drives.SelectedIndex < 0) throw new InvalidOperationException("请选择刻录机。");
            string artifact = Path.GetFullPath(path.Text ?? "");
            if (!File.Exists(artifact)) throw new FileNotFoundException("请先生成或选择文件。", artifact);
            string extension = Path.GetExtension(artifact).ToLowerInvariant(); bool audio = extension == ".wav";
            if (!audio && extension != ".iso") throw new ArgumentException("请使用导出的 WAV 或 ISO；RAW 请先重新生成 WAV。");
            if (Session.Document!.IsBluRay) throw new InvalidOperationException("蓝光 ISO 请使用支持 BD-R 的外部刻录软件。此入口尚未验证蓝光硬件。");
            var recorder = recorders[drives.SelectedIndex];
            long sectors; string burnPath = artifact;
            if (audio)
            {
                burnPath = Session.CachePath("audio-burn"); Directory.CreateDirectory(burnPath);
                sectors = MacDiscRecorder.WriteAudioAiff(artifact, Path.Combine(burnPath, "track.aiff"));
            }
            else
            {
                long bytes = new FileInfo(artifact).Length;
                if (bytes == 0 || bytes % 2048 != 0) throw new InvalidDataException("ISO 长度必须是 2048 字节的整数倍。");
                sectors = bytes / 2048;
            }
            var media = await MacDiscRecorder.InspectAsync(recorder.Id);
            MacDiscRecorder.ValidateMedia(media, audio, sectors);
            if (!await Confirm("确认刻录", $"{recorder.Name}\n{artifact}\n{sectors:N0} sectors\n\n开始写入这张空白盘？")) return;
            // Recheck immediately before starting; users can swap media while the confirmation is open.
            MacDiscRecorder.ValidateMedia(await MacDiscRecorder.InspectAsync(recorder.Id), audio, sectors);
            await RunWork("正在刻录，请勿断开设备…", false, async _ =>
            {
                List<string> args = ["-drive", recorder.Id, "burn", "-noerase", "-noappendable", "-eject"];
                if (audio) args.AddRange(["-audio", "-pregap", "0"]);
                if (speed > 0) args.AddRange(["-speed", speed.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
                args.Add(burnPath);
                await MacDiscRecorder.Run("/usr/bin/drutil", args, Log);
            });
        }));
        fields.Children.Add(Button("在 Finder 中显示成品", async () =>
        {
            string artifact = Path.GetFullPath(path.Text ?? "");
            if (!File.Exists(artifact)) throw new FileNotFoundException("文件不存在。", artifact);
            await MacDiscRecorder.Run("/usr/bin/open", ["-R", artifact]);
        }));
        return Card("刻录（实验性）", fields);
    }
}
