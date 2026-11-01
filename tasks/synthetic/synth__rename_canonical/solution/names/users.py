from names.utils import canonical_name

def register(raw):
    return {'name': canonical_name(raw), 'active': True}
