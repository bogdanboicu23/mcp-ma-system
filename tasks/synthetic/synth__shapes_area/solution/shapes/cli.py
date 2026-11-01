from shapes.geometry import area

def describe(w, h):
    return f"{w} x {h} = {area('rect', w=w, h=h)}"
