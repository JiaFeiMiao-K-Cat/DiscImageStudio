using System.Runtime.InteropServices;

namespace DiscImageStudio.Burning;

internal static class DvdBurnVerification
{
    internal static void Disable(object format)
    {
        // IBurnVerification is IUnknown, not the format's default IDispatch.
        // Set and confirm NONE before any physical write starts.
        IImapiBurnVerification verification = (IImapiBurnVerification)format;
        verification.SetBurnVerificationLevel(0);
        if (verification.GetBurnVerificationLevel() != 0)
            throw new InvalidOperationException("Windows 刻录接口未接受关闭 DVD 刻后校验的设置。");
        // This interface shares the caller-owned RCW. Do not release it here.
    }
}

// Windows SDK imapi2.idl: IBurnVerification, setter precedes getter in the vtable.
[ComImport]
[Guid("D2FFD834-958B-426D-8470-2A13879C6A91")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IImapiBurnVerification
{
    void SetBurnVerificationLevel(int value);
    int GetBurnVerificationLevel();
}
