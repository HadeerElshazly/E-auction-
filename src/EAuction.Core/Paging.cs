namespace EAuction.Core;

/// <summary>
/// One page of a list, as every list endpoint takes it: <c>?skip=&amp;take=</c>, and
/// answers <c>{ total, skip, take, items }</c>. The portals ask for ten rows at a
/// time; filtering and paging happen on the server, so a page holds only what the
/// filters chose and <c>total</c> is the real count behind it.
///
/// Bounded here rather than trusted from the query string: a portal bug should not
/// be able to ask for every row ever written. Endpoints that other screens read
/// whole pass their own default for a call without <c>take</c>.
/// </summary>
public readonly record struct Slice(int Skip, int Take)
{
    /// <summary>What a portal's grid shows per page.</summary>
    public const int PageSize = 10;

    public const int MaxTake = 200;

    public static Slice From(int? skip, int? take, int defaultTake = PageSize) =>
        new(Math.Max(0, skip ?? 0), Math.Clamp(take ?? defaultTake, 1, MaxTake));

    /// <summary>This page of rows already filtered and ordered.</summary>
    public T[] Of<T>(IEnumerable<T> ordered) => ordered.Skip(Skip).Take(Take).ToArray();
}
