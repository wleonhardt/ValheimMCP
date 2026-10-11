import os, sys, unittest
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
import csscan, lint_catch, lint_writes, lint_tools


class MaskTests(unittest.TestCase):
    def test_comments_and_strings_are_blanked_but_offsets_survive(self):
        src = 'int a; // catch { }\nvar s = "catch { }"; /* catch */ var v = @"x ""catch"" y"; char c = \'"\';\nreal();'
        m = csscan.mask(src)
        self.assertEqual(len(m), len(src))
        self.assertNotIn('catch', m)
        self.assertIn('real();', m)
        self.assertEqual(m.count('\n'), src.count('\n'))


class CatchLintTests(unittest.TestCase):
    def flags(self, body):
        return [k for _, k in lint_catch.flagged('x.cs', body)]

    def test_empty_catch_is_flagged(self):
        self.assertEqual(self.flags('void M(){ try { A(); } catch { } }'), ['empty'])

    def test_typed_empty_catch_is_flagged(self):
        self.assertEqual(self.flags('void M(){ try { A(); } catch (Exception) { } }'), ['empty'])

    def test_swallowing_catch_is_flagged(self):
        self.assertEqual(self.flags('void M(){ try { A(); } catch (FormatException) { return null; } }'), ['silent'])

    def test_bare_catch_that_returns_is_flagged(self):
        self.assertEqual(self.flags('void M(){ try { A(); } catch { return 1; } }'), ['bare'])

    def test_logging_or_rethrow_is_fine(self):
        for body in ('catch (Exception ex) { U.Once("a", ex); }', 'catch (Exception ex) { Plugin.Log?.LogWarning("x"); }', 'catch { throw; }', 'catch (X e) { Fault.Report(e, "a"); }'):
            self.assertEqual(self.flags('void M(){ try { A(); } ' + body + ' }'), [], body)

    def test_allow_comment_accepts_a_site(self):
        self.assertEqual(self.flags('void M(){ try { A(); }\n // allow: the file may not exist yet\n catch (IOException) { } }'), [])
        self.assertEqual(self.flags('void M(){ try { A(); } catch (IOException) { /* allow: gone already */ } }'), [])

    def test_catch_in_a_string_or_comment_is_ignored(self):
        self.assertEqual(self.flags('var s = "catch { }"; // catch { }\n'), [])

    def test_exception_filter(self):
        self.assertEqual(self.flags('void M(){ try { A(); } catch (X e) when (e.Code == 1) { } }'), ['empty'])

    def test_the_repository_is_not_worse_than_its_baseline(self):
        self.assertEqual(lint_catch.main([]), 0)


class WriteLintTests(unittest.TestCase):
    def test_a_new_file_cannot_create_zdos(self):
        bad = lint_writes.check('ext/Sneaky.cs', 'class S { void M() { ZDOMan.instance.CreateNewZDO(p, h); } }')
        self.assertEqual([w for _, w, _ in bad], ['creating a ZDO'])

    def test_the_owner_file_may(self):
        self.assertEqual(lint_writes.check('ext/Plans.cs', 'ZDOMan.instance.CreateNewZDO(p, h); ZDOMan.instance.DestroyZDO(z);'), [])

    def test_a_mention_in_a_comment_or_string_is_not_a_use(self):
        self.assertEqual(lint_writes.check('ext/Other.cs', '// CreateNewZDO(\nvar s = "DestroyZDO(";'), [])

    def test_kick_and_ban_are_allowed_nowhere(self):
        self.assertTrue(lint_writes.check('ext/Plans.cs', 'ZNet.instance.Kick(name);'))

    def test_the_repository_obeys_the_allowlist(self):
        self.assertEqual(lint_writes.main(), 0)


class ToolLintTests(unittest.TestCase):
    def test_the_catalogue_is_current(self):
        tools = lint_tools.collect()
        self.assertEqual(lint_tools.problems(tools), [])
        with open(lint_tools.DOC, encoding='utf-8') as f:
            self.assertEqual(f.read(), lint_tools.render(tools))

    def test_first_sentence_does_not_stop_at_eg(self):
        self.assertTrue(lint_tools.first_sentence('Run a console command (e.g. pos) and return the lines. More.').endswith('lines.'))

    def test_names_are_snake_case(self):
        self.assertTrue(lint_tools.NAME.match('plan_apply'))
        self.assertFalse(lint_tools.NAME.match('planApply'))
        self.assertFalse(lint_tools.NAME.match('plan__apply'))


if __name__ == '__main__':
    unittest.main()
