from names.utils import normalize_name

def register(raw):
    return {'name': normalize_name(raw), 'active': True}
