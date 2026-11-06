namespace DvdImageSolver.Encoding;

internal static partial class EfmPlusTables
{
    // ECMA-267, 3rd edition, Table 4.
    // Indexed by [state group (1/2 or 3/4), primary/secondary, category].
    private static readonly uint[,,] SyncCodes =
    {
        {
            {
                0x12440011, 0x04040011, 0x10040011, 0x08040011,
                0x20040011, 0x22440011, 0x24840011, 0x24440011,
            },
            {
                0x12040011, 0x04440011, 0x10440011, 0x08440011,
                0x20440011, 0x22040011, 0x20840011, 0x24040011,
            },
        },
        {
            {
                0x92040011, 0x84440011, 0x90440011, 0x82440011,
                0x88440011, 0x89040011, 0x90840011, 0x88840011,
            },
            {
                0x92440011, 0x84040011, 0x90040011, 0x82040011,
                0x88040011, 0x81040011, 0x80440011, 0x80840011,
            },
        },
    };

    internal static (ushort Word, byte NextState) LookupMain(byte value, byte state)
    {
        int index = value * 4 + StateIndex(state);
        return (MainWords[index], MainNextStates[index]);
    }

    internal static (ushort Word, byte NextState) Substitution(byte value, byte state)
    {
        if (value > 87)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "The substitution table only covers bytes 0..87.");
        }

        int index = value * 4 + StateIndex(state);
        return (SubstitutionWords[index], SubstitutionNextStates[index]);
    }

    internal static uint Sync(byte state, int category, bool primary)
    {
        if ((uint)category > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        int group = state <= 2 ? 0 : 1;
        return SyncCodes[group, primary ? 0 : 1, category];
    }

    private static int StateIndex(byte state)
    {
        if (state is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(state), "EFMPlus state must be 1..4.");
        }

        return state - 1;
    }
}
