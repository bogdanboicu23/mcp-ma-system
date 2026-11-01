import math, unittest
from shapes import geometry, report, cli

class T(unittest.TestCase):
    def test_area(self):
        self.assertEqual(geometry.area("rect", w=2, h=3), 6)
        self.assertAlmostEqual(geometry.area("circle", r=1), math.pi)
        self.assertAlmostEqual(geometry.area("triangle", b=4, h=3), 6.0)
        with self.assertRaises(ValueError):
            geometry.area("hexagon", s=1)
        with self.assertRaises(TypeError):
            geometry.area(2, 3)
    def test_callers(self):
        self.assertEqual(report.total_area([(1, 2), (3, 4)]), 14)
        self.assertEqual(cli.describe(2, 5), "2 x 5 = 10")
