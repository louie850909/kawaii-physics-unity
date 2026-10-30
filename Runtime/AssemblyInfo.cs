// 低階的資料、工作、解算是 internal：測試與量測的組件才看得到。
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("KawaiiPhysicsUnity.Editor")]
[assembly: InternalsVisibleTo("KawaiiPhysicsUnity.Tests.Editor")]
[assembly: InternalsVisibleTo("KawaiiPhysicsUnity.Tests")]
