using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using DiscImageStudio.Localization;

namespace DiscImageStudio.ViewModels;

/// <summary>Bounded toast window for the shell. Only the newest toasts stay visible; when
/// the window is full, the oldest visible card yields its slot and waits in a counted
/// backlog instead of growing an endless column over page content. Hidden toasts keep
/// their lifetime timers, so the backlog drains on its own; closing a visible toast
/// promotes the oldest hidden one, which keeps the stack honest about what is pending.</summary>
public sealed partial class ToastStack : ObservableObject
{
    private const int MaxVisibleToasts = 4;
    private readonly Queue<ToastItem> _hidden = new();

    public ObservableCollection<ToastItem> Visible { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverflowLabel))]
    [NotifyPropertyChangedFor(nameof(OverflowVisibility))]
    private int _overflowCount;

    public string? OverflowLabel => OverflowCount > 0 ? L.Format("Xaml.MoreToasts", OverflowCount) : null;

    public Visibility OverflowVisibility => OverflowCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    public void Push(ToastItem toast)
    {
        if (Visible.Count >= MaxVisibleToasts)
        {
            _hidden.Enqueue(Visible[0]);
            Visible.RemoveAt(0);
        }

        Visible.Add(toast);
        OverflowCount = _hidden.Count;
    }

    public void Remove(ToastItem toast)
    {
        if (!Visible.Remove(toast))
        {
            // A hidden toast expired while waiting; drop it from the backlog. A toast
            // that is in neither collection was already removed, so there is nothing to do.
            ToastItem[] remaining = _hidden.Where(item => !ReferenceEquals(item, toast)).ToArray();
            if (remaining.Length == _hidden.Count)
            {
                return;
            }

            _hidden.Clear();
            foreach (ToastItem item in remaining)
            {
                _hidden.Enqueue(item);
            }
        }

        // A visible slot freed up: promote the oldest waiting toast. It slots in at the
        // top — the oldest position — so the window keeps reading oldest to newest.
        while (Visible.Count < MaxVisibleToasts && _hidden.Count > 0)
        {
            Visible.Insert(0, _hidden.Dequeue());
        }

        OverflowCount = _hidden.Count;
    }
}
