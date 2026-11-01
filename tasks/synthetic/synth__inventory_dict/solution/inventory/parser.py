def parse_line(line):
    name, qty = line.split(',')
    qty = int(qty)
    if qty < 0:
        raise ValueError('negative quantity')
    return {'name': name.strip(), 'qty': qty}
