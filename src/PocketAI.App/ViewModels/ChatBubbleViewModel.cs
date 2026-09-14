using PocketAI.App.Infrastructure;

namespace PocketAI.App.ViewModels;

public sealed class ChatBubbleViewModel : ObservableObject
{
    private string _content;
    private string _sourcesText = "";

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

    public string SourcesText
    {
        get => _sourcesText;
        set
        {
            if (SetProperty(ref _sourcesText, value))
                OnPropertyChanged(nameof(HasSources));
        }
    }

    public bool HasSources => !string.IsNullOrWhiteSpace(_sourcesText);
}
