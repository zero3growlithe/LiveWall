namespace LiveWall.Models;

/// <summary>
/// Which palette the settings window and its dialogs paint with. Persisted as a number, so a settings file
/// written by a build that had no theme at all still opens — it simply lands on
/// <see cref="Light"/>.
/// </summary>
public enum AppTheme
{
    Light = 0,

    Dark = 1
}
