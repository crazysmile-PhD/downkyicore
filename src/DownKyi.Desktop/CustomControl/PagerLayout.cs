namespace DownKyi.CustomControl;

internal readonly record struct PagerLayout(
    int First,
    int PreviousSecond,
    int PreviousFirst,
    int NextFirst,
    int NextSecond,
    bool PreviousVisibility,
    bool FirstVisibility,
    bool LeftJumpVisibility,
    bool PreviousSecondVisibility,
    bool PreviousFirstVisibility,
    bool NextFirstVisibility,
    bool NextSecondVisibility,
    bool RightJumpVisibility,
    bool LastVisibility,
    bool NextVisibility)
{
    public static PagerLayout Create(int current, int count)
    {
        var hasPrevious = current > 1;
        var hasNext = current < count;
        return new PagerLayout(
            First: 1,
            PreviousSecond: current - 2,
            PreviousFirst: current - 1,
            NextFirst: current + 1,
            NextSecond: current + 2,
            PreviousVisibility: hasPrevious,
            FirstVisibility: current >= 4,
            LeftJumpVisibility: current >= 5,
            PreviousSecondVisibility: current >= 3,
            PreviousFirstVisibility: hasPrevious,
            NextFirstVisibility: hasNext,
            NextSecondVisibility: current <= count - 2,
            RightJumpVisibility: current <= count - 4,
            LastVisibility: current <= count - 3,
            NextVisibility: hasNext);
    }
}
