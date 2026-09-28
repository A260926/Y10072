# ANALYSIS.md — hjson-cs 保真（KeepWsc）链路交接说明

本说明沿"解析 → 修改 → 写回"链路，讲清 hjson-cs 如何在 KeepWsc 模式下保留键顺序、空白与注释，并给出三个已实测复现的独立风险。每个结论附 文件:行号，可在当前提交核对（演示命令见文末）。

## 1. 公开入口到 reader 的选择

Hjson 侧入口（`HjsonValue`）：

- `HjsonValue.Parse(string)`（文件:Hjson/HjsonValue.cs:87）与带 options 的重载（文件:Hjson/HjsonValue.cs:94）都转入私有 `load(string, ...)`（文件:Hjson/HjsonValue.cs:83）。
- 三个 `Load` 重载——`Load(string path)`（文件:Hjson/HjsonValue.cs:15）、`Load(Stream)`（文件:Hjson/HjsonValue.cs:27）、`Load(TextReader, IJsonReader)`（文件:Hjson/HjsonValue.cs:39）——以及对应的 options 重载（文件:Hjson/HjsonValue.cs:21、:33、:45），最终都汇入 `load(TextReader, ...)`（文件:Hjson/HjsonValue.cs:74-77）。
- 旧签名 `LoadWsc` 已标记 `[Obsolete(..., true)]`（文件:Hjson/HjsonValue.cs:52），不应再使用。

reader 选择规则：

- 上述路径最终都是 `new HjsonReader(textReader, jsonReader, options).Read()`（文件:Hjson/HjsonValue.cs:77；字符串输入在 :83）。HjsonReader 是宽松语法分析器，Hjson 与 JSON 都由它读。
- 按文件路径加载时，`.json` 扩展名改走严格 JSON 通道：`if (Path.GetExtension(path).ToLower() == ".json") return JsonValue.Load(path);`（文件:Hjson/HjsonValue.cs:58）。
- `JsonValue.Parse`（文件:Hjson/JsonValue.cs:144）与 `JsonValue.Load`（文件:Hjson/JsonValue.cs:139）直接 `new JsonReader(textReader, jsonReader).Read()`（文件:Hjson/JsonValue.cs:139）；JsonReader 只接受严格 JSON（文件:Hjson/JsonReader.cs:13）。
- 自定义 `IJsonReader`（文件:Hjson/IJsonReader.cs:9-17）不是装饰器，而是回调接收器：BaseReader 在读到键/下标/值时回调 `Key`/`Index`/`Value`（Hjson 通道见 文件:Hjson/HjsonReader.cs:152、:154、:179、:181；JSON 通道见 文件:Hjson/JsonReader.cs:37、:41、:63、:68）。

`HjsonReader.Read()` 还处理"根对象无大括号"：首字符不是 `{`/`[` 且输入含 `:` 时，按无大括号对象重试解析（文件:Hjson/HjsonReader.cs:50-67）。`WscJsonObject.RootBraces` 就在此刻被赋值。

## 2. Reader：空白与注释如何被收集

- 开关唯一来源：构造函数里 `ReadWsc = options.KeepWsc`（文件:Hjson/HjsonReader.cs:23 与 :34）。options 为 null 时 `ReadWsc` 保持 false，走非保真通道。
- 空白累积器：`BaseReader` 维护 `StringBuilder white`（文件:Hjson/BaseReader.cs:13）；`ReadChar` 在 ReadWsc 时把每个非 `\r` 字符追加进去（文件:Hjson/BaseReader.cs:69）；`ResetWhite` 清空（文件:Hjson/BaseReader.cs:86）；`GetWhite` 取出（文件:Hjson/BaseReader.cs:92）。字符串字面量整体追加（文件:Hjson/BaseReader.cs:285）。
- HjsonReader 重写了 `GetWhite`：裁掉尾部空格/制表符，但保留到行尾的换行与注释（文件:Hjson/HjsonReader.cs:111-124）。
- 数组分支（文件:Hjson/HjsonReader.cs:140-159）：开括号后 ResetWhite，若 ReadWsc 则 `new WscJsonArray()`（:145），并在第一个元素前 `Comments.Add(GetWhite())`（:148）；此后每读完一个元素（含逗号处理）再 `Comments.Add(GetWhite())`（:158）。n 个元素对应 n+1 个槽位：槽 0 在元素 0 之前，槽 i（i≥1）在元素 i-1 与 i 之间，最后一个槽在尾元素与 `]` 之间。
- 对象分支（文件:Hjson/HjsonReader.cs:161-190）：`new WscJsonObject() { RootBraces = !objectWithoutBraces }`（:169）；首个键之前的空白存入 `Comments[""]`（:172）；每个键值对读完后 `wsc.Comments[name] = GetWhite(); wsc.Order.Add(name)`（:187）。键值本身仍进基类 Dictionary（`obj.Add(...)`，:183）——Order/Comments 是平行的两份元数据。
- 值解析：引号字符串走 `ReadStringLiteral`（含 `'''` 多行串，文件:Hjson/HjsonReader.cs:203）；裸值走 `readTfnns`（文件:Hjson/HjsonReader.cs:415），true/false/null/数字之外的文本，若挂了 DSF 提供商会先尝试 DSF 解析（`HjsonDsf.Parse`，文件:Hjson/HjsonReader.cs:456）。

## 3. WSC 数据结构：顺序、注释、根大括号

- `WscJsonObject`（文件:Hjson/HjsonWsc.cs:9-18）：
  - `List<string> Order`（:12）：键的写出顺序，私有 set，仅 reader 追加。
  - `Dictionary<string,string> Comments`（:14）：键 → 该键之后到下一个键之前的空白/注释；`""` 条目是第一个键之前的内容。
  - `bool RootBraces`（:17）：仅根对象有效，记录原文是否带 `{}`，reader 在 文件:Hjson/HjsonReader.cs:169 写入。
- `WscJsonArray`（文件:Hjson/HjsonWsc.cs:21-25）：`List<string> Comments`（:24），按下标槽位保存，语义见第 2 节数组分支。
- 基类 `JsonObject` 用 `Dictionary<string,JsonValue> map` 存值（文件:Hjson/JsonObject.cs:15），`Add` 直接 `map[key] = value`（文件:Hjson/JsonObject.cs:62，允许重复键）；`JsonArray` 用 `List<JsonValue> list` 存值（文件:Hjson/JsonArray.cs:13）。值容器与 WSC 元数据是两套独立结构，这是风险二、风险三的结构根源。

## 4. Writer：旧空白与新值怎样拼回去

入口：`HjsonValue.Save(...)` 最终 `new HjsonWriter(options).Save(json, textWriter, 0, false, "", true, true)`（文件:Hjson/HjsonValue.cs:119），最后两个参数为 noIndent=true、isRootObject=true。options 为 null 时 writer 内 keepWsc=false、emitRootBraces=true（文件:Hjson/HjsonWriter.cs:23-27）。

- 换行与缩进：`nl` 写 `JsonValue.eol` + `level*2` 个空格（文件:Hjson/HjsonWriter.cs:30-33）。
- 注释还原：`getWsc(string)`（文件:Hjson/HjsonWriter.cs:36-48）判断空白串里第一个可见字符是不是注释起始（`#`、`//`、`/*`）或换行：是则原样返回（再经 normalizeEol）；若先遇到其他可见字符，则补一个空格变成 `# ...` 注释（:44）。按键取名见 :50，按下标取名见 :51；`testWsc`（:52）判断注释是否与值同行（决定字符串是否必须加引号）。
- 换行归一化：reader 收集的空白里是裸 `\n`；`normalizeEol`（文件:Hjson/HjsonWriter.cs:54-66）在当前 Eol 为 CRLF 时把 `\n`/`\r\n` 统一替换（:71、:74），Eol 为 LF 时原样返回（:56）。
- 对象写出（文件:Hjson/HjsonWriter.cs:102-138）：
  - `kw = writeWsc ? obj as WscJsonObject : null`（:102）——KeepWsc=false 或对象不是 Wsc 类型时，整段 WSC 逻辑跳过，走 :126-138 的普通路径（按 Dictionary 枚举顺序，无注释）。
  - `showBraces = !isRootObject || (kw != null ? kw.RootBraces : emitRootBraces)`（:103）：根对象是否带大括号，Wsc 看 RootBraces，非 Wsc 看 EmitRootBraces；嵌套对象恒为 true。
  - 键顺序：`kw.Order.Concat(kw.Keys).Distinct()`（:110）——先按 Order，再补上解析后新增的键（不在 Order 里的），同名去重。
  - `if (!obj.ContainsKey(key)) continue;`（:111）——已从 map 删除的键即使还在 Order 里也跳过。
  - 每个键：先写该键之前的注释 kwl（:112），换行，写键名，再写该键之后的注释（:116），值用 `Save(val, ..., testWsc(kwl), " ")`（:120）写出；循环后写尾部注释（:122）。
- 数组写出（文件:Hjson/HjsonWriter.cs:141-162）：先取槽 0（:148）；对每个下标 i，写当前槽注释，再预取槽 i+1（:156），换行写元素；循环后写剩余的尾槽注释（:161）。即"槽 i 的内容写在元素 i 之前"。
- 值是重新生成的：字符串由 writeString 按当前内容决定裸写/引号/多行串（文件:Hjson/HjsonWriter.cs:170 起）；数字由 `JsonPrimitive.GetRawString()` 用 `IFormattable` 的 "G" 格式输出（文件:Hjson/JsonPrimitive.cs:64-67），原文 `1.0` 会变成 `1`；键名由 escapeName 按当前内容决定是否加引号（文件:Hjson/HjsonWriter.cs:165-170）。writer 全程只读对象图，不回写 Order/Comments。

## 5. 四条开关的作用边界

- `JsonValue.Eol`（文件:Hjson/JsonValue.cs:28、:32-35）：进程级静态字段，setter 只接受 `"\r\n"` 或 `"\n"`（:35）。HjsonWriter 的 nl（文件:Hjson/HjsonWriter.cs:32）、normalizeEol（:56）和 JsonWriter 的 nl（文件:Hjson/JsonWriter.cs:18）都在每次写出时读它。它只控制换行，不控制注释/顺序；非 KeepWsc 的 JSON 写出也受它影响。
- `KeepWsc`（文件:Hjson/HjsonOptions.cs:12）：解析期决定是否构造 WscJsonObject/WscJsonArray（文件:Hjson/HjsonReader.cs:145、:169）；保存期决定 writer 是否走 WSC 路径（文件:Hjson/HjsonWriter.cs:23、:102）。默认 false：注释、键顺序、根大括号信息既不读也不写。
- `EmitRootBraces`（文件:Hjson/HjsonOptions.cs:17，默认 true）：仅保存期生效，且只影响根对象、且根对象不是 WscJsonObject 的情形（文件:Hjson/HjsonWriter.cs:103）。对 Wsc 根对象，解析时记录的 RootBraces 优先级更高（实测：带大括号的输入即使 EmitRootBraces=false 仍输出大括号；无大括号输入即使 EmitRootBraces=true 仍无大括号）。对数组和嵌套对象无任何影响。
- `DsfProviders`（文件:Hjson/HjsonOptions.cs:25）：解析期只对裸值生效（文件:Hjson/HjsonReader.cs:456），保存期在序列化前先尝试 `HjsonDsf.Stringify`（文件:Hjson/HjsonWriter.cs:88-93；文件:Hjson/HjsonDsf.cs:57-71）。DSF 解析可能产生 JsonType.Unknown 的值（如日期，文件:Hjson/HjsonDsf.cs:160-173），这类值只有匹配的 stringify 提供器能写回；不挂提供器直接 Save 会在 GetRawString 处抛 InvalidOperationException（文件:Hjson/JsonPrimitive.cs:61-68）。

## 6. 同一实例：Parse → 局部修改 → Save 两次，信息从哪来

对 `HjsonValue.Parse(input, keepWscOptions)` 得到的同一实例做局部修改后连续 Save 两次，输出字节完全一致（writer 只读不写，已实测）。信息来源划分：

来自原文（解析时固化在 WSC 元数据中，两次保存不变）：

- 键顺序 Order（文件:Hjson/HjsonWsc.cs:12）与每个键的注释槽（:14）；
- 数组每个下标的注释槽（文件:Hjson/HjsonWsc.cs:24）；
- 根大括号标志 RootBraces（:17）；
- 未被修改的元素值本身。

重新生成（每次保存按当前对象状态与当前 Eol 重算）：

- 缩进（level*2 空格，文件:Hjson/HjsonWriter.cs:32）与换行（normalizeEol，:54-66）；
- 键名/字符串的引号风格（escapeName/writeString）；
- 数字字面量（GetRawString 的 "G" 格式，`1.0`→`1`）；
- 被替换进去的新值、新增键（新增键走 `Order.Concat(Keys)` 的尾部，无注释，文件:Hjson/HjsonWriter.cs:110）；
- DSF 文本（若挂了提供器）。

## 7. 三个彼此独立的风险

### 风险一：并发请求改变全局换行设置

- 最小输入：任意含注释的 KeepWsc 文档，如 `# c1\na: 1\n# c2\nb: 2\n`。
- 操作步骤：线程 A 设 `JsonValue.Eol="\n"` 并开始 Save；在 writer 第一次 Write 时阻塞（用自定义 TextWriter 钩子）；线程 B 设 `JsonValue.Eol="\r\n"`；释放 A。
- 当前可观察结果：输出换行混用——第一段注释是裸 `\n`，后续行是 `\r\n`（已实测）。
- 依赖的不变量：writer 假设一次 Save 期间 Eol 恒定，因此只在每次 nl/normalizeEol 时读静态字段（文件:Hjson/HjsonWriter.cs:32、:56）；setter 只做合法性校验（文件:Hjson/JsonValue.cs:35），不冻结、无锁。两个请求用不同 Eol 保存即会互踩。

### 风险二：删除再插入同名键后，顺序/注释归属错乱

- 最小输入：`# head\n# comment for a\na: 1\n# comment for b\nb: 2\n`，options = KeepWsc + EmitRootBraces=false。
- 操作步骤：Parse 后 `obj.Remove("a")` 再 `obj.Add("a", 99)`，然后 Save。
- 当前可观察结果：Order 仍为 [a,b]，输出中 `# comment for a` 仍贴在 `a: 99` 前面、位置仍在 b 之前（已实测）。被删键的旧注释与旧槽位被重新插入的同名键"继承"。
- 依赖的不变量：JsonObject.Remove/Add 只操作基类 map（文件:Hjson/JsonObject.cs:59-62、:95），不同步 WSC 的 Order/Comments；writer 按 `Order.Concat(Keys).Distinct()` 遍历（文件:Hjson/HjsonWriter.cs:110）并按键名取注释（:116），默认 Order/Comments 中的键都活着。注意这与 `obj["a"]=99` 下标赋值不同——后者只改值、不动顺序与注释，是正常用法。

### 风险三：嵌套数组修改时 comment 槽位与元素下标错位（取证测试选中项）

- 最小输入：`[\n  # c0\n  1,\n  # c1\n  2,\n  # c2\n  3\n]\n`，options = KeepWsc。
- 操作步骤：Parse 后 `arr.Insert(0, 99)`（RemoveAt/Add 同理），然后 Save；根数组与嵌套数组（`obj["items"].Qa().Insert(...)`）均复现。
- 当前可观察结果：输出为

  ```
  [
    # c0
    99
    # c1
    1
    # c2
    2
    3
  ]
  ```

  `# c0` 原本在元素 1 之前，现在被写到新元素 99 之前；`# c1` 错放到 99 与 1 之间；`# c2` 错放到 1 与 2 之间（已实测）。Add 时尾注释会被挤到旧尾元素与新尾元素之间、新尾元素后无注释；RemoveAt 时所有槽位整体前移。下标替换 `arr[0]=99` 不错位（已实测），因为不改变元素数。
- 依赖的不变量：注释是"按元素下标的槽位"（文件:Hjson/HjsonWsc.cs:24），writer 对下标 i 写槽 i 的内容（文件:Hjson/HjsonWriter.cs:148、:156），假设槽 i 描述的是元素 i；而槽位只在解析时追加（文件:Hjson/HjsonReader.cs:148、:158），JsonArray 的 Insert/RemoveAt/Add（文件:Hjson/JsonArray.cs:45、:67、:73）不维护槽位。注释不随元素身份移动。

## 8. 取证测试

`test/UnderstandingProofTests.cs` 钉住风险三的当前行为（pin current behavior，而非期望行为）：测试名 `KeepWsc_ArrayInsert_CommentsShiftToWrongElements_PinsCurrentBehavior`，类注释明确说明它钉住的是"注释槽位不随 Insert 移动"这一可观察行为；若未来修复（注释随元素移动），该测试需同步改写。测试不改动任何现有断言或公开 API。
