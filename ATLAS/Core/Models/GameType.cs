namespace Atlas.Core.Models;

/// <summary>
/// Supported game targets. Only <see cref="Arma3"/> is implemented; <see cref="ArmaReforger"/>
/// exists for the game-agnostic abstractions and shows a "Coming Soon" overlay in the UI.
/// </summary>
public enum GameType
{
    Arma3,
    ArmaReforger
}
