namespace FluentLegionToolbar.Hardware;

/// <summary>A refusal the user can act on (for example Performance mode needs AC power). Shown as its own message, unlike a change that could not be confirmed.</summary>
sealed class DeviceMessageException : InvalidOperationException
{
    public string English { get; }
    public string Arabic { get; }
    public DeviceMessageException(string english, string arabic) : base(english) { English = english; Arabic = arabic; }
}
