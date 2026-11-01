from names.utils import normalize_name

def matches(query, raw):
    return normalize_name(query) == normalize_name(raw)
