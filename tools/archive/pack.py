#!/usr/bin/env python3
"""FavSync 总库离线归档：按桶切分 + 7z 头加密 + 可增量。

设计要点（为什么是可扩展的）：
  * 桶 = 一组独立的 7z 分卷，**单个条目<avid>/c_<cid> 绝不跨桶**；
  * 每期只打"上期之后新增的条目"，旧卷一个字节都不动；
  * 恢复 = 把所有桶解到**同一个库根**，目录不重叠 ⇒ 并集就是完整库。

子命令：
  plan     按磁盘现状算出这一期要打哪些桶（不写盘，只出 plan/清单），可加 --copy 做小样
  pack     真正调用 7z 出卷 + 逐卷 sha256 + 写 MANIFEST
  verify   校验已出卷的 sha256
  mark     标记某期已上传（之后可 --prune 删本地卷）

示例：
  python pack.py plan  --root D:/YourLibrary --batch 1 --bucket-gib 8 --out out/archive
  python pack.py pack  --root D:/YourLibrary --batch 1 --out out/archive
  python pack.py verify --out out/archive --batch 1
  python pack.py mark  --out out/archive --batch 1
"""
from __future__ import annotations

import argparse
import getpass
import hashlib
import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

def _find_7z() -> str:
    """按 环境变量 → 常见安装位置 → PATH 的顺序找 7z。

    不硬编码单机路径：换台电脑就得改源码，也会把某台机器的软件布局带进仓库。
    都没命中时提示用户用 FAVSYNC_7Z 指过去。
    """
    env = os.environ.get("FAVSYNC_7Z")
    if env and Path(env).is_file():
        return env
    for c in (r"C:\Program Files\7-Zip\7z.exe",
              r"C:\Program Files (x86)\7-Zip\7z.exe"):
        if Path(c).is_file():
            return c
    found = shutil.which("7z") or shutil.which("7za")
    if found:
        return found
    sys.exit("找不到 7z：请用环境变量 FAVSYNC_7Z 指定 7z.exe 的路径")


SEVENZIP = _find_7z()
SKIP_TOP = {".favsync", ".incoming", ".trash", ".versions", "covers", "entry-backup"}


def log(msg: str) -> None:
    print(f"[{time.strftime('%H:%M:%S')}] {msg}", flush=True)


def human(n: float) -> str:
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if n < 1024 or unit == "TB":
            return f"{n:.1f} {unit}"
        n /= 1024
    return f"{n:.1f} TB"


def is_item_dir(p: Path) -> bool:
    return p.is_dir() and p.name.startswith("c_")


def discover(root: Path) -> list[tuple[str, int]]:
    """返回 [(rel_dir, bytes)]，rel_dir 形如 <avid>/c_<cid>。"""
    items: list[tuple[str, int]] = []
    for avid in sorted(root.iterdir()):
        if not avid.is_dir() or avid.name.startswith(".") or avid.name in SKIP_TOP:
            continue
        for cdir in sorted(avid.iterdir()):
            if not is_item_dir(cdir):
                continue
            total = 0
            for dirpath, _dirnames, filenames in os.walk(cdir):
                for fn in filenames:
                    try:
                        total += (Path(dirpath) / fn).stat().st_size
                    except OSError:
                        pass
            items.append((f"{avid.name}/{cdir.name}", total))
    return items


def load_state(out: Path) -> dict:
    f = out / "state.json"
    if f.exists():
        return json.loads(f.read_text(encoding="utf-8"))
    return {"nextBucket": 1, "doneItems": {}, "buckets": {}}


def save_state(out: Path, state: dict) -> None:
    (out / "state.json").write_text(
        json.dumps(state, ensure_ascii=False, indent=1), encoding="utf-8")


def cmd_plan(args) -> None:
    root, out = Path(args.root), Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    (out / "lists").mkdir(exist_ok=True)
    state = load_state(out)
    done = state["doneItems"]

    all_items = discover(root)
    todo = [(rel, size) for rel, size in all_items if rel not in done]
    total = sum(s for _r, s in todo)
    log(f"库内条目 {len(all_items)}，已归档 {len(done)}，本期待打 {len(todo)}（{human(total)}）")

    cap = int(args.bucket_gib * 1024 ** 3)
    buckets: list[dict] = []
    cur: list[tuple[str, int]] = []
    cur_bytes = 0
    next_id = state["nextBucket"]
    for rel, size in todo:
        if size > cap:
            log(f"⚠ 单条 {rel} = {human(size)} 超过桶上限，独占一桶")
        if cur and cur_bytes + size > cap:
            buckets.append({"id": next_id, "bytes": cur_bytes, "items": cur})
            next_id += 1
            cur, cur_bytes = [], 0
        cur.append((rel, size))
        cur_bytes += size
    if cur:
        buckets.append({"id": next_id, "bytes": cur_bytes, "items": cur})

    for b in buckets:
        lines = []
        for rel, _s in b["items"]:
            for dirpath, _dn, filenames in os.walk(root / rel.replace("/", os.sep)):
                for fn in filenames:
                    full = Path(dirpath) / fn
                    lines.append(str(full.relative_to(root)))
        (out / "lists" / f"bkt-{b['id']:04d}.txt").write_text(
            "\n".join(lines), encoding="utf-8")
        b["files"] = len(lines)
        log(f"  桶 {b['id']:04d}: {len(b['items'])} 条目 / {b['files']} 文件 / {human(b['bytes'])}")

    plan = {
        "batch": args.batch,
        "createdAt": time.strftime("%Y-%m-%dT%H:%M:%S"),
        "root": str(root),
        "bucketGiB": args.bucket_gib,
        "totalBytes": total,
        "itemCount": len(todo),
        "buckets": [{k: b[k] for k in ("id", "bytes", "files")} | {"items": [r for r, _ in b["items"]], "itemBytes": [s for _r, s in b["items"]]}
                    for b in buckets],
    }
    (out / f"plan-batch{args.batch}.json").write_text(
        json.dumps(plan, ensure_ascii=False, indent=1), encoding="utf-8")
    log(f"桶数 {len(buckets)}，计划写入 plan-batch{args.batch}.json")


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(8 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def cmd_pack(args) -> None:
    root, out = Path(args.root), Path(args.out)
    plan_path = out / f"plan-batch{args.batch}.json"
    plan = json.loads(plan_path.read_text(encoding="utf-8"))
    state = load_state(out)
    pw = os.environ.get("FAVSYNC_7Z_PASS") or getpass.getpass("7z 密码: ")
    if not pw:
        sys.exit("密码不能为空")
    sevenzip = args.sevenzip or SEVENZIP

    manifest = {"batch": args.batch, "createdAt": plan["createdAt"], "root": plan["root"], "buckets": []}
    for b in plan["buckets"]:
        bid = b["id"]
        archive = out / f"bkt-{bid:04d}.7z"
        listfile = out / "lists" / f"bkt-{bid:04d}.txt"
        if (out / f"bkt-{bid:04d}.7z.001").exists() and not args.force:
            log(f"跳过桶 {bid:04d}：已存在（--force 可重打）")
            continue
        log(f"打包桶 {bid:04d}（{len(b['items'])} 条目 / {human(b['bytes'])}）…")
        t0 = time.time()
        cmd = [sevenzip, "a", "-t7z", "-mx0", "-ms=off", "-mhe=on",
               f"-v{args.volume_size}", f"-p{pw}", str(archive), f"@{listfile}"]
        r = subprocess.run(cmd, cwd=str(root), stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        if r.returncode != 0:
            sys.exit(f"7z 失败（{r.returncode}）：\n{r.stdout.decode('utf-8', 'replace')[-2000:]}")
        vols = sorted(out.glob(f"bkt-{bid:04d}.7z.*"))
        rec = {"id": bid, "items": b["items"], "itemBytes": b["itemBytes"],
               "bytes": b["bytes"], "volumes": []}
        for v in vols:
            digest = sha256_file(v)
            rec["volumes"].append({"name": v.name, "bytes": v.stat().st_size, "sha256": digest})
            (out / f"{v.name}.sha256").write_text(f"{digest}  {v.name}\n", encoding="utf-8")
        log(f"  完成 {len(vols)} 卷，{time.time() - t0:.0f}s")
        manifest["buckets"].append(rec)
        state["buckets"][str(bid)] = {"items": b["items"], "volumes": [v["name"] for v in rec["volumes"]]}
        for rel in b["items"]:
            state["doneItems"][rel] = bid
        state["nextBucket"] = bid + 1
        save_state(out, state)
        (out / f"MANIFEST-batch{args.batch}.json").write_text(
            json.dumps(manifest, ensure_ascii=False, indent=1), encoding="utf-8")

    log("打包结束：MANIFEST-batch%d.json" % args.batch)


def cmd_verify(args) -> None:
    out = Path(args.out)
    mf = json.loads((out / f"MANIFEST-batch{args.batch}.json").read_text(encoding="utf-8"))
    bad = miss = ok = 0
    for b in mf["buckets"]:
        for v in b["volumes"]:
            f = out / v["name"]
            if not f.exists():
                miss += 1
                log(f"缺卷 {v['name']}")
            elif sha256_file(f) != v["sha256"]:
                bad += 1
                log(f"校验失败 {v['name']}")
            else:
                ok += 1
    log(f"校验完成：通过 {ok}，缺失 {miss}，损坏 {bad}")
    sys.exit(0 if bad == 0 else 1)


def cmd_mark(args) -> None:
    out = Path(args.out)
    mf = json.loads((out / f"MANIFEST-batch{args.batch}.json").read_text(encoding="utf-8"))
    state = load_state(out)
    n = 0
    for b in mf["buckets"]:
        state["buckets"].setdefault(str(b["id"]), {})["uploaded"] = True
        n += 1
    save_state(out, state)
    log(f"已把 {n} 个桶标记为已上传")
    if args.prune:
        freed = 0
        for b in mf["buckets"]:
            for v in b["volumes"]:
                f = out / v["name"]
                if f.exists():
                    freed += f.stat().st_size
                    f.unlink()
        log(f"已删除本地卷，释放 {human(freed)}（云端为准，sha256 已记录）")


def cmd_status(args) -> None:
    out = Path(args.out)
    state = load_state(out)
    done = state["doneItems"]
    total = sum(1 for _ in done)
    log(f"已归档条目 {total}，下一个桶号 {state['nextBucket']}")
    for bid, rec in sorted(state["buckets"].items(), key=lambda x: int(x[0])):
        flag = "已上传" if rec.get("uploaded") else "未上传"
        log(f"  桶 {int(bid):04d}: {len(rec.get('items', []))} 条目 / {len(rec.get('volumes', []))} 卷 / {flag}")


def main() -> None:
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("plan"); p.set_defaults(fn=cmd_plan)
    p.add_argument("--root", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--batch", type=int, required=True)
    p.add_argument("--bucket-gib", type=float, default=8)

    p = sub.add_parser("pack"); p.set_defaults(fn=cmd_pack)
    p.add_argument("--root", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--batch", type=int, required=True)
    p.add_argument("--volume-size", default="2g")
    p.add_argument("--sevenzip", default=None)
    p.add_argument("--force", action="store_true")

    p = sub.add_parser("verify"); p.set_defaults(fn=cmd_verify)
    p.add_argument("--out", required=True)
    p.add_argument("--batch", type=int, required=True)

    p = sub.add_parser("mark"); p.set_defaults(fn=cmd_mark)
    p.add_argument("--out", required=True)
    p.add_argument("--batch", type=int, required=True)
    p.add_argument("--prune", action="store_true")

    p = sub.add_parser("status"); p.set_defaults(fn=cmd_status)
    p.add_argument("--out", required=True)

    args = ap.parse_args()
    args.fn(args)


if __name__ == "__main__":
    main()
