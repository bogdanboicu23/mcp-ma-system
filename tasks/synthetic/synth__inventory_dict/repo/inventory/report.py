from inventory.parser import parse_line

def total_qty(lines):
    total = 0
    for line in lines:
        name, qty = parse_line(line)
        total += qty
    return total

def summary(lines):
    result = {}
    for line in lines:
        name, qty = parse_line(line)
        result[name] = result.get(name, 0) + qty
    return result
