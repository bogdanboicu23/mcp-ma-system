def clip(text, n):
    return text if len(text) <= n else text[:n - 3] + '...'
