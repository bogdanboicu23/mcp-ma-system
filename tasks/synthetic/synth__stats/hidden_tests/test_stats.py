import unittest
from stats import mean, median, mode

class T(unittest.TestCase):
    def test_mean(self):
        self.assertAlmostEqual(mean.mean([1, 2, 3, 4]), 2.5)
    def test_median(self):
        self.assertEqual(median.median([5, 1, 3]), 3)
        self.assertEqual(median.median([4, 1, 3, 2]), 2.5)
    def test_mode(self):
        self.assertEqual(mode.mode([1, 2, 2, 3]), 2)
        self.assertEqual(mode.mode([3, 3, 1, 1, 2]), 1)
