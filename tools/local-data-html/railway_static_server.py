import os
import signal
import threading
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer


SERVE_DIR = os.getenv("SERVE_DIR") or ("/app" if os.path.isdir("/app") else os.getcwd())
DEFAULT_PORT = 8010


def build_ports():
    ports = [DEFAULT_PORT]
    raw_port = os.getenv("PORT", "").strip()
    if raw_port.isdigit():
      env_port = int(raw_port)
      if env_port not in ports:
        ports.append(env_port)
    return ports


def serve_on_port(port):
    handler = partial(SimpleHTTPRequestHandler, directory=SERVE_DIR)
    server = ThreadingHTTPServer(("0.0.0.0", port), handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    return server, thread


def main():
    ports = build_ports()
    servers = []
    for port in ports:
        server, thread = serve_on_port(port)
        servers.append((server, thread, port))

    stop_event = threading.Event()

    def shutdown(*_args):
        for server, _, _ in servers:
            server.shutdown()
            server.server_close()
        stop_event.set()

    signal.signal(signal.SIGTERM, shutdown)
    signal.signal(signal.SIGINT, shutdown)

    print("Serving static files from", SERVE_DIR, "on ports", ", ".join(str(port) for _, _, port in servers), flush=True)
    stop_event.wait()


if __name__ == "__main__":
    main()