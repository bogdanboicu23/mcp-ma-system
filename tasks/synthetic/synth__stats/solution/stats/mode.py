def mode(xs):
    return min(xs, key=lambda x: (-xs.count(x), x))
