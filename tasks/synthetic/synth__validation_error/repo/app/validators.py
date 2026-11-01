def check_age(age):
    if not 0 <= age <= 150:
        raise ValueError(f'invalid age: {age}')
    return age
