#nullable enable

using System;
using System.IO;

using Hjson;

using Xunit;

namespace Hjson.Tests;

// These tests are the executable evidence referenced by ANALYSIS.md.
// They CHARACTERIZE (pin) current behavior; they do NOT assert that the
// behavior is desirable. The pinned behavior is a documented risk.
[Collection("EolTests")]
public class UnderstandingProofTests
{
    const string NestedArrayInput =
        "{\n" +
        "  arr: [\n" +
        "    # c0\n" +
        "    10\n" +
        "    # c1\n" +
        "    20\n" +
        "    # c2\n" +
        "    30\n" +
        "  ]\n" +
        "}\n";

    static string SaveWsc(JsonValue value)
    {
        var options = new HjsonOptions { KeepWsc = true };
        var writer = new StringWriter();
        HjsonValue.Save(value, writer, options);
        return writer.ToString();
    }

    // CHARACTERIZATION TEST (exposes the risk, pins the current result):
    // Removing an array element shifts the JsonValue indices but never
    // shifts the WscJsonArray.Comments slots, so every comment after the
    // deletion point becomes associated with the wrong element.
    [Fact]
    public void Characterization_NestedArrayRemove_CommentSlotsDoNotShiftWithElements()
    {
        var savedEol = JsonValue.Eol;
        JsonValue.Eol = "\n";
        try
        {
            var options = new HjsonOptions { KeepWsc = true };
            var root = HjsonValue.Parse(NestedArrayInput, options);
            var arr = Assert.IsType<WscJsonArray>(root["arr"]);

            // Invariant while the structure is untouched: 3 elements and
            // n + 1 comment slots (one leading slot plus one per element).
            Assert.Equal(3, arr.Count);
            Assert.Equal(4, arr.Comments.Count);
            Assert.Equal(10L, (long)arr[0]);
            Assert.Equal(20L, (long)arr[1]);
            Assert.Equal(30L, (long)arr[2]);

            arr.RemoveAt(0);

            // Current behavior: the value list shrinks, the comment-slot
            // list does not.
            Assert.Equal(2, arr.Count);
            Assert.Equal(4, arr.Comments.Count);

            string output = SaveWsc(root);

            // "# c0" (captured before the deleted element 10) is now emitted
            // before the survivor that took index 0 (value 20). The comment
            // captured after the last survivor (30) becomes a trailing
            // orphan before the closing bracket.
            Assert.Equal(
                "{\n" +
                "  arr:\n" +
                "  [\n" +
                "    # c0\n" +
                "    20\n" +
                "    # c1\n" +
                "    30\n" +
                "    # c2\n" +
                "  ]\n" +
                "}",
                output);
        }
        finally
        {
            JsonValue.Eol = savedEol;
        }
    }

    // CHARACTERIZATION TEST (pins the safe half of the invariant):
    // replacing a value in place (same index) keeps comments aligned.
    [Fact]
    public void Characterization_NestedArrayReplaceInPlace_CommentsStayAligned()
    {
        var savedEol = JsonValue.Eol;
        JsonValue.Eol = "\n";
        try
        {
            var options = new HjsonOptions { KeepWsc = true };
            var root = HjsonValue.Parse(NestedArrayInput, options);
            var arr = Assert.IsType<WscJsonArray>(root["arr"]);

            arr[0] = 100;

            Assert.Equal(
                "{\n" +
                "  arr:\n" +
                "  [\n" +
                "    # c0\n" +
                "    100\n" +
                "    # c1\n" +
                "    20\n" +
                "    # c2\n" +
                "    30\n" +
                "  ]\n" +
                "}",
                SaveWsc(root));
        }
        finally
        {
            JsonValue.Eol = savedEol;
        }
    }
}
