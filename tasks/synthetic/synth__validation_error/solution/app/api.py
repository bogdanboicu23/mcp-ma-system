from app.errors import ValidationError
from app.validators import check_age

def handle(age):
    try:
        check_age(age)
    except ValidationError as e:
        return 400, str(e)
    return 200, 'ok'
