#!/usr/bin/env python3
"""Arranca el mock_api.py del sandbox escuchando en 0.0.0.0 (para uso dentro de Docker).
El mock original solo escucha en 127.0.0.1, lo que lo hace inalcanzable desde otros contenedores."""
import runpy
import sys
from http.server import ThreadingHTTPServer

import http.server

_original = ThreadingHTTPServer


class _Abierto(_original):
    def __init__(self, direccion, manejador):
        super().__init__(("0.0.0.0", direccion[1]), manejador)


http.server.ThreadingHTTPServer = _Abierto
sys.argv[0] = "/caso/04_Sandbox_API/mock_api.py"
runpy.run_path("/caso/04_Sandbox_API/mock_api.py", run_name="__main__")
