using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Windows.Media.Imaging;

namespace XTPdfMergeApp.Services;

/// <summary>Strong ownership belongs to bounded caches and visible Image controls, not undo data.
/// Not thread safe: callers use their existing cache lock (or the UI dispatcher).</summary>
internal sealed class BitmapMemoryCache<TKey> where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, BitmapSource Bitmap)>> _items = new();
    private readonly LinkedList<(TKey Key, BitmapSource Bitmap)> _lru = new();
    public BitmapMemoryCache(long budgetBytes) => BudgetBytes = budgetBytes;
    public long BudgetBytes { get; }
    public long Bytes { get; private set; }
    public int Count => _items.Count;
    public static long SizeOf(BitmapSource bitmap) =>
        ((long)bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8 * bitmap.PixelHeight;

    public bool ContainsKey(TKey key) => _items.ContainsKey(key);
    public bool TryGetValue(TKey key, [NotNullWhen(true)] out BitmapSource? bitmap)
    {
        if (!_items.TryGetValue(key, out var node)) { bitmap = null; return false; }
        _lru.Remove(node);
        _lru.AddLast(node);
        bitmap = node.Value.Bitmap;
        return true;
    }

    public void Set(TKey key, BitmapSource bitmap, Func<TKey, bool>? keep = null)
    {
        Remove(key);
        // Oversized images can still be displayed by the caller without occupying the cache.
        long size = SizeOf(bitmap);
        if (size > BudgetBytes && keep?.Invoke(key) != true) return;
        _items.Add(key, _lru.AddLast((key, bitmap)));
        Bytes += size;
        Trim(keep);
    }

    // Pinned viewport/staging tiles may temporarily exceed the cache budget. Their LRU
    // nodes remain tracked, so they become reclaimable as soon as the viewport changes.
    public void Trim(Func<TKey, bool>? keep = null)
    {
        var node = _lru.First;
        while (Bytes > BudgetBytes && node != null)
        {
            var next = node.Next;
            if (keep?.Invoke(node.Value.Key) != true) Remove(node.Value.Key);
            node = next;
        }
    }

    public void Remove(TKey key)
    {
        if (!_items.Remove(key, out var node)) return;
        Bytes -= SizeOf(node.Value.Bitmap);
        _lru.Remove(node);
    }

    public void RemoveWhere(Func<TKey, bool> predicate)
    {
        foreach (var key in _items.Keys.Where(predicate).ToArray()) Remove(key);
    }

    public void Clear() { _items.Clear(); _lru.Clear(); Bytes = 0; }
}
