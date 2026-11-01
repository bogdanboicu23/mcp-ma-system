from inventory.parser import parse_line

def format_lines(lines):
    out = []
    for line in lines:
        name, qty = parse_line(line)
        out.append(f'{name} x{qty}')
    return out
