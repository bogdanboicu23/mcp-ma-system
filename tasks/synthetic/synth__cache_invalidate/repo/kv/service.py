from kv.store import Store
from kv.cache import Cache

class Service:
    def __init__(self):
        self.store = Store()
        self.cache = Cache()

    def get(self, key):
        value = self.cache.get(key)
        if value is None:
            value = self.store.get(key)
            if value is not None:
                self.cache.put(key, value)
        return value

    def update(self, key, value):
        self.store.set(key, value)
