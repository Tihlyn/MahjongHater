namespace MahjongHater;

// Set once when the plugin builds its policy stack; read by the overlay's Diagnostics tab.
public sealed record LearnedPolicyState(bool Loaded, string Detail, string Folder);
