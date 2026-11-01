def is_valid(s):
    if len(s) == 5:
        return s.isdigit()
    return len(s) == 10 and s[:5].isdigit() and s[5] == '-' and s[6:].isdigit()
