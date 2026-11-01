import math

def area(kind, **dims):
    if kind == 'rect':
        return dims['w'] * dims['h']
    if kind == 'circle':
        return math.pi * dims['r'] ** 2
    if kind == 'triangle':
        return dims['b'] * dims['h'] / 2
    raise ValueError(f'unknown shape {kind}')
