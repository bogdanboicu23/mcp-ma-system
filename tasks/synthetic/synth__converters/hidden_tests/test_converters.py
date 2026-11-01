import unittest
from converters import length, mass, temperature

class T(unittest.TestCase):
    def test_all(self):
        self.assertAlmostEqual(length.km_to_miles(10), 6.21371)
        self.assertAlmostEqual(mass.kg_to_pounds(2), 4.40924)
        self.assertAlmostEqual(temperature.c_to_f(100), 212.0)
        self.assertAlmostEqual(temperature.c_to_f(-40), -40.0)
