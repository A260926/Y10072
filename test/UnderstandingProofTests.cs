#nullable enable

using System.IO;

using Hjson;

using Xunit;

namespace Hjson.Tests;

/// <summary>
/// 取证测试（UnderstandingProof）。
///
/// 本类【钉住当前行为】（pin current behavior），不是期望行为的断言：
/// 它记录的是 ANALYSIS.md 第 7 节风险三的可观察事实——在 KeepWsc 模式下，
/// 对数组做结构性修改（Insert/RemoveAt/Add）后，WscJsonArray 的注释槽位
/// （Comments，按下标保存）不随元素移动，导致注释被写到错误的元素之前。
/// 下标语义替换（arr[i] = ...）不改变元素数，不会触发该错位。
/// 若未来修复为"注释随元素身份移动"，本测试的断言需同步改写。
/// </summary>
[Collection("EolTests")]
public class UnderstandingProofTests
{
    [Fact]
    public void KeepWsc_ArrayInsert_CommentsShiftToWrongElements_PinsCurrentBehavior()
    {
        JsonValue.Eol = "\n";
        // 最小输入：3 个元素、4 个注释槽位（槽 0 在元素 0 前，槽 i 在元素 i 前）。
        // 见 HjsonReader 数组分支：Comments.Add(GetWhite()) 每元素追加一次。
        string input = "[\n  # c0\n  1,\n  # c1\n  2,\n  # c2\n  3\n]\n";
        var options = new HjsonOptions { KeepWsc = true };
        var cfg = HjsonValue.Parse(input, options);

        // 结构性修改：在下标 0 插入新元素 99。
        cfg.Qa().Insert(0, 99);

        var sw = new StringWriter();
        HjsonValue.Save(cfg, sw, options);
        string output = sw.ToString();

        // 当前可观察行为（被钉住的风险）：槽位不随元素移动。
        // 槽 0（# c0）原本描述元素 1，现在被写到新元素 99 之前。
        Assert.Contains("  # c0\n  99", output);
        // 槽 1（# c1）原本在 1 与 2 之间，现在错放到 99 与 1 之间。
        Assert.Contains("  # c1\n  1", output);
        // 槽 2（# c2）原本在 2 与 3 之间，现在错放到 1 与 2 之间。
        Assert.Contains("  # c2\n  2", output);

        // 反证：理想行为（注释仍贴原元素）在当前实现下不成立。
        Assert.DoesNotContain("  # c0\n  1", output);
    }

    [Fact]
    public void KeepWsc_NestedArrayInsert_CommentsShiftToWrongElements_PinsCurrentBehavior()
    {
        JsonValue.Eol = "\n";
        // 风险同样适用于嵌套数组：修改发生在嵌套的 WscJsonArray 上。
        string input = "{\n  items:\n  [\n    # n0\n    10,\n    # n1\n    20\n  ]\n}\n";
        var options = new HjsonOptions { KeepWsc = true };
        var cfg = HjsonValue.Parse(input, options);

        cfg["items"].Qa().Insert(0, 5);

        var sw = new StringWriter();
        HjsonValue.Save(cfg, sw, options);
        string output = sw.ToString();

        // 槽 n0 原本在 10 之前，现在被写到新元素 5 之前。
        Assert.Contains("    # n0\n    5", output);
        // 槽 n1 原本在 10 与 20 之间，现在错放到 5 与 10 之间。
        Assert.Contains("    # n1\n    10", output);
        Assert.DoesNotContain("    # n0\n    10", output);
    }
}
