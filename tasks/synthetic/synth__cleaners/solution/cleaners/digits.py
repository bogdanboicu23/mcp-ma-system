def mask_digits(text):
    return ''.join('#' if c.isdigit() else c for c in text)
