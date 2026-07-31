#!/usr/bin/env python3
"""ProcAnim 调试 MCP —— 让 Claude 直接在这个仓库里下断点、看变量、单步。

架构：
    Claude ──MCP(stdio)──► 本文件 ──HTTP(127.0.0.1)──► VS Code 扩展 ──► vsdbg ──► 目标进程

为什么绕 VS Code：vsdbg 的 license 只允许它在 VS Code / Visual Studio 里运行，
独立进程驱动会被它拒绝执行。扩展在 VS Code 内部代持调试会话，这里只是遥控器。

前置条件：VS Code 打开本仓库工作区，且 proc-anim-debug-bridge 扩展已激活。
"""
from __future__ import annotations

import json
import subprocess
import time
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any

from mcp.server import MCPServer

app = MCPServer("procanim-debug")

PORT_FILE = Path.home() / ".proc_anim_debug_bridge.json"
REPO = Path(__file__).resolve().parents[2]
DEBUG_APP = Path.home() / "Applications" / "Godot_mono_debug.app"


# ---------------------------------------------------------------- 桥接层

class BridgeError(RuntimeError):
    pass


def _port() -> int:
    if not PORT_FILE.exists():
        raise BridgeError(
            f"找不到桥端口文件 {PORT_FILE}。\n"
            "请确认：① VS Code 打开了本仓库工作区；② proc-anim-debug-bridge 扩展已激活"
            "（装完扩展需要重载窗口：命令面板 → Developer: Reload Window）。"
        )
    return json.loads(PORT_FILE.read_text())["port"]


def _call(route: str, payload: dict | None = None, timeout: float = 30.0) -> dict:
    url = f"http://127.0.0.1:{_port()}{route}"
    raw = json.dumps(payload or {}).encode("utf-8")
    req = urllib.request.Request(url, data=raw, headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        detail = e.read().decode("utf-8", "replace")
        try:
            detail = json.loads(detail).get("error", detail)
        except Exception:
            pass
        raise BridgeError(f"{route} 失败：{detail}") from None
    except urllib.error.URLError as e:
        raise BridgeError(f"连不上 VS Code 桥（{url}）：{e.reason}") from None


def _dap(command: str, args: dict | None = None) -> Any:
    return _call("/request", {"command": command, "args": args or {}})["result"]


def _abs(file: str) -> str:
    p = Path(file)
    return str(p if p.is_absolute() else (REPO / p))


def _rel(file: str) -> str:
    try:
        return str(Path(file).relative_to(REPO))
    except ValueError:
        return file


def _source_line(file: str, line: int) -> str:
    try:
        text = Path(file).read_text(encoding="utf-8", errors="replace").splitlines()
        return text[line - 1].strip() if 0 < line <= len(text) else ""
    except OSError:
        return ""


# ---------------------------------------------------------------- 停驻状态

def _wait_stop(timeout_ms: int) -> dict:
    """轮询直到目标停下（断点/单步完成）或超时。"""
    deadline = time.time() + timeout_ms / 1000.0
    while time.time() < deadline:
        st = _call("/status")
        if st.get("stopped"):
            return st
        if st.get("terminated"):
            return st
        time.sleep(0.08)
    return _call("/status")


def _describe_stop(st: dict) -> str:
    if st.get("terminated"):
        return "目标进程已结束。"
    stopped = st.get("stopped")
    if not stopped:
        return "目标仍在运行（未命中断点）。"
    frames = _dap("stackTrace", {"threadId": stopped["threadId"], "startFrame": 0, "levels": 1})
    top = (frames or {}).get("stackFrames", [{}])[0]
    src = ((top.get("source") or {}).get("path")) or "?"
    line = top.get("line")
    code = _source_line(src, line) if line else ""
    extra = f" [{stopped['text']}]" if stopped.get("text") else ""
    return (f"停在 {top.get('name', '?')}\n"
            f"  {_rel(src)}:{line}{extra}\n"
            f"  > {code}")


def _require_no_session() -> None:
    """已有活动会话时拒绝再起一个。

    VS Code 允许多会话并存，但 activeDebugSession 只指向最后一个——桥的所有 DAP
    透传都打在它身上，旧会话会变成看不见摸不着的僵尸。
    """
    s = _call("/status").get("session")
    if s:
        raise BridgeError(f"已有活动调试会话「{s['name']}」——先 debug_stop 再起新的。")


def _require_stop() -> dict:
    st = _call("/status")
    if not st.get("stopped"):
        raise BridgeError("目标当前不在断点上——先 debug_continue 或设个断点。")
    return st


# ---------------------------------------------------------------- 工具

@app.tool()
def debug_status() -> str:
    """查看调试桥与当前会话状态：是否连上 VS Code、有无活动会话、停在哪、有哪些断点。"""
    st = _call("/status")
    lines = [f"桥: ok  工作区: {', '.join(st.get('workspace') or []) or '(无)'}"]
    s = st.get("session")
    lines.append(f"会话: {s['name']} (type={s['type']})" if s else "会话: 无")
    if s:
        lines.append(_describe_stop(st))
    bps = st.get("breakpoints") or []
    if bps:
        lines.append("断点:")
        for b in bps:
            cond = f"  条件: {b['condition']}" if b.get("condition") else ""
            lines.append(f"  #{b['id']} {_rel(b.get('file', '?'))}:{b.get('line')}{cond}")
    else:
        lines.append("断点: 无")
    return "\n".join(lines)


@app.tool()
def debug_launch(config: str, wait_ms: int = 20000) -> str:
    """按 .vscode/launch.json 里的配置名启动一个调试会话。

    可用配置：
      "内核 smoke（Lizard/Centipede/Vulture/Humanoid）" / "内核 spider_smoke" / "内核 cicada_smoke"
        —— 纯 dotnet 宿主，开箱即用
      "Godot 沙盒（交互）" / "Godot 无头矩阵配置（可改 args）"
        —— 走重签名副本 ~/Applications/Godot_mono_debug.app（官方那份带 hardened
           runtime 且无 get-task-allow，macOS 会拒绝调试器接管）
    启动后若在 wait_ms 内命中断点，直接返回停驻位置。
    """
    _require_no_session()
    _call("/launch", {"config": config}, timeout=60)
    st = _wait_stop(wait_ms)
    return f"已启动「{config}」\n{_describe_stop(st)}"


def _godot_processes() -> list[dict]:
    """枚举本机 Godot 进程。

    attachable 只看可执行文件是不是重签名那份：macOS 的 task_for_pid 闸门认的是
    目标进程的签名，attach 和 launch 过的是同一道关——官方 app 起的进程附加不上。
    """
    out = subprocess.run(["ps", "-ax", "-o", "pid=,ppid=,args="],
                         capture_output=True, text=True).stdout
    procs = []
    for line in out.splitlines():
        parts = line.strip().split(None, 2)
        if len(parts) < 3:
            continue
        pid, ppid, args = parts
        exe = args.split(" ", 1)[0]
        if not exe.endswith("/Godot"):
            continue
        procs.append({
            "pid": int(pid),
            "ppid": int(ppid),
            "exe": exe,
            "args": args,
            "attachable": exe.startswith(str(DEBUG_APP)),
            # 编辑器播放场景时给子进程加的自有调试端口，用来区分「被播放的场景」和编辑器本体
            "played": "--remote-debug" in args,
        })
    return procs


def _format_procs(procs: list[dict]) -> str:
    lines = []
    for p in procs:
        tag = "可附加" if p["attachable"] else "不可附加(官方签名)"
        role = "播放中的场景" if p["played"] else "编辑器/独立进程"
        lines.append(f"  pid={p['pid']} ppid={p['ppid']} [{tag}] [{role}]\n    {p['args'][:150]}")
    return "\n".join(lines)


@app.tool()
def debug_list_processes() -> str:
    """列出运行中的 Godot 进程，标出哪些可以附加调试器。"""
    procs = _godot_processes()
    return _format_procs(procs) if procs else "没有运行中的 Godot 进程。"


@app.tool()
def debug_attach(pid: int = 0, wait_ms: int = 10000) -> str:
    """附加到已在运行的 Godot 进程——用于你自己起的场景（Godot 编辑器里播放，
    或直接运行 app），不打断它、不重启。

    不传 pid 时自动挑：优先带 --remote-debug 的「播放中的场景」；多个候选会列出来
    让你指定。目标必须是 ~/Applications/Godot_mono_debug.app 起的进程。
    """
    _require_no_session()
    procs = _godot_processes()
    if not procs:
        return "没有找到运行中的 Godot 进程。"

    if pid:
        target = next((p for p in procs if p["pid"] == pid), None)
        if target is None:
            return f"pid {pid} 不是运行中的 Godot 进程。当前：\n{_format_procs(procs)}"
    else:
        candidates = [p for p in procs if p["played"]] or procs
        if len(candidates) > 1:
            return f"有多个候选，请指定 pid：\n{_format_procs(candidates)}"
        target = candidates[0]

    if not target["attachable"]:
        return (f"pid {target['pid']} 用的是\n  {target['exe']}\n"
                "它没有 get-task-allow，macOS 会拒绝附加（跟 launch 同一道闸门）。\n"
                f"请改用 {DEBUG_APP} 打开编辑器或场景——"
                "编辑器播放出来的子进程会继承同一份签名，也就一并可调试。")

    _call("/launch", {"config": {
        "name": f"attach Godot {target['pid']}",
        "type": "coreclr",
        "request": "attach",
        "processId": str(target["pid"]),
    }}, timeout=60)

    deadline = time.time() + 15
    while time.time() < deadline:
        st = _call("/status")
        if st.get("session"):
            break
        time.sleep(0.1)
    else:
        return f"附加请求已发出，但 15s 内没看到调试会话建立（pid {target['pid']}）。"

    st = _wait_stop(wait_ms) if _call("/status").get("breakpoints") else _call("/status")
    tail = _describe_stop(st) if st.get("stopped") else "进程继续运行中（未停在断点）。"
    return f"已附加到 pid {target['pid']}\n  {target['args'][:150]}\n{tail}"


@app.tool()
def debug_set_breakpoint(file: str, line: int, condition: str = "") -> str:
    """下断点。file 可用相对仓库根的路径（如 core/Limb.cs）。

    condition 是 C# 布尔表达式，在这个确定性内核里极有用——
    例如 "TickIndex == 1290"、"_footing < 2"、"float.IsNaN(vel.X)"。
    单步走不完 2000 tick，条件断点才是主力。
    """
    payload = {"file": _abs(file), "line": line}
    if condition:
        payload["condition"] = condition
    bp = _call("/breakpoint/add", payload)["added"]
    code = _source_line(_abs(file), line)
    cond = f"\n  条件: {condition}" if condition else ""
    return f"断点 #{bp['id']} → {_rel(_abs(file))}:{line}{cond}\n  > {code}"


@app.tool()
def debug_remove_breakpoint(file: str = "", line: int = 0, id: str = "") -> str:
    """移除断点：给 file+line，或给 debug_status 里显示的 id。"""
    payload = {"id": id} if id else {"file": _abs(file), "line": line}
    removed = _call("/breakpoint/remove", payload)["removed"]
    return "已移除：" + ", ".join(f"{_rel(b.get('file', '?'))}:{b.get('line')}" for b in removed)


@app.tool()
def debug_clear_breakpoints() -> str:
    """清掉所有断点。"""
    removed = _call("/breakpoint/clear")["removed"]
    return f"已清除 {len(removed)} 个断点。"


@app.tool()
def debug_continue(wait_ms: int = 20000) -> str:
    """继续运行，直到命中下一个断点、进程结束或超时。返回停在哪。"""
    st = _call("/status")
    if st.get("stopped"):
        _dap("continue", {"threadId": st["stopped"]["threadId"]})
    return _describe_stop(_wait_stop(wait_ms))


@app.tool()
def debug_step(kind: str = "over", wait_ms: int = 15000) -> str:
    """单步：kind = over（下一行）/ into（进入调用）/ out（跳出当前函数）。"""
    command = {"over": "next", "into": "stepIn", "out": "stepOut"}.get(kind)
    if not command:
        return f"kind 只能是 over/into/out，收到 {kind!r}"
    st = _require_stop()
    _dap(command, {"threadId": st["stopped"]["threadId"]})
    return _describe_stop(_wait_stop(wait_ms))


@app.tool()
def debug_stack(depth: int = 12) -> str:
    """打印当前调用栈。"""
    st = _require_stop()
    frames = _dap("stackTrace", {"threadId": st["stopped"]["threadId"],
                                 "startFrame": 0, "levels": depth}) or {}
    out = []
    for i, f in enumerate(frames.get("stackFrames", [])):
        src = (f.get("source") or {}).get("path")
        where = f"{_rel(src)}:{f.get('line')}" if src else "(无源码)"
        out.append(f"  #{i} {f.get('name')}   @ {where}")
    return "调用栈：\n" + "\n".join(out) if out else "拿不到调用栈。"


def _expand(ref: int, depth: int, indent: str = "    ") -> list[str]:
    """递归展开变量树。Vector3 这类结构体默认展一层，省往返。"""
    if ref <= 0 or depth <= 0:
        return []
    result = _dap("variables", {"variablesReference": ref}) or {}
    lines = []
    for v in result.get("variables", []):
        name, value = v.get("name"), v.get("value", "")
        lines.append(f"{indent}{name} = {value}")
        child = v.get("variablesReference", 0)
        # 只对结构体/对象继续展开，数组和长集合留给 debug_eval 按需取
        if child and depth > 1 and not value.startswith("Count ="):
            lines.extend(_expand(child, depth - 1, indent + "  "))
    return lines


@app.tool()
def debug_locals(frame: int = 0, depth: int = 2) -> str:
    """看当前栈帧的局部变量和 this 字段（自动展开 depth 层，Vector3 之类直接看到 X/Y/Z）。"""
    st = _require_stop()
    frames = _dap("stackTrace", {"threadId": st["stopped"]["threadId"],
                                 "startFrame": frame, "levels": 1}) or {}
    fr = (frames.get("stackFrames") or [None])[0]
    if not fr:
        return f"没有第 {frame} 层栈帧。"
    scopes = _dap("scopes", {"frameId": fr["id"]}) or {}
    out = [f"帧 #{frame} {fr.get('name')}"]
    for sc in scopes.get("scopes", []):
        if sc.get("expensive"):
            continue
        out.append(f"  [{sc.get('name')}]")
        out.extend(_expand(sc.get("variablesReference", 0), depth))
    return "\n".join(out)


@app.tool()
def debug_eval(expr: str, frame: int = 0, depth: int = 1) -> str:
    """在当前栈帧里求值任意 C# 表达式，例如 "_chunks[0].Pos"、"head.Pos - hips.Pos"、"_limbs.Length"。"""
    st = _require_stop()
    frames = _dap("stackTrace", {"threadId": st["stopped"]["threadId"],
                                 "startFrame": frame, "levels": 1}) or {}
    fr = (frames.get("stackFrames") or [None])[0]
    if not fr:
        return f"没有第 {frame} 层栈帧。"
    res = _dap("evaluate", {"expression": expr, "frameId": fr["id"], "context": "repl"}) or {}
    lines = [f"{expr} = {res.get('result')}"]
    lines.extend(_expand(res.get("variablesReference", 0), depth))
    return "\n".join(lines)


@app.tool()
def debug_output(drain: bool = True) -> str:
    """取目标进程到目前为止的 stdout/stderr（[SANDBOX]/[METRIC]/[RESULT] 这些都在这里）。"""
    text = _call("/output", {"drain": drain}).get("output", "")
    return text or "(无输出)"


@app.tool()
def debug_stop() -> str:
    """结束当前调试会话。"""
    _call("/stop")
    return "调试会话已结束。"


if __name__ == "__main__":
    app.run()
