#!/usr/bin/env python3

import argparse
import errno
import json
import os
from pathlib import Path
import shutil
import signal
import sys
import tempfile
import time


class FeasibilityFailure(RuntimeError):
    pass


class InjectedMembershipFailure(FeasibilityFailure):
    pass


def read_populated(events_path: Path, inject_failure: bool = False) -> int:
    if inject_failure:
        raise InjectedMembershipFailure("Injected cgroup membership query failure.")

    try:
        lines = events_path.read_text(encoding="ascii").splitlines()
    except OSError as error:
        raise FeasibilityFailure(
            f"Cannot read authoritative cgroup membership state: {error}") from error

    values = []
    for line in lines:
        fields = line.split()
        if len(fields) == 2 and fields[0] == "populated":
            values.append(fields[1])

    if len(values) != 1 or values[0] not in {"0", "1"}:
        raise FeasibilityFailure(
            "cgroup.events does not contain exactly one valid populated value.")

    return int(values[0])


def wait_until(predicate, timeout_seconds: float, description: str) -> None:
    deadline = time.monotonic() + timeout_seconds
    while time.monotonic() < deadline:
        if predicate():
            return
        time.sleep(0.02)
    raise FeasibilityFailure(f"Timed out waiting for {description}.")


def current_cgroup_path() -> Path:
    entries = Path("/proc/self/cgroup").read_text(encoding="ascii").splitlines()
    unified = [entry.split("::", 1)[1] for entry in entries if entry.startswith("0::")]
    if len(unified) != 1:
        raise FeasibilityFailure("The runner is not in one unified cgroup v2 hierarchy.")
    return Path("/sys/fs/cgroup") / unified[0].lstrip("/")


def read_parent_process_id(process_id: int) -> int:
    status_path = Path("/proc") / str(process_id) / "status"
    for line in status_path.read_text(encoding="ascii").splitlines():
        if line.startswith("PPid:"):
            return int(line.split(":", 1)[1].strip())
    raise FeasibilityFailure(f"PPid is missing for descendant {process_id}.")


def assert_fail_closed_parser() -> None:
    with tempfile.TemporaryDirectory(prefix="downkyi-cgroup-parser-") as directory:
        malformed = Path(directory) / "cgroup.events"
        malformed.write_text("populated unknown\n", encoding="ascii")
        try:
            read_populated(malformed)
        except FeasibilityFailure:
            pass
        else:
            raise FeasibilityFailure("Malformed membership state was accepted as quiescent.")

        try:
            read_populated(Path(directory) / "missing.events")
        except FeasibilityFailure:
            pass
        else:
            raise FeasibilityFailure("Missing membership state was accepted as quiescent.")


def run_probe(inject_membership_failure: bool) -> dict[str, object]:
    if sys.platform != "linux":
        raise FeasibilityFailure("The cgroup feasibility probe requires Linux.")
    if os.geteuid() == 0:
        raise FeasibilityFailure(
            "The proof must run as the unprivileged runner user, not as root.")
    if os.statvfs("/sys/fs/cgroup").f_flag & getattr(os, "ST_RDONLY", 1):
        raise FeasibilityFailure("The cgroup v2 mount is read-only.")

    parent_cgroup = current_cgroup_path()
    probe_cgroup = parent_cgroup / f"downkyi-feasibility-{os.getpid()}"
    try:
        probe_cgroup.mkdir(mode=0o700)
    except OSError as error:
        raise FeasibilityFailure(
            "The GitHub runner cgroup is not delegated to the runner user: "
            f"cannot create {probe_cgroup}: {error}") from error

    events_path = probe_cgroup / "cgroup.events"
    processes_path = probe_cgroup / "cgroup.procs"
    kill_path = probe_cgroup / "cgroup.kill"
    required_paths = [events_path, processes_path, kill_path]
    if not all(path.exists() for path in required_paths):
        shutil.rmtree(probe_cgroup, ignore_errors=True)
        raise FeasibilityFailure(
            "The delegated cgroup lacks cgroup.events, cgroup.procs, or cgroup.kill.")

    authorize_read, authorize_write = os.pipe()
    descendant_read, descendant_write = os.pipe()
    root_process_id = -1
    descendant_process_id = -1
    root_reaped = False
    operation_failure: Exception | None = None

    try:
        root_process_id = os.fork()
        if root_process_id == 0:
            try:
                os.close(authorize_write)
                os.close(descendant_read)
                if os.read(authorize_read, 1) != b"A":
                    os._exit(70)
                os.close(authorize_read)

                descendant_process_id = os.fork()
                if descendant_process_id == 0:
                    os.close(descendant_write)
                    signal.signal(signal.SIGTERM, signal.SIG_DFL)
                    signal.signal(signal.SIGINT, signal.SIG_DFL)
                    while True:
                        signal.pause()

                os.write(descendant_write, f"{descendant_process_id}\n".encode("ascii"))
                os.close(descendant_write)
                os._exit(0)
            except BaseException:
                os._exit(71)

        os.close(authorize_read)
        os.close(descendant_write)

        processes_path.write_text(f"{root_process_id}\n", encoding="ascii")
        if read_populated(events_path) != 1:
            raise FeasibilityFailure("The inert root was not recorded in the delegated cgroup.")

        os.write(authorize_write, b"A")
        os.close(authorize_write)
        authorize_write = -1

        descendant_bytes = bytearray()
        while not descendant_bytes.endswith(b"\n"):
            chunk = os.read(descendant_read, 32)
            if not chunk:
                break
            descendant_bytes.extend(chunk)
        os.close(descendant_read)
        descendant_read = -1
        descendant_process_id = int(descendant_bytes.decode("ascii").strip())

        waited_process_id, status = os.waitpid(root_process_id, 0)
        root_reaped = True
        if waited_process_id != root_process_id or status != 0:
            raise FeasibilityFailure("The workload parent did not exit cleanly.")

        reparented_to = root_process_id
        wait_until(
            lambda: read_parent_process_id(descendant_process_id) != root_process_id,
            5.0,
            "the live descendant to be reparented",
        )
        reparented_to = read_parent_process_id(descendant_process_id)
        os.kill(descendant_process_id, 0)

        if inject_membership_failure:
            try:
                read_populated(events_path, inject_failure=True)
            except FeasibilityFailure as error:
                operation_failure = error
            else:
                raise FeasibilityFailure("Injected membership failure produced success.")
        elif read_populated(events_path) != 1:
            raise FeasibilityFailure(
                "The authoritative cgroup became quiescent while a descendant was alive.")

        kill_path.write_text("1\n", encoding="ascii")
        wait_until(
            lambda: read_populated(events_path) == 0,
            5.0,
            "cgroup membership to converge to quiescence",
        )

        result = {
            "backend": "cgroup-v2",
            "parentCgroup": str(parent_cgroup),
            "rootExited": True,
            "descendantWasAlive": True,
            "descendantReparented": reparented_to != root_process_id,
            "termination": "cgroup.kill",
            "quiescent": True,
            "failureInjected": inject_membership_failure,
            "failedClosed": operation_failure is not None,
        }
        if operation_failure is not None:
            raise operation_failure
        return result
    finally:
        for descriptor in [authorize_read, authorize_write, descendant_read, descendant_write]:
            if descriptor >= 0:
                try:
                    os.close(descriptor)
                except OSError:
                    pass

        if root_process_id > 0 and not root_reaped:
            try:
                kill_path.write_text("1\n", encoding="ascii")
            except OSError:
                try:
                    os.kill(root_process_id, signal.SIGKILL)
                except ProcessLookupError:
                    pass
            try:
                os.waitpid(root_process_id, 0)
            except ChildProcessError:
                pass

        try:
            if read_populated(events_path) != 0:
                kill_path.write_text("1\n", encoding="ascii")
                wait_until(
                    lambda: read_populated(events_path) == 0,
                    5.0,
                    "cleanup cgroup membership to become quiescent",
                )
            probe_cgroup.rmdir()
        except BaseException as error:
            raise FeasibilityFailure(f"The delegated cgroup could not be reaped: {error}") from error


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--inject-membership-failure", action="store_true")
    arguments = parser.parse_args()

    assert_fail_closed_parser()
    try:
        result = run_probe(arguments.inject_membership_failure)
    except FeasibilityFailure as error:
        injected_failure = isinstance(error, InjectedMembershipFailure)
        print(
            json.dumps(
                {
                    "success": False,
                    "error": str(error),
                    "failureInjected": arguments.inject_membership_failure,
                    "failedClosed": injected_failure,
                }
            ),
            flush=True,
        )
        return 42 if injected_failure else 1

    print(json.dumps({"success": True, **result}), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
