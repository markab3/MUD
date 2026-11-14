#!/usr/bin/env python3
"""Scripted telnet session for acceptance runs (card P5-3).

Usage:  python3 -I scripts/telnet_session.py PORT NAME PASSWORD FILE

Connects to a running game over telnet, logs in with the one-line 'connect NAME PASSWORD' form, then sends each line of FILE as one
command and prints what the game answered (colour codes removed). A line 'sleep:N' waits N seconds and prints whatever arrives
meanwhile (storm messages, for example). Blank lines and lines starting with '#' are ignored. The last thing sent is 'quit'.
This is a test tool for a throwaway database: the password travels on the command line.
"""
import re
import socket
import sys
import time

COLOUR_CODES = re.compile(r"\x1b\[[0-9;]*m")


def strip_negotiation(data):
    """Removes telnet option negotiation bytes and decodes the rest."""
    out = bytearray()
    index = 0
    while index < len(data):
        byte = data[index]
        if byte == 255 and index + 1 < len(data):
            verb = data[index + 1]
            if verb in (251, 252, 253, 254):
                index += 3
                continue
            if verb == 250:
                end = data.find(bytes([255, 240]), index)
                index = end + 2 if end >= 0 else len(data)
                continue
            index += 2
            continue
        out.append(byte)
        index += 1
    return out.decode("utf-8", "replace")


def read_for(connection, seconds):
    """Reads whatever arrives within the given number of seconds."""
    connection.settimeout(seconds)
    received = b""
    end = time.time() + seconds
    while time.time() < end:
        try:
            chunk = connection.recv(65536)
        except socket.timeout:
            break
        if not chunk:
            break
        received += chunk
    return COLOUR_CODES.sub("", strip_negotiation(received)).replace("\r", "")


def main(arguments):
    if len(arguments) != 4:
        print(__doc__)
        return 2
    port, name, password, path = int(arguments[0]), arguments[1], arguments[2], arguments[3]
    with open(path, encoding="utf-8") as handle:
        lines = [line.rstrip("\n") for line in handle if line.strip() and not line.startswith("#")]

    connection = socket.create_connection(("127.0.0.1", port))
    read_for(connection, 1.0)
    connection.sendall(("connect %s %s\r\n" % (name, password)).encode())
    print(read_for(connection, 1.5))
    for line in lines:
        if line.startswith("sleep:"):
            print(read_for(connection, float(line[len("sleep:"):])))
            continue
        print(">>> " + line)
        connection.sendall((line + "\r\n").encode())
        print(read_for(connection, 0.8))
    connection.sendall(b"quit\r\n")
    print(read_for(connection, 0.8))
    connection.close()
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
