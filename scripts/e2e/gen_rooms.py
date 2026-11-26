#!/usr/bin/env python3
"""Generate and import the sample_land map over telnet for the e2e run (creator commands 'genmap' and 'import').

Usage:  python3 -I scripts/e2e/gen_rooms.py PORT      (character and password come from E2E_USER / E2E_PW in the environment,
so the password never appears in a command line.)
"""
import os
import socket
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
from telnet_session import read_for  # noqa: E402  (the helper of the acceptance scripts)


def main():
    port = int(sys.argv[1])
    name, password = os.environ["E2E_USER"], os.environ["E2E_PW"]
    connection = socket.create_connection(("127.0.0.1", port))
    read_for(connection, 1.0)
    connection.sendall(("connect %s %s\r\n" % (name, password)).encode())
    login = read_for(connection, 2.0)
    if "Welcome" not in login and "Hello" not in login and len(login.strip()) == 0:
        print("no answer to login")
        return 1
    for line in ("genmap sample_land", "import sample_land"):
        connection.sendall((line + "\r\n").encode())
        print(">>> " + line)
        print(read_for(connection, 3.0)[-400:])
    connection.sendall(b"quit\r\n")
    connection.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
