from inventory.parser import parse_line

def format_lines(lines):
    out = []
    for line in lines:
        item = parse_line(line)
        out.append(f"{item['name']} x{item['qty']}")
    return out
