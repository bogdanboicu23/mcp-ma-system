import unittest
from inventory import parser, report, cli

LINES = ["apple, 3", " pear ,2", "apple,4"]

class T(unittest.TestCase):
    def test_parse(self):
        self.assertEqual(parser.parse_line(" kiwi , 5"), {"name": "kiwi", "qty": 5})
        with self.assertRaises(ValueError):
            parser.parse_line("kiwi,-1")
    def test_callers(self):
        self.assertEqual(report.total_qty(LINES), 9)
        self.assertEqual(report.summary(LINES), {"apple": 7, "pear": 2})
        self.assertEqual(cli.format_lines(LINES), ["apple x3", "pear x2", "apple x4"])
