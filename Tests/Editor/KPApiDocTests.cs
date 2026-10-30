// API 參考（Documentation~/api.md）有沒有涵蓋所有公開型別與成員；套件需要的文件檔都在。
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.PackageManager;

namespace KawaiiPhysicsUnity.Tests
{
    public class KPApiDocTests
    {
        static string PackageRoot => PackageInfo.FindForAssembly(typeof(KPSystem).Assembly).resolvedPath;

        [TestCase("package.json")]
        [TestCase("README.md")]
        [TestCase("CHANGELOG.md")]
        [TestCase("LICENSE.md")]
        [TestCase("Documentation~/index.md")]
        [TestCase("Documentation~/api.md")]
        [TestCase("Samples~/Basic")]
        public void PackageFileExists(string relative)
        {
            var path = Path.Combine(PackageRoot, relative);
            Assert.That(File.Exists(path) || Directory.Exists(path), Is.True, path);
        }

        [Test]
        public void ApiReferenceCoversEveryPublicMember()
        {
            var doc = File.ReadAllText(Path.Combine(PackageRoot, "Documentation~", "api.md"));
            var assemblies = new[] { typeof(KPSystem).Assembly, typeof(KawaiiPhysicsUnity.Editor.KPExternalForceMenu).Assembly };
            var missing = new System.Collections.Generic.List<string>();
            foreach (var t in assemblies.SelectMany(a => a.GetExportedTypes()))
            {
                var name = t.FullName.Replace('+', '.');
                name = name.Substring(name.IndexOf('.') + 1);
                if (name.StartsWith("Editor.")) name = name.Substring("Editor.".Length);
                var heading = "### " + name + "\n";
                var at = doc.IndexOf(heading, System.StringComparison.Ordinal);
                if (at < 0)
                {
                    missing.Add("型別 " + name);
                    continue;
                }
                var next = doc.IndexOf("\n### ", at + heading.Length, System.StringComparison.Ordinal);
                var section = next < 0 ? doc.Substring(at) : doc.Substring(at, next - at);
                const BindingFlags f = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                // 名稱層級＋多載的數量：表裡同名的列數要跟公開的多載一樣多
                var counts = new System.Collections.Generic.Dictionary<string, int>();
                foreach (var m in t.GetMembers(f))
                {
                    if (m is MethodInfo mi && mi.IsSpecialName) continue;
                    if (m is System.Type) continue;
                    if (t.IsEnum && m.Name == "value__") continue;
                    var member = m is ConstructorInfo ? ".ctor" : m.Name;
                    counts[member] = counts.TryGetValue(member, out var c) ? c + 1 : 1;
                }
                foreach (var kv in counts)
                {
                    var row = "| `" + kv.Key + "` |";
                    var rows = 0;
                    for (var i = section.IndexOf(row, System.StringComparison.Ordinal); i >= 0; i = section.IndexOf(row, i + row.Length, System.StringComparison.Ordinal)) rows++;
                    if (rows == 0) missing.Add(name + "." + kv.Key);
                    else if (rows < kv.Value) missing.Add($"{name}.{kv.Key}（多載 {kv.Value} 個，表裡 {rows} 列）");
                }
            }
            Assert.That(missing, Is.Empty, "api.md 沒寫到：" + string.Join("、", missing) + "（請補進 Documentation~/api.md）");
        }
    }
}
