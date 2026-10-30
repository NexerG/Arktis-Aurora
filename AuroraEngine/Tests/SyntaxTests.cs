using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Text;
using System.Text;

namespace ArctisAurora.Tests
{
    internal static class SyntaxTests
    {
        // One letter per character: . plain, k keyword, s string, n number, c comment.
        private static string Tokens(string line, string language, ref SyntaxState state)
        {
            SyntaxToken[] tokens = new SyntaxToken[line.Length];
            state = SyntaxTokenizer.Tokenize(line, language, state, tokens);
            return new string(tokens.Select(token => ".ksnc"[(int)token]).ToArray());
        }

        private static string Tokens(string line, string language)
        {
            SyntaxState state = SyntaxState.None;
            return Tokens(line, language, ref state);
        }

        [A_XSDActionDependency("Syntax.CSharpLine", "Test")]
        private static IEnumerator<int> CSharpLine(TestContext t)
        {
            t.Check(Tokens("int x = 42; // hi", "cs") == "kkk.....nn..ccccc", "keyword, number and line comment");
            t.Check(Tokens("\"a\\\"b\" 'c'", "csharp") == "ssssss.sss", "an escaped quote stays inside its string");
            t.Check(Tokens("var vec4 = 0x1F;", "cs") == "kkk........nnnn.", "a GLSL type is no C# keyword; hex is one number");
            yield break;
        }

        [A_XSDActionDependency("Syntax.CommentSpansLines", "Test")]
        private static IEnumerator<int> CommentSpansLines(TestContext t)
        {
            SyntaxState state = SyntaxState.None;
            t.Check(Tokens("a /* b", "cs", ref state) == "..cccc" && state == SyntaxState.BlockComment, "a block comment left open carries down");
            t.Check(Tokens("c */ d", "cs", ref state) == "cccc.." && state == SyntaxState.None, "the next line is comment up to the close");

            state = SyntaxState.None;
            t.Check(Tokens("x = \"\"\"a", "py", ref state) == "....ssss" && state == SyntaxState.TripleDouble, "a Python triple quote carries down");
            t.Check(Tokens("b\"\"\" + 1", "python", ref state) == "ssss...n" && state == SyntaxState.None, "and closes on the next line");

            state = SyntaxState.None;
            t.Check(Tokens("<!-- a", "xml", ref state) == "cccccc" && state == SyntaxState.XmlComment, "an XML comment carries down");
            t.Check(Tokens("b --> <c/>", "xml", ref state) == "ccccc.kkkk" && state == SyntaxState.None, "and closes before the next tag");
            yield break;
        }

        [A_XSDActionDependency("Syntax.Languages", "Test")]
        private static IEnumerator<int> Languages(TestContext t)
        {
            t.Check(Tokens("def f(): # x", "py") == "kkk......ccc", "Python keywords and # comments");
            t.Check(Tokens("<a href=\"x\">t</a>", "xml") == "kk......sssk.kkkk", "XML tags, attribute values and text");
            t.Check(Tokens("#version 450", "glsl") == "kkkkkkkk.nnn", "a GLSL directive and its number");
            t.Check(Tokens("return 1; // x", "") == "kkkkkk.n..cccc", "an unnamed language takes the C-like rules");
            yield break;
        }

        [A_XSDActionDependency("Syntax.Latex", "Test")]
        private static IEnumerator<int> Latex(TestContext t)
        {
            t.Check(Tokens(@"\section{Intro} % x", "latex") == "kkkkkkkk........ccc", "commands and % comments");
            t.Check(Tokens("See $x^2$ at 12pt.", "tex") == "....sssss....nnnn.", "inline math and a number with its unit");
            t.Check(Tokens(@"\% 5", "latex") == "kk.n", "an escaped % is a command, not a comment");

            SyntaxState state = SyntaxState.None;
            t.Check(Tokens(@"\[ a", "latex", ref state) == "ssss" && state == SyntaxState.TexMath, "display math carries down");
            t.Check(Tokens(@"b \] c", "latex", ref state) == "ssss.." && state == SyntaxState.None, "and closes on the next line");
            yield break;
        }

        [A_XSDActionDependency("Syntax.XmlSourceRoundTrip", "Test")]
        private static IEnumerator<int> XmlSourceRoundTrip(TestContext t)
        {
            foreach (string source in new[] { "\uFEFF<?xml version=\"1.0\"?>\r\n<A B=\"1\"/>\r\n", "<A>\n  <!-- c -->\n</A>\n", "<A/>", "" })
            {
                byte[] bytes = Encoding.UTF8.GetBytes(source);
                string from = Path.Combine(Path.GetTempPath(), $"aurora-xml-{Guid.NewGuid():N}.xml");
                string to = Path.Combine(Path.GetTempPath(), $"aurora-xml-{Guid.NewGuid():N}.xml");
                File.WriteAllBytes(from, bytes);

                RichTextDocument document = RichTextDocument.LoadSource(from, "xml");
                bool xml = document.blocks.OfType<NoteBlock>().All(b => b.stylingType == TextStyleType.Code && b.language == "xml");
                document.Save(to);
                byte[] back = File.ReadAllBytes(to);
                File.Delete(from);
                File.Delete(to);

                string shown = source.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\uFEFF", "BOM");
                t.Check(back.SequenceEqual(bytes), $"'{shown}' saves back byte for byte");
                t.Check(xml, $"'{shown}' loads as XML code lines");
            }
            yield break;
        }
    }
}
