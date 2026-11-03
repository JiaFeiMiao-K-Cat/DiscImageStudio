using CommunityToolkit.Mvvm.ComponentModel;
using DiscImageStudio.Linux.Controls;

namespace DiscImageStudio.Linux.ViewModels;

public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error,
}

public partial class ToastViewModel : ObservableObject
{
    public ToastViewModel(ToastKind kind, string title, string? message)
    {
        Kind = kind;
        Title = title;
        Message = message;
    }

    public ToastKind Kind { get; }

    public string Title { get; }

    public string? Message { get; }

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    public bool IsSuccess => Kind == ToastKind.Success;

    public bool IsError => Kind == ToastKind.Error;

    public bool IsWarning => Kind == ToastKind.Warning;

    public bool IsInfo => Kind == ToastKind.Info;

    public string Icon => Kind switch
    {
        ToastKind.Success => Icons.CircleCheck,
        ToastKind.Error => Icons.CircleX,
        ToastKind.Warning => Icons.TriangleAlert,
        _ => Icons.Info,
    };
}
