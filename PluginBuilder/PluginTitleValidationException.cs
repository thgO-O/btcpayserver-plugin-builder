namespace PluginBuilder;

public sealed class PluginTitleValidationException(string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public const string DuplicateMessage = "This plugin title is already in use. Please choose a different title.";
    public const string RequiredMessage = "Published plugins must have a title. Enter a title in plugin settings.";
    public const string LengthMessage = "Plugin title must be 500 characters or fewer.";
    public const string UniqueConstraint = "plugins_title_unique";
    public const string RequiredConstraint = "plugins_published_title_required";
    public const string LengthConstraint = "plugins_title_length";
}
