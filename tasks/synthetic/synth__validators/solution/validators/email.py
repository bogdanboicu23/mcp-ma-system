def is_valid(s):
    if ' ' in s or s.count('@') != 1:
        return False
    local, domain = s.split('@')
    return bool(local) and '.' in domain
