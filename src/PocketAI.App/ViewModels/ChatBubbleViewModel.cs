using PocketAI.App.Infrastructure;

namespace PocketAI.App.ViewModels;

public sealed class ChatBubbleViewModel : ObservableObject
{
    private string _content;

    public ChatBubbleViewModel(string role, string content)
    {
        Role = role;
        _content = content;
    }

    public string Role { get; }
    public bool IsUser => Role.Equals("user", StringComparison.OrdinalIgnoreCase);

    public string Content
    {
        get => _content;
        set => SetProperty(ref _content, value);
    }
}
