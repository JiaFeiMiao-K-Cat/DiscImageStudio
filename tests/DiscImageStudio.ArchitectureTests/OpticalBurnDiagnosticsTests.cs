using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using DiscImageStudio.Burning;

internal static class OpticalBurnDiagnosticsTests
{
    internal static void Run()
    {
        TestExceptionTree();
        TestFirstFailureContextIsRetained();
        TestVerificationInterfaceMetadata();
        bool nativeVerificationChecked = TestNativeVerificationWithoutRecorder();
        Console.WriteLine("optical-burn-diagnostics: passed (exception tree, first failure context, verification interface"
            + (nativeVerificationChecked ? ", native verification setting; no recorder access)"
                : "; native verification unavailable, no recorder access)"));
    }

    private static void TestExceptionTree()
    {
        COMException device = CaptureThrown(new COMException("feature unavailable", unchecked((int)0xC0AA020B)));
        device.Source = "native-format";
        device.Data["Device.Detail"] = "current feature not enabled";
        InvalidOperationException write = CaptureThrown(new InvalidOperationException("write boundary", device));
        write.Data["Write.Detail"] = "DVD finalization";
        IOException cleanup = CaptureThrown(new IOException("cleanup boundary"));
        cleanup.Data["Cleanup.Detail"] = "release failed";
        AggregateException aggregate = CaptureThrown(new AggregateException("multiple failures", write, cleanup, device));
        aggregate.Data["Root.Detail"] = "burn request";

        string formatted = OpticalBurnDiagnostics.FormatException(aggregate);
        foreach (Exception exception in new Exception[] { aggregate, write, device, cleanup })
        {
            Require(formatted.Contains($"HRESULT=0x{exception.HResult:X8}", StringComparison.Ordinal),
                "every exception level retains its own HRESULT");
            Require(formatted.Contains(exception.GetType().FullName!, StringComparison.Ordinal)
                && formatted.Contains(exception.Message, StringComparison.Ordinal),
                "every exception type and message remains available");
            Require(!string.IsNullOrEmpty(exception.StackTrace)
                && formatted.Contains(exception.StackTrace!, StringComparison.Ordinal),
                "the formatter preserves actual captured stack traces");
        }
        foreach (string detail in new[]
        {
            "Root.Detail: burn request", "Write.Detail: DVD finalization",
            "Device.Detail: current feature not enabled", "Cleanup.Detail: release failed",
            "Source=native-format", "[Exception.InnerExceptions[0].InnerException]",
            "[Exception.InnerExceptions[1]]",
        })
            Require(formatted.Contains(detail, StringComparison.Ordinal), "nested diagnostic context: " + detail);
        Require(Count(formatted, "Device.Detail: current feature not enabled") == 1,
            "a shared inner exception is traversed only once without losing other aggregate branches");
    }

    private static void TestFirstFailureContextIsRetained()
    {
        COMException exception = CaptureThrown(new COMException("write failed", unchecked((int)0x80004005)));
        exception.Data["Caller.Detail"] = "preserve me";
        OpticalBurnDiagnostics.CaptureFailure(exception, "IDiscFormat2Data.Write",
            "Stage=finalize; CurrentAction=5; LastWrittenLba=2297887");
        object? firstTimestamp = exception.Data["Burn.FailedAt"];
        Require(firstTimestamp is string timestamp
            && DateTimeOffset.TryParseExact(timestamp, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _), "failure time is recorded in round-trip form");
        OpticalBurnDiagnostics.CaptureFailure(exception, "ReleaseMedia",
            "Stage=release; CurrentAction=6; LastWrittenLba=0");
        Require(Equals(exception.Data["Burn.Operation"], "IDiscFormat2Data.Write")
            && Equals(exception.Data["Burn.FailedAt"], firstTimestamp)
            && Equals(exception.Data["Burn.ProgressAtFailure"],
                "Stage=finalize; CurrentAction=5; LastWrittenLba=2297887"),
            "cleanup context cannot overwrite the original operation, timestamp or device snapshot");
        Require(exception.HResult == unchecked((int)0x80004005)
            && Equals(exception.Data["Caller.Detail"], "preserve me")
            && exception.StackTrace!.Contains(nameof(CaptureThrown), StringComparison.Ordinal),
            "capturing context retains the original exception, stack, HRESULT and caller data");
        string formatted = OpticalBurnDiagnostics.FormatException(new InvalidOperationException("outer boundary", exception));
        Require(formatted.Contains("Burn.Operation: IDiscFormat2Data.Write", StringComparison.Ordinal)
            && formatted.Contains("LastWrittenLba=2297887", StringComparison.Ordinal)
            && !formatted.Contains("Stage=release", StringComparison.Ordinal),
            "the final exception report exposes the failure snapshot rather than the later cleanup state");
    }

    private static void TestVerificationInterfaceMetadata()
    {
        Type contract = typeof(IImapiBurnVerification);
        Require(contract.IsImport && contract.GUID == new Guid("D2FFD834-958B-426D-8470-2A13879C6A91")
            && contract.GetCustomAttribute<InterfaceTypeAttribute>()?.Value == ComInterfaceType.InterfaceIsIUnknown,
            "IBurnVerification uses the SDK IID and IUnknown rather than the format's IDispatch");
        MethodInfo[] methods = contract.GetMethods().OrderBy(method => method.MetadataToken).ToArray();
        Require(methods.Length == 2
            && methods[0].Name == nameof(IImapiBurnVerification.SetBurnVerificationLevel)
            && methods[0].ReturnType == typeof(void)
            && methods[0].GetParameters() is [{ ParameterType: var parameterType }]
            && parameterType == typeof(int)
            && methods[1].Name == nameof(IImapiBurnVerification.GetBurnVerificationLevel)
            && methods[1].ReturnType == typeof(int)
            && methods[1].GetParameters().Length == 0,
            "verification setter precedes getter in the native vtable with HRESULT translation");
        Require(methods.All(method => (method.MethodImplementationFlags & MethodImplAttributes.PreserveSig) == 0),
            "COM HRESULT failures are translated instead of being mistaken for verification levels");
    }

    private static bool TestNativeVerificationWithoutRecorder()
    {
        Exception? failure = null;
        bool checkedNative = false;
        Thread thread = new(() =>
        {
            object? format = null;
            try
            {
                Type? type = Type.GetTypeFromProgID("IMAPI2.MsftDiscFormat2Data", throwOnError: false);
                if (type is null)
                {
                    Console.WriteLine("optical-burn-diagnostics: native verification skipped (IMAPI2 class is not registered)");
                    return;
                }
                try { format = Activator.CreateInstance(type); }
                catch (COMException exception) when (exception.HResult is unchecked((int)0x80040154)
                    or unchecked((int)0x80040111))
                {
                    Console.WriteLine($"optical-burn-diagnostics: native verification skipped (COM class unavailable, 0x{exception.HResult:X8})");
                    return;
                }
                Require(format is not null, "the native data-format COM object can be created");
                // This object has no Recorder. Only its verification configuration
                // is accessed: no device enumeration, media queries or writes.
                IImapiBurnVerification verification = (IImapiBurnVerification)format!;
                verification.SetBurnVerificationLevel(1); // IMAPI_BURN_VERIFICATION_QUICK
                Require(verification.GetBurnVerificationLevel() == 1,
                    "the native verification setter/getter slots accept QUICK");
                DvdBurnVerification.Disable(format!);
                Require(verification.GetBurnVerificationLevel() == 0,
                    "Disable changes native QUICK to NONE without releasing the caller's RCW");
                checkedNative = true;
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                if (format is not null && Marshal.IsComObject(format))
                {
                    try { _ = Marshal.FinalReleaseComObject(format); }
                    catch (Exception exception) { failure ??= exception; }
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return checkedNative;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static T CaptureThrown<T>(T exception) where T : Exception
    {
        try { throw exception; }
        catch (T captured) { return captured; }
    }

    private static int Count(string text, string value)
    {
        int count = 0;
        for (int offset = 0; (offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0; offset += value.Length)
            count++;
        return count;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Optical burn diagnostics: " + message);
    }
}
