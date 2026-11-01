SMALL = {'a', 'an', 'the', 'of', 'in', 'on'}

def title_case(text):
    words = text.split()
    return ' '.join(w if (i > 0 and w.lower() in SMALL) else w.capitalize() for i, w in enumerate(words))
