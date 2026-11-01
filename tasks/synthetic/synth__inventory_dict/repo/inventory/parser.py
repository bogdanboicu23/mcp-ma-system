def parse_line(line):
    name, qty = line.split(',')
    return name.strip(), int(qty)
