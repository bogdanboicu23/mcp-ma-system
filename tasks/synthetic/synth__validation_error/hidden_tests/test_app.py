import unittest
from app import errors, validators, api

class T(unittest.TestCase):
    def test_hierarchy(self):
        self.assertTrue(issubclass(errors.ValidationError, errors.AppError))
    def test_validator(self):
        self.assertEqual(validators.check_age(30), 30)
        with self.assertRaises(errors.ValidationError):
            validators.check_age(200)
    def test_api(self):
        self.assertEqual(api.handle(30), (200, "ok"))
        self.assertEqual(api.handle(-1), (400, "invalid age: -1"))
