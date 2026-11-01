import unittest
from validators import email, phone, zipcode

class T(unittest.TestCase):
    def test_email(self):
        self.assertTrue(email.is_valid("a@b.co"))
        self.assertFalse(email.is_valid("a@@b.co"))
        self.assertFalse(email.is_valid("@b.co"))
        self.assertFalse(email.is_valid("a@bco"))
        self.assertFalse(email.is_valid("a b@c.co"))
    def test_phone(self):
        self.assertTrue(phone.is_valid("555-123 4567"))
        self.assertFalse(phone.is_valid("555-123-456"))
        self.assertFalse(phone.is_valid("555-123-45a7"))
    def test_zip(self):
        self.assertTrue(zipcode.is_valid("12345"))
        self.assertTrue(zipcode.is_valid("12345-6789"))
        self.assertFalse(zipcode.is_valid("1234"))
        self.assertFalse(zipcode.is_valid("12345-678"))
        self.assertFalse(zipcode.is_valid("abcde"))
