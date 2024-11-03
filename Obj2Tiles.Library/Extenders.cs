﻿namespace Obj2Tiles.Library;

public static class Extenders
{
    public static int AddIndex<T>(this ICollection<T> collection, T item)
    {
        collection.Add(item);
        return collection.Count - 1;
    }
    
    public static int AddIndex<T>(this IDictionary<T, int> dictionary, T item)
    {   

        // If the item is already in the dictionary, return the index
        if (dictionary.TryGetValue(item, out var index))
            return index;

        // If the item is not in the dictionary, add it and return the index
        dictionary.Add(item, dictionary.Count);
        return dictionary.Count - 1;

    }
}