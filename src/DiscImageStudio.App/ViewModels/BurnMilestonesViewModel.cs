using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DiscImageStudio.Burning;
using DiscImageStudio.Localization;

namespace DiscImageStudio.ViewModels;

public enum MilestoneState
{
    Pending,
    Active,
    Done,
    Failed,
    Skipped,
}

public sealed partial class BurnMilestone : ObservableObject
{
    internal BurnMilestone(string key) => Key = key;

    internal string Key { get; }

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    private MilestoneState _state;
}

/// <summary>
/// The three facts a user needs after a burn, kept apart so "100 %" can never be read as
/// "the disc is good":
/// <list type="number">
/// <item><b>写入完成</b> — the recorder accepted every sector (the write stage ended).</item>
/// <item><b>已封盘</b> — the session/disc was closed by the recorder.</item>
/// <item><b>读回校验通过</b> — sectors read back from the disc matched the source. If the
/// burn ends without it, the row says "校验未完成" (failed) or "本次未校验" (not run).</item>
/// </list>
/// The model only listens to the stages the burning backend already reports
/// (write, write-complete, flush, close-session, finalize, close-disc, verify, complete) and
/// to how the job ended; it never changes what is written.
/// </summary>
public sealed partial class BurnMilestonesViewModel : ObservableObject
{
    private readonly BurnMilestone _written = new("written");
    private readonly BurnMilestone _finalized = new("finalized");
    private readonly BurnMilestone _verified = new("verified");
    private readonly BurnMilestone _cdText = new("cdtext");
    private bool _testWrite;
    private bool _sawVerify;
    private bool _tracking;

    public BurnMilestonesViewModel()
    {
        Items = [_written, _finalized, _verified];
        Reset();
    }

    public ObservableCollection<BurnMilestone> Items { get; }

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>Starts tracking a new job. A resume job starts from a disc that may already
    /// be written and closed, so its first two rows begin as "checking".</summary>
    internal void Begin(bool testWrite = false, bool resume = false, bool cdText = false)
    {
        Reset();
        // The fourth row exists only for a burn that carries CD-TEXT.
        if (cdText && !Items.Contains(_cdText)) Items.Add(_cdText);
        else if (!cdText) Items.Remove(_cdText);
        if (cdText && testWrite)
        {
            _cdText.State = MilestoneState.Skipped;
            _cdText.Detail = L.T("模拟写入不会写入 CD-TEXT");
        }
        _testWrite = testWrite;
        _tracking = true;
        IsVisible = true;
        if (testWrite)
        {
            _finalized.State = MilestoneState.Skipped;
            _finalized.Detail = L.T("模拟写入不会封盘");
            _verified.State = MilestoneState.Skipped;
            _verified.Detail = L.T("模拟写入不会读回校验");
        }

        if (resume)
        {
            _written.Detail = L.T("正在核对原盘和已写前缀…");
        }

        Summary = L.T("刻录进行中");
    }

    internal void Observe(OpticalBurnProgress progress)
    {
        if (!_tracking) return;
        switch (progress.Stage)
        {
            case "write":
                Activate(_written, progress.TotalBytes > 0
                    ? L.F($"设备已接收 {progress.Fraction:P0}")
                    : progress.Message);
                break;
            case "write-complete" or "flush" or "close-track":
                Complete(_written, L.T("设备已接收全部数据"));
                break;
            case "close-session":
                // CD Extra: session 1 (music) closes between the two writes; the disc stays
                // open for the picture session, so this is not the final close.
                _finalized.Detail = L.T("第 1 区段（音乐）已关闭，等待写入图案后封盘");
                break;
            case "finalize" or "close-disc":
                Complete(_written, L.T("设备已接收全部数据"));
                Activate(_finalized, progress.Message);
                break;
            case "verify":
                Complete(_written, L.T("设备已接收全部数据"));
                if (!_testWrite) Complete(_finalized, L.T("光驱已确认封盘"));
                _sawVerify = true;
                Activate(_verified, progress.TotalBytes > 0
                    ? L.F($"已读回 {progress.Fraction:P0}")
                    : progress.Message);
                break;
            case "cdtext-verify":
                Activate(_cdText, progress.Message);
                break;
            case "complete":
                Complete(_written, L.T("设备已接收全部数据"));
                if (!_testWrite) Complete(_finalized, L.T("光驱已确认封盘"));
                if (_sawVerify && !_testWrite) Complete(_verified, progress.Message);
                break;
        }
    }

    /// <summary>The job returned normally.</summary>
    internal void Succeed(string? verifiedDetail = null)
    {
        if (!_tracking) return;
        _tracking = false;
        Complete(_written, L.T("设备已接收全部数据"));
        if (_testWrite)
        {
            Summary = L.T("模拟写入完成：光驱接受了全部写入命令，盘片没有被改变。");
            return;
        }

        Complete(_finalized, L.T("光驱已确认封盘"));
        if (_sawVerify || verifiedDetail is not null)
        {
            Complete(_verified, verifiedDetail ?? _verified.Detail);
            Summary = L.T("写入完成、已封盘、读回校验通过。");
        }
        else
        {
            _verified.State = MilestoneState.Skipped;
            _verified.Detail = L.T("本次刻录未进行读回校验");
            Summary = L.T("写入完成并已封盘；本次未读回校验。");
        }
    }

    /// <summary>The job failed or was cancelled. The row that was in progress turns red with
    /// the backend's message (it carries the failing LBA and checkpoint path when known);
    /// rows that never started are marked as not done instead of staying grey.</summary>
    internal void Fail(string message, bool cancelled)
    {
        // Validation errors before a job starts (no device, unticked confirmation) never
        // touch the outcome of the previous burn.
        if (!_tracking) return;
        _tracking = false;
        BurnMilestone? current = Items.FirstOrDefault(item => item.State == MilestoneState.Active)
            ?? Items.FirstOrDefault(item => item.State == MilestoneState.Pending);
        if (current is not null)
        {
            current.State = MilestoneState.Failed;
            current.Detail = message;
        }

        foreach (BurnMilestone item in Items.Where(item => item.State == MilestoneState.Pending))
        {
            item.State = MilestoneState.Failed;
            item.Detail = cancelled ? L.T("已取消") : L.T("未完成");
        }

        if (_verified.State == MilestoneState.Failed && _verified != current)
        {
            _verified.Detail = cancelled ? L.T("已取消，校验未完成") : L.T("校验未完成");
        }

        Summary = _written.State == MilestoneState.Done && _finalized.State == MilestoneState.Done
            ? L.T("已写入并封盘，但读回校验未完成。")
            : _written.State == MilestoneState.Done
                ? L.T("数据已写入，盘片尚未确认封盘。")
                : cancelled ? L.T("刻录已取消。") : L.T("刻录未完成。");
        Relocalize();
    }

    /// <summary>Result of the CD-TEXT read-back, compared by the caller with what was
    /// requested. <paramref name="differences"/> empty means every field and the track order
    /// matched.</summary>
    internal void ReportCdText(CdTextBurnOutcome? outcome, IReadOnlyList<string>? differences)
    {
        if (!Items.Contains(_cdText) || _cdText.State == MilestoneState.Skipped) return;
        if (outcome is null || !outcome.Written)
        {
            _cdText.State = MilestoneState.Skipped;
            _cdText.Detail = outcome?.SkippedReason ?? L.T("本次未写入 CD-TEXT");
        }
        else if (outcome.ReadbackError is not null)
        {
            _cdText.State = MilestoneState.Failed;
            _cdText.Detail = outcome.ReadbackError;
        }
        else if (differences is { Count: > 0 })
        {
            _cdText.State = MilestoneState.Failed;
            _cdText.Detail = string.Join("；", differences.Take(3)) + (differences.Count > 3 ? L.F($"……共 {differences.Count} 处") : string.Empty);
        }
        else
        {
            _cdText.State = MilestoneState.Done;
            _cdText.Detail = L.T("专辑、曲名、艺术家与曲序和设置一致");
        }
        Relocalize();
    }

    internal void Hide() => IsVisible = false;

    internal void Relocalize()
    {
        _written.Title = L.T("写入完成");
        _finalized.Title = L.T("已封盘");
        _verified.Title = _verified.State switch
        {
            MilestoneState.Failed => L.T("校验未完成"),
            MilestoneState.Skipped => L.T("未读回校验"),
            _ => L.T("读回校验通过"),
        };
        _cdText.Title = _cdText.State switch
        {
            MilestoneState.Failed => L.T("CD-TEXT 不一致"),
            MilestoneState.Skipped => L.T("未写入 CD-TEXT"),
            _ => L.T("CD-TEXT 读回一致"),
        };
    }

    private void Reset()
    {
        _testWrite = false;
        _sawVerify = false;
        foreach (BurnMilestone item in Items.Append(_cdText).Distinct())
        {
            item.State = MilestoneState.Pending;
            item.Detail = string.Empty;
        }

        Summary = string.Empty;
        Relocalize();
    }

    private void Activate(BurnMilestone item, string detail)
    {
        if (item.State is MilestoneState.Done or MilestoneState.Skipped) return;
        item.State = MilestoneState.Active;
        item.Detail = detail;
        Relocalize();
    }

    private void Complete(BurnMilestone item, string detail)
    {
        if (item.State is MilestoneState.Done or MilestoneState.Skipped) return;
        item.State = MilestoneState.Done;
        item.Detail = detail;
        Relocalize();
    }
}
