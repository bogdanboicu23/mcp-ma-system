import unittest
from formatters import slug, title, clip

class T(unittest.TestCase):
    def test_slug(self):
        self.assertEqual(slug.slugify("  Hello, World!  "), "hello-world")
        self.assertEqual(slug.slugify("A--B  c"), "a-b-c")
    def test_title(self):
        self.assertEqual(title.title_case("the lord of the rings"), "The Lord of the Rings")
        self.assertEqual(title.title_case("a tale in one"), "A Tale in One")
    def test_clip(self):
        self.assertEqual(clip.clip("hello", 5), "hello")
        self.assertEqual(clip.clip("hello world", 8), "hello...")
