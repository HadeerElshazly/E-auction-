using System.Runtime.CompilerServices;

// The test suite injects eligibility directly to exercise the screening logic
// without standing up the participant service.
[assembly: InternalsVisibleTo("EAuction.Tests")]
