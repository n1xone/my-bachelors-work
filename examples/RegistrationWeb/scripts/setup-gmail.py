#!/usr/bin/env python3
"""Configure Gmail locally; never put the app password in shell arguments or chat."""
import getpass
import json
import os
from pathlib import Path
import smtplib
import ssl
import sys
import tempfile


def main():
    if not sys.stdin.isatty():
        raise SystemExit('Spustte prikaz v beznem terminalu, kde se heslo zadava skryte.')
    print('Je potreba heslo aplikace Google: https://myaccount.google.com/apppasswords')
    email = input('E-mailova adresa odesilatele Gmail: ').strip()
    if email.count('@') != 1 or any(c.isspace() for c in email) or not all(email.split('@')):
        raise SystemExit('Zadejte uplnou e-mailovou adresu.')
    password = getpass.getpass('Heslo aplikace Google (zadani je skryte): ').replace(' ', '')
    if len(password) != 16 or not password.isascii() or not password.isalpha():
        raise SystemExit('Zadejte 16mistne heslo aplikace Google, nikoli bezne heslo k uctu.')
    print('Overuji pripojeni ke Gmail...')
    try:
        with smtplib.SMTP('smtp.gmail.com', 587, timeout=20) as smtp:
            smtp.ehlo()
            smtp.starttls(context=ssl.create_default_context())
            smtp.ehlo()
            smtp.login(email, password)
    except smtplib.SMTPAuthenticationError:
        raise SystemExit('Gmail odmitl prihlaseni. Zkontrolujte adresu, dvoufaktorove overeni a heslo aplikace. Nastaveni nebylo zmeneno.')
    except (OSError, smtplib.SMTPException):
        raise SystemExit('Nelze se pripojit ke Gmail SMTP. Zkontrolujte internet a pristup k portu 587. Nastaveni nebylo zmeneno.')
    root = Path(__file__).resolve().parents[1]
    directory = root / 'Data'
    directory.mkdir(exist_ok=True)
    config = {'Smtp': {'Delivery': 'Network', 'Host': 'smtp.gmail.com', 'Port': 587,
                       'From': email, 'Username': email, 'Password': password}}
    fd, temporary = tempfile.mkstemp(prefix='.smtp-', dir=directory)
    try:
        with os.fdopen(fd, 'w') as stream:
            json.dump(config, stream)
        os.replace(temporary, directory / 'smtp.local.json')
    finally:
        if os.path.exists(temporary): os.unlink(temporary)
    print('Gmail je pripojen. Heslo je ulozeno pouze lokalne v Data/smtp.local.json (pristup ma vlastnik).')
    print('Obnovte stranku registrace a stisknete Znovu odeslat potvrzeni.')
    print('Prihlaseni bylo overeno; tento prikaz neodesila testovaci e-mail.')


if __name__ == '__main__':
    try:
        main()
    except (KeyboardInterrupt, EOFError):
        raise SystemExit('\nNastaveni bylo zruseno.')
