using System;
using System.Collections.Generic;

namespace FlaxMcpBridge.Host;

/// <summary>
/// The most recently used compiled snippets, by key: a repeated snippet against the same references is not compiled
/// again. Thread-safe; the least recently used entry goes when the cache is full.
/// </summary>
public sealed class SnippetCache
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<string, LinkedListNode<SnippetCacheEntry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<SnippetCacheEntry> _order = new();

    /// <summary>Creates an empty cache.</summary>
    /// <param name="capacity">How many images it keeps.</param>
    /// <exception cref="ArgumentOutOfRangeException">The capacity is not positive.</exception>
    public SnippetCache(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "The snippet cache needs a positive capacity.");
        }

        _capacity = capacity;
    }

    /// <summary>How many images it holds.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>The image stored under a key, which becomes the most recently used.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The image, or null when none is stored.</returns>
    public byte[]? Find(string key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var node))
            {
                return null;
            }

            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value.Image;
        }
    }

    /// <summary>Stores an image; the least recently used one goes when the cache is full.</summary>
    /// <param name="key">The key.</param>
    /// <param name="image">The compiled assembly.</param>
    public void Store(string key, byte[] image)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
            }
            else if (_entries.Count >= _capacity)
            {
                var oldest = _order.Last!;
                _order.RemoveLast();
                _entries.Remove(oldest.Value.Key);
            }

            _entries[key] = _order.AddFirst(new SnippetCacheEntry(key, image));
        }
    }
}

/// <summary>One cached image.</summary>
/// <param name="Key">Its key.</param>
/// <param name="Image">The compiled assembly.</param>
public sealed record SnippetCacheEntry(string Key, byte[] Image);
