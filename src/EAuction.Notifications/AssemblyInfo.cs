using System.Runtime.CompilerServices;

// WebApplicationFactory needs the generated Program type, which top-level
// statements make internal. It stays internal rather than public so it cannot
// collide with the other services' Program in a test assembly referencing more
// than one of them.
[assembly: InternalsVisibleTo("EAuction.Notifications.Tests")]
