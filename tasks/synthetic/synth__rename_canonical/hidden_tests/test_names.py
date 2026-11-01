import unittest
from names import utils, users, search

class T(unittest.TestCase):
    def test_renamed(self):
        self.assertEqual(utils.canonical_name("  ada   LOVELACE "), "Ada Lovelace")
        self.assertFalse(hasattr(utils, "normalize_name"))
    def test_users(self):
        self.assertEqual(users.register(" bob  smith"), {"name": "Bob Smith", "active": True})
    def test_search(self):
        self.assertTrue(search.matches("ann  lee", "ANN LEE"))
        self.assertFalse(search.matches("ann", "bob"))
