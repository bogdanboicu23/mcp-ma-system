import unittest
from kv.service import Service
from kv.cache import Cache

class T(unittest.TestCase):
    def test_invalidate(self):
        c = Cache()
        c.put("a", 1)
        c.invalidate("a")
        c.invalidate("missing")
        self.assertIsNone(c.get("a"))
    def test_update_visible(self):
        s = Service()
        s.update("k", 1)
        self.assertEqual(s.get("k"), 1)
        s.update("k", 2)
        self.assertEqual(s.get("k"), 2)
    def test_delete(self):
        s = Service()
        s.update("k", 1)
        s.get("k")
        s.delete("k")
        self.assertIsNone(s.get("k"))
