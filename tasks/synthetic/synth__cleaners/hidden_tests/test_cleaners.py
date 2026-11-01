import unittest
from cleaners import html, spaces, digits

class T(unittest.TestCase):
    def test_html(self):
        self.assertEqual(html.strip_html("<p>Hi <b>there</b></p>"), "Hi there")
    def test_spaces(self):
        self.assertEqual(spaces.collapse_ws("  a \t b\n\nc "), "a b c")
    def test_digits(self):
        self.assertEqual(digits.mask_digits("call 555-1234"), "call ###-####")
