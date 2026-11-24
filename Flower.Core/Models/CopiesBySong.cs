using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Flower.Models
{
    // A song's copies in one list, handed out in turn to its copies in another:
    // the nth copy there is paired with the nth copy here. How two lists of
    // rows are matched up where entry ids cannot say (a list worked out again
    // from songs, a queue made from a playlist, copies made before rows had
    // ids).
    public sealed class CopiesBySong<T>
    {
        private readonly Dictionary<Guid, Queue<T>> _waiting = new();

        // A row with no song cannot be paired, and is skipped.
        public void Add(Guid? song, T value)
        {
            if (song is not { } id)
                return;
            if (!_waiting.TryGetValue(id, out var copies))
                _waiting[id] = copies = new Queue<T>();
            copies.Enqueue(value);
        }

        // The next copy of this song not yet taken, if there is one.
        public bool TryTake(Guid? song, [MaybeNullWhen(false)] out T value)
        {
            if (song is { } id && _waiting.TryGetValue(id, out var copies) && copies.TryDequeue(out value))
                return true;

            value = default;
            return false;
        }
    }
}
