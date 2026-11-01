from app.validators import check_age

def handle(age):
    check_age(age)
    return 200, 'ok'
