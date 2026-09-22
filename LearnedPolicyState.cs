namespace MahjongHater;

// Set once when the plugin builds its policy stack; read by the overlay's Diagnostics tab.
public sealed record LearnedPolicyState(bool Loaded, string Detail, string Folder);

// An optional offline component (precomputed / simulation policy tables): loaded or not, and why.
public sealed record OptionalComponentState(bool Loaded, string Detail);
