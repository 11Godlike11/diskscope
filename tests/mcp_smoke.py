"""Exercise the packaged stdio server. Creates/deletes only new temporary fixtures."""
import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import sys
import tempfile
import threading
import time

checks = 0

def check(condition, name):
    global checks
    if not condition:
        raise AssertionError(name)
    checks += 1
    print(f"PASS: {name}", flush=True)

class Client:
    def __init__(self, exe, state, read_only=False):
        args = [str(exe), "mcp", "--data-dir", str(state), "--idle-seconds", "1"]
        if read_only:
            args.append("--read-only")
        self.process = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.PIPE, text=True, encoding="utf-8", bufsize=1)
        self.responses = queue.Queue()
        self.errors = []
        self.counter = 0
        threading.Thread(target=self._read, daemon=True).start()
        threading.Thread(target=lambda: self.errors.extend(self.process.stderr), daemon=True).start()

    def _read(self):
        for line in self.process.stdout:
            try:
                self.responses.put(json.loads(line))
            except ValueError:
                self.responses.put({"invalid_stdout": line})

    def request(self, method, params=None):
        self.counter += 1
        request = {"jsonrpc": "2.0", "id": self.counter, "method": method}
        if params is not None:
            request["params"] = params
        self.process.stdin.write(json.dumps(request) + "\n")
        self.process.stdin.flush()
        response = self.responses.get(timeout=15)
        if response.get("id") != self.counter:
            raise AssertionError(f"Invalid protocol response: {response}")
        return response

    def tool(self, name, arguments=None, expect_error=False):
        response = self.request("tools/call", {"name": name, "arguments": arguments or {}})
        result = response["result"]
        if bool(result.get("isError")) != expect_error:
            raise AssertionError(f"Unexpected tool outcome: {name}: {result}")
        return json.loads(result["content"][0]["text"])

    def initialize(self):
        result = self.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
            "clientInfo": {"name": "diskscope-smoke", "version": "1"}})["result"]
        check(result["serverInfo"]["version"] == "1.0.0", "Packaged MCP version")
        self.process.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
        self.process.stdin.flush()

    def wait_job(self, job):
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            status = self.tool("disk_job_status", {"job_id": job["job_id"]})
            if status["state"] != "running":
                return status
            time.sleep(0.05)
        raise TimeoutError("Fixture job did not finish")

    def close(self):
        self.process.stdin.close()
        self.process.wait(timeout=30)
        check(self.process.returncode == 0, "Clean stdio shutdown")

def main():
    exe = Path(sys.argv[1]).resolve(strict=True)
    root = Path(tempfile.mkdtemp(prefix="diskscope-mcp-")).resolve()
    clients = []
    try:
        selection = root / "selection"
        selection.mkdir()
        for i in range(120):
            (selection / f"测试 file {i}.txt").write_text("temporary fixture", encoding="utf-8")
        c = Client(exe, root / "state")
        clients.append(c)
        c.initialize()
        check(c.request("ping")["result"] == {}, "Ping while connected")
        tools = c.request("tools/list")["result"]["tools"]
        check(len(tools) == 12, "All 12 public tools discovered")
        check(all(t["inputSchema"]["type"] == "object" for t in tools), "Tool schemas use MCP casing")
        check(next(t for t in tools if t["name"] == "disk_cleanup_execute")["annotations"]["destructiveHint"], "Deletion is annotated destructive")
        check(isinstance(c.tool("disk_volumes"), list), "Live volume summary is bounded metadata")
        check("error" in c.tool("disk_job_status", {"job_id": "missing"}, expect_error=True), "Unknown job produces a tool error")
        started_at = time.monotonic()
        job = c.tool("disk_cleanup_plan", {"paths": [str(selection)]})
        check(time.monotonic() - started_at < 2 and "job_id" in job, "Preview returns a job immediately")
        status = c.wait_job(job)
        check(status["state"] == "complete", "Preview job completes")
        plan = status["result"]
        check(plan["file_count"] == 120 and len(plan["items"]) == 20, "Preview is exact and paginated")
        page = c.tool("disk_cleanup_plan_show", {"plan_id": plan["plan_id"], "offset": 20, "limit": 7})
        check(len(page["items"]) == 7 and page["offset"] == 20, "Preview pagination")
        survivor = selection / "created-after-preview.txt"
        survivor.write_text("keep", encoding="utf-8")
        started_at = time.monotonic()
        execution = c.tool("disk_cleanup_execute", {"plan_id": plan["plan_id"], "confirm_token": plan["confirm_token"]})
        check(time.monotonic() - started_at < 2 and "job_id" in execution, "Deletion returns a job immediately")
        check(c.request("ping")["result"] == {}, "Server remains responsive during background work")
        result = c.wait_job(execution)
        check(result["state"] == "complete" and result["result"]["deleted_files"] == 120, "Background deletion completes exactly")
        check(survivor.exists() and len(list(selection.iterdir())) == 1, "New files and directory survive MCP cleanup")
        audit = c.tool("disk_cleanup_audit", {"plan_id": plan["plan_id"]})
        check(audit["state"] == "complete" and audit["deleted_files"] == 120, "MCP durable audit summary")
        replay = c.tool("disk_cleanup_execute", {"plan_id": plan["plan_id"], "confirm_token": plan["confirm_token"]})
        check(c.wait_job(replay)["state"] == "failed", "Attempted plan cannot be replayed")
        check(c.tool("disk_release")["snapshots_on_disk_preserved"], "Explicit release is exposed through MCP")
        c.close()
        clients.remove(c)
        ro = Client(exe, root / "state-ro", read_only=True)
        clients.append(ro)
        ro.initialize()
        ro_tools = ro.request("tools/list")["result"]["tools"]
        check(len(ro_tools) == 8 and not any(t["name"].startswith("disk_cleanup_") for t in ro_tools), "Analysis-only mode hides all cleanup tools")
        check("disabled" in ro.tool("disk_cleanup_execute", {"plan_id": "x", "confirm_token": "x"}, expect_error=True)["error"], "Hidden deletion tool is also rejected")
        ro.close()
        clients.remove(ro)
        cli = subprocess.run([str(exe), "plan", str(survivor), "--read-only", "--data-dir", str(root / "cli-state")], capture_output=True, text=True, encoding="utf-8")
        check(cli.returncode != 0 and "disabled" in json.loads(cli.stdout)["error"], "CLI analysis-only mode rejects cleanup")
        print(f"{checks} protocol/package checks passed")
    finally:
        for c in clients:
            if c.process.poll() is None:
                c.process.stdin.close()
                try:
                    c.process.wait(timeout=30)
                except subprocess.TimeoutExpired:
                    c.process.kill()
                    c.process.wait()
        expected_parent = Path(tempfile.gettempdir()).resolve()
        if root.parent != expected_parent or not root.name.startswith("diskscope-mcp-"):
            raise RuntimeError("Refusing cleanup outside the owned temporary fixture")
        shutil.rmtree(root)

if __name__ == "__main__":
    main()
