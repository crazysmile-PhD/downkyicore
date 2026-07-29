using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownKyi.CustomControl;

namespace DownKyi.Tests;

public sealed class CustomPagerViewModelTests
{
    [Fact]
    public void ConstructorHonorsCurrentPageAndBuildsItsLayout()
    {
        var pager = new CustomPagerViewModel(5, 10);

        Assert.Equal(5, pager.Current);
        Assert.Equal(3, pager.PreviousSecond);
        Assert.Equal(4, pager.PreviousFirst);
        Assert.Equal(6, pager.NextFirst);
        Assert.Equal(7, pager.NextSecond);
        Assert.True(pager.LeftJumpVisibility);
        Assert.True(pager.RightJumpVisibility);
    }

    [Fact]
    public void CurrentCanChangeBeforeAListenerIsAttached()
    {
        var pager = new CustomPagerViewModel(1, 3);

        pager.Current = 2;

        Assert.Equal(2, pager.Current);
        Assert.Equal(2, pager.ProposedCurrent);
    }

    [Fact]
    public void NavigationButtonsExecuteWithoutCommandParameters()
    {
        var pager = new CustomPagerViewModel(3, 10);

        Execute(pager.PreviousCommand);
        Assert.Equal(2, pager.Current);

        Execute(pager.FirstCommand);
        Assert.Equal(1, pager.Current);

        Execute(pager.NextCommand);
        Execute(pager.NextSecondCommand);
        Assert.Equal(4, pager.Current);

        Execute(pager.LastCommand);
        Assert.Equal(10, pager.Current);
    }

    [Fact]
    public void CurrentChangingCanVetoACommand()
    {
        var pager = new CustomPagerViewModel(2, 4);
        pager.CurrentChanging += (_, args) => args.Cancel = true;

        Execute(pager.NextCommand);

        Assert.Equal(2, pager.Current);
        Assert.Equal(3, pager.ProposedCurrent);
    }

    [Fact]
    public void JumpClampsToTheLastPageAndRejectsZero()
    {
        var pager = new CustomPagerViewModel(2, 6);

        pager.JumpCommand.Execute("99");
        Assert.Equal(6, pager.Current);

        pager.JumpCommand.Execute("0");
        Assert.Equal(6, pager.Current);
    }

    [Fact]
    public void CountBelowCurrentHidesPagerWithoutPublishingAnInvalidRange()
    {
        var pager = new CustomPagerViewModel(3, 5);

        pager.Count = 2;

        Assert.Equal(5, pager.Count);
        Assert.Equal(3, pager.Current);
        Assert.False(pager.Visibility);
    }

    private static void Execute(RelayCommand command)
    {
        command.Execute(null);
    }
}
