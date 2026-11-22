using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

/// <summary>Fixed PLATEAU metadata, shared by all builds and independent of streamed model visibility.</summary>
internal sealed class FuchuOnlineBuildingCatalog
{
    internal sealed class Entry
    {
        internal string Id;
        internal Vector2 Position;
        internal double Points;
    }
    internal readonly List<Entry> Entries = new List<Entry>();
    internal readonly Dictionary<string, int> Index = new Dictionary<string, int>(StringComparer.Ordinal);
    internal string Hash { get; }

    internal FuchuOnlineBuildingCatalog(Vector2 center)
    {
        TextAsset data = Resources.Load<TextAsset>("FuchuOnlineBuildings");
        if (data == null) throw new InvalidOperationException("オンライン建物カタログがありません。");
        string content = data.text.TrimStart('\uFEFF').Replace("\r\n", "\n");
        using (var sha = SHA256.Create())
            Hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(content))).Replace("-", "");
        foreach (string line in content.Split('\n'))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length != 4) throw new InvalidOperationException("オンライン建物カタログの形式が不正です。");
            var position = new Vector2(float.Parse(fields[1], CultureInfo.InvariantCulture), float.Parse(fields[2], CultureInfo.InvariantCulture));
            if ((position - center).sqrMagnitude > 1000000f) continue;
            Index.Add(fields[0], Entries.Count);
            Entries.Add(new Entry { Id = fields[0], Position = position, Points = double.Parse(fields[3], CultureInfo.InvariantCulture) });
        }
        if (Entries.Count == 0) throw new InvalidOperationException("プレイ範囲内の建物がありません。");
    }
}
