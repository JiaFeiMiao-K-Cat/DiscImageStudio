namespace DiscImageStudio.Burning;

internal interface IBluRayRewritablePreparationSession
{
    BluRayRewritableMedia ReadRewritableMedia();
    BluRayRewritableLayout ReadRewritableLayout();
    BluRayRewritableLayout FormatRewritableWithoutSpareArea(CancellationToken cancellationToken);
}

internal static class BluRayRewritableBurn
{
    internal static OpticalMediaCapacity ReadCapacity(IMmcCommandTransport transport)
    {
        BluRayRewritableMmcCommands commands = new(transport);
        BluRayRewritablePreparation preparation = GetPreparation(commands.ReadMedia());
        uint writable = preparation == BluRayRewritablePreparation.Overwrite
            ? commands.ReadLayout().SectorCount
            : BluRayRewritableMmcCommands.SingleLayerSectors;
        return new(BluRayRewritableMmcCommands.SingleLayerSectors, writable)
        {
            BluRayRewritablePreparation = preparation,
        };
    }

    // Called only with the recorder held exclusively and removal prevented.
    internal static BluRayRewritableLayout Prepare(
        IBluRayRewritablePreparationSession session, OpticalBurnRequest request,
        CancellationToken cancellationToken, Action<string> reportOperation)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BluRayRewritablePreparation preparation = GetPreparation(session.ReadRewritableMedia());
        if (request.BluRayRewritablePreparation != preparation
            || request.ConfirmBluRayRewritable is not { } confirm
            || request.ProduceMappedBluRayContent is null)
            throw new InvalidOperationException("当前 BD-RE 盘片的准备方式已变化，请重新检查并确认刻录。");

        bool overwrite = preparation == BluRayRewritablePreparation.Overwrite;
        BluRayRewritableLayout existing = overwrite ? session.ReadRewritableLayout() : default;
        ValidateLength(request, overwrite ? existing.SectorCount : BluRayRewritableMmcCommands.SingleLayerSectors);

        cancellationToken.ThrowIfCancellationRequested();
        if (!confirm(preparation)) throw new OperationCanceledException("已取消 BD-RE 格式化或覆写。");
        cancellationToken.ThrowIfCancellationRequested();

        if (overwrite) return existing;

        reportOperation("正在无备用区格式化 BD-RE（请等待完成）");
        BluRayRewritableLayout layout = session.FormatRewritableWithoutSpareArea(cancellationToken);
        ValidateLength(request, layout.SectorCount);
        return layout;
    }

    private static BluRayRewritablePreparation GetPreparation(BluRayRewritableMedia media)
    {
        if (media.IsWriteProtected)
            throw new InvalidOperationException("BD-RE 盘片处于写保护状态。");
        if (media.IsFormatted && media.SpareClusters == 0)
            return BluRayRewritablePreparation.Overwrite;
        if (!media.CanFormatWithoutSpareArea)
            throw new NotSupportedException("当前光驱和盘片未共同报告无备用区格式化支持（RENoSA／格式 31h）。");

        return media.IsFormatted ? BluRayRewritablePreparation.Reformat : BluRayRewritablePreparation.Format;
    }

    private static void ValidateLength(OpticalBurnRequest request, uint writableSectors)
    {
        if (request.ContentLength <= 0 || request.ContentLength % BluRaySequentialWriter.BytesPerWrite != 0
            || request.ContentLength > (long)writableSectors * BluRaySequentialWriter.SectorBytes)
            throw new InvalidOperationException("BD-RE 绘图需要 32 扇区对齐，且内容必须装入已验证的用户数据区。");
    }
}
