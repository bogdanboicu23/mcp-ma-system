from inventory.parser import parse_line

def total_qty(lines):
    total = 0
    for line in lines:
        item = parse_line(line)
        total += item['qty']
    return total

def summary(lines):
    result = {}
    for line in lines:
        item = parse_line(line)
        result[item['name']] = result.get(item['name'], 0) + item['qty']
    return result
