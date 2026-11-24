using System;
using System.Collections.Generic;
using System.Windows.Threading;
using DiscImageStudio.ViewModels;

/// <summary>Covers the bounded toast window: the newest toasts stay visible, older ones
/// collapse into a counted backlog, closing a visible toast promotes the oldest hidden
/// one, and hidden toasts expiring on their own drain the backlog label.</summary>
internal static class ToastStackTests
{
    internal static void Run()
    {
        ToastStack stack = new();
        List<ToastItem> toasts = new();
        ToastItem Make()
        {
            ToastItem toast = new(
                Dispatcher.CurrentDispatcher, "t" + (toasts.Count + 1), "m", ToastKind.Info,
                TimeSpan.FromMinutes(1));
            toasts.Add(toast);
            return toast;
        }

        for (int index = 0; index < 6; index++)
        {
            stack.Push(Make());
        }

        Require(stack.Visible.Count == 4, "the visible window is capped at four toasts");
        Require(stack.OverflowCount == 2, "toasts beyond the window collapse into the backlog");
        Require(stack.OverflowLabel!.Contains("2", StringComparison.Ordinal),
            "the overflow label reports the backlog count: " + stack.OverflowLabel);
        Require(stack.Visible[0].Title == "t3" && stack.Visible[3].Title == "t6",
            "the newest toasts stay visible and the oldest visible one is the fourth-newest");

        // A hidden toast expiring on its own only drains the backlog.
        stack.Remove(toasts[0]);
        Require(stack.Visible.Count == 4 && stack.OverflowCount == 1,
            "a hidden toast expiring drains the backlog without touching the window");
        Require(stack.Visible[0].Title == "t3", "hidden expiry leaves the window untouched");

        // Closing a visible toast promotes the oldest hidden toast.
        stack.Remove(stack.Visible[0]);
        Require(stack.Visible.Count == 4 && stack.OverflowCount == 0,
            "closing a visible toast pulls the next hidden toast into the window");
        Require(stack.Visible[0].Title == "t2", "the oldest hidden toast is promoted first");
        Require(stack.OverflowLabel is null, "an empty backlog clears the overflow label");

        // A full window demotes the oldest visible toast when a new one arrives.
        stack.Push(Make());
        Require(stack.Visible.Count == 4 && stack.Visible[3].Title == "t7" && stack.OverflowCount == 1,
            "a new toast takes the newest slot and demotes the oldest visible one");
        Require(!stack.Visible.Any(item => item.Title == "t2"), "the demoted toast leaves the window");

        // Removing an unknown toast is a no-op, not a crash.
        stack.Remove(Make());
        Require(stack.Visible.Count == 4 && stack.OverflowCount == 1,
            "removing a toast that is not in the stack changes nothing");

        Console.WriteLine("toast-stack: passed (window cap, backlog count, promotion, drain)");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Toast stack check failed: " + name);
        }
    }
}
