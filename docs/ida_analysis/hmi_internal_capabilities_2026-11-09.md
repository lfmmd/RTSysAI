# RTSys HMI 内部能力接口报告（进程内对象模型）

**日期**：2026-11-09  
**目标**：为 AI 插件提供 RTSys 软件**内部** HMI 编辑/读取/修改能力接口（不做 `.hmi` 文件读写）  
**证据**：IDA Pro 反编译（ExtMainHmi.dll / ExtHmiControl_W.dll / RTSys.exe / zmotion.dll）  
**IDA 实例**：RTSys.exe:13337、zmotion.dll:13338、ExtMainHmi.dll:13339、ExtHmiControl_W.dll:13340

---

## 1. 结论

RTSys 的 HMI 能力是 **ExtMainHmi.dll 中的 C++ 对象模型**，RTSys.exe 通过导入表调用约 190 个 `ZHmi_*` 导出符号。插件（`PlugInAdapter`）运行在 RTSys.exe 进程内，可直接：

1. 用 `GetModuleHandle("ExtMainHmi.dll")` + `GetProcAddress(符号名)` 取得导出函数（**无需硬编码 RVA**）；
2. 用 `CallingConvention.ThisCall` 委托调用 `ZHmi_*Class` 成员方法（`this` 为对象指针）；
3. 在宿主 UI 线程执行（MFC/GDI+ 对象非线程安全），模式与现有 `RTSysProjectModel.UiInvoke` 一致；
4. 通过文档对象或“当前编辑对象”取得 `ZHmi_FileClass*`，进而遍历/修改窗口、控件、特征。

**不需要、也不应该**通过 `.hmi` 文件字节读写来实现编辑。

---

## 2. 对象获取入口（关键）

### 2.1 当前编辑对象 → 所属文件

```
ZHmi_BaseObject* obj = ZHMI_GetEditObject();   // ExtMainHmi 导出，返回最近选中的对象
ZHmi_FileClass*  file = *(ZHmi_FileClass**)((char*)obj + 0x10);  // 所有 BaseObject 的 +0x10 都回指所属 FileClass
```

- `ZHMI_SetEditObject(obj)` 由 RTSys.exe 在设计师选择变化时调用（`sub_4AF650`），`ZHMI_GetEditObject()` 读取全局 `dword_1005DC2C`（ExtMainHmi RVA 0x5DC2C）。
- 优点：纯导出，无版本 RVA 依赖。缺点：**依赖用户当前有选中对象**；无选中时为空或过期。

### 2.2 HMI 文档对象（最可靠）

```
ZHmiDoc*       doc  = <当前活动 MDI 文档>;
ZHmi_FileClass* file = *(ZHmi_FileClass**)((char*)doc + 0x290);  // +656
```

已由两条路径交叉确认：
- `RTSys.exe sub_58C7D0`：打开文件时 `doc = sub_584050(entry)`，随后 `sub_58CBF0(idx, *(ZHmi_FileClass**)(doc + 656), ...)`；
- `RTSys.exe sub_4D3F60`：导入窗口时对 `wParam + 656` 调用 `ZHmi_FileClass::DelWindow/AddWindow`。

取得 `ZHmiDoc*` 的方式（按稳健性排序）：
1. MFC 文档管理器：`AfxGetModuleState()->m_pDocManager` → `GetFirstDocPosition/GetNextDoc` → 匹配文档路径 `.hmi`（`sub_584050` 即此逻辑，RVA 0x184050，需版本签名门控）；
2. 宿主工程文件表：全局 `dword_8268AC`（RVA 0x4268AC），条目 76 字节，`[0]=路径字符串`；`sub_584050(entry)` → doc；
3. 现有 `RTSysProjectModel` 已定位的 `CFileView`，再经 MFC 当前视图/文档关系取 doc（需补充偏移校验）。

> 建议实现：优先 2.2，失败回退 2.1；两者都要求 **UI 线程** 且对象有效。

---

## 3. 对象模型与内存布局（x86, 32 位）

### 3.1 ZHmi_FileClass（文件/画面集，`operator new(0x54)`）

| 偏移 | 字段 | 说明 |
|------|------|------|
| +0x00 | vtable | 含 `Save`(slot4,+16) / `Read`(slot5,+20) / `Draw` 等 |
| +0x10 | ? | 预留/库指针 |
| +0x20 | `ZHmi_WindowClass* first` | 窗口双向循环链表头（`this+8 dword`） |
| +0x4C | `ZHmi_WindowClass* last` | 尾窗口（`this+19 dword`） |
| +0x0C | 文件级特征链表 | 由 `FindFeature` 走 `this+6 dword`（BaseObject 布局） |

窗口遍历：`w = file->first; do { ...; next = *(void**)((char*)w+0x0C); w = next ? (next-4) : 0; } while (w != file->first);`
（`-4` 是因为链表节点是对象内嵌的 `Struct_ZHMI_Item`，位于对象 +4）

### 3.2 ZHmi_WindowClass / ZHmi_ControlClass（均继承 ZHmi_BaseObject）

| 偏移 | 字段 | 说明 |
|------|------|------|
| +0x00 | vtable | — |
| +0x0C | next node | 循环链表下一个节点的指针（节点在 +4） |
| +0x10 | `ZHmi_FileClass* owner` | 所属文件（AddWindow 设置） |
| +0x18 | `ZHmi_FeatureClass* features` | 特征循环链表头 |
| Window +0x1C | `ZHmi_ControlClass* firstControl` | 控件链表头 |
| Control | 自身 0x?? | 含子控件集合等 |

控件遍历（窗口内）：同窗口遍历，头为 `*(void**)((char*)win+0x1C)`，next 在 `+0x0C`。

### 3.3 ZHmi_FeatureClass（0x38 字节）

| 偏移 | 字段 | 说明 |
|------|------|------|
| +0x00 | `int mainId` | 特征主 ID（如 1000/1001/2000/20000…） |
| +0x11 | `byte subId` | 子状态/子索引（0..255） |
| +0x1C | `CArray* array` | 数组特征元素表 |
| +0x20 | `int value` | 整数值（GetFeatureInt/SetFeatureInt 读写） |
| +0x28 | `double value` | 浮点值（1002/1003 同时同步此字段） |
| +0x30 | `CString* string` | 宽字符串对象指针（GetFeatureCString/SetString） |

特征寻址统一为三元组：**`(mainId, subId, index)`**，`index=0` 表示特征本身，`>0` 表示数组元素（`ZHMI_FindFeatureinList` / `FindFeature`）。

### 3.4 Struct_ZHMI_Item（链表节点，位于对象 +4）

`ZHmi_ItemFindNext(item)` 返回 `item + 0x08`（即下一个节点的指针）。`ZHmi_ItemGetListNum`、`ZHmi_GetItemType`、`ZHmi_ItemSeparate` 用于通用遍历/拆分。

### 3.5 控件几何与属性字段（已逐条确认）

几何**不是**对象头里的独立字段，而是普通特征：

| 含义 | 特征 | 子索引 |
|------|------|--------|
| 控件编号 | 1000 | 0（`GetControlId`/`SetControlId` 即读写它） |
| 宽度 | 1002 | subId = 0 |
| 高度 | 1002 | subId = 1 |
| X 坐标 | 1003 | subId = 0 |
| Y 坐标 | 1003 | subId = 1 |
| 名称 | 1001 | 0 |
| 显示文本 | 2000 / 2001 | 0 |
| 窗口编号 | 20001 | 0 |
| 窗口名称 | 20003 | 0 |

证据：
- `ZHmi_ControlClass::GetSizeRect`（0x1001b6a0）用 `1003[0]/1003[1]` 作 left/top、`1002[0]/1002[1]` 作 width/height，并叠加 1282/1284（边框/分组偏移）；
- `ZHmi_WindowClass::NewAndAddControl`（0x100244c0）签名 `(Window*, id, type, x, y, w, h)`，内部依次 `SetFeatureInt(1000,0,0,id)`、`(1002,0,0,w)`、`(1002,1,0,h)`、`(1003,0,0,x)`、`(1003,1,0,y)`。

**几何写入的额外守卫（`ZHmi_ControlClass::SetFeatureInt` 0x1001d3e0）**：
- 若特征 `1270` 非 0，则直接返回 0、**不修改** 1002/1003（相当于位置锁定）；需要编辑时先把 1270 置 0（若该控件允许）；
- 1003 的负值会被钳为 0；1002 负值钳为 0；
- 1002/1003 同时写入特征 +0x28 的 double 值；
- 修改 1002（以及 1080/1010/1517/1030）后自动置 1081 = 1（标记需重绘/重生成图片）。

### 3.6 属性枚举与可读名称

- 遍历对象特征链表（BaseObject +0x18，节点 next 在 +0x08）可得该控件**全部**特征 `(mainId, subId)`；
- `ZHmi_FeatureClass::GetShowName()`（0x10005100）通过特征描述符（feature +0x34）返回本地化显示名；无描述符时返回 `?<mainId>`，subId 非 0 时追加 `(<subId>)`；
- `ZHmi_FeatureClass::GetShowComment()`（0x100052d0）返回该属性的说明文本。
- 因此可以做成通用 `hmi_list_features`：枚举 → 读值 → 附带显示名/说明，无需为每种控件硬编码属性表。

---

## 4. 完整内部 API 清单（按能力分组）

> 下表符号名为 ExtMainHmi.dll 的 **mangled 导出名**，可原样传给 `GetProcAddress`。  
> `ThisCall` 表示 C# 用 `[UnmanagedFunctionPointer(CallingConvention.ThisCall)]`，首个参数为 `this`。

### 4.1 文件/画面集（ZHmi_FileClass）

| 能力 | 导出符号 | 签名/语义 |
|------|----------|-----------|
| 创建/销毁 | `??0ZHmi_FileClass@@QAE@XZ` / `??1ZHmi_FileClass@@QAE@XZ` | `new(0x54)` 后构造；`operator delete` |
| 清空 | `?ClearAll@ZHmi_FileClass@@QAEXXZ` | 清空所有窗口 |
| 枚举窗口 | 读 `*(+0x20)`，按 `+0x0C` 遍历 | 见 §3.1 |
| 按编号查找窗口 | `?FindWindowW@ZHmi_FileClass@@QAEPAVZHmi_WindowClass@@H@Z` | `(int windowNo)` → Window*（比较特征 20001） |
| 前一个窗口 | `?FindBeforeWindow@ZHmi_FileClass@@QAEPAVZHmi_WindowClass@@H@Z` | `(int windowNo)` → Window* |
| 添加窗口 | `?AddWindow@ZHmi_FileClass@@QAEXPAVZHmi_WindowClass@@@Z` | `(Window*)`，设置 owner 并挂链 |
| 删除窗口（按对象） | `?DelWindow@ZHmi_FileClass@@QAEIPAVStruct_ZHMI_Item@@@Z` | `(Item*)` |
| 删除窗口（按编号） | `?DelWindow@ZHmi_FileClass@@QAEPAVZHmi_WindowClass@@H@Z` | `(int windowNo)` → 被摘链对象 |
| 取合法 ID | `?GetValidId@ZHmi_FileClass@@QAEHH@Z` | `(int seed)` → 未占用 ID |
| 新建普通窗口 | `?NewAndAddNopWindow@ZHmi_FileClass@@QAEPAVZHmi_WindowClass@@H@Z` | `(int id)` → 新窗口 |
| 新建数字键盘窗口 | `?NewAndAddNumKeyWindow@ZHmi_FileClass@@QAEPAVZHmi_WindowClass@@H@Z` | `(int id)` |
| 新建字符键盘窗口 | `?NewAndAddStrKeyWindow@ZHmi_FileClass@@QAEPAVZHmi_WindowClass@@H@Z` | `(int id)` |
| 新建大字符键盘窗口 | `?NewAndAddStrKeyWindowLarge@ZHmi_FileClass@@QAEPAVZHmi_WindowClass@@H@Z` | `(int id)` |
| 导入窗口 | `?ImportWin@ZHmi_FileClass@@QAEIPBD@Z` | `(const char* path)`（宿主文件导入，慎用） |
| 改分辨率 | `?ModifyWinsAndCons@ZHmi_FileClass@@QAEIHHUstruct_ModifyResolution@@@Z` | `(int,int,ModifyResolution)` |
| 改字号 | `?ModifyTextsize@ZHmi_FileClass@@QAEIN@Z` | `(int size)` |
| 设备 | `?AddDevice@ZHmi_FileClass@@QAEXPAVZHmi_DeviceClass@@@Z` / `?DelDevice@...QAEIPAVStruct_ZHMI_Item@@@Z` | 设备表 |

### 4.2 窗口/控件（ZHmi_WindowClass / ZHmi_ControlClass）

| 能力 | 导出符号 | 签名/语义 |
|------|----------|-----------|
| 枚举控件 | 读 `*(win+0x1C)`，按 `+0x0C` 遍历 | 见 §3.2 |
| 按 ID 查找控件 | `?FindControl@ZHmi_WindowClass@@QAEPAVZHmi_ControlClass@@H@Z` | `(int controlId)`（比较特征 1000） |
| 添加控件 | `?AddControl@ZHmi_WindowClass@@QAEXPAVZHmi_ControlClass@@@Z` | `(Control*)`，设置 owner 并挂链 |
| 删除控件（按对象） | `?DelControl@ZHmi_WindowClass@@QAEIPAVStruct_ZHMI_Item@@@Z` | `(Item*)` |
| 删除控件（按 ID） | `?DelControl@ZHmi_WindowClass@@QAEPAVZHmi_ControlClass@@H@Z` | `(int id)` → 被摘链对象 |
| 新建控件（通用） | `?NewAndAddControl@ZHmi_WindowClass@@QAEPAVZHmi_ControlClass@@HHHHHH@Z` | `(type,id,x,y,w,h?)` 以 IDA 原型为准 |
| 新建按钮 | `?NewAndAddButton@ZHmi_WindowClass@@QAEPAVZHmi_ControlClass@@HHHHHEPBD@Z` | `(type,id,x,y,w,h,flags,text)` |
| 控件 ID | `?GetControlId@ZHmi_ControlClass@@QAEHXZ` / `?SetControlId@ZHmi_ControlClass@@QAEIH@Z` | 等价 `Get/SetFeatureInt(1000,0,0,id)` |
| 控件矩形 | `?GetSizeRect@ZHmi_ControlClass@@QAEPAUD2D_RECT_F@@XZ` | 由特征 1002/1003/1282/1284 计算，返回 0x80 字节矩形数组 |
| 窗口编号 | `?GetWindId@ZHmi_WindowClass@@QAEHXZ` / `?SetWindId@ZHmi_WindowClass@@QAEXH@Z` | 特征 20001 |
| 命中测试 | `?HitTest@ZHmi_ControlClass@@QAEHHH@Z` | `(x,y)` |
| 选择 | `?SelectAll@@QAEXXZ` / `?ClearSelect@@QAEXXZ` / `?SelectRange@@QAEXHHHH_N@Z` / `?SelectPoint@@QAE_NHH_N@Z` / `?MoveSelect@@QAEXHH@Z` / `?GetSelectRange@@QAEXAAVCRect@@@Z` | 设计师选择状态 |
| 子控件 | `?AddSubControl@@UAE_NPAV1@@Z` / `?UnloadSubControl@@UAE_NH@Z` / `?LoadSubControl@@UAE_NH@Z` / `?GetSubControl@@UAE?AVset@H...@@_N@Z` / `?ModifyControlSubId@@UAEXHH@Z` / `?IfGroupControl@@UAE_NXZ` | 组合控件/子控件 |
| 刷新 | `?ReflashSelList@@QAEXXZ` / `?CheckGroupControlSubId@@QAEXHH@Z` | 选择/分组刷新 |
| 位图 | `?GetWinBmp@ZHmi_WindowClass@@QAEPAUHBITMAP__@@HHHH@Z` | 截取窗口位图 |

### 4.3 特征读写（ZHmi_BaseObject / ZHmi_FeatureClass）

| 能力 | 导出符号 | 签名/语义 |
|------|----------|-----------|
| 查找特征 | `?FindFeature@ZHmi_BaseObject@@QAEPAVZHmi_FeatureClass@@HHH@Z` | `(mainId,subId,index)` → Feature* |
| 取整数值 | `?GetFeatureInt@ZHmi_BaseObject@@UAEHHHH@Z` | `(mainId,subId,index)` → int |
| 取浮点值 | `?GetFeatureDouble@ZHmi_BaseObject@@UAENHHH@Z` | → double |
| 取宽字符串 | `?GetFeatureCString@ZHmi_BaseObject@@UAE?AVCString@@HHH@Z` | 返回 CString **按值**（ABI 需隐藏返回值参数，建议走 Feature->GetString） |
| 取字符串指针 | `?GetFeatureString@ZHmi_BaseObject@@UAEPAVCString@@HHH@Z` | 返回 CString*（更易调用） |
| 取有效标志 | `?GetFeatureValid@ZHmi_BaseObject@@UAE_NHHH@Z` | bool |
| 写整数 | `?SetFeatureInt@ZHmi_BaseObject@@UAEIHHHH@Z` | `(mainId,subId,index,value)`；特征不存在返回 20002；1002/1003 同时同步 double |
| 写浮点 | `?SetFeatureDouble@ZHmi_BaseObject@@UAEIHHHN@Z` | `(...,double)` |
| 写字符串 | `?SetFeatureString@ZHmi_BaseObject@@UAEIHHHVCString@@@Z` | CString 按值（ABI 复杂）；推荐 `FindFeature`+`ZHmi_FeatureClass::SetString(const char*)` |
| 写有效标志 | `?SetFeatureValid@ZHmi_BaseObject@@UAEIHHH_N@Z` | bool |
| 新增特征 | `?NewAndAddFeature@ZHmi_BaseObject@@QAEPAVZHmi_FeatureClass@@HH_NH@Z` | `(mainId,subId,isArray,arrayCount)` → 新 Feature* |
| 新增无效特征 | `?NewAndAddInvalidFeature@...` | 同上，标记无效 |
| 追加特征 | `?AddFeature@ZHmi_BaseObject@@QAEIPAVZHmi_FeatureClass@@@Z` | 挂到特征链表 |
| 替换特征表 | `?ReplaceFeatureList@ZHmi_BaseObject@@QAEIPAV1@@Z` | 用另一对象的特征表替换 |
| 刷新特征 | `?ReflashFeatureList@ZHmi_BaseObject@@UAEHXZ` / `?ReflashFeatureAction@ZHmi_BaseObject@@UAEHPAVZHmi_FeatureClass@@@Z` | 修改后必须调用 |
| 校验 | `?CheckFeatureValid@ZHmi_BaseObject@@UAEHHHH_N@Z` | 有效值检查 |
| 默认格式 | `?InitFormatData@@UAEIH@Z` / `?SaveDefaultFormat@@UAEIH@Z` | 格式化文本默认值 |
| 存盘钩子 | `?DoBeforeSave@ZHmi_BaseObject@@UAEIPAVZHmi_FileClass@@@Z` | 宿主保存前整理（宿主调用，插件不调用） |

### 4.4 特征值对象（ZHmi_FeatureClass）

| 能力 | 导出符号 | 签名/语义 |
|------|----------|-----------|
| 写整型 | `?SetInt@ZHmi_FeatureClass@@QAEXH@Z` | `feature->value(+0x20)=v` |
| 写浮点 | `?SetDouble@ZHmi_FeatureClass@@QAEXN@Z` | `feature->dbl(+0x28)=v` |
| 写字符串（ANSI） | `?SetString@ZHmi_FeatureClass@@QAEXPBD@Z` | `const char*`，内部转宽字符 CString（+0x30） |
| 写字符串（CString） | `?SetString@ZHmi_FeatureClass@@QAEXAAVCString@@@Z` | 宽 CString 引用 |
| 读字符串 | `?GetString@ZHmi_FeatureClass@@QAE?AVCString@@XZ` | 按值返回（ABI 复杂） |
| 有效/常量 | `?SetValid@@QAEX_N@Z` / `?SetConst@@QAEX_N@Z` | 标志位 |
| 数组 | `?SetArray@@QAEH_NH@Z` / `?GetArrayMainFeature@@QAEPAV1@XZ` / `?GetIndexItem@@QAEPAV1@H@Z` | 数组特征 |
| 显示名 | `?GetShowName@@QAE?AVCString@@XZ` / `?GetShowComment@@QAE?AVCString@@XZ` | 属性面板文案 |

### 4.5 通用 Item 链表

| 能力 | 导出符号 |
|------|----------|
| 新建/删除 | `?ZHmi_NewItem@@YAPAVStruct_ZHMI_Item@@HPAVZHmi_FileClass@@@Z`、`?ZHmi_DeleteItem@@YAIPAVStruct_ZHMI_Item@@@Z` |
| 插入 | `?ZHmi_ItemInsertBefore@@YAIPAVStruct_ZHMI_Item@@0@Z`、`?ZHmi_ItemInsertAfter@@YAIPAVStruct_ZHMI_Item@@0@Z` |
| 遍历 | `?ZHmi_ItemFindNext@@YAPAVStruct_ZHMI_Item@@PAV1@@Z`、`?ZHmi_ItemFindPrev@@...`、`?ZHmi_ItemGetListNum@@YAHPAVStruct_ZHMI_Item@@@Z` |
| 类型/拆分 | `?ZHmi_GetItemType@@YAHPAVStruct_ZHMI_Item@@@Z`、`?ZHmi_ItemSeparate@@YAIPAVStruct_ZHMI_Item@@@Z`、`?ZHmi_File_GetItemType@@YAIPAUstr_ZString@@@Z` |
| 跳转 | `?ZHmi_File_JumpItem/Object/Feature@@...` |

### 4.6 全局/编辑器状态

| 能力 | 导出符号 |
|------|----------|
| 当前编辑对象 | `?ZHMI_GetEditObject@@YAPAVZHmi_BaseObject@@XZ` / `?ZHMI_SetEditObject@@YAHPAVZHmi_BaseObject@@@Z` |
| 文件信息数组 | `?ZHMI_GetFileInfo@@YAHPAPAVCStringArray@@0PAPAV?$CArray@HH@@@Z` / `?ZHMI_SetFileInfo@@...` |
| 默认文本格式 | `?ZHMI_GetDefTextFormat@@YAAAPAVZHmi_BaseObject@@XZ` / `?ZHMI_SetDefTextFormat@@YAHAAPAVZHmi_BaseObject@@@Z` |
| 新特征类型 | `?ZHMI_GetNewFeatureType@@YAHXZ` |
| 当前控制 | `?ZHMI_GetControl@@YAHXZ` |
| 语言/状态 | `?ZHMI_GetCurLang@@YAAAHXZ` / `?ZHMI_SetCurLang@@YAHH@Z`、`?ZHMI_GetCurState@@YAAAHXZ` / `?ZHMI_SetCurState@@YAHH@Z` |
| DPI | `?ZHMI_GetDpi@@YAAAMXZ` / `?ZHMI_SetDpi@@YAHM@Z` |
| 主题色 | `?ZHMI_GetMainThemeColor@@YAHPAPAK00@Z` / `?ZHMI_SetMainThemeColor@@YAHKKK@Z` |
| 显示开关 | `?ZHMI_GetShowInfo@@YAHPAPA_N00@Z` / `?ZHMI_SetShowInfo@@YAH_N0PA_N@Z` |
| 定义文本格式开关 | `?ZHMI_GetUseDefTextFormat@@YAHPAPA_N@Z` / `?ZHMI_SetUseDefTextFormat@@YAHPA_N@Z` |
| 系统图片库 | `?ZHMI_GetSysPiclib@@YAAAPAVZHmi_PicLibClass@@XZ` / `?ZHMI_SetSysPiclib@@YAHAAPAVZHmi_PicLibClass@@@Z` |
| 语言管理器 | `?ZHMI_GetMainLanguageManager@@YAAAVZLanguageManager@@XZ` / `?ZHMI_SetMainLanguageManager@@YAHAAV...@Z` |
| 版本信息 | `?ZHMI_GetReadHmiVersion@@YAAAHXZ`、`?ZHMI_GetSoftVerInt@@YAAAHXZ` / `?ZHMI_SetSoftVerInt@@YAHH@Z`、`?ZHMI_GetCompVerison@@YAAAHXZ` / `?ZHMI_SetCompVerison@@YAHH@Z` |
| 字体缓冲 | `?ZHMI_ReleaseTtfbuf@@YAHXZ` |

### 4.7 扩展注册（自定义控件/特征/属性）

| 能力 | 导出符号 | 说明 |
|------|----------|------|
| 注册控件 | `?ZHMI_RegisterControl@@YAHUstruct_RegControl@@@Z` | 传入 `struct_RegControl`（类型 ID、名称、创建/复制/删除回调、属性表等）；ExtHmiControl_W.dll 在静态初始化时调用 18 次 |
| 注册特征 | `?ZHMI_RegisterFeature@@YAHUstr_FeatureItemDisp@@V?$function@...@@@Z` | 属性面板特征项 + 回调 `void(BaseObject*, Feature*, CBCGPProp*)` |
| 注册属性变更 | `?ZHMI_RegisterPropChanged@@YAHV?$function@...@@@Z` | 回调 `long(CWnd*, CPtrArray&, CBCGPProp*)` |

---

## 5. ExtHmiControl_W.dll 已注册的自定义控件（类型 ID → 名称）

来源：`ExtHmiControl_W.dll sub_1002A850` 中 18 次 `ZHMI_RegisterControl` 调用。

| 类型 | 名称 | 类型 | 名称 |
|------|------|------|------|
| 622 | Pie Chart | 623 | Bar Chart |
| 624 | Meter | 625 | XY Chart |
| 626 | Trend Chart | 627 | Data Group Show |
| 635 | Event Show | 636 | Alarm Show |
| 637 | Alarm Bar | 719 | Progress Bar |
| 720 | Dynamic Scale | 722 | Scheduling |
| 724 | Bar Code | 725 | Radio Button |
| 726 | Check Box | 727 | Message Out |
| 728 | LayDate | 736 | Basic Extend |

> 现有 `HmiFile.cs` 的 `ControlTypes` **不包含**以上类型（会显示为 `Type622` 等）；内部 API 方案不存在该问题，因为类型由控件对象的 vtable/注册表决定。

---

## 6. AI 工具 → 内部 API 映射（建议）

所有工具均在 UI 线程执行；写操作复用 `WriteTools` 确认/PlanMode 机制。

| AI 工具 | 内部调用序列 |
|---------|--------------|
| `hmi_list_windows` | 取 `file` → 遍历 `file+0x20` 链表 → 每个窗口 `GetFeatureInt(20001,0,0)` 编号、`(20003,0,0)` 名称、`(1002/1003/…)` 几何 |
| `hmi_read_window` | `FindWindowW(no)` → 遍历 `win+0x1C` → 每个控件 `GetControlId()`、`GetFeatureInt(1001?)` 名称、特征值（按 `hmi_feature_table.json` 映射） |
| `hmi_get_feature` | `FindFeature(obj, mainId, subId, index)` → 按类型读 `+0x20/+0x28/+0x30` |
| `hmi_set_feature` | `FindFeature` → `ZHmi_FeatureClass::SetInt/SetDouble/SetString` → `ReflashFeatureList` / `ReflashFeatureAction` |
| `hmi_set_geometry` | `SetFeatureInt(1002,...)` / `(1003,...)`（内部同步 double）或 `SetFeatureInt` 写 x/y/w/h |
| `hmi_rename_object` | `SetFeatureString(1001,...)`（名称） |
| `hmi_add_control` | `NewAndAddControl(type,id,x,y,w,h)` 或 `NewAndAddButton(...)` → `AddControl` |
| `hmi_delete_control` | `DelControl(window, id)` → `ZHmi_DeleteItem(control+4)` 释放 |
| `hmi_add_window` | `GetValidId(seed)` → `NewAndAddNopWindow(id)` → `AddWindow` |
| `hmi_delete_window` | `DelWindow(file, no)` → `ZHmi_DeleteItem(win+4)` |
| `hmi_select_object` | `ZHMI_SetEditObject(obj)` + `SetSelect`，触发设计师刷新 |
| `hmi_set_visible/valid` | `SetFeatureValid(...)` 或特征 1004/1021/1024 |
| `hmi_set_text` | 特征 2000/2001 → `SetString` |
| `hmi_set_binding` | 特征 1011-1014/1019/20040/20041/20050（设备/变量绑定） |

> 持久化：不暴露文件 API。用户保存由宿主正常“保存”命令完成（可选：插件发送 `WM_COMMAND / ID_FILE_SAVE`，与 `RTSysEditor` 现有做法一致）。

---

## 7. 调用约束与安全

1. **进程内 + UI 线程**：所有 `ZHmi_*` 对象属于宿主 UI 线程；必须在主窗口线程执行（复用 `RTSysProjectModel.UiInvoke` 的 `SendMessageTimeout` 封送）。
2. **导出名调用**：`GetModuleHandle("ExtMainHmi.dll")` + `GetProcAddress(mangled)`；`ExtHmiControl_W.dll` 仅用于自定义控件类型，AI 编辑不需要直接调用它。
3. **ThisCall ABI**：成员方法首参为 `this`；返回 CString **按值**的方法（`GetFeatureCString`、`GetString`、`GetShowName`）ABI 复杂，优先使用返回 `CString*` 的 `GetFeatureString` 或 `Feature->GetString` 的指针形式；字符串写入优先用 `SetString(const char*)`（ANSI）。
4. **对象生命周期**：`DelWindow`/`DelControl` 只摘链并返回对象指针，**不释放内存**；调用方需 `ZHmi_DeleteItem(obj+4)` 或 `operator delete`。
5. **修改后刷新**：写入特征后调用 `ReflashFeatureList`/`ReflashFeatureAction`；结构性变更（增删窗口/控件）后由宿主文档模型刷新（可触发 `WM_COMMAND` 或 `ZHMI_SetEditObject`）。
6. **版本门控**：2.2 的文档偏移（+0x290）、文档管理器枚举、`dword_8268AC` 均需校验 RTSys.exe SHA-256（现有 `RTSysProjectModel.ExpectedExeHash` 机制）与函数序言；不匹配时降级到 2.1（`ZHMI_GetEditObject`）。
7. **回退**：若 `ZHMI_GetEditObject()` 为空且文档定位失败，返回结构化错误（“请在 HMI 设计器中选中一个对象或打开 .hmi 画面”），不做盲写。
8. **只读优先**：`hmi_list_*`/`hmi_get_*` 不修改对象；写工具进入 `WriteTools`，PlanMode 下阻断。

---

## 8. 与“文件方案”的关系

| 维度 | 文件方案（已弃用） | 内部对象模型（本报告） |
|------|--------------------|------------------------|
| 操作对象 | `.hmi` 字节流 | 宿主内存中的 `ZHmi_FileClass/WindowClass/ControlClass/FeatureClass` |
| 读写一致性 | 需完整重编码，未知特征有丢失风险 | 宿主原生结构，零丢失 |
| 界面刷新 | 需重新加载文件 | 宿主设计师实时刷新 |
| 依赖 | 文件路径/编码/格式 | ExtMainHmi.dll 导出 + UI 线程 + 版本门控 |
| 适用 | 离线批处理 | **AI 在线编辑（本需求）** |

---

## 8.5 第二轮深挖：补充内部能力面

### 8.5.1 控件类型注册表（内置 + 自定义）

**内置（RTSys.exe `sub_4EE220`，47 条）**：每条注册记录（168 字节）包含类型 ID、名称（CString）、两个分类索引 `n2/n2_1`、创建/复制/删除回调（3 个函数指针）和属性表构建器。已确认的分类索引示例：

| 类型 | 名称 | 分类索引 n2,n2_1 |
|------|------|------------------|
| 700 | Line/Polyline/Polygon | 0,0 |
| 702 | Rect | 1,1 |
| 706 | Bezier | 2,2 |
| 701 | Ellipse/Arc/Sector | 3,3 |
| 712 | Scale | 4,4 |
| 645 | Table | 5,5 |
| 707 / 705/708/709/710/711 | Import / 子类型 | 6,6 / 继承 |
| 703 | StaticText | 0,0 |
| 704 | Picture | 1,1 |
| 600 | BitState | 2,2 |
| 601 | WordState | 3,3 |
| 616 | Animation | 11,4 |
| 723/630/631 | FlowBlock | 12,5 / 继承 |
| 602/603/604/605 | BitModify/WordModify/BitSwitch/WordSwitch | 0,0 / 1,1 / 2,2 / 3,3 |
| 609/608/610 | Button/KeyButton | 4,4 / 5,5 / 继承 |
| 612/611/713 | Value show/String show | 8,6 / 9,7 / 继承 |
| 607/717/714/900 | Slip/Report view/File3Edit | 10,8 / 11,9 / 12,10 / 继承 |
| 715 | CAD | 5,0 |
| 606/730/721/716 | List/Menu/Tree/File browser | 0,0 / 1,1 / 2,2 / 3,3 |
| 733/731/734 | Group box/Tab control/Panel | 4,7 / 5,6 / 5,8 |
| 621/620/640/641 | Timer | 5,4 / 继承 |
| 901 | Custom | 6,5 |

**自定义（ExtHmiControl_W.dll `sub_1002A850`，18 条）**：见 §5。注册时同时提供创建/复制/删除 lambda 与属性回调。  
另有 `ExtHmiControl_W.dll sub_1002C100` 注册 **732 Spin Button**、**735 Recipe View**。运行时实测共 **67 类**（见 §8.6）。

> 运行时可通过 `ZHmi_NewItem(type, file)` / `Window.NewAndAddControl` 直接按类型 ID 创建；无需自行调用注册回调。

### 8.5.2 特征编辑器类型码（静态注册表）

对 188 条静态特征记录的第三字段（编辑器类型）统计：

| 类型码 | 数量 | 典型特征 |
|--------|------|----------|
| 0 | 8 | 1019/20050 设备、1016 释放动作、1122 表格外框色、6300 垂直滚动条、1033 矢量图、1006 标签样式、20026 默认 BASIC |
| 1 | 49 | 20030 LCD 屏号、20001 窗口号、20003 窗口名、20015/20016 拖动/缩放、1000 对象号、1001 名称、1004 显示、1024 位显示、1070 软键盘窗口、1074 定时器周期、1011-1014 设备 |
| 2 | 86 | 20013 标题栏、1090 多行、1251 行号、1100-1129 线条/表格/对齐、1140-1154 滑块、1200/1427 滚动条、1517 透明度、1029 背景色、1034 边框、1009/1010 图片库、20010-20012 公共窗口、1320-1351 文件浏览器/行列 |
| 3 | 6 | 1005 状态数、2000 标签样式、1538 标签页数、1539 标签文本、1327 单元格编辑、4000 列表状态 |
| 4 | 9 | 1050-1054 按键动作、1078/1079 按下/释放函数、1205 修改完成函数、1352 双击函数、1416 动画位置、1500/1511 控件消息/切页函数 |
| 5 | 3 | 1002 宽、1003 位置、20027 水平分辨率 |
| 未解析 | 27 | 因默认值用 `movups/push` 构造，第三指令不是类型 mov（20034/20037/20036/20039/1021/1023/1073/1160/1250/1104/1109/1121/1127/1128/1153/1201/1426/1551/1264/1007/1008/1035/1045/1323/1325/1053/1078） |

> 该类型码是“属性编辑器种类”，不是数据存储类型；实际读写仍按特征对象的 int(+0x20)/double(+0x28)/CString(+0x30) 处理。

### 8.5.3 渲染与预览（可让 AI “看到”画面）

| 能力 | 导出符号 | 说明 |
|------|----------|------|
| 窗口截图 | `?GetWinBmp@ZHmi_WindowClass@@QAEPAUHBITMAP__@@HHHH@Z` | `(w,h,clipW,clipH)` → HBITMAP；内部按 1002/1003/20008/1029/1010/1008/1009 完整渲染窗口及控件，可直接编码为 PNG 供 AI 视觉使用 |
| 单控件绘制 | `?DrawOneControl@ZHmi_WindowClass@@QAEXAAVZHmi_D2DDraw@@PAVZHmi_ControlClass@@_N2UD2D_RECT_F@@@Z` | 指定矩形绘制控件 |
| 状态图生成 | `?MakeOneStatePic@ZHmi_ControlClass@@QAEPAUstr_strRawPicFile@@PAVZHmi_FileClass@@HH@Z` | `(file,state,?)` → 原始位图；依赖 1080/1081 |
| 全状态图 | `?MakeStatePicAll@ZHmi_ControlClass@@QAEIPAVZHmi_FileClass@@@Z` | 生成控件全部状态图 |
| 生成图显示 | `?ShowMakePic@ZHmi_ControlClass@@QAEXAAVZHmi_D2DDraw@@UD2D_RECT_F@@@Z` | 绘制控件“生成图” |
| D2D 初始化/缩放 | `?InitD2D@ZHmi_D2DDraw@@QAE_NPAUHWND__@@I@Z`、`?ReSizeWin@ZHmi_D2DDraw@@QAEXHH@Z` | 渲染上下文 |
| 颜色转换 | `?RGBToD2D1Col@ZHmi_D2DDraw@@QAE?AU_D3DCOLORVALUE@@K@Z`、`?SetBrushCol@ZHmi_D2DDraw@@QAEHK@Z` | RGB↔D2D |

### 8.5.4 批量缩放 / 分辨率 / 字号

| 能力 | 导出符号 | 说明 |
|------|----------|------|
| 窗口与控件整体缩放 | `?ModifyWinsAndCons@ZHmi_FileClass@@QAEIHHUstruct_ModifyResolution@@@Z` | `(oldW,oldH,newW,newH,ModifyResolution)`；对每个窗口/控件按比例重算 1002/1003；**临时清除 1270 位置锁再恢复** |
| 控件自身缩放 | `?ModifyResolution@ZHmi_ControlClass@@UAEIHHUstruct_ModifyResolution@@@Z` | 虚拟方法（slot +132） |
| 字号缩放 | `?ModifyTextsize@ZHmi_FileClass@@QAEIN@Z` | `(newSize)` |
| 窗口尺寸 | 特征 1002/1003；`?GetSizeRect@ZHmi_ControlClass@@QAEPAUD2D_RECT_F@@XZ` | — |

### 8.5.5 资源库对象（控件参数引用的底层对象）

| 类 | 能力 | 代表导出 |
|----|------|----------|
| `ZHmi_PicLibClass` / `ZHmi_PicLibClassOne` | 图片库：新增/查找/删除/清空/导入/设置图片/取图片数据/状态图 | `AddOne`、`FindOne`、`Delete`、`Clear`、`Import`、`SetPic`、`GetOnePicData`、`SetStates` |
| `ZHmi_StrLibClass` / `One` | 文本库：新增/查找/删除/清空/导入/取字符串/状态 | `AddOne`、`FindOne`、`Delete`、`Clear`、`Import`、`GetString`、`SetStates` |
| `ZHmi_DataSampLibClass` / `One` | 数据采样库：新增/插入/删除/清空/查找/通道数/导入导出 | `AddOne`、`InsertOne`、`DeleteOne`、`Clear`、`FindOne`、`GetChannelNum`、`Import`、`Export` |
| `ZHmi_RecipeLibClass` / `One` | 配方库：同上 | `AddOne`、`InsertOne`、`DeleteOne`、`Clear`、`FindOne`、`Import`、`Export` |
| `ZHmi_KeyTransClass` | 按键映射：新增/删除/清空/导入导出 | `Add`、`Del`、`Clear`、`Import`、`Export` |
| `ZHmi_AlmListClass` / `AlmClass` | 报警列表：新增/删除 | `AddAlm`、`DelAlm` |
| `ZHmi_BackPreset` / `One` | 背景预设（窗口背景） | `NewAndAddBackPreset`、`Add`、`Read`、`Save` |
| `ZHmi_WindowClass` 图像 | 窗口位图 | `GetWinBmp` |

> 控件参数里通常只存“库条目 ID/名称”；要新建或修改库条目本身，才需要调用这些类。

### 8.5.6 状态、有效性与图片生成标志

| 特征 | 含义 | 用法 |
|------|------|------|
| 1005 | 控件状态数（1–256） | 决定状态图数量 |
| 6000–6015 | 每状态属性/状态图组 | 按 `subId` 或数组元素读写 |
| 1004 / 1021 / 1024 | 显示/有效/位控制显示 | `SetFeatureInt`/`SetFeatureValid` |
| 1080 | 控件转为图片（变量标签字体） | 触发状态图生成 |
| 1081 | 需要重绘/重生成图片 | `SetFeatureInt(1002/1080/1010/1517/1030)` 后自动置 1 |
| 1270 | 位置锁定 | 非 0 时 1002/1003 写入被忽略；批量缩放会临时清零 |
| 1282 / 1284 | 分组/边框偏移 | `GetSizeRect` 叠加计算 |

### 8.5.7 文件级/窗口级附加能力

| 能力 | 导出符号 |
|------|----------|
| 设备表 | `?AddDevice@ZHmi_FileClass@@QAEXPAVZHmi_DeviceClass@@@Z`、`?DelDevice@...QAEIPAVStruct_ZHMI_Item@@@Z` |
| 导入窗口子树 | `?ImportWin@ZHmi_FileClass@@QAEIPBD@Z` |
| 系统图库/工程图库 | `?ReadToSystemPic@ZHmi_FileClass@@QAEHXZ`、`?ReadZPIPicLib@ZHmi_FileClass@@QAEHPB_W@Z` |
| 新建键盘窗口 | `NewAndAddNumKeyWindow` / `NewAndAddStrKeyWindow` / `NewAndAddStrKeyWindowLarge` |
| 背景预设 | `?NewAndAddBackPreset@ZHmi_FileClass@@QAEXHV?$CStringT@_WV...@@HKK@Z` |
| 窗口编号/名称 | 特征 20001 / 20003 |
| 窗口类型 | 特征 20007（Base/Pop/Menu/SoftKeyboard/Top，18 个枚举项） |
| 公共窗口 1/2/3 | 特征 20010/20011/20012 |
| 背景透明度 | 特征 20008 |
| 标题栏 | 特征 20013/20014（显示/高度） |
| 拖动/缩放 | 特征 20015/20016 |
| 背光/屏保 | 特征 20020/20023 |
| 初始窗口 | 特征 20024/20025 |
| 启动子函数 | 特征 20028/20029 |
| 压缩/画质 | 特征 20035/20037 |
| 文本自适应/越界/鼠标事件 | 特征 20036/20038/20039 |

### 8.5.8 建议新增的 AI 工具（第二轮）

| 工具 | 内部调用 |
|------|----------|
| `hmi_screenshot_window` | `FindWindowW` → `GetWinBmp(w,h,clipW,clipH)` → PNG |
| `hmi_list_features` | 遍历 obj+0x18 → `GetShowName`/`GetShowComment` + 值 |
| `hmi_get_feature` / `hmi_set_feature` | `FindFeature` + `Get/SetFeatureInt/Double/String/Valid` |
| `hmi_set_geometry` | `SetFeatureInt(1002/1003, sub0/1)`（注意 1270 锁） |
| `hmi_scale_all` | `ModifyWinsAndCons(oldW,oldH,newW,newH,res)` |
| `hmi_set_textsize` | `ModifyTextsize(size)` |
| `hmi_add_control` | `NewAndAddControl(win,id,type,x,y,w,h)` |
| `hmi_add_button` | `NewAndAddButton(...)` |
| `hmi_delete_control` | `DelControl(win,id)` + `ZHmi_DeleteItem(ctrl+4)` |
| `hmi_make_state_pics` | `MakeStatePicAll(ctrl,file)` / `MakeOneStatePic` |
| `hmi_list_control_types` | 遍历控件注册表（168B）读类型 ID/名称/分类 |
| `hmi_list_piclib/strlib/datasamp/recipe/keys` | 对应库类 `FindOne`/`GetString`/`GetOnePicData` 等 |

## 8.6 实测验证（进程内只读探针，2026-11-09）

### 8.6.1 方法

- 独立 Python 脚本（`tools/hmi_probe.py`）使用 `ReadProcessMemory` 读取正在运行的 `RTSys.exe`（本次快照 PID 14748），**不注入、不写内存、不调用宿主函数**。
- 通过 Toolhelp32 取得模块基址：`ExtMainHmi.dll = 0x69A20000`。
- 读取 ExtMainHmi 全局（RVA 相对模块基址）：
  - 特征注册表 begin/end：`0x15DCB8 / 0x15DCBC`，条目 152 字节
  - 控件注册表 begin/end：`0x15DCEC / 0x15DCF0`，条目 168 字节
  - 当前编辑对象：`0x5DC2C`（`ZHMI_GetEditObject` 全局）
  - `ZHmi_FileClass` vtable：`0x4973C`（首项为 `?Draw@ZHmi_FileClass`）
- 宿主工程文件表：RTSys.exe `0x4268AC`（数组）/`0x4268B0`（数量），条目 76 字节，`+0` 为宽字符串路径。
- 产物：`docs/ida_analysis/hmi_live_feature_registry_2026-11-09.json`（521 条特征 + 67 类控件的完整运行时快照）。

### 8.6.2 实测结果

**特征注册表：521 条**

| 指标 | 结果 |
|------|------|
| 注册表条目总数 | 521 |
| 唯一特征 ID | 292 |
| 唯一 (mainId, subId) | 520（仅 6300,0 重复一次） |
| 类型分布 | 0:37、1:95、2:259、3:22、4:69、5:6、**6:33（新增类型）** |
| 静态 188 条命中 | **188/188 全部命中**（static − live = 0） |
| live-only 特征 | 104 个（自定义控件扩展） |

live-only 特征的主要族（来自自定义控件）：
- 进度条：1300–1303
- 图表/仪表：1305–1315（Pie/Bar/XY/Trend/Meter/Dynamic Scale）
- 图表通道/格式：1203、1210、1211
- 排程：1217
- 事件/报警显示：1330–1342
- 配方视图：1046、1047
- 几何/重绘控制：1270（位置锁）、1081（重绘标志）
- 状态/数据组：3000、6000、6600、6900、8000、9500 等

字段布局验证：
- `+0` mainId、`+4` subId（字节）、`+8` 编辑器类型、`+148` CString 名称；
- 前 5 条与静态表逐字一致（20030/20013/20014/20015/20016）。

**控件注册表：67 类**

- 47 内置（RTSys.exe `sub_4EE220`）
- 18 自定义（ExtHmiControl_W.dll `sub_1002A850`）
- **2 新增**（ExtHmiControl_W.dll `sub_1002C100`）：**732 Spin Button**、**735 Recipe View**
- 类型 ID 位于条目 `+4`，与静态注册顺序一致；67 类全部有名称映射。

**当前文档状态（两次快照）**

快照 A —— 未打开 HMI：

| 检查项 | 实测结果 |
|--------|----------|
| 工程文件表 | 5 个文件，全部 `.bas`，0 个 `.hmi` |
| `ZHMI_GetEditObject()` | 0（无选中对象） |
| `ZHmi_FileClass` vtable 全内存扫描 | 703,414,272 字节，**0 命中** |

快照 B —— 打开 `追剪应用DEMO\Hmi1.hmi` 并选中控件后：

| 检查项 | 实测结果 |
|--------|----------|
| `ZHMI_GetEditObject()` | `0x25C2F588` |
| `ZHmi_FileClass*`（edit+0x10） | `0x14ADA970` |
| 窗口数 / 控件数 | **7 / 169** |
| 名称读取 | 中文正常（工作画面 / 模板窗口 / 手动运动 / 参数设置） |
| 控件类型字段 | `u32(obj+4)`，与静态表完全一致 |
| 遍历链 | `file+0x20` 窗口链、`win+0x1C` 控件链、`obj+0x18` 特征链全部正常终止 |

### 8.6.3 完成画面级验证的步骤

1. 在 RTSys 中打开任一 `.hmi`（例如 `D:\RTSysProjects\Demo\Hmi1.hmi`）。
2. 在 HMI 设计器中选中一个控件（使 `ZHMI_GetEditObject()` 非空）。
3. 运行 `python -I tools/hmi_probe.py`（未选中时脚本会自动用 vtable 扫描定位 `ZHmi_FileClass`）。
4. 输出：`window_count`、每个窗口的 `id/type/name/geo/control_count`、每个控件的 `id/type/typeName/name/geo/feature_count`。

### 8.6.4 真实画面实测数据（`追剪应用DEMO/Hmi1.hmi`）

**窗口表（7 个，全部 type=11）**

| 窗口 id | 名称 | 尺寸 (宽×高) | 位置 | 控件数 |
|--------|------|--------------|------|--------|
| 6 | CharPad-Lower | 682 × 285 | (0,0) | 47 |
| 7 | CharPad-Upper | 682 × 285 | (0,0) | 47 |
| 8 | NumKeypad | 264 × 320 | (0,0) | 16 |
| 10 | 工作画面 | 800 × 480 | (0,0) | 17 |
| 11 | 模板窗口 | 800 × 70 | (0,0) | 6 |
| 20 | 手动运动 | 800 × 480 | (0,0) | 12 |
| 21 | 参数设置 | 800 × 480 | (0,0) | 24 |

**控件类型分布（169 个）**

| 类型 | 名称 | 数量 |
|------|------|------|
| 609 | Button | 116 |
| 703 | StaticText | 20 |
| 612 | Value show | 13 |
| 604 | BitSwitch | 12 |
| 611 | String show | 5 |
| 702 | Rect | 2 |
| 601 | WordState | 1 |

**控件样本（id / 名称 / 几何 / 特征数）**

| 窗口 | 控件 id | 类型 | 名称 | 宽×高 | 位置 | 特征数 |
|------|---------|------|------|-------|------|--------|
| 10 | 1 | 609 Button | Button1 | 134×40 | (0,440) | 73 |
| 10 | 7 | 703 StaticText | StaticText7 | 100×32 | (16,13) | 46 |
| 11 | 10 | 601 WordState | WordState7 | 100×25 | (446,8) | 66 |
| 11 | 15 | 611 String show | Text15 | 100×25 | (694,6) | 61 |
| 20 | 9 | 604 BitSwitch | BitSwitch9 | 100×40 | (167,195) | 73 |
| 20 | 12 | 612 Value show | Value12 | 100×40 | (291,252) | 61 |
| 11 | 15 | 702 Rect | Rect5 | 336×286 | (427,138) | 30 |

**验证结论**

1. 窗口链表：从 `file+0x20` 出发，沿 `+0x0C` 遍历 7 个窗口后正确回到表头；
2. 控件链表：从 `win+0x1C` 出发，7 个窗口分别得到 47/47/16/17/6/12/24 个控件，共 169 个；
3. 特征链表：每个控件均能遍历到 30–73 个特征，同类型控件特征数一致（609/604=73、703=46、611/612=61、601=66、702=30），说明 `obj+0x18` 遍历逻辑正确；
4. 几何：`1002[0]=宽`、`1002[1]=高`、`1003[0]=X`、`1003[1]=Y`，与设计师显示一致；
5. 名称：`特征+0x30 → CString 对象 → +0 → UTF-16` 双重解引用后，中文名称完全正确；
6. 类型：`u32(obj+4)` 给出 11/609/703/612/604/611/702/601，与静态控件表一致。

> 至此，HMI 内部对象模型的**读取路径已全部在真实画面上验证通过**；写入路径已在 §8.7 通过自包含草稿对象验证。

## 8.7 写入路径实测验证（自包含草稿对象，2026-11-09）

### 8.7.1 方法

为避免改动用户工程，测试在 RTSys.exe 进程内创建一个**临时 `ZHmi_FileClass` 草稿对象**（不挂接任何文档、不写文件），在其上执行创建/读写，最后销毁：

1. `VirtualAllocEx` 分配 0x200 字节；
2. `ZHmi_FileClass::ZHmi_FileClass()` 构造；
3. `ZHmi_FileClass::NewAndAddNopWindow(1234)` 新建窗口；
4. `ZHmi_WindowClass::NewAndAddControl(1, 609, 10, 20, 100, 40)` 新建 Button；
5. 远程线程桩调用 `SetFeatureInt` / `GetFeatureInt` / `FindFeature` / `SetString`；
6. 读取内存校验；
7. `ZHmi_FileClass::~ZHmi_FileClass()` 销毁 + `VirtualFreeEx`。

测试脚本：`tools/hmi_api_test.py`；结果：`tools/hmi_api_test_last.json`。

### 8.7.2 实测结果

| 步骤 | 期望 | 实测 | 结论 |
|------|------|------|------|
| 构造函数 | vtable = ExtMainHmi+0x4973C | `0x69A6973C` | ✅ |
| 新建窗口 1234 | type=11 | type=11 | ✅ |
| 新建 Button | type=609，几何 (10,20,100,40) | type=609，几何完全一致 | ✅ |
| `GetFeatureInt(1003,0,0)` | 10 | 10 | ✅ |
| `SetFeatureInt(1003,0,0,77)` | int=77，double=77.0 | 77 / 77.0 | ✅ |
| `SetFeatureInt(1002,0,0,150)` | int=150，double=150.0 | 150 / 150.0 | ✅ |
| 1270 位置锁 | 1270=1 时改 1003 无效 | x 保持 77，未变成 99 | ✅ |
| `FindFeature(1001,0,0)` + `SetString("HelloAI")` | 名称="HelloAI" | "HelloAI" | ✅ |
| 析构 | 无异常 | 正常返回 | ✅ |
| 测试后宿主健康 | 注册表完整 | 521 特征 / 67 控件，正常运行 | ✅ |

### 8.7.3 验证结论

1. **创建 API 可用**：`ZHmi_FileClass` 构造、`NewAndAddNopWindow`、`NewAndAddControl` 均按预期工作；
2. **特征读写 API 可用**：`GetFeatureInt`、`SetFeatureInt` 正常；1002/1003 写入会同步 int(+0x20) 与 double(+0x28)；
3. **1270 位置锁语义确认**：`1270=1` 时 `SetFeatureInt(1003)` 被忽略；
4. **字符串写入可用**：`FindFeature` + `ZHmi_FeatureClass::SetString(const char*)` 成功写入并读回；
5. **对象生命周期可用**：临时 FileClass 可安全析构。

### 8.7.4 尚未覆盖

- `SetFeatureDouble`、`SetFeatureValid`、`SetFeatureString`（CString 按值 ABI）未单独测试；
- 增删窗口/控件后的工程树/文档脏标记联动未验证；
- 保存落盘未验证（测试刻意不保存）。

> 测试全程未保存文件、未修改用户工程；`Hmi1.hmi` 备份位于 `%TEMP%\hmi_probe\backup\Hmi1.hmi.bak`。

### 8.7.5 工程画面写入 + UI 刷新实测（2026-11-09）

在已打开的 `Hmi1.hmi` 上，对用户选中的控件直接写入并截图对比：

| 项目 | 实测 |
|------|------|
| 目标控件 | id=11，type=604 BitSwitch，名称 `BitSwitch11` |
| 原始几何 | 100×40 @ (163,316)，1270=0 |
| 写入 | `SetFeatureInt(1003,0,0,193)`，返回 0，读回 193 |
| 强制重绘 | `InvalidateRect(canvas)` + `UpdateWindow` + `RedrawWindow(ALLCHILDREN|UPDATENOW)` + `WM_PAINT` |
| 重绘后截图差异 | **1062 像素**，差异区域 `(156,309)-(183,367)`，正是控件左边缘移动区 |
| 恢复 | `SetFeatureInt(1003,0,0,163)`，读回 163 |
| 恢复后截图差异 | **0 像素**（与原始截图完全一致） |
| 不触发重绘 | 写入后等待 1.5s，截图差异 **0 像素** → **宿主不会自动重绘** |

**结论**

1. 对**真实工程画面**的控件写入成功，且可精确恢复；
2. 设计师的 `Draw` 会读取更新后的特征值，重绘后画面正确变化；
3. **写入后必须主动触发 Invalidate/Redraw**（或调用宿主刷新函数），否则界面保持旧画面；
4. 本次测试未保存文件，恢复后画面与数据均回到原始状态。

证据截图：`tools/ui_before.png`、`tools/ui_after.png`、`tools/ui_restored.png`、`tools/ui_auto_before.png`、`tools/ui_auto_after.png`。

## 9. 关键证据地址

| 证据 | 位置 |
|------|------|
| `ZMC_HmiFileEncode/Decode`（旧文件方案算法） | zmotion.dll `0x100081d0/0x100081f0` → `sub_10016FF0/10017020`（`b=2-b`） |
| `ZHmi_FileClass::SavetoFile/ReadfromFile` | ExtMainHmi.dll `0x1002e2f0 / 0x1002e560` |
| `FindWindowW`（窗口链表 + 特征 20001） | ExtMainHmi.dll `0x1002ba20` |
| `FindControl`（控件链表 + 特征 1000） | ExtMainHmi.dll `0x10024960` |
| `FindFeature`（mainId/subId/index） | ExtMainHmi.dll `0x1000e700` |
| `SetFeatureInt` | ExtMainHmi.dll `0x1000ea00` |
| `ZHMI_GetEditObject/SetEditObject` | ExtMainHmi.dll `0x10003c60 / 0x10003970`（全局 `0x1005DC2C`） |
| `ZHmiDoc + 0x290 = FileClass*` | RTSys.exe `sub_58C7D0`、`sub_4D3F60`、`sub_584050` |
| 自定义控件注册 | ExtHmiControl_W.dll `sub_1002A850`（18 次 `ZHMI_RegisterControl`） |
| 特征注册 | ExtHmiControl_W.dll `sub_1002C470` |
