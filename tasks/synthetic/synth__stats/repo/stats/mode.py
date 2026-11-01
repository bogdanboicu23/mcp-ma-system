def mode(xs):
    best = xs[0]
    for x in xs:
        if xs.count(x) > xs.count(best):
            best = x
    return best
