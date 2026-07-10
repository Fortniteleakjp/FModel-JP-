# SPDX-License-Identifier: GPL-3.0-or-later
"""FModel to Blender bridge for h4lfheart/UEFormat.

This script is started by FModel through Blender's --python argument. Blender API
operations stay on the main thread while a small loopback-only socket accepts
subsequent animation transfers from the same FModel session.
"""

import json
import os
import queue
import socket
import sys
import threading
import traceback

import bpy


BRIDGE_HOST = "127.0.0.1"
BRIDGE_PORT = 24280
BRIDGE_SERVICE = "FModelBlenderBridge"
STATE_KEY = "_fmodel_blender_bridge"


class BridgeState:
    def __init__(self):
        self.requests = queue.Queue()
        self.current = None
        self.socket = None


def popup(message, icon="INFO"):
    lines = str(message).splitlines() or [str(message)]

    def draw(self, _context):
        for line in lines:
            self.layout.label(text=line)

    try:
        bpy.context.window_manager.popup_menu(draw, title="FModel - UEFormat", icon=icon)
    except Exception:
        print(f"[FModel] {message}")


def normalize_request(payload, request_path=None):
    if not isinstance(payload, dict) or payload.get("command") != "import_animations":
        return None

    files = []
    for path in payload.get("files", []):
        if not isinstance(path, str):
            continue
        full_path = os.path.abspath(path)
        if full_path.lower().endswith(".ueanim") and os.path.isfile(full_path):
            files.append(full_path)

    if not files:
        return None

    return {
        "files": list(dict.fromkeys(files)),
        "request_path": request_path,
        "missing_addon_notified": False,
        "missing_rig_notified": False,
    }


def active_armature():
    obj = bpy.context.object
    if obj is None:
        return None
    if obj.type == "ARMATURE":
        return obj
    if obj.type == "MESH":
        for modifier in obj.modifiers:
            if modifier.type == "ARMATURE" and modifier.object is not None:
                return modifier.object
    return None


def ueformat_operator_available():
    try:
        bpy.ops.uf.import_ueanim.poll()
        return True
    except Exception:
        return False


def import_request(state, request):
    if not ueformat_operator_available():
        if not request["missing_addon_notified"]:
            popup(
                "UEFormatアドオンが有効になっていません。\n"
                "h4lfheart/UEFormatをインストールして有効化してください。",
                "ERROR",
            )
            request["missing_addon_notified"] = True
        return False

    if active_armature() is None:
        if not request["missing_rig_notified"]:
            popup(
                "アニメーションを適用する互換リグを選択してください。\n"
                "リグまたは、そのリグを使用するメッシュを選択すると自動で読み込みます。",
                "INFO",
            )
            request["missing_rig_notified"] = True
        return False

    imported = 0
    for path in request["files"]:
        directory = os.path.dirname(path) + os.sep
        result = bpy.ops.uf.import_ueanim(
            "EXEC_DEFAULT",
            directory=directory,
            files=[{"name": os.path.basename(path)}],
        )
        if "FINISHED" in result:
            imported += 1

    popup(f"{imported}件のUEFormatアニメーションを読み込みました。", "CHECKMARK")
    request_path = request.get("request_path")
    if request_path:
        try:
            os.remove(request_path)
        except OSError:
            pass
    return True


def process_requests(state):
    try:
        if state.current is None:
            try:
                state.current = state.requests.get_nowait()
            except queue.Empty:
                return 0.25

        if import_request(state, state.current):
            state.current = None
    except Exception:
        traceback.print_exc()
        popup(
            "UEFormatアニメーションの読み込みに失敗しました。\n"
            "詳細はBlenderのシステムコンソールを確認してください。",
            "ERROR",
        )
        state.current = None

    return 0.25


def receive_message(connection):
    data = bytearray()
    while len(data) < 1024 * 1024:
        chunk = connection.recv(4096)
        if not chunk:
            break
        data.extend(chunk)
        if b"\n" in chunk:
            break

    raw_message = bytes(data).split(b"\n", 1)[0]
    return json.loads(raw_message.decode("utf-8"))


def socket_server(state):
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    try:
        server.bind((BRIDGE_HOST, BRIDGE_PORT))
        server.listen(4)
        state.socket = server
        print(f"[FModel] Blender bridge listening on {BRIDGE_HOST}:{BRIDGE_PORT}")

        while True:
            connection, _address = server.accept()
            with connection:
                try:
                    payload = receive_message(connection)
                    if isinstance(payload, dict) and payload.get("command") == "status":
                        response = {
                            "ok": True,
                            "service": BRIDGE_SERVICE,
                            "ueformat_available": ueformat_operator_available(),
                        }
                    else:
                        request = normalize_request(payload)
                        if request is None:
                            response = {
                                "ok": False,
                                "service": BRIDGE_SERVICE,
                                "ueformat_available": ueformat_operator_available(),
                            }
                        else:
                            state.requests.put(request)
                            response = {
                                "ok": True,
                                "service": BRIDGE_SERVICE,
                                "ueformat_available": ueformat_operator_available(),
                            }
                    connection.sendall((json.dumps(response) + "\n").encode("utf-8"))
                except Exception:
                    traceback.print_exc()
                    response = {
                        "ok": False,
                        "service": BRIDGE_SERVICE,
                        "ueformat_available": False,
                    }
                    try:
                        connection.sendall((json.dumps(response) + "\n").encode("utf-8"))
                    except OSError:
                        pass
    except OSError as error:
        print(f"[FModel] Blender bridge socket unavailable: {error}")
    finally:
        server.close()


def initial_request_path():
    if "--" not in sys.argv:
        return None
    arguments = sys.argv[sys.argv.index("--") + 1 :]
    return arguments[0] if arguments else None


def load_initial_request(state):
    request_path = initial_request_path()
    if not request_path or not os.path.isfile(request_path):
        return

    try:
        with open(request_path, "r", encoding="utf-8") as request_file:
            request = normalize_request(json.load(request_file), request_path)
        if request is not None:
            state.requests.put(request)
    except Exception:
        traceback.print_exc()
        popup("FModelからのインポート要求を読み込めませんでした。", "ERROR")


def start_bridge():
    previous_state = bpy.app.driver_namespace.get(STATE_KEY)
    if previous_state is not None:
        load_initial_request(previous_state)
        return

    state = BridgeState()
    bpy.app.driver_namespace[STATE_KEY] = state
    load_initial_request(state)

    server_thread = threading.Thread(target=socket_server, args=(state,), daemon=True)
    server_thread.start()
    bpy.app.timers.register(lambda: process_requests(state), first_interval=0.5, persistent=True)


start_bridge()
