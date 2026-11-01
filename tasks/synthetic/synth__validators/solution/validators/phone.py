def is_valid(s):
    digits = s.replace(' ', '').replace('-', '')
    return len(digits) == 10 and digits.isdigit()
