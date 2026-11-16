"""A stand-in for an OpenAI-compatible model server, for the website's AI videos (eng/showcase/ai-chat.sh).

It answers /v1/models and streams /v1/chat/completions a few words at a time, as a model would. Every answer comes from
the script file it's given (JSON): a list of turns, each {"when": text the last message contains, or "tool" for the
answer to a tool's result, "say": the reply, "tool": a tool to call, "args": its arguments}. Nothing reaches a model.

Usage: python3 -I eng/showcase/model-stand-in.py <port> <script.json>
"""

import json
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = int(sys.argv[1])
with open(sys.argv[2], encoding="utf-8") as f:
    SCRIPT = json.load(f)


def turn_for(messages):
    last = messages[-1] if messages else {}
    content = last.get("content") or ""
    if isinstance(content, list):
        content = " ".join(part.get("text", "") for part in content if isinstance(part, dict))
    for turn in SCRIPT:
        if turn["when"] == "tool" and last.get("role") == "tool":
            return turn
        if turn["when"] != "tool" and last.get("role") == "user" and turn["when"] in content:
            return turn
    return {"say": "I can't help with that one."}


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, format, *args):
        pass

    def do_GET(self):
        if self.path.rstrip("/") == "/v1/models":
            body = json.dumps({"object": "list", "data": [{"id": "local-model"}]}).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        else:
            self.send_error(404)

    def do_POST(self):
        if self.path.rstrip("/") != "/v1/chat/completions":
            self.send_error(404)
            return
        request = json.loads(self.rfile.read(int(self.headers.get("Content-Length", "0"))))
        turn = turn_for(request.get("messages", []))
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Transfer-Encoding", "chunked")
        self.end_headers()

        def send(data):
            chunk = ("data: " + json.dumps(data) + "\n\n").encode()
            self.wfile.write(b"%x\r\n%s\r\n" % (len(chunk), chunk))
            self.wfile.flush()

        time.sleep(0.6)
        words = turn.get("say", "").split(" ")
        for i, word in enumerate(words):
            send({"choices": [{"index": 0, "delta": {"content": word + (" " if i < len(words) - 1 else "")}}]})
            time.sleep(0.045)
        if "tool" in turn:
            call = {"index": 0, "id": "call_1", "type": "function", "function": {"name": turn["tool"], "arguments": json.dumps(turn.get("args", {}))}}
            send({"choices": [{"index": 0, "delta": {"tool_calls": [call]}}]})
            send({"choices": [{"index": 0, "delta": {}, "finish_reason": "tool_calls"}]})
        else:
            send({"choices": [{"index": 0, "delta": {}, "finish_reason": "stop"}]})
        send({"choices": [], "usage": {"prompt_tokens": 420, "completion_tokens": len(words)}})
        self.wfile.write(b"e\r\ndata: [DONE]\n\n\r\n0\r\n\r\n")
        self.wfile.flush()


ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
