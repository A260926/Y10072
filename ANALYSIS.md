# Hjson-CS 保真链路交接分析（Parse → 修改 → Save）

本文所有结论均可沿“文件:行号”在当前提交核对，并标注了对应的可运行实验（实验程序与输出在撰写时于本机用 .NET 10 实际运行；长期留档的取证测试是 `test/UnderstandingProofTests.cs`，运行方式见文末）。

- 阅读约定：行号对应当前提交的源文件；“WSC”指 whitespace/comments（空白与注释）；`KeepWsc` 指 `HjsonOptions.KeepWsc`。
- 本文只描述代码当前行为，凡“可能/风险”均与“已观察到的行为”明确分开。

## 1. 入口：Parse 与三个 Load 重载如何选 reader

公开入口分两条互不相交的 reader 路线：

1. Hjson/JSON 双格式路线：`HjsonValue`。
   - `HjsonValue.Parse(string)`（文件: `Hjson/HjsonValue.cs:87`）与带 options 的重载（文件: `Hjson/HjsonValue.cs:94`）都汇入私有 `load(string, IJsonReader, HjsonOptions)`（文件: `Hjson/HjsonValue.cs:80`），后者 `new HjsonReader(input, jsonReader, options).Read()`（文件: `Hjson/HjsonValue.cs:83`）。
   - 三个核心 `Load` 重载分别吃文件、流、TextReader：
     - `Load(string path)` / `Load(string path, HjsonOptions)`（文件: `Hjson/HjsonValue.cs:15`、`Hjson/HjsonValue.cs:21`）→ `loadFile`（文件: `Hjson/HjsonValue.cs:57`）。
     - `Load(Stream)` / `Load(Stream, HjsonOptions)`（文件: `Hjson/HjsonValue.cs:27`、`Hjson/HjsonValue.cs:33`）→ `load(Stream,…)`（文件: `Hjson/HjsonValue.cs:68`），再包成 `StreamReader` 进入 TextReader 版本（文件: `Hjson/HjsonValue.cs:72`）。
     - `Load(TextReader, IJsonReader)` / `Load(TextReader, HjsonOptions, IJsonReader)`（文件: `Hjson/HjsonValue.cs:39`、`Hjson/HjsonValue.cs:45`）→ `load(TextReader,…)`（文件: `Hjson/HjsonValue.cs:74`），同样落到 `new HjsonReader(…).Read()`（文件: `Hjson/HjsonValue.cs:77`）。
   - 关键分叉在 `loadFile`：扩展名小写为 `.json` 时直接 `return JsonValue.Load(path)`（文件: `Hjson/HjsonValue.cs:59`），**HjsonOptions（含 KeepWsc、EmitRootBraces、DsfProviders）在这条分支被整体丢弃**；非 `.json` 才走 `HjsonReader`，异常会被包上文件路径重抛（文件: `Hjson/HjsonValue.cs:62`）。
2. 严格 JSON 路线：`JsonValue.Load/Parse`。
   - `JsonValue.Load(TextReader, IJsonReader)`（文件: `Hjson/JsonValue.cs:136`）构造的是 `JsonReader`（文件: `Hjson/JsonValue.cs:139`）；`JsonValue.Parse(string)`（文件: `Hjson/JsonValue.cs:144`）经 `Load(new StringReader(...))`（文件: `Hjson/JsonValue.cs:148`）汇入同一处。
   - `JsonReader` 只读严格 JSON，且**没有 options 参数、从不创建 Wsc 类型**：数组固定 `new JsonArray(list)`、对象固定 `new JsonObject(obj)`（文件: `Hjson/JsonReader.cs:49`、`Hjson/JsonReader.cs:75`）。因此严格 JSON 路线在结构上不可能保留 WSC。

reader 的选择只由“入口类型 + 扩展名”决定，与文件内容无关：一个内容是纯 JSON 但扩展名不是 `.json` 的文件，仍由 `HjsonReader` 解析（这也是它能读 JSON 的原因）。

`HjsonReader` 的构造把 options 翻译成两个开关（文件: `Hjson/HjsonReader.cs:17-37`）：`ReadWsc = options.KeepWsc`（文件: `Hjson/HjsonReader.cs:23`、`Hjson/HjsonReader.cs:34`），以及 DSF 提供者列表与 `hasDsfProviders`（文件: `Hjson/HjsonReader.cs:24-26`）。`ReadWsc` 是基类 `BaseReader` 的实例属性（文件: `Hjson/BaseReader.cs:22`）。

## 2. 解析根：root 无大括号判定与裸空白的采集

`HjsonReader.Read()`（文件: `Hjson/HjsonReader.cs:40`）先用 `SkipPeekChar()`（文件: `Hjson/HjsonReader.cs:44`，实现于 `Hjson/HjsonReader.cs:128-132`，它先 `skipWhite2()` 再 Peek）看首字符：

- 是 `[` 或 `{`：`ReadCore()` 正常解析（文件: `Hjson/HjsonReader.cs:47-49`）。
- 其他字符但输入里含 `:`：先假定是“无大括号 root 对象”，调 `ReadCore(true)`（文件: `Hjson/HjsonReader.cs:51-56`）；抛错就 `Reset()`（文件: `Hjson/HjsonReader.cs:61`，重置位置/行列/空白缓冲，见 `BaseReader.Reset()` 文件: `Hjson/BaseReader.cs:40-46`）后再按单值 `ReadCore()` 试一次（文件: `Hjson/HjsonReader.cs:62`）。
- 否则按单值解析（文件: `Hjson/HjsonReader.cs:69`）。
- 解析完统一过 `checkTrailing`（文件: `Hjson/HjsonReader.cs:74-79`）：用 `skipWhite2()` 吞掉尾部空白和注释后若还有字符就报 “Extra characters in input”（文件: `Hjson/HjsonReader.cs:77`）。

`skipWhite2()`（文件: `Hjson/HjsonReader.cs:81-109`）比基类的 `SkipWhite()`（文件: `Hjson/BaseReader.cs:100-102`）多吃 `#…`、`//…` 和 `/* … */` 注释。注释字符在经过 `ReadChar()` 时仍进入 WSC 缓冲：`ReadChar()` 在 `ReadWsc` 下把除 `\r` 外的每个字符追加进 `white`（文件: `Hjson/BaseReader.cs:69`），`\r` 被刻意丢弃（这决定了 CRLF 输入在注释槽里只保留 `\n`，见第 6 节）。

空白采集是“事件槽”模型，不是保留整段原文：

- `ResetWhite()` 清空 `white`（文件: `Hjson/BaseReader.cs:84-87`）。
- `GetWhite()` 基类版本直接交出缓冲（文件: `Hjson/BaseReader.cs:89-93`）；`HjsonReader` 重写后会剥掉“最后一个换行之后的行尾空白”，并在整段只剩空白时返回 `""`（文件: `Hjson/HjsonReader.cs:111-126`）。
- 带引号字符串是特例：`ReadStringLiteral` 扫描结束后把从开引号到闭引号的原文一次性补进 `white`（文件: `Hjson/BaseReader.cs:285`），因此引号样式不会进 WSC 槽（写出时样式重新决定，见第 6 节）。

## 3. WscJsonObject / WscJsonArray：顺序与注释槽位怎么存

### 3.1 对象（WscJsonObject）

进入 `{` 分支（文件: `Hjson/HjsonReader.cs:161-189`）：

- `ReadWsc` 为真时创建 `WscJsonObject`，并记下 `RootBraces = !objectWithoutBraces`（文件: `Hjson/HjsonReader.cs:169`）；否则是普通 `JsonObject`（文件: `Hjson/HjsonReader.cs:170`）。
- 读完 `{` 后先 `ResetWhite()`（文件: `Hjson/HjsonReader.cs:167`），再 `SkipPeekChar()` 扫过空白/注释，把结果存进保留键 `Comments[""]`（文件: `Hjson/HjsonReader.cs:171-172`）。这就是“任何元素之前”的头槽。
- 每读一个成员：`readKeyName()`（文件: `Hjson/HjsonReader.cs:177`，键名引号在 Hjson 里可选，规则在 `Hjson/HjsonReader.cs:196` 起）→ `Expect(':')`（文件: `Hjson/HjsonReader.cs:179`）→ 递归 `ReadCore()` 取值（文件: `Hjson/HjsonReader.cs:182`）→ `obj.Add`（文件: `Hjson/HjsonReader.cs:184`）。
- 值之后：`ResetWhite()`，若有逗号则消费逗号并再次 `ResetWhite()`+`SkipPeekChar()`（文件: `Hjson/HjsonReader.cs:185-186`），然后 `Comments[name] = GetWhite(); Order.Add(name)`（文件: `Hjson/HjsonReader.cs:187`）。

`WscJsonObject` 自身只有三件数据（文件: `Hjson/HjsonWsc.cs:9-18`）：`Order: List<string>`（文件: `Hjson/HjsonWsc.cs:12`）、`Comments: Dictionary<string,string>`（文件: `Hjson/HjsonWsc.cs:14`）、`RootBraces`（文件: `Hjson/HjsonWsc.cs:17`）。值本身存在基类 `JsonObject` 的 `Dictionary<string,JsonValue> map` 里（文件: `Hjson/JsonObject.cs:15`）。因此顺序信息与值存储是**两套并行结构**。

两个直接后果（已实验确认，不是推测）：

- `Order` 允许重复：`JsonObject.Add` 实际是 `map[key] = value`，注释明确“json allows duplicate keys”（文件: `Hjson/JsonObject.cs:62`），而 `Order.Add` 不去重（文件: `Hjson/HjsonReader.cs:187`）。实验输入 `{x:1 # first, x:2 # second}` 得到 `Order.Count == 2`、map 里 `x=2`。
- `Comments` 以键名为槽位：同名重复键在读取时 `Comments[name]=…` 覆盖（文件: `Hjson/HjsonReader.cs:187`），所以重复键只有最后一个尾槽留下。

### 3.2 数组（WscJsonArray）

进入 `[` 分支（文件: `Hjson/HjsonReader.cs:140-160`）：

- 消费 `[`、`ResetWhite()`，`ReadWsc` 时创建 `WscJsonArray`（文件: `Hjson/HjsonReader.cs:143-146`）。
- 扫过开头空白后 `wscL.Comments.Add(GetWhite())`（文件: `Hjson/HjsonReader.cs:147-148`）——这是槽 0，任何元素之前。
- 循环里：递归读值 `ReadCore()`（文件: `Hjson/HjsonReader.cs:153`）、`list.Add(value)`（文件: `Hjson/HjsonReader.cs:155`）、`ResetWhite()`、可选消费逗号（文件: `Hjson/HjsonReader.cs:156-157`），然后再 `Comments.Add(GetWhite())`（文件: `Hjson/HjsonReader.cs:158`）。

所以数组注释槽的编号是固定的（文件: `Hjson/HjsonWsc.cs:21-24`）：

```
[ 槽0, 元素0, 槽1, 元素1, 槽2, ..., 元素(n-1), 槽n ]
```

槽 `i`（i≥1）采集的是“元素 i-1 结束之后到元素 i 开始之前（含元素 i-1 行上的尾注释）”的原文；最后一个槽 n 是尾槽（最后一个元素到 `]` 之间）。空数组只有槽 0。读取阶段能保持 `Comments.Count == 元素数 + 1`，纯粹因为 Add 元素与 Add 槽在同一循环中成对发生；没有任何机制在之后维护这个关系（见第 7 节风险 C）。

## 4. 写回：writer 如何把旧空白与新值拼起来

写出入口：`HjsonValue.Save(JsonValue, TextWriter, HjsonOptions)`（文件: `Hjson/HjsonValue.cs:116-120`）`new HjsonWriter(options).Save(json, tw, 0, false, "", true, true)`（文件: `Hjson/HjsonValue.cs:119`）。写文件版本在 `.json` 扩展名时改走 `json.Save(path, Stringify.Formatted)`（文件: `Hjson/HjsonValue.cs:103`，即 `JsonWriter`，文件: `Hjson/JsonWriter.cs:10`），其余扩展名才用 `HjsonWriter`（文件: `Hjson/HjsonValue.cs:104-105`）。

`HjsonWriter` 构造函数把 options 固定为三个只读字段（文件: `Hjson/HjsonWriter.cs:19-28`）：`writeWsc`、`emitRootBraces`、`dsfProviders`；options 为 null 时 `emitRootBraces=true`、`writeWsc=false`（文件: `Hjson/HjsonWriter.cs:27`）。

核心 `Save(...)`（文件: `Hjson/HjsonWriter.cs:80-176`）每次先做 DSF 短路：`HjsonDsf.Stringify(dsfProviders, value)` 返回非 null 就直接写该字面量并 return（文件: `Hjson/HjsonWriter.cs:90-96`），早于对象/数组分支——这是 DSF 与 KeepWsc 的边界（第 5 节）。

对象拼接（文件: `Hjson/HjsonWriter.cs:100-138`）：

- `kw = writeWsc ? obj as WscJsonObject : null`（文件: `Hjson/HjsonWriter.cs:102`）。注意这是 `as`：对 KeepWsc 解析出来的 `WscJsonObject` 成立；对 `new JsonObject()` 手工构造的对象即使开了 `writeWsc` 也为 null，自动退回普通分支。
- 键顺序：WSC 分支遍历 `kw.Order.Concat(kw.Keys).Distinct()`（文件: `Hjson/HjsonWriter.cs:110`）——原顺序优先，map 里新增但不在 Order 的键（`Add` 出来的新键）通过 `Concat(kw.Keys)` 补在后面；`Distinct()` 让重复键只写一次，值取 map 当前值。每写一键前 `if (!obj.ContainsKey(key)) continue`（文件: `Hjson/HjsonWriter.cs:112`），所以被删除键的旧槽位直接跳过（见风险 B）。
- 旧空白与新值的交错方式：头槽 `kwl` 取 `Comments[""]`（文件: `Hjson/HjsonWriter.cs:109`）；每轮先 `tw.Write(kwl)` 写“上一键之后”的旧空白，再 `nl()` 生成全新换行+缩进（文件: `Hjson/HjsonWriter.cs:114-115`），再为当前键取新槽 `kwl = getWsc(Comments, key)`（文件: `Hjson/HjsonWriter.cs:116`），然后写键名、`:`，递归写“当前的新值”（文件: `Hjson/HjsonWriter.cs:118-120`）。循环结束把最后一个槽写出（文件: `Hjson/HjsonWriter.cs:122`）。
- 普通分支（kw==null）按 `Dictionary` 枚举顺序写，无 WSC（文件: `Hjson/HjsonWriter.cs:125-136`）。

数组拼接（文件: `Hjson/HjsonWriter.cs:139-164`）：槽位通过 `getWsc(whiteL.Comments, i+1)` 按下标取（文件: `Hjson/HjsonWriter.cs:156`），越界时 `getWsc(List,index)` 返回 `""`（文件: `Hjson/HjsonWriter.cs:51`，条件 `white.Count > index`）。注意这里**不存在**对象分支那样的“结构过滤”：数组槽永远按 0..n 的下标写，元素与槽一旦失配就静默错位（风险 C）。

槽文本的再加工：`getWsc(string)`（文件: `Hjson/HjsonWriter.cs:36-48`）发现槽里有非空白可见字符但没有注释起始符（`\n`/`#`/`//`/`/*`）时，把整段改写成 Hjson 尾注释 ` # <原文>`（文件: `Hjson/HjsonWriter.cs:45`）；否则走 `normalizeEol`（文件: `Hjson/HjsonWriter.cs:47`）。`normalizeEol` 在全局 `JsonValue.eol != "\n"` 时把槽里裸 `\n`、`\r\n` 统一重写成当前 eol（文件: `Hjson/HjsonWriter.cs:54-78`）。`testWsc`（文件: `Hjson/HjsonWriter.cs:52`）判断槽是否以“非换行”开头，其结果作为 `hasComment` 传入字符串值的写出（文件: `Hjson/HjsonWriter.cs:120`、`Hjson/HjsonWriter.cs:159`），决定该字符串是否必须加引号（`doEscape = hasComment || …`，文件: `Hjson/HjsonWriter.cs:193`）。

新值本体一律重新生成：布尔/数字写 `GetRawString()`（文件: `Hjson/HjsonWriter.cs:165-175`，数字经 `JsonPrimitive.GetRawString()`（文件: `Hjson/JsonPrimitive.cs:63-71`，数字分支用 `G` 格式，文件: `Hjson/JsonPrimitive.cs:68`）），字符串经 `writeString` 重新决定裸写/引号/多行（文件: `Hjson/HjsonWriter.cs:187-216`），缩进由 `nl()` 统一输出两空格/层（文件: `Hjson/HjsonWriter.cs:30-34`）。

## 5. JsonValue.Eol、KeepWsc、EmitRootBraces、DsfProviders 的作用边界

- `JsonValue.Eol` 是**进程级静态可变状态**：`internal static string eol = Environment.NewLine`（文件: `Hjson/JsonValue.cs:28`），setter 只接受 `"\r\n"` 或 `"\n"`（文件: `Hjson/JsonValue.cs:32-35`）。它只影响“写出时新生成的换行”：Hjson 的 `nl()`（文件: `Hjson/HjsonWriter.cs:32`）、JSON 的 `JsonWriter.nl()`（文件: `Hjson/JsonWriter.cs:18`）以及 WSC 槽的 `normalizeEol`（文件: `Hjson/HjsonWriter.cs:56`、`Hjson/HjsonWriter.cs:71`、`Hjson/HjsonWriter.cs:74`）。解析时不读它——输入的 `\r` 在 WSC 缓冲里被直接丢弃（文件: `Hjson/BaseReader.cs:69`）。
- `KeepWsc`（文件: `Hjson/HjsonOptions.cs:17`）：解析时决定建 `WscJsonObject/WscJsonArray` 还是普通容器（文件: `Hjson/HjsonReader.cs:145`、`Hjson/HjsonReader.cs:169`），并决定 `BaseReader` 是否采集空白（文件: `Hjson/BaseReader.cs:69`）；写出时决定 `writeWsc`，进而决定是否使用 WSC 槽（文件: `Hjson/HjsonWriter.cs:102`、`Hjson/HjsonWriter.cs:145-148`）。解析与写出处的两次 KeepWsc 相互独立：用 KeepWsc 读入、再用无 options 的 `ToString(Stringify.Hjson)` 写出时，`writeWsc=false`，注释与 Order 全被忽略（已实验确认：`{ # header, a:1 # after a, …}` 经 `ToString(Stringify.Hjson)` 输出无任何注释）。
- `EmitRootBraces`（默认 true，文件: `Hjson/HjsonOptions.cs:20`）：**只对 root 对象、且只在写出处生效**。判定式在文件: `Hjson/HjsonWriter.cs:103`：
  `showBraces = !isRootObject || (kw != null ? kw.RootBraces : emitRootBraces)`。
  - root 调用恒以 `isRootObject=true` 进入（文件: `Hjson/HjsonValue.cs:119`），嵌套对象 `isRootObject=false` 必然带大括号；
  - 当容器是 WscJsonObject 时，以读入时快照的 `kw.RootBraces` 为准（文件: `Hjson/HjsonReader.cs:169`），**写出处的 EmitRootBraces 被忽略**（已实验确认：rootless 输入用 KeepWsc 读入后，写出处把 EmitRootBraces 改成 true 仍无大括号）；
  - 仅当 root 不是 WscJsonObject（普通分支）时，写出处的 `EmitRootBraces=false` 才生效；
  - root 是数组时该选项无作用（数组无对应分支，`[` 恒写出，文件: `Hjson/HjsonWriter.cs:142`）。
- `DsfProviders`（文件: `Hjson/HjsonOptions.cs:25-28`）：解析时仅作用于“到行尾的无引号字符串”——`readTfnns` 在 isEol 分支里调 `HjsonDsf.Parse(dsfProviders, str)`（文件: `Hjson/HjsonReader.cs:451-456`），数字/true/false/null 字面量在更早的分支返回，不经过 DSF（文件: `Hjson/HjsonReader.cs:429-449`）。写出时 DSF 在每次 `Save` 最前面短路（文件: `Hjson/HjsonWriter.cs:90-96`），命中时连对象/数组/引号逻辑都不进入（实验：带 `HjsonDsf.Date()` 保存 `DateTime` 得到 `2020-01-02T00:00:00Z`；带 `HjsonDsf.Hex(true)` 时数字 255 写为 `0xff`，不带则写 `255`）。DSF 输出合法性校验在 `HjsonDsf.Stringify`（文件: `Hjson/HjsonDsf.cs:57-83`）。
- 严格 JSON 写出边界：`JsonValue.Save(path/Stringify)` 在 `Stringify.Hjson` 时转调 `HjsonValue.Save(this, textWriter)`——**注意此处不传 options**（文件: `Hjson/JsonValue.cs:79`）；Plain/Formatted 用 `new JsonWriter(formatted)`（文件: `Hjson/JsonValue.cs:80`），`JsonWriter` 没有 WSC 概念（文件: `Hjson/JsonWriter.cs:10-125`）。

## 6. 同一实例 Parse → 局部修改 → Save 两次：信息来源清单

用 `{ # header ⏎ a: 1 # after a ⏎ # before b ⏎ b: 2 }` 这类输入实测（`JsonValue.Eol="\n"`，KeepWsc 读+写）：第一次修改 `v["a"]=111` 后连续 Save 两次，输出逐字节相同；把第一次输出再 Parse 再 Save 也稳定。逐项来源如下：

来自原文（保存在 WSC 槽里，文件: `Hjson/HjsonWsc.cs:12`、`Hjson/HjsonWsc.cs:14`、`Hjson/HjsonWsc.cs:24`）：

- 注释正文本身：头注释 `# header`、键尾注释 `# after a`、独立行注释 `# before b`，按槽位（`""` 头槽、键名槽）回放到原位置（文件: `Hjson/HjsonWriter.cs:109-122`）。
- 注释前的换行与“注释所在行的缩进”：保存在槽文本里（采集于文件: `Hjson/HjsonReader.cs:172`、`Hjson/HjsonReader.cs:187`），写出时原样 `tw.Write(kwl)`（文件: `Hjson/HjsonWriter.cs:114`、`Hjson/HjsonWriter.cs:122`）。
- 键的名字与键顺序（`Order`），以及 root 是否曾有大括号（`RootBraces`，文件: `Hjson/HjsonWsc.cs:17`）。

每次 Save 重新生成（不来自原文逐字保留）：

- 所有值的字面渲染：改后的 `111`、未改值也重新 `GetRawString()`（文件: `Hjson/HjsonWriter.cs:174`）；引号风格全部重判（实验：`"hello"`、`'single'`、`1.0` 再存分别变 `hello`、`single`、`1`，入口文件: `Hjson/HjsonWriter.cs:187`）。
- 结构性空白：每个 `{ [ , :`、每层两空格缩进、键值分隔空格，均由 writer 输出（`nl` 文件: `Hjson/HjsonWriter.cs:30-34`；`":"` 文件: `Hjson/HjsonWriter.cs:119`）。
- 换行字符：全部取写出当下的全局 `JsonValue.eol`（文件: `Hjson/HjsonWriter.cs:32`、文件: `Hjson/HjsonWriter.cs:54-78`），包括槽内旧换行的改写；因此“同一实例 Save 两次”只有在两次之间没人改 `JsonValue.Eol` 时才逐字节一致。
- 文件尾换行：writer 不输出结尾换行（实测输出结束于 `}`，无尾 `\n`），故原文末换行不属于保真范围。

局部修改只动值存储，不动 WSC：`this[key]` 的 set 仅 `map[key] = value`（文件: `Hjson/JsonObject.cs:45`），不碰 `Order/Comments`；数组下标 set 仅 `list[index]=value`（文件: `Hjson/JsonArray.cs:35-39`）。因此“旧槽位 + 新值”的拼接正是修改后保真的机制，也是第 7 节三类风险的共同根源：**writer 假定 WSC 槽与当前结构仍然对齐，但修改 API 没有任何槽位维护逻辑**（对比：Remove 只删 map，文件: `Hjson/JsonObject.cs:95-99`；Insert/RemoveAt 只动 list，文件: `Hjson/JsonArray.cs:67`、`Hjson/JsonArray.cs:73`）。

## 7. 三个彼此独立的风险

三者修改的是不同数据结构（全局静态 / 对象的键集合 / 数组的下标列表），互不依赖。每条给出最小输入、步骤、当前可观察结果、所依赖的不变量。实验时 `JsonValue.Eol` 均先显式设为 `"\n"`（除风险 A 本身）。

### 风险 A：并发请求改变全局换行设置（进程级静态状态）

- 依赖的代码事实：换行是静态可变字段 `JsonValue.eol`（文件: `Hjson/JsonValue.cs:28`、setter 文件: `Hjson/JsonValue.cs:35`），一次 Save 中 writer 会在**很多个时刻**反复读它：每个 `nl()`（文件: `Hjson/HjsonWriter.cs:32`）和槽内每个换行的 `normalizeEol`（文件: `Hjson/HjsonWriter.cs:71`、`Hjson/HjsonWriter.cs:74`）都重新读取，没有在 Save 开始时快照。
- 最小输入：任意非空对象，例如 200 个键的对象（键越多窗口越大；单键也能复现，只是概率低）。
- 操作步骤：线程 1 在循环里反复 `JsonValue.Eol = "\r\n"` / `"\n"`；线程 2 同时反复 `HjsonValue.Save(big, …)`（options 传 null 即可，`nl` 一定参与）。
- 当前可观察结果（已实测，20000 次 Save **全部**为混合换行）：同一次 Save 的输出里同时出现 `\r\n` 与裸 `\n`，样例片段 `{\r\n\n  k0: 0\r\n\n  k1: 1\n  k2: 2\r\n…`。这不是格式选择问题，而是单次文档内部换行不自洽。
- 依赖的不变量：一次 Save 期间 `JsonValue.eol` 不被其他线程改变。代码未提供该保证；既有测试用 `[Collection("EolTests")]` 在测试进程内串行化（文件: `test/EdgeCaseTests.cs:12`、文件: `test/ParserTests.cs:13`），但这是测试侧约束，库本身无防护。

### 风险 B：删除再插入同名键后，顺序与注释归属

- 依赖的代码事实：对象的值、顺序、注释是三套结构——`map`（文件: `Hjson/JsonObject.cs:15`）、`Order`、`Comments`（文件: `Hjson/HjsonWsc.cs:12`、`Hjson/HjsonWsc.cs:14`）。`Remove(key)` 只删 `map`（文件: `Hjson/JsonObject.cs:95-99`）；`Add(key,value)` 只做 `map[key]=value`（文件: `Hjson/JsonObject.cs:59-63`），不向 `Order` 追加、不清 `Comments`。写出顺序是 `Order.Concat(Keys).Distinct()`，且对 map 已删键 `continue`（文件: `Hjson/HjsonWriter.cs:110-112`）。
- 最小输入：
  ```
  {
    a: 1 # cA
    b: 2 # cB
    c: 3 # cC
  }
  ```
- 操作步骤：KeepWsc Parse；`((JsonObject)v).Remove("b")` 后 `Add("b", 222)`，再 KeepWsc Save。
- 当前可观察结果（已实测）：
  - 只 Remove 不 Add 时，`Order` 仍是 `a,b,c`、`Comments` 仍含键 `b`（孤立槽），输出里 `b` 的槽被 `ContainsKey` 跳过，`a` 与 `c` 的注释位置正确。
  - Remove 后立刻 Add 同名键：新值**复用旧的 Order 位置和旧注释槽**——输出中 `b: 222# cB` 仍位于 a、c 之间。维护者若以为“删除后重新插入 = 追加到末尾并失去旧注释”，会得到相反结果。
  - 对照：Add 一个全新键 `d` 时，`d` 不在 `Order`，经 `Concat(kw.Keys)` 追加到末尾，且没有注释槽（输出 `d: 4` 无尾注释）。
- 依赖的不变量：`Order` 与 `Comments` 的键集合始终等于 `map` 的键集合，且 `Order` 表达“当前插入顺序”。删除/添加路径不维护它，只在“新键名”时恰好表现为追加；同名键的语义（复用旧位置）是当前实现的偶然结果，不应被当作承诺。

### 风险 C：嵌套数组增删元素时，comment 槽位与元素下标错位（最隐蔽，已加取证测试）

- 依赖的代码事实：读取时槽与元素靠同一循环成对 Add 保持 `槽数 == 元素数 + 1`（文件: `Hjson/HjsonReader.cs:148`、`Hjson/HjsonReader.cs:155`、`Hjson/HjsonReader.cs:158`）。写出时按**当前下标**取槽：槽 i 写在元素 i 之前（文件: `Hjson/HjsonWriter.cs:150-160`，取槽在 `Hjson/HjsonWriter.cs:156`），越界返回 `""`（文件: `Hjson/HjsonWriter.cs:51`）。而 `Insert`/`RemoveAt`/`Remove` 只改值列表（文件: `Hjson/JsonArray.cs:67`、`Hjson/JsonArray.cs:70`、`Hjson/JsonArray.cs:73`），从不调整 `WscJsonArray.Comments`（文件: `Hjson/HjsonWsc.cs:24`）。数组分支没有对象分支那样的 `ContainsKey` 结构过滤（对比文件: `Hjson/HjsonWriter.cs:112` 与 `Hjson/HjsonWriter.cs:153-161`）。
- 最小输入：
  ```
  {
    arr: [
      # c0
      10
      # c1
      20
      # c2
      30
    ]
  }
  ```
- 操作步骤：KeepWsc Parse；取 `WscJsonArray arr`；`arr.RemoveAt(0)`（删除 10），KeepWsc Save。另做两个对照：`arr.Insert(0, 99)`；以及只做 `arr[0] = 100` 的原地替换。
- 当前可观察结果（已实测，并由取证测试钉住）：
  - RemoveAt 后：`arr.Count == 2` 但 `arr.Comments.Count == 4`（槽没缩）。输出为
    ```
    {
      arr:
      [
        # c0
        20
        # c1
        30
        # c2
      ]
    }
    ```
    即 `# c0`（原本属于已删元素 10 的前槽）现在挂在存活元素 20 前，20 自己的 `# c1` 挂到了 30 前，`# c2` 成为 `]` 前的孤立尾槽。
  - Insert 后：4 个元素对 4 个槽，`# c0/# c1/# c2` 同样整体错位，原元素 10、20 前分别变成 `# c1`、`# c2`，最后元素 30 前无槽。
  - 原地替换 `arr[0]=100`：不错位，`# c0` 仍在 100 前。
- 依赖的不变量：`Comments[i+1]` 必须语义上属于元素 i。它只在“解析后不改变元素个数/次序、仅原地替换值”时成立。这是三者中最隐蔽的一条：对象至少还有 `ContainsKey` 跳过孤立键槽，数组则静默错位，不抛异常、不丢字符，极难在代码评审中发现。

## 8. 取证测试

文件: `test/UnderstandingProofTests.cs`，针对风险 C，类型为**特征化/钉住当前行为（CHARACTERIZATION，暴露风险而非验证正确性）**：

- `Characterization_NestedArrayRemove_CommentSlotsDoNotShiftWithElements`：钉住 RemoveAt 后 `Count==2` 而 `Comments.Count==4`，并钉住错位输出全文（`# c0` 跑到 20 前、`# c2` 成尾槽）。
- `Characterization_NestedArrayInPlace_CommentsStayAligned`：钉住安全边界——原地同下标替换不错位。
- 两个测试都在 finally 中恢复 `JsonValue.Eol`，并归入既有 `[Collection("EolTests")]` 以免污染其他测试（风险 A 的全局状态）。未修改任何现有断言或公开 API。

复现命令（与任务给定一致）：

```
dotnet restore
dotnet build Hjson.slnx --no-restore
dotnet test test/test.csproj --no-build --filter UnderstandingProof -v normal
```

第 6、7 节中标注“已实测”的交互现象，撰写时另用临时控制台程序（引用本仓库 `Hjson.csproj`）验证，未作为测试文件提交；可长期回归的证据仅为 `test/UnderstandingProofTests.cs`。
