# Changelog

格式參考 [Keep a Changelog](https://keepachangelog.com/)，版本依 [Semantic Versioning](https://semver.org/)。

## [0.4.7] - 2026-11-08

### Fixed
- 骨鏈把碰撞體的尺寸（`KawaiiPhysicsCollider` 的半徑、膠囊長度、箱的半邊長、凸包，與簡易世界碰撞收集的形狀）當成模擬單位：
  元件（模擬空間）縮放不是 1 時，碰撞體跟著縮放（縮放 0.5 時只剩一半大，骨頭陷進去；縮放 2 時變兩倍大）。
  改成照文件寫的世界單位（公尺），每幀依各條鏈的模擬空間縮放換算。軟組織本來就是世界單位，不受影響。
- 測試：縮放 0.5／1／2 的元件靠在球、箱碰撞體上，末端停在表面外一個骨頭半徑（舊程式在 0.5、2 時失敗）。

## [0.4.6] - 2026-11-08

### Fixed
- 停用（或刪除）Chain 之後，骨頭要到下一幀動畫前才放回靜止姿勢，而且靜止姿勢永久留在快取：呼叫端在停用後改了骨頭的區域姿勢（換裝、體型），
  下一幀會被舊的靜止姿勢蓋掉；換裝後的新 Chain 用到同一根骨頭時也沿用舊的靜止姿勢。
  改成停用時當場移除那一條（骨頭立刻放回靜止姿勢，同一幀之後的解算不再寫回），並忘掉那些骨頭的靜止姿勢，下次用到時重新記。

## [0.4.5] - 2026-10-30

第五次獨立審查的中嚴重度發現修正；低嚴重度寫進手冊的「限制」。

### Fixed
- 祖先在重建時被重置（同一幀停用再啟用、換 `referenceRoot`、骨頭搬到別的元件），另一個元件的子骨被甩（25 mm）：
  這種骨頭上一幀還在模擬，第一次解算當成重置（子孫重新取基準），不再當成「新加入、上一幀自己的變化是 I」。
- 祖先只略過一幀（元件關一幀、角色根縮放 0 一幀）恢復時，孫骨被甩（62 mm）：略過的骨頭給子孫的變化改成父骨的（它照樣跟著祖先動），
  恢復那一幀「上一幀自己的變化是 I」才成立。

### Documentation
- 限制：祖先晃動傳給子孫的幾個近似（重建時換鏈、換鏈前一幀 dt＝0、祖先在晃時 alpha 一次跳、碰撞設定改變）與實測量級；縮放 0 啟用時延後記錄的重力參考。

## [0.4.4] - 2026-10-30

第四次獨立審查的發現全部修正。

### Fixed
- 子孫「重新取基準」時連祖先真的晃動也丟掉兩幀（祖先在晃時改 alpha、祖先加入或離開，子孫最多偏 14.9 mm，連沒有跳動的事件也觸發）。改成：
  - 祖先的輸出設定（alpha、傾斜、伸縮）改了、祖先剛開始模擬：子孫用「照上一幀」的變換量祖先的速度，那一段移動精確扣掉，不丟掉任何晃動；
  - 重建時祖先鏈換了（不論哪一層加入或離開）：用各祖先上兩幀自己的變化，在新的鏈下重算祖先晃動的位置與速度；
  - 只有祖先重置、略過、位移上限改了（跳動量不知道）才重新取基準。
- alpha 的跳動判定用每幀 0.05 的門檻，同一段漸變在不同幀率下結果不同：拿掉門檻（alpha 一律精確扣掉，漸變與一次跳都不給子孫慣性）。
- 祖先的 tilt、stretchAmount、位移上限一幀內改變時子孫被甩（7.5–16 mm）：前兩者精確扣掉，上限改了時重新取基準。
- 角色在縮放 0 時啟用（或 `CaptureRestPose()`），重力參考被記成單位矩陣、之後站著不動也一直偏（39 mm，0.4.2 的回歸）：等第一次能用的幀再記。
- 讀取時的退化判定改成相對門檻（某一軸剛好是 0、三軸接近共面的矩陣不模擬），避免鏡像的正負隨機、反覆重新初始化。

## [0.4.3] - 2026-10-30

第三次獨立審查的發現全部修正。

### Fixed
- 軟組織骨頭三層以上時，上層祖先離開模擬（元件停用、從清單拿掉），孫骨仍被甩一下（25 mm）：重建時重新取基準的骨頭也往下標記，子孫跟著重新取基準。
- 同一根骨頭在同一個元件的清單裡出現兩次時，衝量加倍、`IsSimulating` 回 true（0.4.2 的回歸）：重複的只算第一次。
- 祖先的 `alpha` 一幀內大幅改變（包括動畫事件 `KawaiiPhysicsSetAlpha`）時子孫被甩：變化超過 0.05 時子孫重新取基準；漸變照常有慣性。
- 執行中把角色根的縮放正負翻轉時整個軟組織被甩到上限：當成瞬移重置；骨頭自己的縮放正負翻轉時那根骨頭重新開始。
- 同一幀把骨頭從一個元件搬到另一個元件、又呼叫 `CaptureRestPose()` 時新的靜止姿勢被丟掉：原來的元件清單已經拿掉時照樣記錄。
- `Frame()`（從世界矩陣取軸向）在某一軸縮放很小時會丟掉鏡像的正負：正負改由行列式決定。
- （驗證時發現，只存在於未發佈的中間版）上一項 alpha 跳動的判定在重建時沒有沿用上一幀的 alpha，無關的元件加入或移除後第一幀會誤判、子孫丟掉兩幀祖先慣性（13.7 mm）：重建時一起沿用。

## [0.4.2] - 2026-10-30

第二次獨立審查的中嚴重度發現修正；低嚴重度的寫進手冊的「限制」。

### Fixed
- 祖先軟組織骨頭單獨重置、加入或離開模擬（例如父骨的元件晚啟用、停用、只重置父骨的元件、把父骨加進同一個元件），
  子骨把那一段跳動當成錨點加速、被甩一下（25 mm）。祖先這一幀跳了，或重建時祖先換了，子骨的祖先晃動那一份重新取基準。
- `CaptureRestPose()`、`GetOffset`、`GetVelocity`、`IsSimulating`、`AddImpulse(int, …)` 在 bones 改過、還沒重建時用了建置時的索引，
  靜止姿勢會記到別根骨頭（最多偏 163 mm）。改成用 Transform 對應。
- 負縮放（鏡像）的階層裡，重力參考、傾斜、位移上限的軸用 `Transform.rotation` 推導，跟 Unity 實際的軸不一致（站著不動偏 14 mm、傾斜方向反了）。
  軸向改成一律從世界矩陣取，寫回的區域旋轉在父骨的矩陣軸向裡算。

### Changed（第三次審查指出，原本漏寫）
- 骨頭自己是負縮放時，`limitMin`／`limitMax` 與伸縮的正負方向跟著實際的區域軸鏡像（0.4.1 以前不會）；非對稱上限時同一個設定的結果跟 0.4.1 差約 41 mm。
- 不等比縮放的祖先底下有轉動的骨頭（剪切）時，軸向與傾斜改用正交化的世界矩陣軸，結果跟 0.4.1 略有不同（例：位置差 1.2 mm、區域旋轉差 2.6°）。沒有剪切時相同。

### Documentation
- 限制：錨點在碰撞體裡時 `collisionPriority` 會把骨頭推到碰撞體表面；離原點很遠時子孫骨頭的浮點誤差；v0.4.1 起重建成本比 v0.4 高約 25–40%。

## [0.4.1] - 2026-10-30

獨立審查的發現全部修正。

### Fixed
- 軟組織在角色移動中（或動畫正在動時）重置、瞬移、啟用，下一幀會被甩一下（整個移動速度的反衝，等速 1.5 m/s 時 56 mm）。
  重置後下一個 dt＞0 的幀只記錄錨點速度，不當成加速。
- 父骨的壓扁與拉伸讓子軟組織骨頭的實際位置偏離模擬位置：子孫的錨點改用含伸縮的變換。
- 父子骨分在不同元件時，子骨感受不到父骨的晃動：有祖先關係的元件併成一個群組，依階層深度一起解。
- 祖先晃動的慣性跟著 `animationInertia` 一起縮放：改成獨立的一份，一律是真實慣性。
- `referenceRoot` 被刪掉後元件永遠停止模擬：自動重建並退回上層的 Animator 或自己。`IsSimulating` 對已刪除的骨頭回 false。
- 停用再啟用在同一幀內會沿用狀態、跨幀會重置：一律重新開始。
- `CaptureRestPose()` 對被其他元件模擬中的骨頭會改掉靜止姿勢的記錄：改成略過。
- `KawaiiPhysicsAnimationEvents` 每次事件都配置元件陣列：改成重用清單。`KPSoftBodySystem.Shutdown` 清掉建置用的暫存。
- API 參考的產生器沒有產生 `<exception>` 的型別；API 文件測試改成也檢查多載的數量。

### Added
- `KawaiiPhysicsSoftBody.collisionPriority`（預設開）：碰撞與位移上限衝突時碰撞優先（骨頭可以被推到上限外，不穿透）；關掉時上限優先。

## [0.4.0] - 2026-10-30

平移式軟組織（胸、臀）。套件只提供機制與介面，不含特定遊戲或模型的骨頭與參數對應。

### Added
- `KawaiiPhysicsSoftBody`：每根骨頭是以動畫姿勢為錨點的阻尼彈簧（解析解，任何幀率穩定）。
  頻率與阻尼比、重力（可相對靜止姿勢）、額外加速度、慣性拆成角色移動與動畫兩個倍率、骨頭區域軸向的非對稱位移上限（0＝鎖住）、
  依位移傾斜、沿軸壓扁與拉伸（可維持體積）、KawaiiPhysicsCollider 碰撞（球內外、膠囊、錐形膠囊、箱、平面）、
  同一個元件內的階層（子骨跟著祖先的位移）、alpha、子步進、瞬移判定。
- 衝量與狀態 API：`AddImpulse`（整組／指定骨頭）、`AddImpulseAtPosition`（依距離衰減）、`GetOffset`、`GetVelocity`、`IsSimulating`、
  `ResetDynamics`、`CaptureRestPose`、`MarkDirty`。
- `KPSystem.Register／Unregister(KawaiiPhysicsSoftBody)`、`KPSystem.SoftBodyCount`、`KPSystem.SoftBoneCount`。
- `KawaiiPhysicsEventPreset.Kind.SoftBodyImpulse`（`softBodyImpulse`、`softBodyNameFilter`、`Apply(KawaiiPhysicsSoftBody)`）；
  `KawaiiPhysicsAnimationEvents` 的 SetAlpha、ResetDynamics 也作用到軟組織。

### Changed
- `KPSystem.SimulatePhase` 先解軟組織再解 Chain（掛在軟組織骨頭底下的 Chain 會跟著動）；只有軟組織、沒有 Chain 時也會執行並呼叫 `AfterSimulate`。
  `LastSimulateMs` 含軟組織。
- API 參考的產生器多載的方法依參數名挑對應的說明。

## [0.3.0] - 2026-10-30

production ready 驗收的不合格項目修正。

### Changed
- Chain 加入、移除或結構變更時只重建那一條，其他 Chain 的模擬狀態原樣保留（之前會把所有角色一起重置）。
  移除的位置留作空洞給下一個放得下的 Chain 重用，空洞超過一半才整理。
- 設定（damping、stiffness、worldDampingLocation、worldDampingRotation、radius、limitAngle）改了會在下一幀就地生效，不重建、不重置。
  Inspector 的變更先比對結構簽章，結構沒變只更新設定。`KawaiiPhysicsPreset.ApplyTo` 也一樣。
- 停用或移除 Chain 時，骨頭放回靜止姿勢（之前會停在最後的物理姿勢）。
- 場景的 WindZone 改成在場景載入／卸載、Chain 變動時才收集（之前每 60 幀搜尋一次並配置記憶體）；新增 `KPSystem.RefreshWindZones()`。
- 建置範本的骨頭走訪改用 `TryGetComponent`，並去掉一處 O(n²) 的子骨計數。
- **不相容**：低階的資料結構、工作、解算（`KPBone`、`KPInstance`、碰撞形狀的 struct、`KPWorld`、`KPSolver`、`KPMath`、各 Job 等）改成 internal；
  `KPSystem.World`、`KPSystem.ScheduleSolve`、`KPExternalForce.PreApply`、`KawaiiPhysicsCollider.ConvexPlanesOf` 改成 internal。
  外力的型別只限套件提供的五種。
- package.json 宣告用到的引擎模組（Physics、Wind、Animation、JSONSerialize）；不再依賴 Terrain 模組（地形碰撞改用型別比對）。
- 外力的骨頭篩選與 `forceRateByBoneLengthRate` 改了就地生效（不重建、不重置）：Inspector 自動，用程式改要呼叫 `MarkSettingsDirty()`。
- 曲線表依內容共用：形狀一樣的曲線只放一份，角色反覆生成、重建、事件重複觸發不再讓表變大。
- **不相容**：`KPExternalForce` 不能從套件外繼承（建構子改成 internal）。
- 執行中打開或關掉 `useSimpleWorldCollision` 會自動重建那一條。

### Added
- Inspector：外力清單的元素有型別選單（換型別、清空），Chain 的 Inspector 有「新增外力」按鈕；清單按「+」複製出的重複參照自動拆開。
- `KawaiiPhysicsChain.MarkSettingsDirty()`；`KPSystem.ChainCount`、`BoneCount`、`LastSyncMs`。
- 測試（`Tests/`，Editor＋PlayMode）、使用手冊與 API 參考（`Documentation~/`）、範例（`Samples~/Basic`）、這份 CHANGELOG。
- 所有公開成員都有說明；序列化欄位加了 Tooltip。

### Fixed
- 空洞過半整理（Repack）時外力槽位的起點算錯，可能是負數或與別條鏈重疊（編輯器丟例外、Player 寫出陣列範圍）。獨立審查發現。
- 一開始停用的 WindZone 之後啟用也不會吹。
- 多選 Chain 按「新增外力」會截掉外力較多的 Chain 的清單。
- 執行中打開簡易世界碰撞（沒呼叫 MarkDirty）時，地面碰撞寫到相鄰角色的箱子碰撞。
- `PushSettingsMultiplier` 每次呼叫都配置記憶體。
- 半徑決定假骨數量時，只改 `radiusCurve` 不會重建。
- 每幀的 GC 配置：`KPForceCurve` 每幀配置一個陣列、WindZone 的定期搜尋。穩態現在是 0 B。
- 外力曲線表（LUT）的索引在世界重建後、或 Domain Reload 關閉時下一次 Play 會指到錯的位置。
- 角色縮放為 0 時產生 NaN（現在那一幀不模擬、不寫回）。
- 靜止姿勢快取一直留著已刪除的 Transform（啟用中直接刪除時在移除時清掉；先停用再刪除的，快取超過使用量兩倍時清一次）。
- Play 中重新編譯腳本時原生陣列沒有釋放。
- 編譯警告：`FindObjectsByType(FindObjectsSortMode)` 已過時。

## [0.2.0] - 2026-10-30

- 原作 runtime 功能移植完成：細分／橋接假骨、骨間約束與資產、SyncBone、三種模擬空間、五種外力、暫時外力／突風／設定倍率、
  WindZone 風、世界碰撞、簡易世界碰撞、共用碰撞、碰撞資產鏡像、預設、AnimationEvent。

## [0.1.0] - 2026-10-30

- 概念驗證：核心解算（Verlet、剛性、碰撞、角度限制、骨間約束）的 Burst 版與 golden 測試。
