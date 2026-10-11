namespace Fixture.Items;

public sealed class Widget
{
    public string Name => Resources.Title + " " + Greeting.Ada + " " + Shared.Constants.Name;
}