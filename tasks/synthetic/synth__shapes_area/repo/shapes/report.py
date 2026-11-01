from shapes.geometry import area

def total_area(rects):
    return sum(area(w, h) for w, h in rects)
