# KawaiiPhysics for Unity

[KawaiiPhysics](https://github.com/pafuhana1213/KawaiiPhysics)（Unreal Engine 的骨頭偽物理，pafuhana1213，MIT）移植到 Unity 6.6：頭髮、裙子、飾品沿著骨頭鏈晃動。
胸、臀這類可以平移的軟組織用 `KawaiiPhysicsSoftBody`（阻尼彈簧、位移上限、慣性拆分、壓扁與拉伸、衝量 API）。
Burst＋Job System 平行處理所有角色，穩態每幀 0 GC 配置。

A Unity 6.6 port of [KawaiiPhysics](https://github.com/pafuhana1213/KawaiiPhysics) by pafuhana1213 — bone-chain pseudo-physics for hair, skirts
and accessories, plus a translational soft-tissue component for breasts and hips. Burst + Job System, zero GC allocations per frame in steady state.
This is an unofficial port and is not affiliated with the original author. Documentation is in Traditional Chinese.

## 安裝 / Install

Unity 6000.6 以上。Package Manager →「+」→「Add package from git URL…」：

```
https://github.com/louie850909/kawaii-physics-unity.git
```

指定版本：網址後面加 `#v0.5.0`。

## 文件 / Docs

- 使用手冊：[Documentation~/index.md](Documentation~/index.md)（安裝、快速開始、執行時 API、軟組織、效能、平台、限制、測試）
- API 參考：[Documentation~/api.md](Documentation~/api.md)
- 變更：[CHANGELOG.md](CHANGELOG.md)
- 範例：Package Manager → KawaiiPhysics for Unity → Samples → Basic

## 授權 / License

MIT — 見 [LICENSE.md](LICENSE.md)。

本套件移植自 KawaiiPhysics（Copyright (c) 2019-2026 pafuhana1213，MIT License）。
移植的演算法與從原作取得的測試資料沿用原作的授權；原作的著作權與授權聲明完整收在 [Third Party Notices.md](Third%20Party%20Notices.md)，散布時請一併附上。

This package is derived from KawaiiPhysics, Copyright (c) 2019-2026 pafuhana1213, released under the MIT License.
The original copyright and permission notice is reproduced in [Third Party Notices.md](Third%20Party%20Notices.md) and must be kept with any copy of this package.
