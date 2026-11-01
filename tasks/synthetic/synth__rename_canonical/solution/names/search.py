from names.utils import canonical_name

def matches(query, raw):
    return canonical_name(query) == canonical_name(raw)
