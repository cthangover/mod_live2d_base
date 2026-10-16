using System.Runtime.CompilerServices;

// Exposes internal pure-math/parsing helpers of the Live2D mod to the game's
// unit test project (tests/Cthangover.Tests). Internal members are the test
// seam; public runtime API stays unchanged.
[assembly: InternalsVisibleTo("Cthangover.Tests")]
