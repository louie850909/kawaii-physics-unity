// 可重用的資料資產：碰撞組（LimitsDataAsset）、骨間約束組（BoneConstraintsDataAsset）、設定預設（PresetDataAsset）、
// 共用程序風（Shared Publisher 的風）。都用骨頭名稱指定，套到不同角色時用名稱在階層裡找。
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace KawaiiPhysicsUnity
{
    /// <summary>對應 UKawaiiPhysicsBoneConstraintsDataAsset：用骨頭名稱指定的骨間約束組，可用正規表示式產生。</summary>
    [CreateAssetMenu(menuName = "KawaiiPhysics/Bone Constraints Asset")]
    public class KawaiiPhysicsConstraintsAsset : ScriptableObject
    {
        /// <summary>一條骨間約束。</summary>
        [Serializable]
        public class Entry
        {
            [Tooltip("第一根骨頭的名稱")]
            public string boneName1;
            [Tooltip("第二根骨頭的名稱")]
            public string boneName2;
            [Tooltip("用這條約束自己的柔軟度")]
            public bool overrideCompliance;
            [Tooltip("這條約束的柔軟度")]
            public KPComplianceType complianceType = KPComplianceType.Leather;
            [Tooltip("這條約束不插入橋接假骨")]
            public bool excludeFromSubdivision;
        }

        /// <summary>ApplyRegex 用的一對樣式。</summary>
        [Serializable]
        public class RegexPair
        {
            [Tooltip("第一根骨頭名稱的正規表示式")]
            public string pattern1;
            [Tooltip("第二根骨頭名稱的正規表示式")]
            public string pattern2;
        }

        [Tooltip("約束（兩根骨頭的名稱）")]
        public List<Entry> entries = new List<Entry>();
        [Tooltip("ApplyRegex 用：兩個樣式在骨頭名稱清單裡依序配對（例：skirt_00_(\\d+) 與 skirt_01_(\\d+)）")]
        public List<RegexPair> regexPatterns = new List<RegexPair>();

        /// <summary>原作 ApplyRegex：骨頭名稱清單每行一個，兩個樣式各自依序找下一個符合的，配成一對。</summary>
        public int ApplyRegex(IEnumerable<string> boneNames)
        {
            var listString = string.Join("\n", boneNames);
            var added = 0;
            foreach (var p in regexPatterns)
            {
                if (string.IsNullOrEmpty(p.pattern1) || string.IsNullOrEmpty(p.pattern2)) continue;
                var m1 = Regex.Matches(listString, p.pattern1, RegexOptions.Multiline);
                var m2 = Regex.Matches(listString, p.pattern2, RegexOptions.Multiline);
                for (int i = 0; i < Mathf.Min(m1.Count, m2.Count); i++)
                {
                    entries.Add(new Entry { boneName1 = m1[i].Value, boneName2 = m2[i].Value });
                    added++;
                }
            }
            return added;
        }
    }
}
