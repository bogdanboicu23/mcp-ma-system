from app.errors import ValidationError

def check_age(age):
    if not 0 <= age <= 150:
        raise ValidationError(f'invalid age: {age}')
    return age
