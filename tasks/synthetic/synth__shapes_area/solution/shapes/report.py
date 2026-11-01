from shapes.geometry import area

def total_area(rects):
    return sum(area('rect', w=w, h=h) for w, h in rects)
