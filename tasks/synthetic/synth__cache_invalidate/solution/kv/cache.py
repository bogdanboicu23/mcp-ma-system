class Cache:
    def __init__(self):
        self.items = {}

    def get(self, key):
        return self.items.get(key)

    def put(self, key, value):
        self.items[key] = value

    def invalidate(self, key):
        self.items.pop(key, None)
