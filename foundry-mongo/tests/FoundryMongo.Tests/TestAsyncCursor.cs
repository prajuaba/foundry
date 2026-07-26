using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;

namespace Foundry.Mongo.Tests;

public class TestAsyncCursor<T> : IAsyncCursor<T>
{
    private readonly List<T> _items;
    private bool _hasMoved;

    public TestAsyncCursor(T item)
    {
        _items = new List<T> { item };
    }

    public TestAsyncCursor(List<T> items)
    {
        _items = items;
    }

    public IEnumerable<T> Current => _items;

    public bool MoveNext(CancellationToken cancellationToken = default)
    {
        if (_hasMoved) return false;
        _hasMoved = true;
        return true;
    }

    public Task<bool> MoveNextAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(MoveNext(cancellationToken));
    }

    public void Dispose() { }
}
