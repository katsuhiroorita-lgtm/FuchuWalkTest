using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Stores this device's best completed runs in descending order of claimed area.</summary>
internal static class FuchuRunRanking
{
    private const string SaveKey = "FuchuWalk.RunRanking.v1";
    private const int MaxEntries = 10;

    [Serializable]
    internal struct Entry
    {
        public double coverage;
        public double buildingPoints;
    }

    [Serializable]
    private sealed class SavedRanking
    {
        public List<Entry> entries = new List<Entry>();
    }

    public static IReadOnlyList<Entry> Record(double coverage, double buildingPoints, out int rank)
    {
        SavedRanking ranking = Read();
        List<Entry> entries = ranking.entries;
        int index = entries.FindIndex(entry => coverage > entry.coverage);
        if (index < 0) index = entries.Count;
        rank = index < MaxEntries ? index + 1 : 0;
        if (rank == 0) return entries;

        entries.Insert(index, new Entry { coverage = coverage, buildingPoints = buildingPoints });
        if (entries.Count > MaxEntries) entries.RemoveAt(MaxEntries);
        try
        {
            PlayerPrefs.SetString(SaveKey, JsonUtility.ToJson(ranking));
            PlayerPrefs.Save();
        }
        catch (PlayerPrefsException exception)
        {
            Debug.LogWarning("ランキングを端末に保存できませんでした。 " + exception.Message);
        }
        return entries;
    }

    private static SavedRanking Read()
    {
        string saved = PlayerPrefs.GetString(SaveKey, "");
        if (saved.Length == 0) return new SavedRanking();
        try
        {
            SavedRanking ranking = JsonUtility.FromJson<SavedRanking>(saved);
            if (ranking?.entries != null) return ranking;
        }
        catch (ArgumentException)
        {
            Debug.LogWarning("保存済みのランキングを読み込めませんでした。");
        }
        return new SavedRanking();
    }
}
