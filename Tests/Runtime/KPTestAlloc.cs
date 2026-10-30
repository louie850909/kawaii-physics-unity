// 量一段程式的 GC 配置（測試用）。
// GC.GetAllocatedBytesForCurrentThread 在 Unity 的 Mono 永遠回 0，不能用。
// 優先用 Profiler 計數器「GC Allocated In Frame」的目前值（位元組精確；同一幀內前後相減）；
// 量不到小配置時先打開 Profiler 再試，還是不行就退回 Mono 堆積使用量差（4 KB 頁的粒度，只能證明「有配置」）。
using System;
using Unity.Profiling;
using UnityEngine.Profiling;

namespace KawaiiPhysicsUnity.Tests
{
public static class KPTestAlloc
{
    static ProfilerRecorder recorder;
    static bool useRecorder;

    public static string Method { get; private set; } = "未初始化";
    /// <summary>自我檢查時 object[4] 量到的位元組（0 表示量不到小配置）。</summary>
    public static long SmallProbe { get; private set; }
    public static long LargeProbe { get; private set; }
    public static bool Precise { get; private set; }

    public static void Init()
    {
        if (!recorder.Valid) recorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
        useRecorder = recorder.Valid;
        if (useRecorder && SelfTest())
        {
            Method = "Profiler 計數器「GC Allocated In Frame」的目前值";
            Precise = true;
            return;
        }
        if (useRecorder && !Profiler.enabled)
        {
            Profiler.enabled = true;
            if (SelfTest())
            {
                Method = "Profiler 計數器「GC Allocated In Frame」的目前值（打開 Profiler）";
                Precise = true;
                return;
            }
        }
        useRecorder = false;
        SelfTest();
        Method = "Mono 堆積使用量差（4 KB 頁的粒度）";
        Precise = false;
    }

    static bool SelfTest()
    {
        var a = Now();
        var small = new object[4];
        var b = Now();
        var large = new byte[4096];
        var c = Now();
        GC.KeepAlive(small);
        GC.KeepAlive(large);
        SmallProbe = b - a;
        LargeProbe = c - b;
        return SmallProbe > 0 && LargeProbe >= 4096;
    }

    public static long Now() => useRecorder ? recorder.CurrentValue : Profiler.GetMonoUsedSizeLong();

    public static string Describe() => $"{Method}；自我檢查 object[4]={SmallProbe} B、byte[4096]={LargeProbe} B";
}
}
