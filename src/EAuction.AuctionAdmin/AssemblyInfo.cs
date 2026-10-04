using System.Runtime.CompilerServices;

// See the note in Program.cs: Program stays internal to avoid colliding with
// the other services', and WebApplicationFactory reaches it through this.
[assembly: InternalsVisibleTo("EAuction.AuctionAdmin.Tests")]
